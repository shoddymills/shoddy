// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Runtime;
using Silk.NET.OpenAL;

namespace Shoddy.Mill;

/// <summary>The mill's audio layer — the buzzer, named for the steam
/// hooter on the engine house roof. Implements the six BuzzerRegistry
/// delegates over OpenAL (OpenAL Soft natives): a stolen-oldest pool of
/// anonymous effect sources for SOUND, and eight dedicated channel
/// sources that either hold a looping note (NOTEON/NOTEOFF) or stream a
/// queue of pre-baked notes back-to-back (SOUNDQUEUE).
///
/// OpenAL does all mixing in its own output thread; every call here runs
/// on the Shoddy thread — OpenAL has no thread affinity, so there is no
/// dispatch and no marshal. The device opens lazily on the first sound
/// call; init failure degrades to permanent silence, identical to
/// headless, because a missing sound card must never kill a program
/// whose logic is fine.
///
/// Ownership rule that keeps buffer lifetime trivial: the synthesis
/// cache holds PCM only (synthesis is the expensive half), and every
/// play gens a fresh AL buffer owned by exactly one source slot — pool
/// slot, held note, or queue entry — deleted when that slot moves on.
/// No AL buffer is ever shared, so nothing can delete a buffer still in
/// use.</summary>
public static class Buzzer
{
    const int SampleRate = 44100;
    const double Amplitude = 0.2 * short.MaxValue;  // headroom: 8 channels + pool must sum
    const double RampMs = 5;                        // attack/release — a hard edge clicks
    const int PoolSize = 16;
    const int ChannelCount = 8;
    const int CacheCap = 256;                       // PCM entries, evict least-recent
    const double MinLoopMs = 50;                    // held-note loop: whole periods past this

    static AL? al;
    static ALContext? alc;
    static unsafe Device* device;
    static unsafe Context* context;
    static bool failed;                             // init failed: permanent silence

    // ---- anonymous effect pool (SOUND) ----
    static readonly uint[] poolSrc = new uint[PoolSize];
    static readonly uint[] poolBuf = new uint[PoolSize];    // 0 = none attached
    static readonly long[] poolSeq = new long[PoolSize];    // steal the oldest start
    static long seq;

    enum Mode { Idle, Held, Queue }

    sealed class Channel
    {
        public uint Src;
        public Mode Mode;
        public uint HeldBuf;            // loop buffer when Held; 0 otherwise
        public double HeldBufFreq;      // the loop buffer's effective Hz (pitch base)
        public double HeldFreq;         // the Hz NOTEON last asked for (re-voice target)
        public int HeldWave;            // waveform baked into HeldBuf
        public double GainSet = 1;      // SOUNDGAIN's sticky value; what ramps restore
        public int WaveSet;             // SOUNDWAVE's sticky value: 0 square, 1 tri, 2 sine
        public double QueueEndAt;       // Ticker instant the queue drains (teardown cap)
    }
    static readonly Channel[] chans = new Channel[ChannelCount + 1];   // 1-based

    // ---- synthesis cache: PCM keyed by (freq, ms, wave); ms < 0 keys a loop ----
    sealed class CacheEntry
    {
        public (double F, double Ms, int Wave) Key;
        public short[] Pcm = Array.Empty<short>();
        public double BufFreq;          // effective Hz of the data (loops round to whole periods)
    }
    static readonly Dictionary<(double, double, int), LinkedListNode<CacheEntry>> cache = new();
    static readonly LinkedList<CacheEntry> lru = new();

    // ---- the seam -------------------------------------------------------

