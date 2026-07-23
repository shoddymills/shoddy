// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

namespace Shoddy.Runtime;

// The buzzer seam: the contract between the runtime (which validates
// sound arguments and nothing else) and the mill (which owns OpenAL
// entirely). Follows the ScribblerRegistry precedent for a static seam.
//
// Unlike the scribbler, silence is a no-op, not an error: nothing ever
// blocks waiting on sound, so there is no hang to fail loudly about. A
// headless program's beeps simply do not happen — and a missing sound
// card must never kill a program whose logic is fine.

/// <summary>Delegates the mill installs at startup; all null when no
/// audio backend is running (woven output run via dotnet, headless
/// tests). All six are called from the Shoddy thread — OpenAL has no
/// thread affinity, so there is no main-thread dispatch and no blocking
/// marshal. Every call site is <c>?.Invoke</c>: null means silence.</summary>
public static class BuzzerRegistry
{
    public static Action<double, double>? Sound;        // (freqHz, ms)  anonymous pool
    public static Action<int, double>?    NoteOn;       // (ch, freqHz)  hold until NoteOff
    public static Action<int>?            NoteOff;      // (ch)
    public static Action<int, double, double>? Queue;   // (ch, freqHz, ms) back-to-back
    public static Action<int>?            Stop;         // (ch) silence + flush
    public static Action<int, double>?    Gain;         // (ch, vol 0..1) sticky
}
