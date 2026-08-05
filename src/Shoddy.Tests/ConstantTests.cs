// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Reflection;
using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// A machine's top-level Let: the constant it may now carry, and the
/// refusals for everything it may not.
///
/// The first test is the whole justification. A List compares by
/// identity, so a zero-arg Def returning { "A", "B" } hands back a
/// different value every call and Xs() = Xs() is FALSE — a machine had no
/// way to publish a canonical list at all. A constant closes that, and
/// the pair is asserted together so the difference is visible rather than
/// claimed.
///
/// Everything here weaves real machine DLLs in a temp workspace and runs
/// real programs against them, because the properties under test are
/// properties of the emitted code: one value per process, shared across
/// Engines, and invisible outside the file that declares it.
/// </summary>
[Collection("golden")]
public class ConstantTests
{
    // ---- workspace plumbing --------------------------------------------

    /// <summary>Machines and programs share one directory, the way an
    /// Include next to the includer expects, with the machine DLLs landing
    /// in bin/ beside them exactly as `mill machine` puts them.</summary>
    sealed class Workspace : IDisposable
    {
        public readonly string Dir =
            Path.Combine(Path.GetTempPath(), "shoddy-const", Guid.NewGuid().ToString("N"));

        public Workspace()
        {
            Directory.CreateDirectory(Dir);
            Directory.CreateDirectory(Path.Combine(Dir, "out"));
        }

        public string Machine(string name, string src)
        {
            string p = Path.Combine(Dir, name + ".shoddy");
            File.WriteAllText(p, src);
            return p;
        }

        public string Program(string name, string src) => Machine(name, src);

        /// <summary>A real machine from the tree, copied in so an Include
        /// of it resolves here.</summary>
        public void Standard(string name) =>
            File.Copy(Path.Combine(RepoRoot.Dir, "machines", name + ".shoddy"),
                      Path.Combine(Dir, name + ".shoddy"), overwrite: true);

        // Reading a machine's manifest loads its DLL into this process, so
        // the file stays locked for as long as the test run does. The
        // workspace is under the temp directory; leaving it is the price of
        // testing the real resolution path.
        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    static (ShoddyProgram, MachineSet) Parse(string file)
    {
        var machines = new MachineSet();
        List<Line> lines = Lexer.ReadProgram(file, machines.TryResolve);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        return (Parser.Parse(lines, prog), machines);
    }

    /// <summary>Weave a machine source to its DLL beside it, as
    /// `mill machine` does.</summary>
    static string BuildMachine(string path)
    {
        var (prog, set) = Parse(path);
        return Weaver.WeaveMachine(prog, path, set.Machines);
    }

    /// <summary>Weave a program against whatever machines resolve, run it,
    /// and return everything it printed.</summary>
    static string RunProgram(Workspace ws, string path)
    {
        var (prog, set) = Parse(path);
        string dll = Path.Combine(ws.Dir, "out", Path.GetFileNameWithoutExtension(path) + ".dll");
        Weaver.Weave(prog, dll, set.Machines);
        Assembly asm = Assembly.Load(File.ReadAllBytes(dll));
        MethodInfo run = asm.GetType("Woven")!.GetMethod("Run")!;
        var output = new StringWriter();
        int rc = (int)run.Invoke(null, new object?[] { output, TextReader.Null, Array.Empty<string>() })!;
        Assert.Equal(0, rc);
        return output.ToString();
    }

    static string J(params string[] lines) => string.Join('\n', lines) + "\n";

    /// <summary>The message a machine weave refuses with, or "" if it did
    /// not refuse.</summary>
    static string Refusal(string src)
    {
        using var ws = new Workspace();
        ws.Standard("seq");
        string p = ws.Machine("fixture", src);
        try { BuildMachine(p); return ""; }
        catch (ShoddyError e) { return e.Message; }
    }

    // ---- the semantic hole, closed --------------------------------------

