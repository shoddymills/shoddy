// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// The buzzer, headless. Sound hardware is untestable in CI; everything
/// up to the BuzzerRegistry delegate call is testable, which is why the
/// seam and the pure MML parser exist. Fake delegates record their
/// arguments; null delegates prove silence is a no-op; validation raises
/// both ways. No test asserts on anything audible, ever.
/// Joins the "golden" collection: BuzzerRegistry and the Console.Error
/// redirect are process-global, so these must not run in parallel with
/// the other global-state suites.
/// </summary>
[Collection("golden")]
public class BuzzerTests
{
    static readonly string Root = RepoRoot.Dir;

    // ---- the seam: pass-through and silence -----------------------------

    [Fact]
    public void DelegatesReceiveExactlyWhatTheProgramSaidInOrder()
    {
        var calls = new List<string>();
        string output = Run(string.Join('\n',
            "Def Main()",
            "    Sound(880, 60)",
            "    NoteOn(2, 440)",
            "    SoundGain(2, 0.5)",
            "    SoundQueue(3, 261.63, 250)",
            "    SoundQueue(3, 0, 125)",     // freq 0: a rest, legal
            "    NoteOff(2)",
            "    SoundStop(3)",
            ""), calls);
        Assert.Equal("", output);
        Assert.Equal(new[]
        {
            "Sound 880 60",
            "NoteOn 2 440",
            "Gain 2 0.5",
            "Queue 3 261.63 250",
            "Queue 3 0 125",
            "NoteOff 2",
            "Stop 3",
        }, calls);
    }

    [Fact]
    public void NullDelegatesMakeEverySoundWordASilentNoOp()
    {
        string output = Run(string.Join('\n',
            "Def Main()",
            "    Sound(880, 60)",
            "    Sound(880, 0)",             // 0 ms: legal no-op
            "    NoteOn(2, 440)",
            "    SoundGain(2, 1)",
            "    SoundQueue(3, 261.63, 250)",
            "    NoteOff(2)",
            "    SoundStop(3)",
            "    Print(\"still here\")",
            ""), fakes: null);
        Assert.Equal("still here\n", output);
    }

    [Fact]
    public void ZeroMsSoundNeverReachesTheDelegate()
    {
        var calls = new List<string>();
        Run("Def Main()\n    Sound(440, 0)\n", calls);
        Assert.Empty(calls);
    }

    // ---- validation: raises with delegates null AND installed -----------

    [Theory]
    [InlineData("Sound(0, 100)", "Sound: frequency must be positive")]
    [InlineData("Sound(440, -1)", "Sound: duration must be >= 0")]
    [InlineData("NoteOn(0, 440)", "NoteOn: channel must be 1..8, got 0")]
    [InlineData("NoteOn(9, 440)", "NoteOn: channel must be 1..8, got 9")]
    [InlineData("NoteOn(1.5, 440)", "NoteOn: channel must be 1..8, got 1.5")]
    [InlineData("NoteOn(3, 0)", "NoteOn: frequency must be positive")]
    [InlineData("NoteOff(0)", "NoteOff: channel must be 1..8")]
    [InlineData("SoundQueue(1, -1, 100)", "SoundQueue: frequency must be positive (or 0 for a rest)")]
    [InlineData("SoundQueue(1, 440, -5)", "SoundQueue: duration must be >= 0")]
    [InlineData("SoundStop(42)", "SoundStop: channel must be 1..8, got 42")]
    [InlineData("SoundGain(0, 0.5)", "SoundGain: channel must be 1..8")]
    [InlineData("SoundGain(1, 1.5)", "SoundGain: volume must be 0..1, got 1.5")]
    [InlineData("SoundGain(1, -0.1)", "SoundGain: volume must be 0..1")]
    public void BadArgumentsRaiseEvenInSilence(string call, string expect)
    {
        // Null delegates: headless tests exercise real validation.
        Assert.Contains(expect, RunError($"Def Main()\n    {call}\n", fakes: null));

        // Installed delegates: still raises, and the fake is never called.
        var calls = new List<string>();
        Assert.Contains(expect, RunError($"Def Main()\n    {call}\n", calls));
        Assert.Empty(calls);
    }

