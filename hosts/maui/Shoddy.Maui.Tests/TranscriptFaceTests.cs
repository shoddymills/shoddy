// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Reflection;
using Shoddy.Hosting;
using Shoddy.Maui;
using Xunit;

namespace Shoddy.Maui.Tests;

/// <summary>
/// B4.1's proof, the sharper of D13's two: the Mode N loop the
/// transcript view runs — HalifaxSession, exactly as CalculatorPage
/// wires it — driven over the same input as the woven terminal mill,
/// with every Mode N line required to appear, in order, in what the
/// terminal printed. A passing test and a working page cannot come
/// apart, because they run the same class.
/// </summary>
public class TranscriptFaceTests
{
    static HalifaxSession Fresh(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "shoddy-maui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return HalifaxSession.Open(new ShoddyHostOptions { FileRoot = root });
    }

    static readonly string[] Script =
    {
        "2 3 +",
        "10 *",
        ": DOUBLE",
        "  2 *",
        ";",
        "7 DOUBLE",
    };

    [Fact]
    public async Task ModeNMatchesTheTerminalTranscript()
    {
        // The terminal's transcript, from the same woven mill this
        // project weaves — folder + manifest, source unread.
        var output = new StringWriter();
        int exit = await ShoddyHost.RunWovenAsync(Assembly.Load("halifax"),
            output, new StringReader(string.Join("\n", Script) + "\nQUIT\n"),
            Array.Empty<string>());
        Assert.Equal(0, exit);
        string terminal = output.ToString();

        // The app's loop over the same lines.
        HalifaxSession session = Fresh(out _);
        var appLines = new List<string>(session.Opening());
        foreach (string line in Script)
        {
            HalifaxTurn? turn = await session.SubmitAsync(line);
            Assert.NotNull(turn);
            appLines.AddRange(turn!.Shown);
        }

        // Every line the app shows, the terminal printed — in order.
        int at = 0;
        foreach (string line in appLines)
        {
            int found = terminal.IndexOf(line, at, StringComparison.Ordinal);
            Assert.True(found >= 0,
                $"the app showed a line the terminal never printed (or printed out of order): '{line}'\n--- terminal ---\n{terminal}");
            at = found + line.Length;
        }
    }

    /// <summary>B8's terminal-face box: a definition typed as
    /// `: NAME … ;` across three entries defines the word, and a
    /// half-typed definition is visible as such — the prompt switches
    /// while it is open and back when it closes (B4.7).</summary>
    [Fact]
    public async Task TheMorePromptShowsWhileADefinitionIsOpen()
    {
        HalifaxSession session = Fresh(out _);
        _ = session.Opening();
        string atRest = session.Prompt;

        HalifaxTurn? open = await session.SubmitAsync(": TRIPLE");
        Assert.NotNull(open);
        Assert.NotEqual(atRest, open!.Prompt);

        HalifaxTurn? body = await session.SubmitAsync("  3 *");
        Assert.NotNull(body);
        Assert.NotEqual(atRest, body!.Prompt);

        HalifaxTurn? close = await session.SubmitAsync(";");
        Assert.NotNull(close);
        Assert.Equal(atRest, close!.Prompt);

        HalifaxTurn? used = await session.SubmitAsync("5 TRIPLE");
        Assert.NotNull(used);
        Assert.Contains(used!.Shown, l => l.Contains("15"));
    }

    [Fact]
    public async Task QuitInsideADefinitionIsAnOrdinaryToken()
    {
        HalifaxSession session = Fresh(out _);
        _ = session.Opening();

        HalifaxTurn? open = await session.SubmitAsync(": NOPE");
        Assert.NotNull(open);
        Assert.False(open!.Quit);

        // QUIT typed as the next line of an open definition is a token
        // in that definition's body, not a way out — there is no way to
        // leave a session by accident halfway through one.
        HalifaxTurn? inside = await session.SubmitAsync("QUIT");
        Assert.NotNull(inside);
        Assert.False(inside!.Quit);
    }

    [Fact]
    public async Task QuitAtRestQuits()
    {
        HalifaxSession session = Fresh(out _);
        _ = session.Opening();
        HalifaxTurn? turn = await session.SubmitAsync("QUIT");
        Assert.NotNull(turn);
        Assert.True(turn!.Quit);
    }
}
