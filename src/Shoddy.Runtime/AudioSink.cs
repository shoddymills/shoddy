// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

namespace Shoddy.Runtime;

/// <summary>
/// The entire platform surface of sound (D18): "play this PCM on this
/// voice at this volume". Everything above it — synthesis, the eight
/// channels, the anonymous pool with steal-oldest, sticky gain and
/// waveform, queue bookkeeping, ramped releases — lives in
/// <see cref="BuzzerEngine"/>, written once, so hosts cannot drift
/// apart: behaviour is decided by the shared engine, never by the sink.
///
/// A sink owns its own buffer lifetimes. The engine hands PCM in and
/// never a buffer handle out, so nothing outside the sink can delete a
/// buffer still in use.
///
/// Every call arrives on the Shoddy thread. A sink whose platform wants
/// another thread marshals internally; the desktop's OpenAL needs none.
/// Construction may throw — no device, no audio service — and the
/// engine turns that into permanent silence, identical to headless.
/// </summary>
public interface IAudioSink : IDisposable
{
    /// <summary>A new voice: one independently playing sound slot.</summary>
    int CreateVoice();

    /// <summary>Replace the voice's content with this PCM (16-bit mono)
    /// and play it — once, or looping seamlessly — at the given gain and
    /// pitch multiplier. Whatever the voice held or queued is gone.</summary>
    void Play(int voice, short[] pcm, int sampleRate, bool loop, double gain, double pitch);

    /// <summary>Append PCM to the voice's queue, to sound back-to-back
    /// after whatever is already queued — starting the voice if its
    /// queue had run dry. Finished queue entries are reclaimed here.</summary>
    void Queue(int voice, short[] pcm, int sampleRate, double gain);

    void SetGain(int voice, double gain);

    /// <summary>Retune in place, no re-attack: pitch is a multiplier on
    /// the PCM's own frequency. What a keyboard glide wants.</summary>
    void SetPitch(int voice, double pitch);

    /// <summary>Silence the voice now and discard anything queued.</summary>
    void Stop(int voice);

    bool IsPlaying(int voice);
}