    // ---- the queued-ahead cap -------------------------------------------

    [Fact]
    public void QueueCapRaisesAtFiveMinutesAhead()
    {
        string err = RunError(string.Join('\n',
            "Def Main()",
            "    SoundQueue(1, 440, 100000)",
            "    SoundQueue(1, 440, 100000)",
            "    SoundQueue(1, 440, 100000)",   // 300 s queued: at the cap, fine
            "    SoundQueue(1, 440, 100000)",   // 400 s: over it
            ""), fakes: null);
        Assert.Contains("queued ahead on channel 1", err);
        Assert.Contains("feed long scores incrementally", err);
    }

    [Fact]
    public void SoundStopAndNoteOnResetTheQueueCap()
    {
        string output = Run(string.Join('\n',
            "Def Main()",
            "    SoundQueue(1, 440, 250000)",
            "    SoundStop(1)",                  // flushes: the cap resets
            "    SoundQueue(1, 440, 250000)",
            "    NoteOn(1, 440)",                // a held note flushes too
            "    SoundQueue(1, 440, 250000)",
            "    Print(\"OK\")",
            ""), fakes: null);
        Assert.Equal("OK\n", output);
    }

    [Fact]
    public void QueueCapIsPerChannel()
    {
        string output = Run(string.Join('\n',
            "Def Main()",
            "    SoundQueue(1, 440, 250000)",
            "    SoundQueue(2, 440, 250000)",   // channel 2's cap is its own
            "    Print(\"OK\")",
            ""), fakes: null);
        Assert.Equal("OK\n", output);
    }

    // ---- machines/buzzer.shoddy: frequencies and note names -------------

    [Fact]
    public void MidiFreqHitsThePitchStandard()
    {
        string output = Run(string.Join('\n',
            "Include \"machines/buzzer.shoddy\"",
            "Def Main()",
            "    Print(MidiFreq(69))",
            "    Print(MidiFreq(81))",
            "    Print(NoteMidi(\"C4\"))",
            "    Print(NoteMidi(\"F#3\"))",
            "    Print(NoteMidi(\"Bb5\"))",
            "    Print(NoteMidi(\"b7\"))",       // case-insensitive: B7
            "    Print(NoteFreq(\"A4\"))",
            ""), fakes: null);
        Assert.Equal("440\n880\n60\n54\n82\n107\n440\n", output);
    }

    [Theory]
    [InlineData("H2")]
    [InlineData("C")]        // octave digit required
    [InlineData("C#")]
    [InlineData("C99")]
    [InlineData("")]
    public void MalformedNoteNamesRaise(string name)
    {
        string err = RunError(string.Join('\n',
            "Include \"machines/buzzer.shoddy\"",
            "Def Main()",
            $"    Print(NoteMidi(\"{name}\"))",
            ""), fakes: null);
        Assert.Contains("BAD NOTE NAME", err);
    }

    // ---- MmlNotes golden tests (pure, tolerance on frequencies) ----------

    [Fact]
    public void MmlDefaultsTempoOctaveAndLength()
    {
        // T120: whole note 2000 ms; default L4 -> 500 ms; O4 C is middle C.
        var notes = ParseNotes(RunMml("C"));
        AssertNotes(notes, (261.6256, 500));
    }

    [Fact]
    public void MmlScaleWithTempoOctaveAndLengths()
    {
        var notes = ParseNotes(RunMml("T120 O4 L8 C D E F G2"));
        AssertNotes(notes,
            (261.6256, 250), (293.6648, 250), (329.6276, 250),
            (349.2282, 250), (391.9954, 1000));
    }

