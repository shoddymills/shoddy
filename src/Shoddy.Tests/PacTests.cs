// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// The Pac pure model (mills/pac-vt100/pac-core.shoddy): maze, per-tick
/// simulation, collisions and input actions, none of which touch a
/// terminal. The VT100 half (painting + the loop, in pac.shoddy) is left
/// to manual play. Cherries land randomly, so every assertion here depends
/// only on how many there are, never on where.
/// </summary>
public class PacTests
{
    static readonly string Root = RepoRoot.Dir;

    [Fact]
    public void NewGameSetsTheBoardAndTheCast()
    {
        string output = Probe(
            "    Let g = NewGame()",
            "    Print(\"dots=\" & Str(Dots(g)) & \" start=\" & Str(Px(g)) & \",\" & Str(Py(g)))",
            "    Print(\"ghosts=\" & Str(Length(Ghosts(g))) & \" lives=\" & Str(Lives(g)) & \" ended=\" & YN(Ended(g)))",
            // six cherries were carved out of the dots; every dot and cherry is owed
            "    Print(\"plus=\" & Str(Fold(ToList(Board(g)), 0, Fn(a, r) => a + CountCh(r, \"+\"))))",
            "    Print(\"owed=\" & YN(Fold(ToList(Board(g)), 0, Fn(a, r) => a + CountCh(r, \".\")) = Dots(g) - CherryCount()))",
            "    Print(\"home=\" & Str(Gx(First(Ghosts(g)))) & \",\" & Str(Gy(First(Ghosts(g)))))");
        Assert.Equal(string.Join('\n',
            "dots=488 start=19,19",
            "ghosts=5 lives=5 ended=N",
            "plus=6",
            "owed=Y",
            "home=18,11",
            ""), output);
    }

    [Fact]
    public void WallsBlockStepsAndEatingScores()
    {
        string output = Probe(
            "    Let g = NewGame()",
            // the cell above the start is a wall: no step, but the sprite turns
            "    Let up = TryMove(g, 1)",
            "    Print(\"up=\" & Str(Px(up)) & \",\" & Str(Py(up)) & \" pd=\" & Str(Pd(up)))",
            // stepping left lands on a dot or a cherry; either way it is eaten
            "    Let left = TryMove(g, 4)",
            "    Print(\"left=\" & Str(Px(left)) & \" dots=\" & Str(Dots(left)))",
            "    Print(\"ate=\" & YN(Score(left) = 1 Or EdibleT(left) > 0))");
        Assert.Equal(string.Join('\n',
            "up=19,19 pd=1",
            "left=18 dots=487",
            "ate=Y",
            ""), output);
    }

    [Fact]
    public void GhostsAreDinnerWhileEdibleAndDeadlyOtherwise()
    {
        string output = Probe(
            "    Let g = NewGame()",
            // edible: the ghost is scored and sent home to the cage
            "    Let meal = EatOrDie(With(g, Ghosts = { Ghost(Px(g), Py(g), 0, 17, 11) }, EdibleT = 10))",
            "    Print(\"meal score=\" & Str(Score(meal)) & \" home=\" & Str(Gx(First(Ghosts(meal)))) & \",\" & Str(Gy(First(Ghosts(meal)))))",
            // not edible: a life is lost and the player respawns at the start
            "    Let death = EatOrDie(With(g, Ghosts = { Ghost(Px(g), Py(g), 0, 17, 11) }))",
            "    Print(\"death lives=\" & Str(Lives(death)) & \" respawn=\" & YN(Px(death) = Sx(g) And Py(death) = Sy(g)))",
            // the last life ends the game, and not in victory
            "    Let last = EatOrDie(With(g, Ghosts = { Ghost(Px(g), Py(g), 0, 17, 11) }, Lives = 1))",
            "    Print(\"last=\" & YN(Ended(last) And Not Won(last)))",
            // eating the final dot wins, banks the life bonus, and freezes Advance
            "    Let win = TryMove(With(g, Dots = 1), 4)",
            "    Print(\"win=\" & YN(Won(win) And Ended(win)) & \" bonus=\" & YN(Score(win) >= StartLives() * LifeBonus()))",
            "    Print(\"frozen=\" & YN(Ended(Advance(win))))");
        Assert.Equal(string.Join('\n',
            "meal score=100 home=17,11",
            "death lives=4 respawn=Y",
            "last=Y",
            "win=Y bonus=Y",
            "frozen=Y",
            ""), output);
    }