    [Fact]
    public void AConstantListIsEqualToItselfAndADefIsNot()
    {
        using var ws = new Workspace();
        BuildMachine(ws.Machine("held", J(
            "Let xs = { \"A\", \"B\" }",
            "",
            "Def HeldXs() As List Of String",
            "    xs",
            "",
            "Rem the old idiom, kept beside it: a fresh list every call",
            "Def BuiltXs() As List Of String",
            "    { \"A\", \"B\" }")));

        string outp = RunProgram(ws, ws.Program("use", J(
            "Include \"held.shoddy\"",
            "",
            "Def Main()",
            "    Print(HeldXs() = HeldXs())",
            "    Print(BuiltXs() = BuiltXs())",
            "    Print(Nth(HeldXs(), 2))")));

        // The constant is one value; the Def mints one per call, and a
        // List compares by identity.
        Assert.Equal("True\nFalse\nB\n", outp);
    }

    // ---- the folded builtins (R2.2) and operators (R2.6) ----------------

    [Fact]
    public void ToArrayAndDimFold()
    {
        using var ws = new Workspace();
        BuildMachine(ws.Machine("arr", J(
            "Let table = ToArray({ 1, 2, 3 })",
            "Let zeros = Dim(3, 0)",
            "",
            "Def ArrTable() As Array Of Number",
            "    table",
            "",
            "Def ArrZeros() As Array Of Number",
            "    zeros")));

        Assert.Equal("2\n3\nTrue\n", RunProgram(ws, ws.Program("use", J(
            "Include \"arr.shoddy\"",
            "",
            "Def Main()",
            "    Print(Nth(ArrTable(), 2))",
            "    Print(Length(ArrZeros()))",
            "    Rem an array compares structurally, so this was already",
            "    Rem true of a Def - the point is that it stays true",
            "    Print(ArrTable() = ArrTable())"))));
    }

    [Fact]
    public void ConstantOperatorExpressionsFoldToWhatTheRuntimeComputes()
    {
        using var ws = new Workspace();
        BuildMachine(ws.Machine("ops", J(
            "Let planck = 6.62607015 * (10 ^ (0 - 34))",
            "Let minusOne = 0 - 1",
            "Let joined = \"a\" & \"b\"",
            "Let third = 1 / 3",
            "Rem 'rem' would be a comment, so the binding is spelled out",
            "Let remainder = 7 Mod 3",
            "",
            "Def OpsPlanck() As Number",
            "    planck",
            "",
            "Def OpsMinusOne() As Number",
            "    minusOne",
            "",
            "Def OpsJoined() As String",
            "    joined",
            "",
            "Def OpsThird() As Number",
            "    third",
            "",
            "Def OpsRem() As Number",
            "    remainder")));

        // Each is compared against the same expression evaluated at run
        // time, so the fold cannot drift from the runtime's own Mod and ^.
        Assert.Equal("True\nTrue\nTrue\nTrue\nTrue\n", RunProgram(ws, ws.Program("use", J(
            "Include \"ops.shoddy\"",
            "",
            "Def Main()",
            "    Print(OpsPlanck() = 6.62607015 * (10 ^ (0 - 34)))",
            "    Print(OpsMinusOne() = 0 - 1)",
            "    Print(OpsJoined() = \"ab\")",
            "    Print(OpsThird() = 1 / 3)",
            "    Print(OpsRem() = 7 Mod 3)"))));
    }

    // ---- records, and the emission order that makes them possible -------

