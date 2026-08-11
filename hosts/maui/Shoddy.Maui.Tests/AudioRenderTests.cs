// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace Shoddy.Maui.Tests;

/// <summary>
/// The proof the drain test turned out not to be: that the AudioGraph
/// actually PULLS. A voice's Playing flag goes false in exactly one
/// place — inside the quantum callback, when the PCM runs dry — so a
/// true-to-false transition is positive evidence the graph consumed
/// the samples and pushed them at the device. A sink that never
/// renders holds Playing true forever, and this fails. (Audibility
/// past the endpoint — mixer, volume, routing — still needs an ear.)
/// </summary>
public class AudioRenderTests
{
    readonly ITestOutputHelper output;

    public AudioRenderTests(ITestOutputHelper output) => this.output = output;

    static bool Headless => Environment.GetEnvironmentVariable("SHODDY_HEADLESS_CI") == "1";

    [Fact]
    public async Task QuantumStartedFiresAtAll()
    {
        if (Headless) { output.WriteLine("skipped: SHODDY_HEADLESS_CI"); return; }

        // The sink stripped to its skeleton: does a frame input node's
        // QuantumStarted fire in this process at all? Splits "event
        // never fires" from "our Fill dies on the audio thread".
        var settings = new Windows.Media.Audio.AudioGraphSettings(
            Windows.Media.Render.AudioRenderCategory.Media);
        var made = Windows.Media.Audio.AudioGraph.CreateAsync(settings).GetAwaiter().GetResult();
        Assert.Equal(Windows.Media.Audio.AudioGraphCreationStatus.Success, made.Status);
        var graph = made.Graph;
        try
        {
            var dev = graph.CreateDeviceOutputNodeAsync().GetAwaiter().GetResult();
            Assert.Equal(Windows.Media.Audio.AudioDeviceNodeCreationStatus.Success, dev.Status);

            var enc = Windows.Media.MediaProperties.AudioEncodingProperties.CreatePcm(
                graph.EncodingProperties.SampleRate, 1, 32);
            enc.Subtype = "Float";
            var node = graph.CreateFrameInputNode(enc);
            int fired = 0, filled = 0;
            Exception? died = null;
            node.QuantumStarted += (n, args) =>
            {
                Interlocked.Increment(ref fired);
                try
                {
                    // Exactly the sink's Fill, minus the voice state:
                    // frame, lock, COM cast, write, AddFrame.
                    int required = args.RequiredSamples;
                    if (required <= 0) return;
                    FillSilence(n, required);
                    Interlocked.Increment(ref filled);
                }
                catch (Exception e)
                {
                    died ??= e;
                }
            };
            node.AddOutgoingConnection(dev.DeviceOutputNode);
            node.Start();
            graph.Start();

            await Task.Delay(1000);
            output.WriteLine($"QuantumStarted fired {fired}, filled {filled}, died: {died?.GetType().Name} {died?.Message}");
            Assert.True(fired > 0, "QuantumStarted never fired on a bare graph");
            Assert.True(died is null, $"the fill path threw on the audio thread: {died}");
            Assert.True(filled > 0, "no quantum ever needed samples");
        }
        finally
        {
            try { graph.Stop(); } catch { }
            graph.Dispose();
        }
    }

    static unsafe void FillSilence(Windows.Media.Audio.AudioFrameInputNode node, int required)
    {
        var frame = new Windows.Media.AudioFrame((uint)(required * sizeof(float)));
        using (var buffer = frame.LockBuffer(Windows.Media.AudioBufferAccessMode.Write))
        using (var reference = buffer.CreateReference())
        {
            WinRT.CastExtensions.As<IMemoryBufferByteAccess>(reference).GetBuffer(out byte* raw, out uint _);
            float* samples = (float*)raw;
            for (int i = 0; i < required; i++) samples[i] = 0f;
        }
        node.AddFrame(frame);
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }

    [Fact]
    public async Task TheGraphConsumesAPlayedTone()
    {
        if (Headless)
        {
            output.WriteLine("skipped: SHODDY_HEADLESS_CI — no audio device on this runner");
            return;
        }

        var sink = new WindowsAudioSink();
        try
        {
            int voice = sink.CreateVoice();

            // 300 ms of 440 Hz at the engine's own rate.
            int rate = BuzzerEngine.SampleRate;
            var pcm = new short[rate * 3 / 10];
            for (int i = 0; i < pcm.Length; i++)
                pcm[i] = (short)(6000 * Math.Sin(2 * Math.PI * 440 * i / rate));

            sink.Play(voice, pcm, rate, loop: false, gain: 0.5, pitch: 1.0);
            Assert.True(sink.IsPlaying(voice), "the voice never took the tone");

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (sink.IsPlaying(voice) && DateTime.UtcNow < deadline)
                await Task.Delay(25);

            Assert.False(sink.IsPlaying(voice),
                "the quantum callback never consumed the tone — the graph is not pulling");
        }
        finally
        {
            sink.Dispose();
        }
    }
}
