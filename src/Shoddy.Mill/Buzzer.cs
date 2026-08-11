// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Runtime;
using Silk.NET.OpenAL;

namespace Shoddy.Mill;

/// <summary>The mill's audio layer — the buzzer, named for the steam
/// hooter on the engine house roof. Since the platform split (D18) this
/// file is only the desktop's <see cref="IAudioSink"/>: OpenAL (OpenAL
/// Soft natives) playing the PCM the shared <see cref="BuzzerEngine"/>
/// hands it. Synthesis, the eight channels, the anonymous pool,
/// steal-oldest, sticky gain and waveform, ramps and queue bookkeeping
/// all live in the engine, in Shoddy.Runtime, written once — the sink
/// decides nothing observable.
///
/// OpenAL does all mixing in its own output thread; every call here runs
/// on the Shoddy thread — OpenAL has no thread affinity, so there is no
/// dispatch and no marshal. The engine creates the sink lazily on the
/// first sound call; construction failure degrades to permanent silence,
/// identical to headless, because a missing sound card must never kill a
/// program whose logic is fine.
///
/// Ownership rule that keeps buffer lifetime trivial: every Play or
/// Queue gens a fresh AL buffer owned by exactly one voice — its static
/// slot or its stream queue — deleted when that slot moves on. No AL
/// buffer is ever shared, so nothing can delete a buffer still in
/// use.</summary>
public static class Buzzer
{
    public static void Install() => BuzzerEngine.Install(() => new OpenAlSink());

    public static void Shutdown(bool drain) => BuzzerEngine.Shutdown(drain);
}

sealed class OpenAlSink : IAudioSink
{
    readonly AL al;
    readonly ALContext alc;
    readonly unsafe Device* device;
    readonly unsafe Context* context;

    readonly List<uint> sources = new();
    readonly List<uint> staticBuf = new();   // per voice; 0 = none attached

    public unsafe OpenAlSink()
    {
        ALContext c; AL a;
        try { c = ALContext.GetApi(true); a = AL.GetApi(true); }   // bundled OpenAL Soft
        catch { c = ALContext.GetApi(); a = AL.GetApi(); }         // system OpenAL
        device = c.OpenDevice("");
        if (device == null) throw new InvalidOperationException("no audio device");
        context = c.CreateContext(device, null);
        if (context == null || !c.MakeContextCurrent(context))
            throw new InvalidOperationException("no audio context");
        alc = c; al = a;
    }

    public int CreateVoice()
    {
        uint s = 0;
        unsafe { al.GenSources(1, &s); }
        sources.Add(s);
        staticBuf.Add(0);
        return sources.Count - 1;
    }

    public void Play(int voice, short[] pcm, int sampleRate, bool loop, double gain, double pitch)
    {
        uint src = sources[voice];
        Silence(voice);
        uint buf = MakeBuffer(pcm, sampleRate);
        staticBuf[voice] = buf;
        al.SetSourceProperty(src, SourceInteger.Buffer, (int)buf);
        al.SetSourceProperty(src, SourceBoolean.Looping, loop);
        al.SetSourceProperty(src, SourceFloat.Pitch, (float)pitch);
        al.SetSourceProperty(src, SourceFloat.Gain, (float)gain);
        al.SourcePlay(src);
    }

    public void Queue(int voice, short[] pcm, int sampleRate, double gain)
    {
        uint src = sources[voice];
        // A voice moving from a static attach to streaming sheds the
        // attach first; the engine stops between modes, but the sink
        // stays safe either way.
        if (staticBuf[voice] != 0) Silence(voice);
        DrainProcessed(src);                         // where finished queue buffers die
        uint buf = MakeBuffer(pcm, sampleRate);
        unsafe { al.SourceQueueBuffers(src, 1, &buf); }
        al.SetSourceProperty(src, SourceFloat.Gain, (float)gain);
        // The classic streaming bug: a source that ran its queue dry went
        // to Stopped; without this the rest of the melody never sounds.
        if (State(src) != SourceState.Playing) al.SourcePlay(src);
    }

    public void SetGain(int voice, double gain) =>
        al.SetSourceProperty(sources[voice], SourceFloat.Gain, (float)gain);

    public void SetPitch(int voice, double pitch) =>
        al.SetSourceProperty(sources[voice], SourceFloat.Pitch, (float)pitch);

    public void Stop(int voice) => Silence(voice);

    public bool IsPlaying(int voice) => State(sources[voice]) == SourceState.Playing;

    public void Dispose()
    {
        try
        {
            for (int v = 0; v < sources.Count; v++)
            {
                Silence(v);
                unsafe { uint s = sources[v]; al.DeleteSources(1, &s); }
            }
            unsafe
            {
                alc.MakeContextCurrent(null);
                alc.DestroyContext(context);
                alc.CloseDevice(device);
            }
        }
        catch { /* teardown must not fail the host */ }
    }

    // ---- plumbing ----------------------------------------------------------

    /// <summary>Stop the source and free everything it owns: the queue
    /// (stopping marks every queued buffer processed) and the static
    /// attach. Leaves a clean, idle voice.</summary>
    void Silence(int voice)
    {
        uint src = sources[voice];
        al.SourceStop(src);
        DrainProcessed(src);
        al.SetSourceProperty(src, SourceBoolean.Looping, false);
        al.SetSourceProperty(src, SourceInteger.Buffer, 0);
        if (staticBuf[voice] != 0) { DeleteBuf(staticBuf[voice]); staticBuf[voice] = 0; }
        al.SetSourceProperty(src, SourceFloat.Pitch, 1f);
    }

    /// <summary>Unqueue (and delete) every processed buffer on a
    /// streaming source.</summary>
    void DrainProcessed(uint src)
    {
        al.GetSourceProperty(src, GetSourceInteger.BuffersProcessed, out int done);
        while (done-- > 0)
        {
            uint b = 0;
            unsafe { al.SourceUnqueueBuffers(src, 1, &b); }
            if (b != 0) DeleteBuf(b);
        }
    }

    SourceState State(uint src)
    {
        al.GetSourceProperty(src, GetSourceInteger.SourceState, out int st);
        return (SourceState)st;
    }

    uint MakeBuffer(short[] pcm, int sampleRate)
    {
        uint b = 0;
        unsafe
        {
            al.GenBuffers(1, &b);
            fixed (short* p = pcm)
                al.BufferData(b, BufferFormat.Mono16, p, pcm.Length * sizeof(short), sampleRate);
        }
        return b;
    }

    void DeleteBuf(uint b)
    {
        unsafe { al.DeleteBuffers(1, &b); }
    }
}
