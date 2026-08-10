// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Reflection;
using Shoddy.Hosting;
using Shoddy.Maui;
using Xunit;

namespace Shoddy.Maui.Tests;

/// <summary>
/// The keyboard's pure half: what a named special key becomes on the
/// wire. Arrows and PF keys are the VT100 application-mode sequences —
/// the exact strings the console's own InKey path encodes and vt100's
/// EvalKey decodes — so a key pressed in the app and a key pressed at
/// a real terminal are indistinguishable on the far side of the pipe.
/// </summary>
public class TerminalKeysTests
{
    [Theory]
    [InlineData("Up", "\x1bOA")]
    [InlineData("Down", "\x1bOB")]
    [InlineData("Right", "\x1bOC")]
    [InlineData("Left", "\x1bOD")]
    [InlineData("F1", "\x1bOP")]
    [InlineData("F2", "\x1bOQ")]
    [InlineData("F3", "\x1bOR")]
    [InlineData("F4", "\x1bOS")]
    [InlineData("Escape", "\x1b")]
    [InlineData("Enter", "\r")]
    [InlineData("Back", "\b")]
    [InlineData("Tab", "\t")]
    public void SpecialKeysBecomeTheirSequences(string key, string expected)
        => Assert.Equal(expected, TerminalKeys.Special(key));

    [Fact]
    public void OrdinaryKeysAreNotSpecial()
    {
        // letters and digits arrive as characters, not through this map
        Assert.Null(TerminalKeys.Special("A"));
        Assert.Null(TerminalKeys.Special("Number7"));
        Assert.Null(TerminalKeys.Special("Space"));
    }
}

/// <summary>
/// The eight ANSI colours, monochrome no longer: SGR 30–37/39 set the
/// cell's foreground, 40–47/49 the background, SGR 0 clears both, and
/// none of them lands in Unhandled — a game painting in colour is
/// speaking the protocol, not noise. Codes are stored 1..8 (0 = the
/// theme's default) so the view can map them onto its own palette.
/// </summary>
public class TerminalColourTests
{
    [Fact]
    public void ForegroundColourSticksToTheCell()
    {
        var g = new TerminalGrid(20, 4);
        g.Feed("\x1b[1;33mA");
        TerminalCell cell = g.Cell(0, 0);
        Assert.Equal('A', cell.Ch);
        Assert.True((cell.Attr & TerminalAttr.Bold) != 0);
        Assert.Equal(4, cell.Fg);          // 33 = yellow = 4th of the eight
        Assert.Equal(0, cell.Bg);
        Assert.Empty(g.Unhandled);
    }

    [Fact]
    public void BackgroundAndDefaultsRoundTrip()
    {
        var g = new TerminalGrid(20, 4);
        g.Feed("\x1b[44mA\x1b[49mB\x1b[31mC\x1b[39mD");
        Assert.Equal(5, g.Cell(0, 0).Bg);  // 44 = blue background
        Assert.Equal(0, g.Cell(0, 1).Bg);  // 49 = default again
        Assert.Equal(2, g.Cell(0, 2).Fg);  // 31 = red
        Assert.Equal(0, g.Cell(0, 3).Fg);  // 39 = default again
        Assert.Empty(g.Unhandled);
    }

    [Fact]
    public void SgrZeroClearsColourWithTheAttributes()
    {
        var g = new TerminalGrid(20, 4);
        g.Feed("\x1b[1;34mA\x1b[0mB");
        Assert.Equal(5, g.Cell(0, 0).Fg);
        Assert.Equal(0, g.Cell(0, 1).Fg);
        Assert.Equal(TerminalAttr.None, g.Cell(0, 1).Attr);
    }
}

/// <summary>
/// The game the terminal was built for, running woven through the
/// pipes: pac-vt100's menu answers a queued Q and exits; a full
/// P-play-Q-quit-Q round trip drives INKEY live — including an arrow
/// arriving as one whole escape sequence — with the screen asserting
/// each station. Real time passes (the game paces itself with
/// SleepUntil), so the waits poll with generous timeouts.
/// </summary>
public class PacTerminalTests
{
    static Assembly Pac() => Assembly.Load("pac");

    [Fact]
    public async Task MenuQuitRunsToTheLastScore()
    {
        var grid = new TerminalGrid(80, 26);
        var input = new TerminalInputQueue();
        input.Enqueue("q");
        input.Complete();

        int exit = await ShoddyHost.RunWovenAsync(Pac(),
            new TerminalWriter(grid), input, Array.Empty<string>())
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, exit);
        Assert.Contains("LAST SCORE 0", grid.ScreenText());
        // the whole menu round — art, SGR colour, cursor addressing —
        // spoke nothing the terminal does not understand
        Assert.Empty(grid.Unhandled);
    }

    [Fact]
    public async Task PlayThenQuitDrivesInKeyThroughTheLiveQueue()
    {
        var grid = new TerminalGrid(80, 26);
        var input = new TerminalInputQueue();
        Task<int> run = ShoddyHost.RunWovenAsync(Pac(),
            new TerminalWriter(grid), input, Array.Empty<string>());

        await WaitFor(grid, "P - Play");
        input.Enqueue("p");
        await WaitFor(grid, "SCORE:");     // the board is up, the HUD painted
        input.Enqueue("\x1bOC");           // right arrow, one whole sequence
        await Task.Delay(300);             // a few ticks of steering
        input.Enqueue("q");                // quit the game...
        await WaitFor(grid, "LAST SCORE"); // ...back at the menu
        input.Enqueue("q");                // quit the program

        int exit = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, exit);
        Assert.Empty(grid.Unhandled);
    }

    static async Task WaitFor(TerminalGrid grid, string text)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            lock (grid.Sync)
                if (grid.ScreenText().Contains(text)) return;
            await Task.Delay(50);
        }
        string screen;
        lock (grid.Sync) screen = grid.ScreenText();
        Assert.Fail($"'{text}' never appeared; the screen shows:\n{screen}");
    }
}
