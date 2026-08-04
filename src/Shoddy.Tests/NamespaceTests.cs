// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// INCLUDE ... AS and the IN qualifier.
///
/// Shoddy has one namespace: Include splices, so every Def, Type, variant,
/// and field accessor in every included file lands in a single flat table.
/// A namespace pastes a prefix onto the names a file declares, which is
/// exactly what the mungo-caverns port had been doing by hand (its generator
/// emitted `"Def Msg" & Pascal(name)` as a string) — these pin the compiler
/// doing it instead.
///
/// The rule under test throughout: qualification is never required at the
/// use site. A bare word resolves while its meaning is unambiguous, and
/// only a genuine collision between two namespaces forces an explicit IN.
/// </summary>
public class NamespaceTests
{
    [Fact]
    public void BareNameReachesIntoANamespace()
    {
        Assert.Equal("hello\n", RunFiles(
            ("lib.shoddy", "Def Greet() As String\n    \"hello\"\n"),
            ("prog.shoddy", "Include \"lib.shoddy\" As Lib\n\nDef Main()\n    Print(Greet())\n")));
    }

    [Fact]
    public void PrefixedSpellingReachesTheSameName()
    {
        // The whole point of pasting rather than scoping: a file included
        // AS Msg declares exactly the names a hand-written Msg prefix did,
        // so existing call sites keep working untouched.
        Assert.Equal("hello\n", RunFiles(
            ("lib.shoddy", "Def Greet() As String\n    \"hello\"\n"),
            ("prog.shoddy", "Include \"lib.shoddy\" As Lib\n\nDef Main()\n    Print(LibGreet())\n")));
    }

    [Fact]
    public void CollidingNamespacesAreBothReachable()
    {
        Assert.Equal("1,2\n", RunFiles(
            ("a.shoddy", "Def Hints() As Number\n    1\n"),
            ("b.shoddy", "Def Hints() As Number\n    2\n"),
            ("prog.shoddy",
             "Include \"a.shoddy\" As Tbl\nInclude \"b.shoddy\" As Hint\n\n" +
             "Def Main()\n    Print(Str(Hints In Tbl()) & \",\" & Str(Hints In Hint()))\n")));
    }

    [Fact]
    public void AmbiguousBareNameIsRefused()
    {
        string err = RunFilesError(
            ("a.shoddy", "Def Hints() As Number\n    1\n"),
            ("b.shoddy", "Def Hints() As Number\n    2\n"),
            ("prog.shoddy",
             "Include \"a.shoddy\" As Tbl\nInclude \"b.shoddy\" As Hint\n\n" +
             "Def Main()\n    Print(Str(Hints()))\n"));
        Assert.Contains("ambiguous", err);
        Assert.Contains("TBLHINTS", err);
        Assert.Contains("HINTHINTS", err);
    }

    [Fact]
    public void AFileOwnNamesWinOverAnotherNamespace()
    {
        // Two namespaces share the helper name TWICE. Neither file has to
        // qualify its own call, or qualifying any file would break every
        // other file that happens to use the same internal helper name.
        Assert.Equal("aa|bbbb\n", RunFiles(
            ("a.shoddy",
             "Def Twice(s As String) As String\n    s & s\n\n" +
             "Def Go() As String\n    Twice(\"a\")\n"),
            ("b.shoddy",
             "Def Twice(s As String) As String\n    s & s & s & s\n\n" +
             "Def Go() As String\n    Twice(\"b\")\n"),
            ("prog.shoddy",
             "Include \"a.shoddy\" As A\nInclude \"b.shoddy\" As B\n\n" +
             "Def Main()\n    Print(Go In A() & \"|\" & Go In B())\n")));
    }

    [Fact]
    public void VariantsAndPatternsAreNamespaced()
    {
        // Same union declared twice. Construction and matching must both
        // discriminate, or a Case would catch the other namespace's value.
        Assert.Equal("core:dark\nalt:light\nother\n", RunFiles(
            ("a.shoddy", "Type Fate = Playing | Died(Msg) | Won\n"),
            ("b.shoddy", "Type Fate = Playing | Died(Msg) | Won\n"),
            ("prog.shoddy",
             "Include \"a.shoddy\" As Core\nInclude \"b.shoddy\" As Alt\n\n" +
             "Def Describe(f As Fate) As String\n" +
             "    Select Case f\n" +
             "        Case Died In Core (m)\n            \"core:\" & m\n" +
             "        Case Died In Alt (m)\n            \"alt:\" & m\n" +
             "        Case Else\n            \"other\"\n\n" +
             "Def Main()\n" +
             "    Print(Describe(Died In Core (\"dark\")))\n" +
             "    Print(Describe(Died In Alt (\"light\")))\n" +
             "    Print(Describe(Won In Core))\n")));
    }

