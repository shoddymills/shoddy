// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Reflection;
using Shoddy.Hosting;
using Shoddy.Maui;
using Xunit;

namespace Shoddy.Maui.Tests;

/// <summary>
/// The character pipes, and the proof that matters: a real woven mill
/// running Mode T straight into the terminal grid. The reader's
/// contract is INKEY's — Peek answers -1 without blocking on an empty
/// queue, reads block until a key or the end — and the writer feeds
/// the grid character by character, exactly what the unified terminal
/// control was proposed to be.
/// </summary>
public class TerminalPipeTests
{
    [Fact]
    public void PeekNeverBlocksAndAnswersMinusOneWhenEmpty()
    {
        var q = new TerminalInputQueue();
        Assert.Equal(-1, q.Peek());
        q.Enqueue("a");
        Assert.Equal('a', q.Peek());
        Assert.Equal('a', q.Read());
        Assert.Equal(-1, q.Peek());
    }

    [Fact]
    public async Task ReadBlocksUntilAKeyArrives()
    {
        var q = new TerminalInputQueue();
        Task<int> read = Task.Run(q.Read);
        Task first = await Task.WhenAny(read, Task.Delay(300));
        Assert.NotSame(read, first);        // still parked — good
        q.Enqueue("k");
        Assert.Equal('k', await read.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void CompleteEndsTheInput()
    {
        var q = new TerminalInputQueue();
        q.Enqueue("x");
        q.Complete();
        Assert.Equal('x', q.Read());
        Assert.Equal(-1, q.Read());
        Assert.Equal(-1, q.Read());         // stays ended
    }

    [Fact]
    public async Task AWovenMillRunsModeTIntoTheGrid()
    {
        var grid = new TerminalGrid(cols: 100, rows: 30);
        var input = new TerminalInputQueue();
        input.Enqueue("2 3 +\r\nQUIT\r\n");
        input.Complete();

        int exit = await ShoddyHost.RunWovenAsync(Assembly.Load("halifax"),
            new TerminalWriter(grid), input, Array.Empty<string>());

        Assert.Equal(0, exit);
        string all = string.Join("\n",
            Enumerable.Range(0, grid.ScrollbackCount)
                .Select(i => ScrollbackText(grid, i))
                .Concat(new[] { grid.ScreenText() }));
        Assert.Contains("x: 5", all);
    }

    static string ScrollbackText(TerminalGrid g, int line)
    {
        var sb = new System.Text.StringBuilder();
        for (int c = 0; c < g.Cols; c++)
        {
            char ch = g.ScrollbackCell(line, c).Ch;
            sb.Append(ch == '\0' ? ' ' : ch);
        }
        return sb.ToString().TrimEnd();
    }
}
