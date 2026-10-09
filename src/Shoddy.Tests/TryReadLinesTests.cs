// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// TRYREADLINES — the line-oriented read, native, answering the
/// language's predeclared Result like TRYREADFILE does. It exists because
/// file.shoddy's ReadLines was a Def over READFILE and str's Split, and
/// Split recurses once per field: a file past about fourteen thousand
/// lines died of a stack overflow, a process kill and not an Error. Four
/// things are worth pinning:
///
///   1. The rules for what a line is match what file.shoddy always
///      answered: \r\n and \n both end a line, a final newline closes the
///      last line rather than opening an empty one, and an empty file is
///      an empty list.
///   2. A long file loads. This is the reason the builtin exists, so the
///      test reads one well past where the Def died.
///   3. The Err reasons are TRYREADFILE's closed set, not the platform's.
///   4. The record it pushes matches a Case Ok / Case Err in woven code,
///      with a List Of String inside the Ok.
///
/// (In "golden" because RunSrcError redirects Console.Error, which must
/// not interleave with the other classes that do the same.)
/// </summary>
[Collection("golden")]
public class TryReadLinesTests
{
    [Fact]
    public void OkCarriesTheLinesWithTheirEndingsStripped()
    {
        string path = Path.Combine(TempDir(), "motto.txt");
        File.WriteAllText(path, "USELESS THINGS\r\nMADE USEFUL\n");
        Assert.Equal("2 LINES, FIRST USELESS THINGS, LAST MADE USEFUL\n", RunSays(path));
    }

    [Fact]
    public void NoFinalNewlineStillYieldsTheLastLine()
    {
        string path = Path.Combine(TempDir(), "bare.txt");
        File.WriteAllText(path, "ONE\nTWO");
        Assert.Equal("2 LINES, FIRST ONE, LAST TWO\n", RunSays(path));
    }

    [Fact]
    public void AnEmptyFileIsAnEmptyList()
    {
        string path = Path.Combine(TempDir(), "empty.txt");
        File.WriteAllText(path, "");
        Assert.Equal("0 LINES\n", RunSays(path));
    }

    [Fact]
    public void ALongFileNeedsNoStack()
    {
        // The Def this replaced died somewhere between 14,000 and 14,200
        // lines. Fifty thousand is far enough past it to mean something.
        string path = Path.Combine(TempDir(), "long.txt");
        File.WriteAllLines(path, Enumerable.Range(1, 50_000).Select(i => i.ToString()));
        Assert.Equal("50000 LINES, FIRST 1, LAST 50000\n", RunSays(path));
    }

    [Fact]
    public void MissingPathReportsRatherThanAborts()
    {
        string path = Path.Combine(TempDir(), "nope.txt");
        Assert.Equal($"ERR CANNOT READ '{path}' (NO SUCH FILE) @0\n", RunSays(path));
    }

    [Fact]
    public void ADirectoryIsNamedAsOne()
    {
        string dir = TempDir();
        Assert.Equal($"ERR CANNOT READ '{dir}' (IS A DIRECTORY) @0\n", RunSays(dir));
    }

    // ---- helpers --------------------------------------------------------

    /// <summary>A Select Case over the Result: the count, and the first and
    /// last line when there are any, so a test reads both ends of a long
    /// list without printing it.</summary>
    const string Says =
        "Def Says(r As Result) As String\n" +
        "    Select Case r\n" +
        "        Case Ok(xs)\n" +
        "            If IsEmpty(xs) Then\n" +
        "                \"0 LINES\"\n" +
        "            Else\n" +
        "                Str(Length(xs)) & \" LINES, FIRST \" & First(xs) & \", LAST \" & Nth(xs, Length(xs))\n" +
        "        Case Err(why, at)\n" +
        "            \"ERR \" & why & \" @\" & Str(at)\n" +
        "        Case Else\n" +
        "            \"UNREACHABLE\"\n";

    /// <summary>A Windows path inside a Shoddy string literal: the lexer
    /// takes \\ for a backslash, as C does.</summary>
    static string Esc(string path) => path.Replace("\\", "\\\\");

    static string RunSays(string path) => RunSrc(string.Join('\n',
        Says, "Def Main()", $"    Print(Says(TryReadLines(\"{Esc(path)}\")))", ""));

    static string RunSrc(string src)
    {
        var prog = Parser.Parse(Lexer.ReadProgram(WriteTemp(src)));
        var output = new StringWriter();
        int exit = Weaver.Execute(prog, null, output, TextReader.Null, Array.Empty<string>());
        Assert.True(exit == 0, $"expected exit 0, got {exit}");
        return output.ToString();
    }

    static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shoddy-tryreadlines",
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