    [Fact]
    public void MmlDotsRestsAndOctaveShifts()
    {
        // T60: whole 4000. C. dotted quarter 1500; P8 rest 500; > C is C5;
        // < B- is B flat back in octave 4.
        var notes = ParseNotes(RunMml("T60 L4 C. P8 > C < B-"));
        AssertNotes(notes,
            (261.6256, 1500), (0, 500), (523.2511, 1000), (466.1638, 1000));
    }

    [Fact]
    public void MmlSharpsAndRestsWithDots()
    {
        var notes = ParseNotes(RunMml("L8 C# R4. F+2"));
        AssertNotes(notes, (277.1826, 250), (0, 750), (369.9944, 1000));
    }

    [Fact]
    public void MmlCommaIsAQuarterRest()
    {
        // GW-BASIC's comma: a quarter-note rest, always length 4 — the L8
        // default in force does not shorten it (T120: quarter = 500 ms).
        var notes = ParseNotes(RunMml("T120 L8 C, D ,"));
        AssertNotes(notes, (261.6256, 250), (0, 500), (293.6648, 250), (0, 500));
    }

    [Theory]
    [InlineData("C X", "POSITION 3")]
    [InlineData("T C", "T NEEDS A NUMBER AT POSITION 1")]
    [InlineData("L0 C", "L MUST BE AT LEAST 1")]
    [InlineData("O9 C", "OCTAVE OUT OF RANGE")]
    [InlineData("O0 < C", "OCTAVE OUT OF RANGE")]
    [InlineData("C0", "BAD LENGTH AT POSITION 1")]
    public void MalformedMmlRaisesNamingThePosition(string mml, string expect)
    {
        string err = RunError(string.Join('\n',
            "Include \"machines/buzzer.shoddy\"",
            "Def Main()",
            $"    Print(MmlNotes(\"{mml}\"))",
            ""), fakes: null);
        Assert.Contains(expect, err);
    }

    [Fact]
    public void PlayQueuesTheParsedTuneInOrder()
    {
        var calls = new List<string>();
        Run(string.Join('\n',
            "Include \"machines/buzzer.shoddy\"",
            "Def Main()",
            "    Play(5, \"T120 L8 C P8 D\")",
            ""), calls);
        Assert.Equal(3, calls.Count);
        var first = ParseCall(calls[0]);
        var rest = ParseCall(calls[1]);
        var last = ParseCall(calls[2]);
        Assert.All(new[] { first, rest, last }, c => Assert.Equal(5, c.Ch));
        Assert.Equal(261.6256, first.F, 3);
        Assert.Equal(250, first.Ms, 3);
        Assert.Equal(0, rest.F, 3);
        Assert.Equal(250, rest.Ms, 3);
        Assert.Equal(293.6648, last.F, 3);
        Assert.Equal(250, last.Ms, 3);
    }

    // ---- the machine itself ----------------------------------------------

    [Fact]
    public void BuzzerMachineCompilesWithMillMachine()
    {
        string ws = Workspace();
        var machines = new MachineSet();
        string src = Path.Combine(ws, "machines", "buzzer.shoddy");
        var lines = Lexer.ReadProgram(src, machines.TryResolve);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        Parser.Parse(lines, prog);
        string dll = Weaver.WeaveMachine(prog, src, machines.Machines);
        Assert.True(File.Exists(dll), $"machine DLL not produced: {dll}");
    }

    // ---- helpers ----------------------------------------------------------