    /// <summary>Install the seven delegates. Costs nothing until the first
    /// sound call — a program that never beeps never touches OpenAL, and
    /// `mill lex`/`weave`/`machine` never come near this.</summary>
    public static void Install()
    {
        BuzzerRegistry.Sound   = (f, ms)     => Guard(() => PoolPlay(f, ms));
        BuzzerRegistry.NoteOn  = (ch, f)     => Guard(() => NoteOnCh(ch, f));
        BuzzerRegistry.NoteOff = ch          => Guard(() => NoteOffCh(ch));
        BuzzerRegistry.Queue   = (ch, f, ms) => Guard(() => QueueCh(ch, f, ms));
        BuzzerRegistry.Stop    = ch          => Guard(() => StopCh(ch));
        BuzzerRegistry.Gain    = (ch, v)     => Guard(() => GainCh(ch, v));
        BuzzerRegistry.Wave    = (ch, wv)    => Guard(() => WaveCh(ch, wv));
    }

    /// <summary>Mill exit: held notes stop immediately — they would never
    /// end — but with <paramref name="drain"/> queued notes are allowed
    /// to finish, polled at ~10 ms, capped at the queued duration plus a
    /// second. "Queue a tune, return from Main" is a complete program,
    /// the audio sibling of "draw a picture, return".</summary>
    public static void Shutdown(bool drain)
    {
        BuzzerRegistry.Sound = null; BuzzerRegistry.NoteOn = null;
        BuzzerRegistry.NoteOff = null; BuzzerRegistry.Queue = null;
        BuzzerRegistry.Stop = null; BuzzerRegistry.Gain = null;
        BuzzerRegistry.Wave = null;
        if (al == null) return;
        try
        {
            for (int ch = 1; ch <= ChannelCount; ch++)
                if (chans[ch].Mode == Mode.Held) ReleaseHeld(chans[ch]);
            if (drain)
                for (int ch = 1; ch <= ChannelCount; ch++)
                {
                    Channel c = chans[ch];
                    if (c.Mode != Mode.Queue) continue;
                    double deadline = c.QueueEndAt + 1000;
                    while (State(c.Src) == SourceState.Playing && Ticker.Now < deadline)
                        Thread.Sleep(10);
                }
            for (int ch = 1; ch <= ChannelCount; ch++)
            {
                Channel c = chans[ch];
                if (c.Mode == Mode.Queue) Flush(c);
                unsafe { uint s = c.Src; al.DeleteSources(1, &s); }
            }
            for (int i = 0; i < PoolSize; i++)
            {
                al.SourceStop(poolSrc[i]);
                al.SetSourceProperty(poolSrc[i], SourceInteger.Buffer, 0);
                if (poolBuf[i] != 0) DeleteBuf(poolBuf[i]);
                unsafe { uint s = poolSrc[i]; al.DeleteSources(1, &s); }
            }
            unsafe
            {
                alc!.MakeContextCurrent(null);
                alc.DestroyContext(context);
                alc.CloseDevice(device);
            }
        }
        catch { /* teardown must not fail the mill */ }
        al = null; alc = null;
    }

    // ---- lifecycle -------------------------------------------------------

    static void Guard(Action play)
    {
        if (!Init()) return;                        // silence is a no-op, not an error
        try { play(); }
        catch { /* sound never fails the program */ }
    }

    static bool Init()
    {
        if (al != null) return true;
        if (failed) return false;
        try
        {
            ALContext c; AL a;
            try { c = ALContext.GetApi(true); a = AL.GetApi(true); }   // bundled OpenAL Soft
            catch { c = ALContext.GetApi(); a = AL.GetApi(); }         // system OpenAL
            unsafe
            {
                device = c.OpenDevice("");
                if (device == null) throw new InvalidOperationException("no audio device");
                context = c.CreateContext(device, null);
                if (context == null || !c.MakeContextCurrent(context))
                    throw new InvalidOperationException("no audio context");
            }
            unsafe { fixed (uint* p = poolSrc) a.GenSources(PoolSize, p); }
            for (int ch = 1; ch <= ChannelCount; ch++)
            {
                chans[ch] = new Channel();
                unsafe { uint s = 0; a.GenSources(1, &s); chans[ch].Src = s; }
            }
            alc = c; al = a;
            return true;
        }
        catch
        {
            failed = true;      // CI, a server, a container: permanent silent no-op
            return false;
        }
    }