    [Fact]
    public void ARecordConstantReadsBackAndItsTypeIsEmittedFirst()
    {
        using var ws = new Workspace();
        string src = J(
            "Type Unit",
            "    UName  As String",
            "    Factr  As Number",
            "",
            "Let mile = Unit(\"MILE\", 1609.344)",
            "Rem arithmetic inside a constructor's arguments, which is how",
            "Rem eng's unit table is actually written",
            "Let knot = Unit(\"KNOT\", 1852 / 3600)",
            "",
            "Def RecMile() As Unit",
            "    mile",
            "",
            "Def RecKnot() As Unit",
            "    knot");
        string path = ws.Machine("rec", src);

        // R3.7: the TypeDef field must be declared before the constant
        // field that reads it, because C# runs static field initializers
        // in declaration order. Read off the generated source, because a
        // wrong order fails at class load with a NullReferenceException
        // that names nothing.
        var (prog, set) = Parse(path);
        string cs = Weaver.GenerateSource(prog, "Rec", deps: set.Machines, ownFile: path);
        int typeAt = cs.IndexOf("static readonly TypeDef", StringComparison.Ordinal);
        int constAt = cs.IndexOf("static readonly Value G_MILE", StringComparison.Ordinal);
        Assert.True(typeAt >= 0 && constAt > typeAt,
            "the type field must be emitted before the constant that names it");

        BuildMachine(path);
        Assert.Equal("MILE\nTrue\nTrue\n", RunProgram(ws, ws.Program("use", J(
            "Include \"rec.shoddy\"",
            "",
            "Def Main()",
            "    Print(UName(RecMile()))",
            "    Print(Factr(RecKnot()) = 1852 / 3600)",
            "    Rem a record compares field by field, on a type compared",
            "    Rem by reference - so this also proves both reads reach",
            "    Rem the same TypeDef",
            "    Print(RecMile() = RecMile())"))));
    }

    [Fact]
    public void AConstantMayHoldAPreludeTypeOrAnotherMachinesType()
    {
        using var ws = new Workspace();
        BuildMachine(ws.Machine("shape", J(
            "Type Point",
            "    Px As Number",
            "    Py As Number",
            "",
            "Def ShapeOrigin() As Point",
            "    Point(0, 0)")));
        // The cross-machine case: a constant whose type was declared in a
        // different machine, reached through its manifest. That is a read
        // of a static field on another class, which the CLR initializes
        // before the read - a different guarantee from R3.7's ordering.
        BuildMachine(ws.Machine("holder", J(
            "Include \"shape.shoddy\"",
            "",
            "Let corner = Point(3, 4)",
            "Let found = Some(7)",
            "Let missing = None()",
            "",
            "Def HoldCorner() As Point",
            "    corner",
            "",
            "Def HoldFound() As Option",
            "    found",
            "",
            "Def HoldMissing() As Option",
            "    missing")));

        Assert.Equal("3\nTrue\nTrue\n7\n", RunProgram(ws, ws.Program("use", J(
            "Include \"holder.shoddy\"",
            "Include \"shape.shoddy\"",
            "",
            "Def Main()",
            "    Print(Px(HoldCorner()))",
            "    Rem the machine's Point and the program's Point are the",
            "    Rem same TypeDef, or this record comparison is False",
            "    Print(HoldCorner() = Point(3, 4))",
            "    Print(HoldMissing() = None())",
            "    Select Case HoldFound()",
            "        Case Some(v)",
            "            Print(v)",
            "        Case Else",
            "            Print(\"NONE\")"))));
    }

    // ---- one value per process, shared between Engines ------------------

    [Fact]
    public void TheSameValueIsHandedToEveryEngineInTheProcess()
    {
        using var ws = new Workspace();
        string dll = BuildMachine(ws.Machine("shared", J(
            "Let xs = { \"A\", \"B\" }",
            "",
            "Def SharedXs() As List Of String",
            "    xs")));

        Assembly asm = Assembly.Load(File.ReadAllBytes(dll));
        MethodInfo m = asm.GetType("Shoddy.Machines.Shared")!.GetMethod("SHAREDXS")!;

        Value FromNewEngine()
        {
            var rt = new Engine(TextWriter.Null, TextReader.Null, Array.Empty<string>());
            m.Invoke(null, new object?[] { rt });
            return rt.Pop(0);
        }

        // The assertion the static-initializer choice rests on, and the
        // one that would catch a regression to per-Engine storage. The
        // conformance suite runs its goldens in-process, so two Engines in
        // one process is not a contrivance.
        Assert.Same(FromNewEngine(), FromNewEngine());
    }

