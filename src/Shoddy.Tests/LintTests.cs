// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// The linter, fixture by fixture: every defect in the catalog that drove
/// its design warns (or errors), and the idioms it must never cry wolf on
/// stay silent. Programs are strings written to temp files; machine-free
/// except where the collision fixture needs a real machine surface.
/// </summary>
[Collection("golden")]
public class LintTests
{
    static readonly string Root = RepoRoot.Dir;

    static Lint.Result LintSrc(string src, bool machines = false)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"shoddy-lint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string f = Path.Combine(dir, "fixture.shoddy");
            File.WriteAllText(f, src);
            return LintFile(f, machines);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    static Lint.Result LintFile(string path, bool machines)
    {
        var set = new MachineSet();
        List<Line> lines = machines
            ? Lexer.ReadProgram(path, set.TryResolve)
            : Lexer.ReadProgram(path);
        var prog = new ShoddyProgram();
        set.SeedInto(prog);
        ShoddyProgram parsed = Parser.Parse(lines, prog);
        return Lint.Run(parsed, lines);
    }

    static string J(params string[] lines) => string.Join('\n', lines) + "\n";

    // ---- defect 3: a Let shadows an accessor the program reads ----------

    [Fact]
    public void LetShadowingAUsedAccessorWarns()
    {
        Lint.Result r = LintSrc(J(
            "Type Census",
            "    Aloft As Number",
            "    Landed As Number",
            "",
            "Def Show(c As Census)",
            "    Print(Landed(c))",
            "",
            "Def Bad(f As Number) As Number",
            "    Let landed = f + 1",
            "    landed",
            "",
            "Def Main()",
            "    Print(Bad(1))",
            "    Show(Census(1, 2))"));
        Assert.Contains(r.Warnings, w => w.Contains("LANDED") && w.Contains("shadows"));
    }

