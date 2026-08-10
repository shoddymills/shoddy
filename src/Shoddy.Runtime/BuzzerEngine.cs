// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

namespace Shoddy.Runtime;

/// <summary>
/// The shared half of the buzzer (B3.2, D18): synthesis and the channel
/// engine, platform-free. A host installs it with a sink factory and
/// every one of the seven BuzzerRegistry delegates comes alive over
/// that sink — an anonymous steal-oldest pool for SOUND, and eight
/// dedicated channels that either hold a looping note (NOTEON/NOTEOFF)
/// or stream a queue of pre-baked notes back-to-back (SOUNDQUEUE), with
/// sticky per-channel gain and waveform and ~5 ms ramped releases.
///
/// This is where all the observable behaviour lives, deliberately: the
/// sink's whole job is "play this PCM on this voice at this volume", so
/// every host sounds the same. The desktop mill's OpenAL sink and any
/// other platform's are interchangeable behind <see cref="IAudioSink"/>.
///
/// The sink is created lazily on the first sound call — a program that
/// never beeps never touches a device — and a factory that throws
/// (no sound card, a container, CI) degrades to permanent silence,
/// identical to headless, because a missing device must never kill a
/// program whose logic is fine.
/// </summary>
public static class BuzzerEngine
{
    public const int SampleRate = 44100;
    const double Amplitude = 0.2 * short.MaxValue;  // headroom: 8 channels + pool must sum
    const double RampMs = 5;                        // attack/release — a hard edge clicks
    const int PoolSize = 16;
    const int ChannelCount = 8;
    const int CacheCap = 256;                       // PCM entries, evict least-recent
    const double MinLoopMs = 50;                    // held-note loop: whole periods past this

    static Func<IAudioSink>? factory;
    static IAudioSink? sink;
    static bool failed;                             // sink creation failed: permanent silence

    // ---- anonymous effect pool (SOUND) ----
    static readonly int[] poolVoice = new int[PoolSize];
    static readonly long[] poolSeq = new long[PoolSize];    // steal the oldest start
    static long seq;

    enum Mode { Idle, Held, Queue }

    sealed class Channel
    {
        public int Voice;
        public Mode Mode;
        public double HeldBufFreq;      // the loop PCM's effective Hz (pitch base)
        public double HeldFreq;         // the Hz NOTEON last asked for (re-voice target)
        public int HeldWave;            // waveform baked into the held loop
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

    /// <summary>Install the seven delegates over a sink the factory will
    /// create on the first sound call. Costs nothing until then.</summary>
    public static void Install(Func<IAudioSink> sinkFactory)
    {
        factory = sinkFactory;
        failed = false;
        BuzzerRegistry.Sound   = (f, ms)     => Guard(() => PoolPlay(f, ms));
        BuzzerRegistry.NoteOn  = (ch, f)     => Guard(() => NoteOnCh(ch, f));
        BuzzerRegistry.NoteOff = ch          => Guard(() => NoteOffCh(ch));
        BuzzerRegistry.Queue   = (ch, f, ms) => Guard(() => QueueCh(ch, f, ms));
        BuzzerRegistry.Stop    = ch          => Guard(() => StopCh(ch));
        BuzzerRegistry.Gain    = (ch, v)     => Guard(() => GainCh(ch, v));
        BuzzerRegistry.Wave    = (ch, wv)    => Guard(() => WaveCh(ch, wv));
    }