    // ---- the five behaviours ----------------------------------------------

    static void PoolPlay(double freq, double ms)
    {
        CacheEntry e = Synth(freq, ms, 0);           // the pool speaks square only
        if (e.Pcm.Length == 0) return;
        int pick = -1; long oldest = long.MaxValue; int oldestI = 0;
        for (int i = 0; i < PoolSize; i++)
        {
            SourceState st = State(poolSrc[i]);
            if (st is SourceState.Initial or SourceState.Stopped) { pick = i; break; }
            if (poolSeq[i] < oldest) { oldest = poolSeq[i]; oldestI = i; }
        }
        if (pick < 0) { pick = oldestI; al!.SourceStop(poolSrc[pick]); }  // steal, silently
        ReplaceStatic(poolSrc[pick], ref poolBuf[pick], e);
        al!.SetSourceProperty(poolSrc[pick], SourceFloat.Pitch, 1f);
        al.SetSourceProperty(poolSrc[pick], SourceFloat.Gain, 1f);
        al.SourcePlay(poolSrc[pick]);
        poolSeq[pick] = ++seq;
    }

    static void NoteOnCh(int ch, double freq)
    {
        Channel c = chans[ch];
        c.HeldFreq = freq;
        if (c.Mode == Mode.Held && c.HeldWave == c.WaveSet)
        {
            // Retune in place — no re-attack; AL_PITCH only. What a
            // keyboard glide or pitch-bend wants.
            al!.SetSourceProperty(c.Src, SourceFloat.Pitch, (float)(freq / c.HeldBufFreq));
            return;
        }
        if (c.Mode == Mode.Held) ReleaseHeld(c);     // timbre changed: rebuild the loop
        if (c.Mode == Mode.Queue) Flush(c);          // deterministic replace
        CacheEntry e = Synth(freq, -1, c.WaveSet);
        ReplaceStatic(c.Src, ref c.HeldBuf, e);
        al!.SetSourceProperty(c.Src, SourceBoolean.Looping, true);
        al.SetSourceProperty(c.Src, SourceFloat.Pitch, (float)(freq / e.BufFreq));
        al.SetSourceProperty(c.Src, SourceFloat.Gain, (float)c.GainSet);
        al.SourcePlay(c.Src);
        c.Mode = Mode.Held;
        c.HeldBufFreq = e.BufFreq;
        c.HeldWave = c.WaveSet;
    }

    static void NoteOffCh(int ch)
    {
        Channel c = chans[ch];
        if (c.Mode == Mode.Held) ReleaseHeld(c);     // nothing held: no-op
    }

    static void QueueCh(int ch, double freq, double ms)
    {
        Channel c = chans[ch];
        if (c.Mode == Mode.Held) ReleaseHeld(c);     // queueing releases a held note first
        DrainProcessed(c);                           // also where queue buffers die
        CacheEntry e = Synth(freq, ms, c.WaveSet);
        if (e.Pcm.Length == 0) return;               // 0 ms: nothing to queue
        uint buf = MakeBuffer(e.Pcm);
        unsafe { al!.SourceQueueBuffers(c.Src, 1, &buf); }
        al!.SetSourceProperty(c.Src, SourceFloat.Gain, (float)c.GainSet);
        c.QueueEndAt = Math.Max(c.QueueEndAt, Ticker.Now) + ms;
        c.Mode = Mode.Queue;
        // The classic streaming bug: a source that ran its queue dry went
        // to Stopped; without this the rest of the melody never sounds.
        if (State(c.Src) != SourceState.Playing) al.SourcePlay(c.Src);
    }

    static void StopCh(int ch)
    {
        Channel c = chans[ch];
        switch (c.Mode)
        {
            case Mode.Held:
                ReleaseHeld(c);
                break;
            case Mode.Queue:
                RampGainDown(c);
                Flush(c);
                al!.SetSourceProperty(c.Src, SourceFloat.Gain, (float)c.GainSet);
                break;
        }
    }

