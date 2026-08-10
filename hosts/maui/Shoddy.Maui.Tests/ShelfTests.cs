// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Reflection;
using Shoddy.Hosting;
using Shoddy.Maui;
using Xunit;

namespace Shoddy.Maui.Tests;

/// <summary>
/// The rest of the shelf. The line games boot through the pipes to
/// their first prompt — proof the weave, the closure and the terminal
/// path hold for each — and are then left parked on their blocking
/// read (a background thread waiting on an empty queue costs nothing
/// and ends with the test process; feeding EOF instead would spin
/// oregon's reprompt loop forever). The canvas games cannot run
/// headless — their wait loop needs a window pumping events — so here
/// they prove they are woven and present; their simulations have their
/// own headless suites in their mills.
/// </summary>
public class ShelfTests
{
    [Fact]
    public async Task OregonBootsToItsFirstQuestion()
    {
        var grid = new TerminalGrid(80, 24);
        var input = new TerminalInputQueue();
        _ = ShoddyHost.RunWovenAsync(Assembly.Load("oregon"),
            new TerminalWriter(grid), input, Array.Empty<string>());
        await WaitFor(grid, "DO YOU NEED INSTRUCTIONS");
    }

    [Fact]
    public async Task MungoBootsToItsWelcome()
    {
        var grid = new TerminalGrid(80, 24);
        var input = new TerminalInputQueue();
        _ = ShoddyHost.RunWovenAsync(Assembly.Load("mungo-caverns"),
            new TerminalWriter(grid), input, Array.Empty<string>());
        await WaitFor(grid, "Welcome to Mungo Caverns");
    }

    [Fact]
    public async Task MungoAnswersTheWelcomeAndStandsAtTheRoad()
    {
        // The live-play report was "no key input" — so the input is the
        // thing this proves, line by line: decline instructions, arrive
        // at the road, walk into the building.
        var grid = new TerminalGrid(80, 24);
        var input = new TerminalInputQueue();
        _ = ShoddyHost.RunWovenAsync(Assembly.Load("mungo-caverns"),
            new TerminalWriter(grid), input, Array.Empty<string>());
        await WaitFor(grid, "Welcome to Mungo Caverns");
        input.Enqueue("no\r\n");
        await WaitFor(grid, "standing at the end of a road");
        input.Enqueue("in\r\n");
        await WaitFor(grid, "inside a building");
    }

    [Theory]
    [InlineData("invaders")]
    [InlineData("devils-dust")]
    public void CanvasGamesAreWovenIn(string name)
    {
        Assert.NotNull(Assembly.Load(name).GetType("Woven"));
    }

    static async Task WaitFor(TerminalGrid grid, string text)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            lock (grid.Sync)
                if (grid.ScreenText().Contains(text) || Scrollback(grid).Contains(text))
                    return;
            await Task.Delay(50);
        }
        string screen;
        lock (grid.Sync) screen = Scrollback(grid) + "\n" + grid.ScreenText();
        Assert.Fail($"'{text}' never appeared; the terminal shows:\n{screen}");
    }

    static string Scrollback(TerminalGrid g)
    {
        var sb = new System.Text.StringBuilder();
        for (int line = 0; line < g.ScrollbackCount; line++)
        {
            for (int c = 0; c < g.Cols; c++)
            {
                char ch = g.ScrollbackCell(line, c).Ch;
                sb.Append(ch == '\0' ? ' ' : ch);
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
