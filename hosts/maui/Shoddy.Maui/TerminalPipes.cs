// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Text;

namespace Shoddy.Maui;

/// <summary>
/// The host's half of the Mode T wire, character-level in both
/// directions: a TextWriter that feeds the grid as the mill prints,
/// and a TextReader whose contract is the piped-input convention
/// RunWovenAsync documents — Peek() answers -1 immediately when
/// nothing is pending (INKEY's redirected path then yields "" and a
/// game loop never blocks on an empty poll), while Read and ReadLine
/// block until a keystroke or the end of input arrives.
/// </summary>
public sealed class TerminalWriter : TextWriter
{
    readonly TerminalGrid grid;

    public TerminalWriter(TerminalGrid grid) => this.grid = grid;

    public override Encoding Encoding => Encoding.Unicode;

    public override void Write(char value) => grid.Feed(value.ToString());
    public override void Write(string? value) { if (value != null) grid.Feed(value); }
    public override void WriteLine(string? value) => grid.Feed((value ?? "") + "\r\n");
    public override void WriteLine() => grid.Feed("\r\n");
}

/// <summary>
/// The keystroke queue behind the mill's reader. Enqueue what the user
/// types (or what the on-screen key row injects — arrow keys arrive as
/// their escape sequences, which EvalKey decodes on the far side);
/// Complete() ends the input, after which reads answer -1 — the same
/// end-of-input a redirected script reaches, and the way a host closes
/// a session a game will not close itself.
/// </summary>
public sealed class TerminalInputQueue : TextReader
{
    readonly ConcurrentQueue<char> pending = new();
    readonly SemaphoreSlim available = new(0);
    volatile bool complete;

    public void Enqueue(string text)
    {
        foreach (char ch in text)
        {
            pending.Enqueue(ch);
            available.Release();
        }
    }

    public void Complete()
    {
        complete = true;
        available.Release();
    }

    /// <summary>Never blocks: a pending character, or -1. This is the
    /// half INKEY lives on.</summary>
    public override int Peek() => pending.TryPeek(out char ch) ? ch : -1;

    /// <summary>Blocks until a character or the end of input.</summary>
    public override int Read()
    {
        while (true)
        {
            if (pending.TryDequeue(out char ch)) return ch;
            if (complete) return -1;
            available.Wait();
        }
    }
}