    // ---- privacy: a Let declares a value, not a word --------------------

    [Fact]
    public void AMachineConstantIsNotVisibleToAProgramThatIncludesIt()
    {
        using var ws = new Workspace();
        BuildMachine(ws.Machine("priv", J(
            "Let secret = { \"A\" }",
            "",
            "Def PrivXs() As List Of String",
            "    secret")));

        string p = ws.Program("use", J(
            "Include \"priv.shoddy\"",
            "",
            "Def Main()",
            "    Let n = secret",
            "    Print(Length(n))"));
        var set = new MachineSet();
        List<Line> lines = Lexer.ReadProgram(p, set.TryResolve);
        var seeded = new ShoddyProgram();
        set.SeedInto(seeded);
        Lint.Result r = Lint.Run(Parser.Parse(lines, seeded), lines);
        // The ordinary unknown-word error, with nothing special said about
        // constants — because nothing about the constant crossed over. The
        // manifest carries Defs and Types and gained no third kind.
        Assert.Contains(r.UnknownWords, u => u.Msg.Contains("unknown word: SECRET"));
        Assert.True(seeded.ExternalDefs.ContainsKey("PRIVXS"));   // the Def did cross
    }

    [Fact]
    public void TwoMachinesMayDeclareAConstantOfTheSameName()
    {
        using var ws = new Workspace();
        BuildMachine(ws.Machine("one", J(
            "Let xs = { \"ONE\" }",
            "",
            "Def OneXs() As List Of String",
            "    xs")));
        BuildMachine(ws.Machine("two", J(
            "Let xs = { \"TWO\" }",
            "",
            "Def TwoXs() As List Of String",
            "    xs")));

        // Neither name leaves its file, so there is nothing to collide —
        // with a namespace and without one.
        Assert.Equal("ONE\nTWO\n", RunProgram(ws, ws.Program("use", J(
            "Include \"one.shoddy\"",
            "Include \"two.shoddy\" As B",
            "",
            "Def Main()",
            "    Print(Nth(OneXs(), 1))",
            "    Print(Nth(BTwoXs(), 1))"))));
    }

    // ---- the refusals, on their message text ----------------------------

    [Fact]
    public void AnInitializerThatCallsAWordIsRefusedByThatWordsName()
    {
        string m = Refusal(J(
            "Include \"seq.shoddy\"",
            "",
            "Let xs = { \"A\", \"B\" }",
            "Let ws = Map(xs, Upper)",
            "",
            "Def BadWs() As List Of String",
            "    ws"));
        Assert.Contains("must bind a constant", m);
        // The word, not the feature: Map is what has to go.
        Assert.Contains("calls MAP", m);
    }

    [Fact]
    public void AnInitializerThatCallsALocalDefIsRefusedTheSameWay()
    {
        string m = Refusal(J(
            "Def BadTen() As Number",
            "    10",
            "",
            "Let t = BadTen()",
            "",
            "Def BadT() As Number",
            "    t"));
        Assert.Contains("calls BADTEN", m);
    }

    [Fact]
    public void AQuotationIsRefusedByItsOwnMessageAndNeverAsCallsFn()
    {
        string m = Refusal(J(
            "Let f = Fn(x) => x + 1",
            "",
            "Def BadF() As Quotation",
            "    f"));
        Assert.Contains("cannot hold a quotation", m);
        // R2.4's complaint in a different costume: reporting this as
        // "calls FN" would name a word the writer never wrote.
        Assert.DoesNotContain("FN", m);
    }

    [Fact]
    public void AListOfQuotationsIsRefusedAsAQuotation()
    {
        string m = Refusal(J(
            "Let fs = { Fn(x) => x }",
            "",
            "Def BadFs() As List",
            "    fs"));
        Assert.Contains("cannot hold a quotation", m);
    }