    [Fact]
    public void ParameterNamedAfterAFieldStaysSilent()
    {
        // Params named after fields are the codebase idiom; misuse inside
        // such a scope is the stack checker's job, not a shadow warning's.
        Lint.Result r = LintSrc(J(
            "Type Census",
            "    Aloft As Number",
            "    Landed As Number",
            "",
            "Def Show(c As Census)",
            "    Print(Landed(c))",
            "",
            "Def Fine(landed As Number) As Number",
            "    landed + 1",
            "",
            "Def Main()",
            "    Print(Fine(1))",
            "    Show(Census(1, 2))"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("shadows"));
    }

    [Fact]
    public void ShadowOfAnAccessorNobodyReadsStaysSilent()
    {
        Lint.Result r = LintSrc(J(
            "Type Census",
            "    Aloft As Number",
            "    Landed As Number",
            "",
            "Def Bad(f As Number) As Number",
            "    Let aloft = f + 1",
            "    aloft",
            "",
            "Def Main()",
            "    Print(Bad(1))",
            "    Print(Aloft(Census(5, 6)) * 0)"));
        // ALOFT is read, so shadowing it warns; LANDED is never read, so a
        // shadow of LANDED would not. Prove the used-filter both ways.
        Assert.Contains(r.Warnings, w => w.Contains("ALOFT") && w.Contains("shadows"));
        Lint.Result r2 = LintSrc(J(
            "Type Census",
            "    Aloft As Number",
            "    Landed As Number",
            "",
            "Def Bad(f As Number) As Number",
            "    Let landed = f + 1",
            "    landed",
            "",
            "Def Main()",
            "    Print(Bad(1))",
            "    Print(Aloft(Census(5, 6)))"));
        Assert.DoesNotContain(r2.Warnings, w => w.Contains("LANDED"));
    }

    // ---- defect 8: a machine word collides with a field accessor --------

    [Fact]
    public void MachineWordCollidingWithAMentionedAccessorWarns()
    {
        // The str/mungo incident: a machine exports ToFixed, a record
        // declares a ToFixed field, and every mention resolves to the
        // word. Seed the machine surface directly — loading a real DLL
        // here would collide with the golden suites' own loads.
        string dir = Path.Combine(Path.GetTempPath(), $"shoddy-lint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string f = Path.Combine(dir, "fixture.shoddy");
            File.WriteAllText(f, J(
                "Type Thing",
                "    ToFixed As Number",
                "    Other As Number",
                "",
                "Def Use(t As Thing) As Number",
                "    Other(t)",
                "",
                "Def Main()",
                "    Print(ToFixed(1.5, 2))",
                "    Print(Use(Thing(1, 2)))"));
            List<Line> lines = Lexer.ReadProgram(f);
            var prog = new ShoddyProgram();
            prog.ExternalDefs["TOFIXED"] = "Shoddy.Machines.Str.TOFIXED";
            prog.ExternalEffects["TOFIXED"] = (2, 1);
            prog.NoteBare("TOFIXED", "TOFIXED");
            ShoddyProgram parsed = Parser.Parse(lines, prog);
            Lint.Result r = Lint.Run(parsed, lines);
            Assert.Contains(r.Warnings,
                w => w.Contains("TOFIXED") && w.Contains("collides"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- defect 4: unknown words are errors -----------------------------

    [Fact]
    public void UnknownWordIsAnError()
    {
        Lint.Result r = LintSrc(J(
            "Def Main()",
            "    Plugh(1)"));
        Assert.Contains(r.UnknownWords, u => u.Msg.Contains("PLUGH"));
    }

    // ---- defect 7: auto-quoted typo in argument position ----------------

    [Fact]
    public void AutoQuotedTypoWarnsButIsNotAnError()
    {
        Lint.Result r = LintSrc(J(
            "Def Main()",
            "    Print(Map({ 1, 2 }, Dubble))"));
        Assert.Contains(r.Warnings, w => w.Contains("DUBBLE") && w.Contains("function value"));
        Assert.Empty(r.UnknownWords);
    }

    [Fact]
    public void FnParametersInsideQuotationsAreNotTypos()
    {
        Lint.Result r = LintSrc(J(
            "Def Main()",
            "    Print(Fold({ 1, 2, 3 }, 0, Fn(a, x) => a + x))"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("function value"));
    }

    // ---- defect 6: branch disagreement ----------------------------------

    [Fact]
    public void DisagreeingBranchesWarn()
    {
        Lint.Result r = LintSrc(J(
            "Def F(b As Boolean) As Number",
            "    If b Then",
            "        1",
            "    Else",
            "        Print(\"X\")",
            "",
            "Def Main()",
            "    Print(F(True))"));
        Assert.Contains(r.Warnings, w => w.Contains("branches of this IF disagree"));
    }

    [Fact]
    public void ExhaustiveSelectWithoutCaseElseStaysSilent()
    {
        Lint.Result r = LintSrc(J(
            "Type Shape = Circle(R) | Square(S)",
            "",
            "Def Area(s As Shape) As Number",
            "    Select Case s",
            "        Case Circle(r)",
            "            r * r * 3",
            "        Case Square(w)",
            "            w * w",
            "",
            "Def Main()",
            "    Print(Area(Circle(2)))"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("disagree"));
    }

    [Fact]
    public void ErrorDivergingBranchStaysSilent()
    {
        // The Split/Bool idiom: one branch yields the value, the other
        // stops the program. ERROR never returns, so the nets agree.
        Lint.Result r = LintSrc(J(
            "Def Strict(s As String) As Number",
            "    If IsNumeric(s) Then",
            "        Val(s)",
            "    Else",
            "        Error(\"NOT A NUMBER\")",
            "",
            "Def Main()",
            "    Print(Strict(\"3\"))"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("disagree"));
        Assert.Empty(r.UnknownWords);
    }

    // ---- defect 5 (net effect): stray values ----------------------------

    [Fact]
    public void StrayValuesWarn()
    {
        Lint.Result r = LintSrc(J(
            "Def Two() As Number",
            "    1",
            "    2",
            "",
            "Def Main()",
            "    Print(Two())"));
        Assert.Contains(r.Warnings, w => w.Contains("leaves 2 values"));
    }

    // ---- defect 5 (direct): wrapped infix continuation ------------------

    [Fact]
    public void LineBeginningWithAndWarns()
    {
        Lint.Result r = LintSrc(J(
            "Def F(a As Number, b As Number) As Boolean",
            "    Let ok = a > 0",
            "    And(b > 0)",
            "    ok",
            "",
            "Def Main()",
            "    Print(F(1, 2))"));
        Assert.Contains(r.Warnings, w => w.Contains("begins with 'AND'"));
    }

    // ---- defects 1 and 2: operator sections in value position -----------

    [Fact]
    public void SectionFeedingANumericSlotWarns()
    {
        // The NormPdf shape, caught live in stats.shoddy: unary minus
        // before parens is a section of binary minus.
        Lint.Result r = LintSrc(J(
            "Def F(z As Number) As Number",
            "    Exp(-(z * z) / 2)",
            "",
            "Def Main()",
            "    Print(F(1))"));
        Assert.Contains(r.Warnings, w => w.Contains("section"));
    }

    // ---- defect 9: literal against sum constructors ---------------------

    [Fact]
    public void NumberLiteralPassedToASumScrutinizingDefWarns()
    {
        Lint.Result r = LintSrc(J(
            "Type Knob = AKnob | BKnob",
            "",
            "Def Rail(k As Knob) As Number",
            "    Select Case k",
            "        Case AKnob",
            "            1",
            "        Case Else",
            "            2",
            "",
            "Def Main()",
            "    Print(Rail(7))"));
        Assert.Contains(r.Warnings, w => w.Contains("falls to Case Else"));
    }

    [Fact]
    public void ConstructorPassedToASumScrutinizingDefStaysSilent()
    {
        Lint.Result r = LintSrc(J(
            "Type Knob = AKnob | BKnob",
            "",
            "Def Rail(k As Knob) As Number",
            "    Select Case k",
            "        Case AKnob",
            "            1",
            "        Case Else",
            "            2",
            "",
            "Def Main()",
            "    Print(Rail(AKnob()))"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("falls to Case Else"));
    }

    // A Def that turns a Number INTO a sum type is not a Def that matches
    // its parameter against one. The warning used to fire on all three call
    // sites below while the program printed Cold(), Mild(), Warm() - three
    // different branches - so the claim "always falls to Case Else" was
    // refuted by the program's own output. Found by the apprenticeship
    // curriculum against its Phase 5 capstone.

    [Fact]
    public void ClassifierReturningNullaryVariantsDoesNotWarn()
    {
        Lint.Result r = LintSrc(J(
            "Type Band = Cold | Mild | Warm",
            "",
            "Def Classify(t As Number) As Band",
            "    Select Case t",
            "        Case Is < 10",
            "            Cold()",
            "        Case 10 To 20",
            "            Mild()",
            "        Case Else",
            "            Warm()",
            "",
            "Def Main()",
            "    Print(Classify(5))",
            "    Print(Classify(15))",
            "    Print(Classify(30))"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("falls to Case Else"));
    }

    [Fact]
    public void ClassifierReturningStringsDoesNotWarn()
    {
        Lint.Result r = LintSrc(J(
            "Type Band = Cold | Mild | Warm",
            "",
            "Def Pick(t As Number) As String",
            "    Select Case t",
            "        Case Is < 10",
            "            \"COLD\"",
            "        Case Else",
            "            \"WARM\"",
            "",
            "Def Main()",
            "    Print(Pick(5))"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("falls to Case Else"));
    }

    [Fact]
    public void ClassifierReturningNonNullaryVariantsDoesNotWarn()
    {
        Lint.Result r = LintSrc(J(
            "Type Band = Cold(Deg As Number) | Warm(Deg As Number)",
            "",
            "Def Classify(t As Number) As Band",
            "    Select Case t",
            "        Case Is < 10",
            "            Cold(t)",
            "        Case Else",
            "            Warm(t)",
            "",
            "Def Main()",
            "    Print(Classify(5))"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("falls to Case Else"));
    }

    // The other side of the fix: a constructor in a CASE PATTERN is still
    // evidence, so the true positive above must keep firing. A change that
    // silences this one has thrown the rule away rather than corrected it.
    [Fact]
    public void StringLiteralPassedToASumScrutinizingDefStillWarns()
    {
        Lint.Result r = LintSrc(J(
            "Type Knob = AKnob | BKnob",
            "",
            "Def Rail(k As Knob) As Number",
            "    Select Case k",
            "        Case AKnob",
            "            1",
            "        Case Else",
            "            2",
            "",
            "Def Main()",
            "    Print(Rail(\"seven\"))"));
        Assert.Contains(r.Warnings, w => w.Contains("falls to Case Else"));
    }

    // ---- defect 10: top-level Let in a namespaced include ---------------

    [Fact]
    public void NamespacedLetSharingItsBareNameWarns()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"shoddy-lint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "one.shoddy"), J(
                "Let shared = 1"));
            File.WriteAllText(Path.Combine(dir, "two.shoddy"), J(
                "Let shared = 2"));
            string main = Path.Combine(dir, "main.shoddy");
            File.WriteAllText(main, J(
                "Include \"one.shoddy\" As P",
                "Include \"two.shoddy\" As Q",
                "",
                "Def Main()",
                "    Print(1)"));
            Lint.Result r = LintFile(main, machines: false);
            Assert.Contains(r.Warnings, w => w.Contains("SHARED") &&
                                             w.Contains("namespaced include"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- the unused-include check, and the two ways it used to lie ------

    // A machine reached ONLY by matching on its sum type. `Case LidOpen`
    // parses to an NType.Pat carrying the name, never a Word node, so
    // walking the bodies for Words found nothing and the check called the
    // Include unused. It is not, and the proof is that taking the Include
    // away fails the weave with "unknown word" on the constructor —
    // machines/seeds/seedhtml.shoddy is the real file of this shape, which
    // names nothing from xml.shoddy but `XOk`.
    [Fact]
    public void AMachineUsedOnlyInACasePatternIsNotUnused()
    {
        Lint.Result r = LintAgainst("lidp", J(
            "Def Shut(l As Lid) As Number",
            "    Select Case l",
            "        Case LidOpen(w)",
            "            w",
            "        Case Else",
            "            1",
            "",
            "Def Main()",
            "    Print(1)"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("no word or type from it is used"));
    }

    // The near miss that looks like the same bug and is NOT one. A type
    // named only in an `As` clause does not keep its Include alive, because
    // the parser skips annotations outright — `Def Weigh(l As Lid)` compiles
    // and runs with no Include at all, the annotation being documentation
    // the weave never resolves. So this warning is telling the truth and
    // must keep firing: widening the check to count `As` clauses would
    // silence it, which would be a regression dressed as a fix.
    [Fact]
    public void AMachineNamedOnlyInATypeAnnotationIsStillUnused()
    {
        Lint.Result r = LintAgainst("lidt", J(
            "Def Weigh(l As Lid) As Number",
            "    1",
            "",
            "Def Main()",
            "    Print(Weigh(1))"));
        Assert.Contains(r.Warnings, w => w.Contains("no word or type from it is used"));
    }

    // The true positive, which is the whole value of the check: teaching it
    // to see Case patterns must not have made it toothless.
    [Fact]
    public void AMachineNamedNowhereAtAllStillWarns()
    {
        Lint.Result r = LintAgainst("lidn", J(
            "Def Main()",
            "    Print(1)"));
        Assert.Contains(r.Warnings, w => w.Contains("no word or type from it is used"));
    }

    /// <summary>A one-type machine to include: a sum type, so it can be
    /// matched on, and a Def, so the machine contributes a word as well and
    /// the check has something to miss.
    ///
    /// LidOpen CARRIES A FIELD, and that is not decoration. Pat.Type is set
    /// only for a constructor followed by `(` — a nullary `Case LidShut`
    /// parses as an ordinary Word node and was always counted, so a fixture
    /// built on one passes whether the Pat walk exists or not. The bug only
    /// shows through a constructor with arguments, which is what `XOk(tree)`
    /// is in the file that exposed it.</summary>
    const string LidSource =
        "Type Lid = LidOpen(Width As Number) | LidShut\n" +
        "\n" +
        "Def LidName(l As Lid) As String\n" +
        "    \"LID\"\n";

    /// <summary>Builds a throwaway machine named for the calling test and
    /// lints a program that includes it. Each test needs its OWN machine
    /// name: assemblies load by path but resolve by simple name, so two
    /// tests sharing one name in a single process is a load conflict rather
    /// than a fresh build — MachineTests weaves the whole standard library
    /// into a temp workspace, which is why including a real machine here
    /// dies with "Assembly with same name is already loaded" instead.</summary>
    static Lint.Result LintAgainst(string machineName, string src)
    {
        string dir = Path.Combine(Path.GetTempPath(), "shoddy-lint-inc",
                                  Guid.NewGuid().ToString("N"), "machines");
        Directory.CreateDirectory(dir);
        string sb = Path.Combine(dir, machineName + ".shoddy");
        File.WriteAllText(sb, LidSource);

        var built = new MachineSet();
        ShoddyProgram mprog = Parser.Parse(Lexer.ReadProgram(sb, built.TryResolve));
        Weaver.WeaveMachine(mprog, sb, built.Machines);

        string f = Path.Combine(dir, "fixture.shoddy");
        File.WriteAllText(f, $"Include \"{machineName}.shoddy\"\n\n" + src);
        return LintFile(f, machines: true);
    }

    // ---- the effects table covers every builtin -------------------------

    [Fact]
    public void EveryBuiltinHasADeclaredEffect()
    {
        var missing = Engine.BuiltinWords
            .Where(w => !Effects.Builtin.ContainsKey(w)).ToList();
        Assert.True(missing.Count == 0,
            "builtins without a stack effect: " + string.Join(" ", missing));
        var extra = Effects.Builtin.Keys
            .Where(w => !Engine.BuiltinWords.Contains(w)).ToList();
        Assert.True(extra.Count == 0,
            "effects for non-builtins: " + string.Join(" ", extra));
    }

    // ---- geo-build-issues 1-3: binders shadowing builtins ---------------

    [Fact]
    public void CallingABuiltinThroughItsOwnShadowWarns()
    {
        // The geo hang: parameter `rest` IS the Rest builtin inside its
        // own body, so Rest(rest) answers the list unchanged and the
        // walker never advances. The call site is the live wound.
        Lint.Result r = LintSrc(J(
            "Def Walk(rest As List Of Number) As Number",
            "    If IsEmpty(rest) Then",
            "        0",
            "    Else",
            "        1 + Walk(Rest(rest))",
            "",
            "Def Main()",
            "    Print(Walk({ 1, 2, 3 }))"));
        Assert.Contains(r.Warnings,
            w => w.Contains("REST") && w.Contains("local") && w.Contains("call"));
    }

    [Fact]
    public void LatentLetShadowOfABuiltinIsAVerboseNoteNotAWarning()
    {
        // Found beside the hang, never fired: Let len against the Len
        // builtin. Dormant shadows number ~99 across the healthy standard
        // library (rest, args, mid ARE the recursion idiom), and a clean
        // build prints nothing — so the binder note waits behind
        // --lint-verbose, and the day the scope calls the shadowed name
        // the call-shaped warning fires at the exact line.
        Lint.Result r = LintSrc(J(
            "Def Wide(s As String) As Number",
            "    Let len = 3",
            "    len + 1",
            "",
            "Def Main()",
            "    Print(Wide(\"X\"))"));
        Assert.Contains(r.Verbose,
            w => w.Contains("LEN") && w.Contains("shadows the builtin"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("shadows the builtin"));
    }

    [Fact]
    public void LatentParameterShadowOfABuiltinIsAVerboseNote()
    {
        Lint.Result r = LintSrc(J(
            "Def Area(sqr As Number) As Number",
            "    sqr * 2",
            "",
            "Def Main()",
            "    Print(Area(3))"));
        Assert.Contains(r.Verbose,
            w => w.Contains("SQR") && w.Contains("shadows the builtin"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("shadows the builtin"));
    }

    [Fact]
    public void ParameterShadowingAMachineWordWarns()
    {
        // The third of the trio: a parameter `e` against math's E().
        // Seed the machine surface directly, as the collision test does.
        string dir = Path.Combine(Path.GetTempPath(), $"shoddy-lint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string f = Path.Combine(dir, "fixture.shoddy");
            File.WriteAllText(f, J(
                "Def Grow(e As Number) As Number",
                "    e * 2",
                "",
                "Def Main()",
                "    Print(Grow(1))"));
            List<Line> lines = Lexer.ReadProgram(f);
            var prog = new ShoddyProgram();
            prog.ExternalDefs["E"] = "Shoddy.Machines.Math.E";
            prog.ExternalEffects["E"] = (0, 1);
            prog.NoteBare("E", "E");
            ShoddyProgram parsed = Parser.Parse(lines, prog);
            Lint.Result r = Lint.Run(parsed, lines);
            Assert.Contains(r.Verbose,
                w => w.Contains("'E'") && w.Contains("machine word"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void OrdinaryParameterNamesStaySilent()
    {
        Lint.Result r = LintSrc(J(
            "Def Blend(x As Number, n As Number) As Number",
            "    x + n",
            "",
            "Def Main()",
            "    Print(Blend(1, 2))"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("shadows the builtin"));
        Assert.Empty(r.Verbose);
    }

    // ---- geo-build-issues 5: written arity vs the callee's pops ---------

    [Fact]
    public void ACallWritingMoreArgumentsThanTheDefTakesWarns()
    {
        // The misplaced-paren shape: two arguments reach a one-argument
        // Def, it compiles, and the wrong value binds — the failure then
        // surfaces inside an unrelated machine.
        Lint.Result r = LintSrc(J(
            "Def Inc(n As Number) As Number",
            "    n + 1",
            "",
            "Def Main()",
            "    Print(Inc(1, 2))"));
        Assert.Contains(r.Warnings,
            w => w.Contains("INC") && w.Contains("argument"));
    }

    [Fact]
    public void UnderSupplyOnAnEmptyStackWarnsThroughTheDepthCheck()
    {
        // Under-supply is NOT an arity warning: Map(Fst) after a value-
        // producing line is the point-free pipeline idiom. When the stack
        // genuinely has too little, the existing depth check names it.
        Lint.Result r = LintSrc(J(
            "Def Add2(a As Number, b As Number) As Number",
            "    a + b",
            "",
            "Def Use(n As Number) As Number",
            "    Add2(n)",
            "",
            "Def Main()",
            "    Print(Use(1))"));
        Assert.Contains(r.Warnings, w => w.Contains("below its own parameters"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("argument"));
    }

    [Fact]
    public void PointFreePipelineUnderSupplyStaysSilent()
    {
        // json.shoddy's JKeys: the members, mapped through Fst — one
        // written argument to a two-pop word, fed from the line above.
        Lint.Result r = LintSrc(J(
            "Def Heads(xs As List Of List Of Number) As List Of Number",
            "    Map(xs, First)",
            "",
            "Def Tops(xs As List Of List Of Number) As List Of Number",
            "    xs",
            "        Map(First)",
            "",
            "Def Main()",
            "    Print(First(Heads({ { 1, 2 } })))",
            "    Print(First(Tops({ { 3, 4 } })))"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("argument"));
    }

    [Fact]
    public void ABuiltinCalledWithTheWrongArityWarns()
    {
        Lint.Result r = LintSrc(J(
            "Def Main()",
            "    Print(Abs(1, 2))"));
        Assert.Contains(r.Warnings,
            w => w.Contains("ABS") && w.Contains("argument"));
    }

    [Fact]
    public void MatchingArityAndHigherOrderCallsStaySilent()
    {
        Lint.Result r = LintSrc(J(
            "Def Dub(n As Number) As Number",
            "    n * 2",
            "",
            "Def Main()",
            "    Print(Abs(0 - 3))",
            "    Print(First(Map({ 1, 2 }, Dub)))",
            "    Print(Ifte(1 < 2, 10, 20))"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("argument"));
    }

    // ---- clean programs stay clean --------------------------------------

    [Fact]
    public void IdiomaticProgramsProduceNoDiagnostics()
    {
        Lint.Result r = LintSrc(J(
            "Type Pair2",
            "    Fst2 As Number",
            "    Snd2 As Number",
            "",
            "Rem statements then expression; a void If; a recursive pair",
            "Def Countdown(n As Number)",
            "    If n > 0 Then",
            "        Print(n)",
            "        Countdown(n - 1)",
            "",
            "Def Even(n As Number) As Boolean",
            "    If n = 0 Then",
            "        True",
            "    Else",
            "        Odd(n - 1)",
            "",
            "Def Odd(n As Number) As Boolean",
            "    If n = 0 Then",
            "        False",
            "    Else",
            "        Even(n - 1)",
            "",
            "Def Main()",
            "    Countdown(3)",
            "    Print(Even(4))",
            "    Print(Fst2(Pair2(1, 2)) + Snd2(Pair2(3, 4)))"));
        Assert.Empty(r.Warnings);
        Assert.Empty(r.UnknownWords);
        Assert.Equal(r.DefsTotal, r.DefsChecked);
    }
}
