// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Hosting;
using Shoddy.Maui;
using Xunit;

namespace Shoddy.Maui.Tests;

/// <summary>
/// B4.2: user words persist across launches. A word defined and SAVEd
/// to halifaxrc in one session is there when the next session opens
/// against the same root — the same `: NAME … ;` text the terminal
/// mill writes, loaded the way the terminal loads it, reported the way
/// the terminal reports it.
/// </summary>
public class PersistenceTests
{
    [Fact]
    public async Task UserWordsSurviveARelaunch()
    {
        string root = Path.Combine(Path.GetTempPath(), "shoddy-maui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        // First launch: define, save, quit.
        var first = HalifaxSession.Open(new ShoddyHostOptions { FileRoot = root });
        _ = first.Opening();
        Assert.NotNull(await first.SubmitAsync(": DOZEN 12 * ;"));
        HalifaxTurn? saved = await first.SubmitAsync("SAVE \"halifaxrc\"");
        Assert.NotNull(saved);
        Assert.Contains(saved!.Shown, l => l.Contains("saved") && l.Contains("word"));
        Assert.True(File.Exists(Path.Combine(root, "halifaxrc")));

        // The saved file is the terminal's own format.
        string text = File.ReadAllText(Path.Combine(root, "halifaxrc"));
        Assert.Contains(": DOZEN", text);

        // Second launch against the same root: the rc auto-loads and
        // the word answers.
        var second = HalifaxSession.Open(new ShoddyHostOptions { FileRoot = root });
        IReadOnlyList<string> opening = second.Opening();
        Assert.Contains(opening, l => l.Contains("halifaxrc") && l.Contains("loaded"));
        HalifaxTurn? used = await second.SubmitAsync("3 DOZEN");
        Assert.NotNull(used);
        Assert.Contains(used!.Shown, l => l.Contains("36"));
    }

    [Fact]
    public async Task TapeSaveWritesTheSessionRecord()
    {
        string root = Path.Combine(Path.GetTempPath(), "shoddy-maui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var session = HalifaxSession.Open(new ShoddyHostOptions { FileRoot = root });
        _ = session.Opening();
        Assert.NotNull(await session.SubmitAsync("2 3 +"));
        HalifaxTurn? wrote = await session.SubmitAsync("TAPESAVE \"tape.txt\"");
        Assert.NotNull(wrote);
        Assert.Contains(wrote!.Shown, l => l.Contains("wrote"));
        Assert.Contains("2 3 +", File.ReadAllText(Path.Combine(root, "tape.txt")));
    }

    /// <summary>SAVE, LOAD and TAPESAVE name their file in the shell,
    /// so the engine's boundary never saw the path. They keep to the
    /// root by the same rule now (B3.5), and a plain name still lands
    /// in it.</summary>
    [Fact]
    public async Task TheFileWordsKeepToTheRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "shoddy-maui-tests", Guid.NewGuid().ToString("N"), "root");
        Directory.CreateDirectory(root);
        string parent = Path.GetDirectoryName(root)!;

        var session = HalifaxSession.Open(new ShoddyHostOptions { FileRoot = root });
        _ = session.Opening();
        Assert.NotNull(await session.SubmitAsync(": DOZEN 12 * ;"));

        HalifaxTurn? saved = await session.SubmitAsync("SAVE \"../escaped.halifax\"");
        Assert.NotNull(saved);
        Assert.Contains(saved!.Shown, l => l.Contains("outside the file root"));
        Assert.False(File.Exists(Path.Combine(parent, "escaped.halifax")), "SAVE wrote outside the root");

        string outside = Path.Combine(parent, "escaped.tape").Replace('\\', '/');
        HalifaxTurn? taped = await session.SubmitAsync("TAPESAVE \"" + outside + "\"");
        Assert.NotNull(taped);
        Assert.Contains(taped!.Shown, l => l.Contains("outside the file root"));
        Assert.False(File.Exists(Path.Combine(parent, "escaped.tape")), "TAPESAVE wrote outside the root");

        string secret = Path.Combine(parent, "secret.txt");
        File.WriteAllText(secret, "not a definition\n");
        HalifaxTurn? loaded = await session.SubmitAsync("LOAD \"" + secret.Replace('\\', '/') + "\"");
        Assert.NotNull(loaded);
        Assert.Contains(loaded!.Shown, l => l.Contains("outside the file root"));
        Assert.DoesNotContain(loaded.Shown, l => l.Contains("not a definition"));

        HalifaxTurn? kept = await session.SubmitAsync("SAVE \"kept.halifax\"");
        Assert.NotNull(kept);
        Assert.Contains(kept!.Shown, l => l.Contains("saved"));
        Assert.True(File.Exists(Path.Combine(root, "kept.halifax")), "a plain name did not land in the root");
    }

    [Fact]
    public async Task LoadRefusesAMissingFileInLowerCaseProse()
    {
        string root = Path.Combine(Path.GetTempPath(), "shoddy-maui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var session = HalifaxSession.Open(new ShoddyHostOptions { FileRoot = root });
        _ = session.Opening();
        HalifaxTurn? turn = await session.SubmitAsync("LOAD \"nowhere.halifax\"");
        Assert.NotNull(turn);
        Assert.Contains(turn!.Shown, l => l.Contains("there is no file called nowhere.halifax"));
    }
}