    [Fact]
    public void TheFoldedWordsAreNeverReportedAsOffenders()
    {
        // The first person to write this must not be told their constant
        // "calls TOARRAY", which is true, useless, and contradicts the
        // documentation.
        Assert.Equal("", Refusal(J(
            "Let a = ToArray({ 1, 2, 3 })",
            "Let d = Dim(3, 0)",
            "",
            "Def FoldA() As Array Of Number",
            "    a",
            "",
            "Def FoldD() As Array Of Number",
            "    d")));
    }

    [Fact]
    public void AConstantDivisionByZeroIsRefusedAtCompileTimeNamingTheBinding()
    {
        string m = Refusal(J(
            "Let z = 1 / 0",
            "",
            "Def BadZ() As Number",
            "    z"));
        Assert.Contains("cannot be computed", m);
        Assert.Contains("'Z'", m);          // the binding, by name
        Assert.Contains("division by zero", m);
    }

    [Fact]
    public void AConstantUsedBeforeItIsBoundIsRefused()
    {
        string m = Refusal(J(
            "Let early = { later }",
            "Let later = 3",
            "",
            "Def BadE() As List",
            "    early"));
        Assert.Contains("cannot be computed", m);
        Assert.Contains("not bound until later", m);
    }

    // ---- the five lint passes that walk InitQuot (R3.6) -----------------
    //
    // All five were no-ops on machines only because prog.InitQuot was
    // always null there. The moment a machine can carry a Let, each one
    // runs on machine files for the first time, so each gets an assertion
    // rather than the benefit of the doubt.

    static Lint.Result LintMachine(Workspace ws, string name, string src)
    {
        string p = ws.Machine(name, src);
        var set = new MachineSet();
        List<Line> lines = Lexer.ReadProgram(p, set.TryResolve);
        var seeded = new ShoddyProgram();
        set.SeedInto(seeded);
        return Lint.Run(Parser.Parse(lines, seeded), lines);
    }

    [Fact]
    public void ACleanMachineConstantDrawsNoWarningFromAnyPass()
    {
        using var ws = new Workspace();
        Lint.Result r = LintMachine(ws, "quiet", J(
            "Type Unit",
            "    UName  As String",
            "    Factr  As Number",
            "",
            "Let names = { \"m\", \"kg\" }",
            "Let mile = Unit(\"MILE\", 1609.344)",
            "",
            "Def QuietNames() As List Of String",
            "    names",
            "",
            "Def QuietMile() As Unit",
            "    mile"));
        Assert.Empty(r.Warnings);
        // StackEffects walks the initializer too. A constant is (0 -> 1)
        // by construction, so the simulation is satisfied and no word in
        // it is unknown — confirmed rather than assumed.
        Assert.Empty(r.UnknownWords);
    }

    [Fact]
    public void AConstantShadowingAnAccessorWarnsInAMachineToo()
    {
        using var ws = new Workspace();
        Lint.Result r = LintMachine(ws, "shadow", J(
            "Type Unit",
            "    UName As String",
            "    Factr As Number",
            "",
            "Rem the params-shadow-accessors trap, at machine file scope",
            "Let factr = 3",
            "",
            "Def ShadowFactr() As Number",
            "    factr",
            "",
            "Def ShadowOf(u As Unit) As Number",
            "    Factr(u)"));
        // The pass that must fire, proven firing: a root Let shadows a
        // field accessor exactly the way a parameter does, and the fix is
        // to rename the constant, never to silence the lint.
        Assert.Contains(r.Warnings, w => w.Contains("FACTR") || w.Contains("Factr"));
    }

    [Fact]
    public void AMachineConstantAddsNothingToTheUsedWordSet()
    {
        using var ws = new Workspace();
        // A machine of this test's own, built as a DLL because that pass
        // reads the machine surface rather than spliced source. Not one of
        // the standard machines: assemblies load by name into one context,
        // so a Shoddy.Machines.Seq built by another test in the same run
        // would answer this include instead.
        BuildMachine(ws.Machine("spare", J(
            "Def SpareOne() As Number",
            "    1")));
        // UsedWords feeds UnusedMachineIncludes. A constant cannot call a
        // word, so it cannot make an unused include look used — the
        // warning still fires with a constant sitting beside it.
        Lint.Result r = LintMachine(ws, "unused", J(
            "Include \"spare.shoddy\"",
            "",
            "Let names = { \"m\", \"kg\" }",
            "",
            "Def UnusedNames() As List Of String",
            "    names"));
        Assert.Contains(r.Warnings, w => w.Contains("spare.shoddy") && w.Contains("no word or type"));
    }