    [Fact]
    public void GhostsStepOneLegalCellAndScatterRunsDown()
    {
        string output = Probe(
            // a free-standing ghost with open floor around it moves exactly one cell
            "    Let g = NewGame()",
            "    Let round = MoveGhostsRound(With(g, Ghosts = { Ghost(1, 1, 0, 1, 1) }))",
            "    Let m = First(Ghosts(round))",
            "    Print(\"dist=\" & Str(Abs(Gx(m) - 1) + Abs(Gy(m) - 1)) & \" floor=\" & YN(Not IsWall(g, Gx(m), Gy(m))))",
            "    Print(\"scatter=\" & Str(ScatterT(round)))");
        Assert.Equal(string.Join('\n',
            "dist=1 floor=Y",
            "scatter=245",   // ScatterSteps() 250 - GhostCount() 5
            ""), output);
    }

    [Fact]
    public void KeysSteerAndQuit()
    {
        string output = Probe(
            "    Let g = NewGame()",
            // arrows arrive from EvalKey as KeyLeft etc.; WASD as KeyUnknown
            "    Print(\"arrow=\" & Str(Px(Act(g, KeyLeft()))))",
            "    Print(\"wasd=\" & Str(Px(Act(g, EvalKey(\"a\")))))",
            "    Print(\"quit=\" & YN(QuitKey(EvalKey(\"q\")) And QuitKey(EvalKey(Chr(27)))))",
            "    Print(\"stay=\" & YN(Not QuitKey(EvalKey(\"a\"))))");
        Assert.Equal(string.Join('\n',
            "arrow=18",
            "wasd=18",
            "quit=Y",
            "stay=Y",
            ""), output);
    }

    // ---- helper --------------------------------------------------------

    /// <summary>Weave and run a Main body against pac-core.shoddy. The
    /// core's includes are bare — a mill reaches its machines through the
    /// library search path, not a relative walk — so the probe and the
    /// core sit in the machines directory of a temp workspace, where the
    /// includes resolve beside the file with no $SHODDYLIB in play (this
    /// test runs in-process, so there is no mill to resolve against).
    /// No terminal: the core emits no escape sequences and reads no
    /// keys.</summary>
    static string Probe(params string[] body)
    {
        string ws = Path.Combine(Path.GetTempPath(), "shoddy-pac", Guid.NewGuid().ToString("N"));
        string lib = Path.Combine(ws, "machines");
        Directory.CreateDirectory(lib);
        foreach (string f in Directory.GetFiles(Path.Combine(Root, "machines"), "*.shoddy"))
            File.Copy(f, Path.Combine(lib, Path.GetFileName(f)));
        File.Copy(Path.Combine(Root, "mills", "pac-vt100", "pac-core.shoddy"),
                  Path.Combine(lib, "pac-core.shoddy"));

        string sb = Path.Combine(lib, "probe.shoddy");
        File.WriteAllText(sb,
            "Include \"pac-core.shoddy\"\n\n"
            + "Def YN(b As Boolean) As String\n    If b Then\n        \"Y\"\n    Else\n        \"N\"\n\n"
            + "Def Main()\n" + string.Join('\n', body) + "\n");

        var machines = new MachineSet();
        var lines = Lexer.ReadProgram(sb, machines.TryResolve);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        Parser.Parse(lines, prog);
        var output = new StringWriter();
        int exit = Weaver.Execute(prog, machines.Machines, output, TextReader.Null, Array.Empty<string>());
        Assert.True(exit == 0, $"expected exit 0, got {exit}");
        return output.ToString();
    }
}