    [Fact]
    public void QualifiedAccessorSurvivesAShadowingParameter()
    {
        // The params-shadow-accessors failure: a parameter named `key`
        // outranks the accessor `Key`, silently. A namespaced accessor is
        // spelled in a way no local can be, so it stays reachable.
        Assert.Equal("k1/SHADOW\n", RunFiles(
            ("lib.shoddy", "Type Anchor\n    Key  As String\n    Text As String\n"),
            ("prog.shoddy",
             "Include \"lib.shoddy\" As Vocab\n\n" +
             "Def Probe(a As Anchor In Vocab, key As String) As String\n" +
             "    Key In Vocab (a) & \"/\" & key\n\n" +
             "Def Main()\n    Print(Probe(Anchor In Vocab (\"k1\", \"t1\"), \"SHADOW\"))\n")));
    }

    [Fact]
    public void WithIsUnaffectedByNamespaces()
    {
        // WITH names fields directly rather than resolving them as words,
        // so a namespaced type is still updated with the bare field name.
        Assert.Equal("k1/changed\n", RunFiles(
            ("lib.shoddy", "Type Anchor\n    Key  As String\n    Text As String\n"),
            ("prog.shoddy",
             "Include \"lib.shoddy\" As Vocab\n\n" +
             "Def Main()\n" +
             "    Let a = Anchor In Vocab (\"k1\", \"t1\")\n" +
             "    Let b = With(a, Text = \"changed\")\n" +
             "    Print(Key In Vocab (b) & \"/\" & Text In Vocab (b))\n")));
    }

    [Fact]
    public void OneFileTwoNamespacesIsRefused()
    {
        string err = RunFilesError(
            ("lib.shoddy", "Def Greet() As String\n    \"hi\"\n"),
            ("prog.shoddy",
             "Include \"lib.shoddy\" As A\nInclude \"lib.shoddy\" As B\n\n" +
             "Def Main()\n    Print(\"x\")\n"));
        Assert.Contains("included twice under different namespaces", err);
    }

    [Fact]
    public void InnerNamespaceWinsOverOuter()
    {
        // Qualifiers do not compose through a chain of includes: LEAF is
        // declared LEAFONLY, not MIDLEAFONLY, so a name never depends on
        // who included the includer.
        Assert.Equal("leaf\n", RunFiles(
            ("leaf.shoddy", "Def Only() As String\n    \"leaf\"\n"),
            ("mid.shoddy", "Include \"leaf.shoddy\" As Leaf\n"),
            ("prog.shoddy",
             "Include \"mid.shoddy\" As Mid\n\nDef Main()\n    Print(LeafOnly())\n")));
    }

    [Fact]
    public void ANamespaceDoesNotReachABareInnerInclude()
    {
        // A namespace covers the surface of the file it names and nothing
        // further. mid is included AS Mid, so its own Only is MidOnly —
        // but leaf, which mid includes bare, keeps its bare name.
        //
        // The qualifier used to propagate down the whole subtree, which
        // made a file's spelling depend on its dependencies' include
        // lists, and made the spliced path disagree with the compiled one.
        Assert.Equal("leaf mid\n", RunFiles(
            ("leaf.shoddy", "Def Only() As String\n    \"leaf\"\n"),
            ("mid.shoddy", "Include \"leaf.shoddy\"\n\nDef Only() As String\n    \"mid\"\n"),
            ("prog.shoddy",
             "Include \"mid.shoddy\" As Mid\n\nDef Main()\n    Print(Only() & \" \" & MidOnly())\n")));
    }

    [Fact]
    public void ANamespaceOnAnInnerIncludeIsNotReachedThrough()
    {
        // The same rule from the other side: prog names no namespace at
        // all, and mid's AS Leaf governs leaf. Nothing prog writes gains a
        // Mid prefix, because prog never asked for one.
        Assert.Equal("leaf\n", RunFiles(
            ("leaf.shoddy", "Def Only() As String\n    \"leaf\"\n"),
            ("mid.shoddy", "Include \"leaf.shoddy\" As Leaf\n"),
            ("prog.shoddy",
             "Include \"mid.shoddy\"\n\nDef Main()\n    Print(LeafOnly())\n")));
    }

    [Fact]
    public void DuplicateDefNamesTheEarlierFile()
    {
        string err = RunFilesError(
            ("lib.shoddy", "Def Hints() As Number\n    1\n"),
            ("prog.shoddy",
             "Include \"lib.shoddy\"\n\nDef Hints() As Number\n    2\n\n" +
             "Def Main()\n    Print(\"x\")\n"));
        Assert.Contains("duplicate definition of HINTS", err);
        Assert.Contains("lib.shoddy:1", err);
    }

