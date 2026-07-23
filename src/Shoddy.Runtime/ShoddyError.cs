// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

namespace Shoddy.Runtime;

/// <summary>
/// Any Shoddy-level failure (lex, parse, or runtime). The mill catches it
/// at top level and prints "ERROR (line N): message" to stderr, exit 1 —
/// the same contract as the C interpreter's die(). Line 0 means "no line"
/// and suppresses the parenthetical, as in the C.
/// </summary>
public sealed class ShoddyError : Exception
{
    public int Line { get; }

    public ShoddyError(int line, string message) : base(message) => Line = line;
}
