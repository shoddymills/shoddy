// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Runtime.InteropServices;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using Shoddy.Runtime;
using WinRT;

namespace Shoddy.Maui;

/// <summary>
/// The Windows sink (D18): AudioGraph frame-input nodes, one per voice,
/// platform SDK only — no third-party audio package (B3.7). The sink is
/// deliberately dumb: the shared BuzzerEngine decides all behaviour
/// (channels, pooling, sticky gain, queues, ramps) and this class only
/// answers "play this PCM on this voice at this volume".
///
/// Pull model: each voice's QuantumStarted fills the graph's quantum by
/// stepping through the voice's PCM at (pcmRate × pitch ÷ graphRate),
/// nearest-neighbour — which is how one fixed-rate node plays any
/// sample rate at any pitch multiplier without rebuilding itself.
/// Every seam call arrives on the Shoddy thread; the quantum callback
/// arrives on the audio thread; the per-voice lock is the meeting
/// point. Construction throws when there is no audio device and the
/// engine turns that into permanent silence, identical to headless.
/// </summary>
public sealed class WindowsAudioSink : IAudioSink
{
    sealed class Voice
    {
        public readonly object Gate = new();
        public AudioFrameInputNode Node = null!;
        public short[]? Pcm;            // what is sounding now
        public int Rate;                // its sample rate
        public double Pos;              // fractional read position
        public bool Loop;
        public double Pitch = 1.0;
        public readonly Queue<(short[] Pcm, int Rate, double Gain)> Pending = new();
        public bool Playing;
    }

    readonly AudioGraph graph;
    readonly AudioDeviceOutputNode output;
    readonly List<Voice> voices = new();
    readonly object startGate = new();
    bool started;

    public WindowsAudioSink()
    {
        var settings = new AudioGraphSettings(AudioRenderCategory.Media);
        CreateAudioGraphResult made = AudioGraph.CreateAsync(settings).GetAwaiter().GetResult();
        if (made.Status != AudioGraphCreationStatus.Success)
            throw new InvalidOperationException("no audio graph: " + made.Status);
        graph = made.Graph;

        CreateAudioDeviceOutputNodeResult dev =
            graph.CreateDeviceOutputNodeAsync().GetAwaiter().GetResult();
        if (dev.Status != AudioDeviceNodeCreationStatus.Success)
        {
            graph.Dispose();
            throw new InvalidOperationException("no audio device: " + dev.Status);
        }
        output = dev.DeviceOutputNode;
        // The graph does NOT start here. Frame-input nodes added to an
        // already-running graph never receive QuantumStarted — the
        // canonical ordering is nodes first, Start() last — and the
        // engine creates every voice right after construction. First
        // Play/Queue starts the graph, once, with the wiring complete.
        // (Found by the render test: every voice sat silent, Playing
        // forever true, and the drain-based test could not tell.)
    }

    void EnsureStarted()
    {
        if (started) return;
        lock (startGate)
        {
            if (started) return;
            graph.Start();
            started = true;
        }
    }

    public int CreateVoice()
    {
        var v = new Voice();
        AudioEncodingProperties monoFloat = AudioEncodingProperties.CreatePcm(
            graph.EncodingProperties.SampleRate, 1, 32);
        monoFloat.Subtype = "Float";
        v.Node = graph.CreateFrameInputNode(monoFloat);
        v.Node.QuantumStarted += (node, args) => Fill(v, node, args.RequiredSamples);
        v.Node.AddOutgoingConnection(output);
        v.Node.Start();
        voices.Add(v);
        return voices.Count - 1;
    }

    public void Play(int voice, short[] pcm, int sampleRate, bool loop, double gain, double pitch)
    {
        Voice v = voices[voice];
        lock (v.Gate)
        {
            v.Pcm = pcm;
            v.Rate = sampleRate;
            v.Pos = 0;
            v.Loop = loop;
            v.Pitch = pitch;
            v.Pending.Clear();
            v.Playing = true;
        }
        v.Node.OutgoingGain = gain;
        EnsureStarted();
    }

    public void Queue(int voice, short[] pcm, int sampleRate, double gain)
    {
        Voice v = voices[voice];
        lock (v.Gate)
        {
            if (v.Playing && v.Pcm != null)
            {
                v.Pending.Enqueue((pcm, sampleRate, gain));
                return;
            }
            v.Pcm = pcm;
            v.Rate = sampleRate;
            v.Pos = 0;
            v.Loop = false;
            v.Playing = true;
        }
        v.Node.OutgoingGain = gain;
        EnsureStarted();
    }

    public void SetGain(int voice, double gain) => voices[voice].Node.OutgoingGain = gain;

    public void SetPitch(int voice, double pitch)
    {
        Voice v = voices[voice];
        lock (v.Gate) v.Pitch = pitch;
    }

    public void Stop(int voice)
    {
        Voice v = voices[voice];
        lock (v.Gate)
        {
            v.Pcm = null;
            v.Pending.Clear();
            v.Playing = false;
        }
    }

    public bool IsPlaying(int voice)
    {
        Voice v = voices[voice];
        lock (v.Gate) return v.Playing;
    }

    public void Dispose()
    {
        try { graph.Stop(); } catch { }
        graph.Dispose();
    }

    /// <summary>The audio thread asking for samples: step the voice's
    /// PCM at its own rate times pitch over the graph rate, wrap on
    /// loop, fall through the queue, report silence when dry.</summary>
    unsafe void Fill(Voice v, AudioFrameInputNode node, int required)
    {
        if (required <= 0) return;
        var frame = new AudioFrame((uint)(required * sizeof(float)));
        using (AudioBuffer buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
        using (Windows.Foundation.IMemoryBufferReference reference = buffer.CreateReference())
        {
            // CsWinRT: a projected WinRT object refuses the CLR cast to
            // a ComImport interface (InvalidCastException from
            // WinRT.IInspectable) — As<T>() does the QueryInterface.
            // That cast threw on the audio thread EVERY quantum,
            // swallowed by WinRT, and was the whole of "no sound":
            // the render test caught it the first time anything
            // asserted the graph actually pulls.
            reference.As<IMemoryBufferByteAccess>().GetBuffer(out byte* raw, out uint _);
            float* samples = (float*)raw;
            lock (v.Gate)
            {
                double step = v.Pcm is null ? 0
                    : (double)v.Rate * v.Pitch / graph.EncodingProperties.SampleRate;
                for (int i = 0; i < required; i++)
                {
                    if (v.Pcm is null)
                    {
                        samples[i] = 0f;
                        continue;
                    }
                    if (v.Pos >= v.Pcm.Length)
                    {
                        if (v.Loop)
                        {
                            v.Pos = 0;
                        }
                        else if (v.Pending.Count > 0)
                        {
                            (v.Pcm, v.Rate, double gain) = Dequeued(v);
                            v.Pos = 0;
                            step = (double)v.Rate * v.Pitch / graph.EncodingProperties.SampleRate;
                            node.OutgoingGain = gain;
                        }
                        else
                        {
                            v.Pcm = null;
                            v.Playing = false;
                            samples[i] = 0f;
                            continue;
                        }
                    }
                    samples[i] = v.Pcm[(int)v.Pos] / 32768f;
                    v.Pos += step;
                }
            }
        }
        node.AddFrame(frame);
    }

    static (short[], int, double) Dequeued(Voice v)
    {
        (short[] pcm, int rate, double gain) = v.Pending.Dequeue();
        return (pcm, rate, gain);
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