    [Fact]
    public void TheNamespacedLetWarningCannotReachAMachinesOwnConstants()
    {
        using var ws = new Workspace();
        // The pass warns only where a bare spelling is claimed by more
        // than one namespace, and a machine's own file is never included
        // under one — QualOf is null for it — so the broken resolver path
        // it guards cannot arise for a machine constant.
        Lint.Result r = LintMachine(ws, "nsfree", J(
            "Let names = { \"m\" }",
            "",
            "Def NsNames() As List Of String",
            "    names"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("namespaced include"));
    }

    // ---- the debugger (R3.8) --------------------------------------------

    [Fact]
    public void AMachineConstantIsVisibleInTheDebuggersGlobals()
    {
        // The perch always splices machine SOURCES — no machine DLLs, so
        // every line is steppable — which means a machine's constant is
        // bound by the program's own init phase and appears in Globals
        // through the rt.BindGlobal already emitted there. That is why no
        // registration hook is emitted for a machine weave: debug and
        // machineClass never co-occur.
        string dir = Path.Combine(Path.GetTempPath(), "shoddy-const-dbg", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "held.shoddy"), J(
            "Let unitNames = { \"m\", \"kg\" }",
            "",
            "Def HeldNames() As List Of String",
            "    unitNames"));
        string p = Path.Combine(dir, "prog.shoddy");
        File.WriteAllText(p, J(
            "Include \"held.shoddy\"",
            "",
            "Def Main()",
            "    Print(Length(HeldNames()))"));

        var sink = new GlobalsProbe();
        Engine.PendingSink = sink;
        try
        {
            var entry = Weaver.LoadDebug(Parser.Parse(Lexer.ReadProgram(p)));
            entry(TextWriter.Null, TextReader.Null);
        }
        finally { Engine.PendingSink = null; }

        Assert.Contains("UNITNAMES", sink.Names);
    }

    sealed class GlobalsProbe : IDebugSink
    {
        public readonly HashSet<string> Names = new();
        public void OnLine(Engine rt)
        {
            foreach ((string n, Value _) in rt.Globals) Names.Add(n);
        }
    }

    [Fact]
    public void ANonDebugMachineWeaveEmitsNoDebugRegistration()
    {
        using var ws = new Workspace();
        string path = ws.Machine("plain", J(
            "Let names = { \"m\" }",
            "",
            "Def PlainNames() As List Of String",
            "    names"));
        var (prog, set) = Parse(path);
        string cs = Weaver.GenerateSource(prog, "Plain", deps: set.Machines, ownFile: path);
        Assert.DoesNotContain("BindGlobal", cs);
        Assert.DoesNotContain("rt.Dbg", cs);
        // And the constant is a shared static, not per-Engine storage.
        Assert.Contains("static readonly Value G_NAMES =", cs);
    }

    // ---- programs are untouched (R1.4, D5) ------------------------------

    [Fact]
    public void AMillsTopLevelLetMayStillCallAnything()
    {
        using var ws = new Workspace();
        ws.Standard("seq");
        // Exactly what D5 keeps legal in a program and refuses in a
        // machine, so the narrower rule cannot have leaked outwards.
        Assert.Equal("2\n", RunProgram(ws, ws.Program("mill", J(
            "Include \"seq.shoddy\"",
            "",
            "Def Twice(s As String) As String",
            "    s & s",
            "",
            "Let shouts = Map({ \"a\", \"b\" }, Twice)",
            "",
            "Def Main()",
            "    Print(Length(shouts))"))));
    }
}
