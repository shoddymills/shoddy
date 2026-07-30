// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

namespace Shoddy.Runtime;

/// <summary>
/// Any Shoddy-level failure (lex, parse, or runtime). The mill catches it
/// at top level and prints "ERROR (line N): message" to stderr, exit 1 —
/// the same contract as the C interpreter's die(). Line 0 means "no line"
/// and suppresses the parenthetical, as in the C.
///
/// <see cref="File"/> is optional and names the source file the line is in.
/// Include splices every file into one line list, so "line 3" alone can mean
/// line 3 of any of them; errors that can fire across an include boundary
/// carry the file so the reader knows which one.
/// </summary>
public sealed class ShoddyError : Exception
{
    public int Line { get; }
    public string? File { get; }

    public ShoddyError(int line, string message) : base(message) => Line = line;

    public ShoddyError(int line, string? file, string message) : base(message)
    {
        Line = line;
        File = file;
    }
}