    /// <summary>Host exit: held notes stop immediately — they would never
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
        factory = null;
        if (sink == null) return;
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
                    while (sink.IsPlaying(c.Voice) && Ticker.Now < deadline)
                        Thread.Sleep(10);
                }
            for (int ch = 1; ch <= ChannelCount; ch++)
                if (chans[ch].Mode == Mode.Queue) Flush(chans[ch]);
            sink.Dispose();
        }
        catch { /* teardown must not fail the host */ }
        sink = null;
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
        if (sink != null) return true;
        if (failed || factory == null) return false;
        try
        {
            IAudioSink s = factory();
            for (int i = 0; i < PoolSize; i++) poolVoice[i] = s.CreateVoice();
            for (int ch = 1; ch <= ChannelCount; ch++)
                chans[ch] = new Channel { Voice = s.CreateVoice() };
            sink = s;
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
            if (!sink!.IsPlaying(poolVoice[i])) { pick = i; break; }
            if (poolSeq[i] < oldest) { oldest = poolSeq[i]; oldestI = i; }
        }
        if (pick < 0) pick = oldestI;                // steal, silently
        sink!.Play(poolVoice[pick], e.Pcm, SampleRate, loop: false, gain: 1, pitch: 1);
        poolSeq[pick] = ++seq;
    }

    static void NoteOnCh(int ch, double freq)
    {
        Channel c = chans[ch];
        c.HeldFreq = freq;
        if (c.Mode == Mode.Held && c.HeldWave == c.WaveSet)
        {
            // Retune in place — no re-attack. What a keyboard glide or
            // pitch-bend wants.
            sink!.SetPitch(c.Voice, freq / c.HeldBufFreq);
            return;
        }
        if (c.Mode == Mode.Held) ReleaseHeld(c);     // timbre changed: rebuild the loop
        if (c.Mode == Mode.Queue) Flush(c);          // deterministic replace
        CacheEntry e = Synth(freq, -1, c.WaveSet);
        sink!.Play(c.Voice, e.Pcm, SampleRate, loop: true,
                   gain: c.GainSet, pitch: freq / e.BufFreq);
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
        CacheEntry e = Synth(freq, ms, c.WaveSet);
        if (e.Pcm.Length == 0) return;               // 0 ms: nothing to queue
        // The sink reclaims finished entries and restarts a queue that
        // ran dry — the classic streaming bug lives behind the seam.
        sink!.Queue(c.Voice, e.Pcm, SampleRate, c.GainSet);
        c.QueueEndAt = Math.Max(c.QueueEndAt, Ticker.Now) + ms;
        c.Mode = Mode.Queue;
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
                sink!.SetGain(c.Voice, c.GainSet);
                break;
        }
    }

    static void GainCh(int ch, double vol)
    {
        Channel c = chans[ch];
        c.GainSet = vol;
        sink!.SetGain(c.Voice, vol);
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

    // ---- channel plumbing --------------------------------------------------

    /// <summary>The ~5 ms release: step the gain down on the calling
    /// thread — imperceptible, and it buys click-free releases without an
    /// envelope engine. Engine-side on purpose: every platform releases
    /// identically.</summary>
    static void RampGainDown(Channel c)
    {
        if (!sink!.IsPlaying(c.Voice)) return;
        double t0 = Ticker.Now;
        while (true)
        {
            double k = (Ticker.Now - t0) / RampMs;
            if (k >= 1) break;
            sink.SetGain(c.Voice, c.GainSet * (1 - k));
            Thread.SpinWait(200);
        }
    }

    static void ReleaseHeld(Channel c)
    {
        RampGainDown(c);
        sink!.Stop(c.Voice);
        sink.SetGain(c.Voice, c.GainSet);
        c.Mode = Mode.Idle;
    }

    static void Flush(Channel c)
    {
        sink!.Stop(c.Voice);
        c.QueueEndAt = 0;
        c.Mode = Mode.Idle;
    }

    // ---- synthesis ---------------------------------------------------------
    // 16-bit mono waves at 0.2 of full scale: square (wave 0, the
    // default and the only voice the anonymous pool speaks), triangle
    // (1), and sine (2). Below ~60 Hz a square is perceptually a click
    // train — its edges are all a small speaker can voice — which is
    // what SOUNDWAVE exists to escape. Pure math over the cache;
    // nothing below here touches a voice.

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
            cache.Remove(lru.Last!.Value.Key);      // PCM only — no live object dies here
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
