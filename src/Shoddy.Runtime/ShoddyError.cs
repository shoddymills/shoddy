// Copyright (c) 2026 Stephen Vincent Foster. All rights reserved.
//
// This file is part of the Shoddy Language project.
// Licensed under the Shoddy Language License 1.0.0 (PolyForm Noncommercial
// License 1.0.0 with Additional Use Grant). See the LICENSE file in the
// project root for full terms.

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
