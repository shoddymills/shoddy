// Copyright (c) 2026 Stephen Vincent Foster. All rights reserved.
//
// This file is part of the Shoddy Language project.
// Licensed under the Shoddy Language License 1.0.0 (PolyForm Noncommercial
// License 1.0.0 with Additional Use Grant). See the LICENSE file in the
// project root for full terms.

using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// The Space Invaders pure model (mills/space-invaders/invaders-core.shoddy):
/// state, per-frame simulation, and input actions, none of which touch a
/// window. The windowed half (drawing + the event loop, in invaders.shoddy)
/// is left to manual play. The workspace mirrors the repo's
/// mills/space-invaders layout so the core's ../../machines includes resolve.
/// </summary>
public class InvadersTests
{
    static readonly string Root = RepoRoot.Dir;

    [Fact]
    public void FireCollideMissAndScore()
    {
        string output = Probe(
            "    Let g = NewGame()",
            "    Print(\"cx=\" & Str(Cx(g)) & \" score=\" & Str(Score(g)) & \" ended=\" & YN(Ended(g)))",
            "    Let g1 = Fire(g)",
            "    Print(\"lasers=\" & Str(Length(Lasers(g1))))",
            // an alien sitting on the laser: collide clears both, scores one
            "    Let hit = Collide(With(g1, Aliens = { Alien(Cx(g1), CannonY() - CannonHalf(), 200, 200, 200) }))",
            "    Print(\"hit score=\" & Str(Score(hit)) & \" aliens=\" & Str(Length(Aliens(hit))) & \" lasers=\" & Str(Length(Lasers(hit))))",
            // a far alien is missed: it survives, no score
            "    Let miss = Collide(With(g1, Aliens = { Alien(50, 50, 1, 2, 3) }))",
            "    Print(\"miss score=\" & Str(Score(miss)) & \" aliens=\" & Str(Length(Aliens(miss))))");
        Assert.Equal(string.Join('\n',
            "cx=240 score=0 ended=N",
            "lasers=1",
            "hit score=1 aliens=0 lasers=0",
            "miss score=0 aliens=1",
            ""), output);
    }

    [Fact]
    public void AliensDescendAndEndTheGameAtTheCannonLine()
    {
        string output = Probe(
            // an alien just above the line keeps falling and is not yet over
            "    Let high = MoveAliens(With(NewGame(), Aliens = { Alien(100, 0, 1, 2, 3) }))",
            "    Print(\"y=\" & Str(Ay(First(Aliens(high)))) & \" ended=\" & YN(Ended(high)))",
            // one at the cannon line ends it
            "    Let low = MoveAliens(With(NewGame(), Aliens = { Alien(100, CannonY(), 1, 2, 3) }))",
            "    Print(\"ended=\" & YN(Ended(low)))",
            // once ended, Advance is a no-op (aliens freeze)
            "    Print(\"frozen=\" & YN(Ended(Advance(low, 999999))))");
        Assert.Equal(string.Join('\n',
            "y=2 ended=N",
            "ended=Y",
            "frozen=Y",
            ""), output);
    }

    [Fact]
    public void LasersRiseOffTopAndCannonClampsToGutter()
    {
        string output = Probe(
            // a laser at the top edge rises past the cutoff and is dropped
            "    Let flown = MoveLasers(With(NewGame(), Lasers = { Laser(100, -10) }))",
            "    Print(\"lasers=\" & Str(Length(Lasers(flown))))",
            // pressing left then stepping moves the cannon one step, still -1
            "    Let far = MoveCannon(ActDown(NewGame(), ArrowLeft()))",
            "    Print(\"cx=\" & Str(Cx(far)) & \" vel=\" & Str(Vel(far)))",
            // near the gutter, a left step clamps to it and no further
            "    Let deep = MoveCannon(With(NewGame(), Cx = Gutter() + 3, Vel = -1))",
            "    Print(\"clamped=\" & Str(Cx(deep)))",
            "    Print(\"released=\" & Str(Vel(ActUp(far, ArrowLeft()))))");
        Assert.Equal(string.Join('\n',
            "lasers=0",
            "cx=233 vel=-1",   // 240 - 7
            "clamped=30",      // Gutter(): Clamp(33 - 7, 30, ...) = 30
            "released=0",
            ""), output);
    }

    [Fact]
    public void SpawnAddsOneAlienWhenDue()
    {
        string output = Probe(
            "    Let g = NewGame()",
            // now >= NextSpawn(0): one alien appears, NextSpawn advances
            "    Let s1 = MaybeSpawn(g, 1000)",
            "    Print(\"aliens=\" & Str(Length(Aliens(s1))) & \" next=\" & Str(NextSpawn(s1)))",
            // not yet due again: unchanged
            "    Let s2 = MaybeSpawn(s1, 1000)",
            "    Print(\"aliens=\" & Str(Length(Aliens(s2))))",
            // the spawned alien is within the play field
            "    Let a = First(Aliens(s1))",
            "    Print(\"inbounds=\" & YN(Ax(a) >= Gutter() And Ax(a) <= GameW() - Gutter()))");
        Assert.Equal(string.Join('\n',
            "aliens=1 next=1950",   // 1000 + SpawnMs() 950
            "aliens=1",
            "inbounds=Y",
            ""), output);
    }

    // ---- helper --------------------------------------------------------

    /// <summary>Weave and run a Main body against invaders-core.shoddy,
    /// with the repo's mills/space-invaders layout reproduced in a temp
    /// workspace so the core's ../../machines includes resolve. No window:
    /// the core includes no scribbler.</summary>
    static string Probe(params string[] body)
    {
        string ws = Path.Combine(Path.GetTempPath(), "shoddy-invaders", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(ws, "machines"));
        Directory.CreateDirectory(Path.Combine(ws, "mills", "space-invaders"));
        foreach (string f in Directory.GetFiles(Path.Combine(Root, "machines"), "*.shoddy"))
            File.Copy(f, Path.Combine(ws, "machines", Path.GetFileName(f)));
        File.Copy(Path.Combine(Root, "mills", "space-invaders", "invaders-core.shoddy"),
                  Path.Combine(ws, "mills", "space-invaders", "invaders-core.shoddy"));

        string sb = Path.Combine(ws, "mills", "space-invaders", "probe.shoddy");
        File.WriteAllText(sb,
            "Include \"invaders-core.shoddy\"\n\n"
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