    static void GainCh(int ch, double vol)
    {
        Channel c = chans[ch];
        c.GainSet = vol;
        al!.SetSourceProperty(c.Src, SourceFloat.Gain, (float)vol);
    }

    /// <summary>SOUNDWAVE: sticky per channel, like gain. A held note
    /// re-voices in place at its current frequency — the timbre changes
    /// mid-note, which is what a live instrument wants.</summary>
    static void WaveCh(int ch, int wave)
    {
        Channel c = chans[ch];
        if (c.WaveSet == wave) return;
        c.WaveSet = wave;
        if (c.Mode == Mode.Held) NoteOnCh(ch, c.HeldFreq);
    }

    // ---- source plumbing ---------------------------------------------------

    static SourceState State(uint src)
    {
        al!.GetSourceProperty(src, GetSourceInteger.SourceState, out int st);
        return (SourceState)st;
    }

    /// <summary>Point a source at freshly uploaded PCM, deleting the
    /// buffer the slot owned before. Static attach only (pool + held).</summary>
    static void ReplaceStatic(uint src, ref uint slot, CacheEntry e)
    {
        al!.SetSourceProperty(src, SourceInteger.Buffer, 0);
        if (slot != 0) { DeleteBuf(slot); slot = 0; }
        slot = MakeBuffer(e.Pcm);
        al.SetSourceProperty(src, SourceInteger.Buffer, (int)slot);
    }

    /// <summary>The ~5 ms release: step the gain down on the calling
    /// thread — imperceptible, and it buys click-free releases without an
    /// envelope engine.</summary>
    static void RampGainDown(Channel c)
    {
        if (State(c.Src) != SourceState.Playing) return;
        double t0 = Ticker.Now;
        while (true)
        {
            double k = (Ticker.Now - t0) / RampMs;
            if (k >= 1) break;
            al!.SetSourceProperty(c.Src, SourceFloat.Gain, (float)(c.GainSet * (1 - k)));
            Thread.SpinWait(200);
        }
    }

    static void ReleaseHeld(Channel c)
    {
        RampGainDown(c);
        al!.SourceStop(c.Src);
        al.SetSourceProperty(c.Src, SourceBoolean.Looping, false);
        al.SetSourceProperty(c.Src, SourceInteger.Buffer, 0);
        if (c.HeldBuf != 0) { DeleteBuf(c.HeldBuf); c.HeldBuf = 0; }
        al.SetSourceProperty(c.Src, SourceFloat.Gain, (float)c.GainSet);
        c.Mode = Mode.Idle;
    }

    /// <summary>Unqueue (and delete) every processed buffer on a
    /// streaming channel.</summary>
    static void DrainProcessed(Channel c)
    {
        al!.GetSourceProperty(c.Src, GetSourceInteger.BuffersProcessed, out int done);
        while (done-- > 0)
        {
            uint b = 0;
            unsafe { al.SourceUnqueueBuffers(c.Src, 1, &b); }
            if (b != 0) DeleteBuf(b);
        }
    }

    /// <summary>Stop a streaming channel and free its whole queue —
    /// stopping marks every queued buffer processed.</summary>
    static void Flush(Channel c)
    {
        al!.SourceStop(c.Src);
        DrainProcessed(c);
        al.SetSourceProperty(c.Src, SourceInteger.Buffer, 0);
        c.QueueEndAt = 0;
        c.Mode = Mode.Idle;
    }

    static uint MakeBuffer(short[] pcm)
    {
        uint b = 0;
        unsafe
        {
            al!.GenBuffers(1, &b);
            fixed (short* p = pcm)
                al.BufferData(b, BufferFormat.Mono16, p, pcm.Length * sizeof(short), SampleRate);
        }
        return b;
    }

    static void DeleteBuf(uint b)
    {
        unsafe { al!.DeleteBuffers(1, &b); }
    }