    static string Workspace()
    {
        string ws = Path.Combine(Path.GetTempPath(), "shoddy-buzzer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(ws, "machines"));
        foreach (string f in Directory.GetFiles(Path.Combine(Root, "machines"), "*.shoddy"))
            File.Copy(f, Path.Combine(ws, "machines", Path.GetFileName(f)));
        return ws;
    }

    /// <summary>Run a program with recording fakes (calls != null), or
    /// with every delegate null (calls == null). Returns stdout;
    /// the woven program must exit 0.</summary>
    static string Run(string src, List<string>? fakes)
    {
        var (exit, output, err) = Execute(src, fakes);
        Assert.True(exit == 0, $"expected exit 0, got {exit}: {err}");
        return output;
    }

    /// <summary>Run a program expected to die with a ShoddyError: woven
    /// programs catch it, print to stderr and return 1.</summary>
    static string RunError(string src, List<string>? fakes)
    {
        var (exit, _, err) = Execute(src, fakes);
        Assert.Equal(1, exit);
        return err;
    }

    static string RunMml(string mml) => Run(string.Join('\n',
        "Include \"machines/buzzer.shoddy\"",
        "Def Main()",
        $"    Print(MmlNotes(\"{mml}\"))",
        ""), fakes: null);

    static (int Exit, string Out, string Err) Execute(string src, List<string>? fakes)
    {
        static string N(double d) => d.ToString("R", CultureInfo.InvariantCulture);
        if (fakes != null)
        {
            BuzzerRegistry.Sound   = (f, ms)     => fakes.Add($"Sound {N(f)} {N(ms)}");
            BuzzerRegistry.NoteOn  = (ch, f)     => fakes.Add($"NoteOn {ch} {N(f)}");
            BuzzerRegistry.NoteOff = ch          => fakes.Add($"NoteOff {ch}");
            BuzzerRegistry.Queue   = (ch, f, ms) => fakes.Add($"Queue {ch} {N(f)} {N(ms)}");
            BuzzerRegistry.Stop    = ch          => fakes.Add($"Stop {ch}");
            BuzzerRegistry.Gain    = (ch, v)     => fakes.Add($"Gain {ch} {N(v)}");
        }
        string ws = Workspace();
        string sb = Path.Combine(ws, "prog.shoddy");
        File.WriteAllText(sb, src);
        var err = new StringWriter();
        TextWriter oldErr = Console.Error;
        Console.SetError(err);
        try
        {
            var machines = new MachineSet();
            var lines = Lexer.ReadProgram(sb, machines.TryResolve);
            var prog = new ShoddyProgram();
            machines.SeedInto(prog);
            Parser.Parse(lines, prog);
            var output = new StringWriter();
            int exit = Weaver.Execute(prog, machines.Machines, output, TextReader.Null, Array.Empty<string>());
            return (exit, output.ToString(), err.ToString());
        }
        finally
        {
            Console.SetError(oldErr);
            BuzzerRegistry.Sound = null; BuzzerRegistry.NoteOn = null;
            BuzzerRegistry.NoteOff = null; BuzzerRegistry.Queue = null;
            BuzzerRegistry.Stop = null; BuzzerRegistry.Gain = null;
        }
    }

    // "BuzzerNote(FreqHz = 261.6255653, Ms = 250)" -> (261.6255653, 250)
    static List<(double F, double Ms)> ParseNotes(string output)
    {
        var notes = new List<(double, double)>();
        foreach (Match m in Regex.Matches(output, @"BuzzerNote\(FreqHz = ([-\d.e+]+), Ms = ([-\d.e+]+)\)"))
            notes.Add((double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                       double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));
        return notes;
    }

    static (int Ch, double F, double Ms) ParseCall(string call)
    {
        string[] parts = call.Split(' ');
        Assert.Equal("Queue", parts[0]);
        return (int.Parse(parts[1], CultureInfo.InvariantCulture),
                double.Parse(parts[2], CultureInfo.InvariantCulture),
                double.Parse(parts[3], CultureInfo.InvariantCulture));
    }

    /// <summary>Frequencies to a tolerance, never exact-decimal — equal
    /// temperament comes out irrational.</summary>
    static void AssertNotes(List<(double F, double Ms)> got, params (double F, double Ms)[] want)
    {
        Assert.Equal(want.Length, got.Count);
        for (int i = 0; i < want.Length; i++)
        {
            Assert.True(Math.Abs(got[i].F - want[i].F) < 0.001,
                $"note {i + 1}: FreqHz {got[i].F}, wanted {want[i].F}");
            Assert.True(Math.Abs(got[i].Ms - want[i].Ms) < 0.001,
                $"note {i + 1}: Ms {got[i].Ms}, wanted {want[i].Ms}");
        }
    }
}
