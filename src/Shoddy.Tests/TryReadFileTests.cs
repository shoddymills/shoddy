// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// TRYREADFILE — the guarded twin of READFILE, and the only builtin that
/// answers the language's predeclared Result. Three things are worth
/// pinning and nothing else here does:
///
///   1. The record it pushes matches a Case Ok / Case Err in woven code.
///      Prelude's TypeDefs are the one shared instance precisely so this
///      works; a builtin that minted its own would compile, run, and fall
///      through every match at runtime.
///   2. The Err reason is Shoddy's own closed set of phrases, not the
///      host platform's exception text — which varies by OS and locale
///      and would make the wording untestable and unusable to a program.
///   3. READFILE still aborts. The guarded form is an addition, and an
///      existing caller that relied on the abort must not have quietly
///      acquired a Result it does not match on.
///
/// (In "golden" because RunSrcError redirects Console.Error, which must
/// not interleave with the other classes that do the same.)
/// </summary>
[Collection("golden")]
public class TryReadFileTests
{
    [Fact]
    public void OkCarriesTheWholeFile()
    {
        string path = Path.Combine(TempDir(), "motto.txt");
        File.WriteAllText(path, "USELESS THINGS\nMADE USEFUL\n");
        Assert.Equal("USELESS THINGS\nMADE USEFUL\n\n", RunWhy(path));
    }

    [Fact]
    public void MissingPathReportsRatherThanAborts()
    {
        string path = Path.Combine(TempDir(), "nope.txt");
        Assert.Equal($"ERR CANNOT READ '{path}' (NO SUCH FILE) @0\n", RunWhy(path));
    }

    [Fact]
    public void ADirectoryIsNamedAsOne()
    {
        // The case FILEEXISTS gets wrong, and the case every platform
        // reports as access denied, which is why it is checked explicitly.
        string dir = TempDir();
        Assert.Equal($"ERR CANNOT READ '{dir}' (IS A DIRECTORY) @0\n", RunWhy(dir));
    }

    [Fact]
    public void TheStateAfterAFailureIsWholeAndTheNextReadWorks()
    {
        // The contract is survival, and only a subsequent read proves it.
        string dir = TempDir();
        string after = Path.Combine(dir, "after.txt");
        File.WriteAllText(after, "STILL HERE");
        string output = RunSrc(string.Join('\n',
            Why,
            "Def Main()",
            $"    Print(Why(TryReadFile(\"{Esc(Path.Combine(dir, "nope.txt"))}\")))",
            $"    Print(Why(TryReadFile(\"{Esc(after)}\")))",
            ""));
        Assert.Contains("NO SUCH FILE", output);
        Assert.EndsWith("STILL HERE\n", output);
    }

    [Fact]
    public void ReadFileStillAborts()
    {
        string path = Esc(Path.Combine(TempDir(), "nope.txt"));
        Assert.Contains("READFILE: cannot open",
                        RunSrcError($"Def Main()\n    Print(ReadFile(\"{path}\"))\n"));
    }

    // ---- helpers --------------------------------------------------------

    /// <summary>A Select Case over the Result, which is the only way in —
    /// the predeclared types generate no accessor words. Being able to
    /// write this at all is what test 1 above is checking.</summary>
    const string Why =
        "Def Why(r As Result) As String\n" +
        "    Select Case r\n" +
        "        Case Ok(text)\n" +
        "            text\n" +
        "        Case Err(why, at)\n" +
        "            \"ERR \" & why & \" @\" & Str(at)\n" +
        "        Case Else\n" +
        "            \"UNREACHABLE\"\n";

    /// <summary>A Windows path inside a Shoddy string literal: the lexer
    /// takes \\ for a backslash, as C does.</summary>
    static string Esc(string path) => path.Replace("\\", "\\\\");

    static string RunWhy(string path) => RunSrc(string.Join('\n',
        Why, "Def Main()", $"    Print(Why(TryReadFile(\"{Esc(path)}\")))", ""));

    static string RunSrc(string src)
    {
        var prog = Parser.Parse(Lexer.ReadProgram(WriteTemp(src)));
        var output = new StringWriter();
        int exit = Weaver.Execute(prog, null, output, TextReader.Null, Array.Empty<string>());
        Assert.True(exit == 0, $"expected exit 0, got {exit}");
        return output.ToString();
    }

    static string RunSrcError(string src)
    {
        var err = new StringWriter();
        TextWriter oldErr = Console.Error;
        Console.SetError(err);
        try
        {
            var prog = Parser.Parse(Lexer.ReadProgram(WriteTemp(src)));
            int exit = Weaver.Execute(prog, null, new StringWriter(), TextReader.Null,
                                      Array.Empty<string>());
            Assert.Equal(1, exit);
            return err.ToString();
        }
        finally { Console.SetError(oldErr); }
    }

    static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shoddy-tryread",
                                  Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string WriteTemp(string src)
    {
        string sb = Path.Combine(TempDir(), "prog.shoddy");
        File.WriteAllText(sb, src);
        return sb;
    }
}