    // ---- synthesis ---------------------------------------------------------
    // 16-bit mono waves at 0.2 of full scale: square (wave 0, the
    // default and the only voice the anonymous pool speaks), triangle
    // (1), and sine (2). Below ~60 Hz a square is perceptually a click
    // train — its edges are all a small speaker can voice — which is
    // what SOUNDWAVE exists to escape. Pure math over the cache;
    // nothing below here touches a source.

    /// <summary>One sample of the waveform at phase frac in [0, 1).
    /// The gentler waves get more peak — perceptual compensation: at
    /// equal peak a triangle reads much quieter than a square and a
    /// sine quieter still, low sines doubly so, living where the ear
    /// is least sensitive. The factors keep any sane pair of channels
    /// inside full scale.</summary>
    static double Sample(double frac, int wave) => wave switch
    {
        1 => 1.5 * Amplitude * (1 - 4 * Math.Abs(frac - 0.5)),  // triangle
        2 => 1.9 * Amplitude * Math.Sin(frac * 2 * Math.PI),    // sine
        _ => frac < 0.5 ? Amplitude : -Amplitude,               // square
    };

    static CacheEntry Synth(double freq, double ms, int wave)
    {
        (double, double, int) key = (freq, ms, wave);
        if (cache.TryGetValue(key, out LinkedListNode<CacheEntry>? node))
        {
            lru.Remove(node);
            lru.AddFirst(node);
            return node.Value;
        }
        CacheEntry e = ms < 0 ? SynthLoop(freq, wave) : SynthFixed(freq, ms, wave);
        e.Key = key;
        cache[key] = lru.AddFirst(e);
        if (cache.Count > CacheCap)
        {
            cache.Remove(lru.Last!.Value.Key);      // PCM only — no AL object dies here
            lru.RemoveLast();
        }
        return e;
    }

    /// <summary>A fixed-duration note (or, at freq 0, a rest) with the
    /// ~5 ms linear attack and release baked in — without the ramps every
    /// note edge clicks; with them, queued melodies articulate.</summary>
    static CacheEntry SynthFixed(double freq, double ms, int wave)
    {
        int n = (int)Math.Round(SampleRate * ms / 1000.0);
        if (n < 0) n = 0;                           // absurd durations: silence, not a crash
        var pcm = new short[n];
        if (freq > 0)
        {
            int ramp = Math.Min(n / 2, (int)(SampleRate * RampMs / 1000.0));
            double step = freq / SampleRate, phase = 0;
            for (int i = 0; i < n; i++)
            {
                double v = Sample(phase - Math.Floor(phase), wave);
                if (ramp > 0)
                {
                    if (i < ramp) v *= i / (double)ramp;
                    int tail = n - 1 - i;
                    if (tail < ramp) v *= tail / (double)ramp;
                }
                pcm[i] = (short)v;
                phase += step;
            }
        }
        return new CacheEntry { Pcm = pcm, BufFreq = freq };
    }

    /// <summary>A held note's loop: an integer number of complete cycles
    /// spanning at least ~50 ms, so the loop seam is phase-continuous — a
    /// fractional period at the seam buzzes. Rounding n to whole samples
    /// shifts the pitch by well under a cent; BufFreq reports the
    /// effective value so retunes pitch against the truth.</summary>
    static CacheEntry SynthLoop(double freq, int wave)
    {
        int periods = Math.Max(1, (int)Math.Ceiling(MinLoopMs / 1000.0 * freq));
        int n = Math.Max(2, (int)Math.Round(periods * SampleRate / freq));
        var pcm = new short[n];
        for (int i = 0; i < n; i++)
        {
            double cyc = i * (double)periods / n;   // exactly `periods` cycles over n samples
            pcm[i] = (short)Sample(cyc - Math.Floor(cyc), wave);
        }
        return new CacheEntry { Pcm = pcm, BufFreq = periods * (double)SampleRate / n };
    }
}