    [Fact]
    public void ShadowingABuiltinIsRefusedButRedefAllows()
    {
        // A Def outranks a builtin in the resolution chain, so `Def Length`
        // silently retires LENGTH program-wide — the same silent overwrite
        // the duplicate check already prevents between two Defs.
        Assert.Contains("is a builtin", RunFilesError(
            ("prog.shoddy", "Def Length(x As Number) As Number\n    42\n\n" +
                            "Def Main()\n    Print(Str(Length({ 1, 2, 3 })))\n")));

        Assert.Equal("42\n", RunFiles(
            ("prog.shoddy", "Redef Length(x As List) As Number\n    42\n\n" +
                            "Def Main()\n    Print(Str(Length({ 1, 2, 3 })))\n")));
    }

    [Fact]
    public void MalformedInIsRefused()
    {
        Assert.Contains("IN wants a plain name on each side",
            RunFilesError(("prog.shoddy", "Def Main()\n    Print(Str(1 In))\n")));
    }

    [Fact]
    public void NamespaceMustBeAPlainWord()
    {
        Assert.Contains("not usable as a namespace", RunFilesError(
            ("lib.shoddy", "Def Greet() As String\n    \"hi\"\n"),
            ("prog.shoddy", "Include \"lib.shoddy\" As 42\n\nDef Main()\n    Print(\"x\")\n")));
    }

    // ---- top-level Let under a namespace ---------------------------------

    [Fact]
    public void NamespacedGlobalIsAValueInArgumentPosition()
    {
        // A bare word in argument position is lifted into a quotation when
        // it names a function. A top-level Let is not a function -- but it
        // is declared qualified, so testing only the written spelling used
        // to miss it, lift it, and hand Length a one-item quotation. The
        // answer was 1 rather than 3, silently.
        const string lib = "Let Names = { \"alpha\", \"beta\", \"gamma\" }\n\n" +
                           "Def Count() As Number\n    Length(Names)\n";
        Assert.Equal("3\n", RunFiles(
            ("lib.shoddy", lib),
            ("prog.shoddy", "Include \"lib.shoddy\" As Lib\n\nDef Main()\n    Print(Str(Count()))\n")));
    }

    [Fact]
    public void NamespacedGlobalReadsTheSameFlatOrNot()
    {
        // The same source, included both ways, has to answer alike.
        const string lib = "Let Names = { \"alpha\", \"beta\", \"gamma\" }\n\n" +
                           "Def Second() As String\n    Nth(Names, 2)\n";
        const string main = "\nDef Main()\n    Print(Second())\n";
        string flat = RunFiles(("lib.shoddy", lib),
                               ("prog.shoddy", "Include \"lib.shoddy\"" + main));
        string named = RunFiles(("lib.shoddy", lib),
                                ("prog.shoddy", "Include \"lib.shoddy\" As Lib" + main));
        Assert.Equal("beta\n", flat);
        Assert.Equal(flat, named);
    }

    [Fact]
    public void FunctionNamesStillLiftUnderANamespace()
    {
        // The other half: fixing the above must not stop a bare function
        // name being passed as a quotation, which is what the pipeline
        // dialect is built on.
        const string lib = "Def IsEven(n As Number) As Boolean\n    Mod(n, 2) = 0\n\n" +
                           "Def Square(n As Number) As Number\n    n * n\n\n" +
                           "Def EvenSquareSum(n As Number) As Number\n" +
                           "    Range(1, n)\n        Filter(IsEven)\n" +
                           "        Map(Square)\n        Fold(0, +)\n";
        Assert.Equal("20\n", RunFiles(
            ("lib.shoddy", lib),
            ("prog.shoddy", "Include \"lib.shoddy\" As Lib\n\nDef Main()\n    Print(Str(EvenSquareSum(4)))\n")));
    }

    // ---- helpers --------------------------------------------------------

    /// <summary>Write a set of files into one directory and run the last.
    /// Includes resolve relative to the including file, so they have to be
    /// real files on disk rather than strings.</summary>
    static string RunFiles(params (string Name, string Src)[] files)
    {
        string entry = WriteAll(files);
        var prog = Parser.Parse(Lexer.ReadProgram(entry));
        var output = new StringWriter();
        int exit = Weaver.Execute(prog, null, output, TextReader.Null, Array.Empty<string>());
        Assert.True(exit == 0, $"expected exit 0, got {exit}");
        return output.ToString();
    }

    static string RunFilesError(params (string Name, string Src)[] files)
    {
        string entry = WriteAll(files);
        try
        {
            var prog = Parser.Parse(Lexer.ReadProgram(entry));
            Weaver.Execute(prog, null, new StringWriter(), TextReader.Null, Array.Empty<string>());
        }
        catch (ShoddyError e)
        {
            return e.File != null ? $"{Path.GetFileName(e.File)}:{e.Line}: {e.Message}" : e.Message;
        }
        Assert.Fail("expected a ShoddyError");
        return "";
    }

    static string WriteAll((string Name, string Src)[] files)
    {
        string dir = Path.Combine(Path.GetTempPath(), "shoddy-ns", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach ((string name, string src) in files)
            File.WriteAllText(Path.Combine(dir, name), src);
        return Path.Combine(dir, files[^1].Name);
    }
}
