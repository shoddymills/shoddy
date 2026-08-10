// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

namespace Shoddy.Maui;

/// <summary>
/// The keyboard's pure half: named special keys to their on-the-wire
/// strings. Arrows and PF1-4 are the VT100 application-mode sequences
/// — the exact strings the engine's own console InKey path encodes and
/// vt100.shoddy's EvalKey decodes — enqueued atomically so the piped
/// INKEY assembles each back into one keystroke. Keys not named here
/// are ordinary characters and travel through the platform's character
/// event instead; Special answers null for them. Platform-agnostic on
/// purpose: the names are VirtualKey.ToString() on Windows, and the
/// other heads' key events map onto the same table.
/// </summary>
public static class TerminalKeys
{
    public static string? Special(string key) => key switch
    {
        "Up" => "\x1bOA",
        "Down" => "\x1bOB",
        "Right" => "\x1bOC",
        "Left" => "\x1bOD",
        "F1" => "\x1bOP",
        "F2" => "\x1bOQ",
        "F3" => "\x1bOR",
        "F4" => "\x1bOS",
        "Escape" => "\x1b",
        "Enter" => "\r",
        "Back" => "\b",
        "Tab" => "\t",
        _ => null,
    };
}
