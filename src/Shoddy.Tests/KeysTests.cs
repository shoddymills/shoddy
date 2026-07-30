// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// machines/keys.shoddy — the physical-key classifier. Pure Number ->
/// GameKey, so it weaves and runs headless with no scribbler.
/// </summary>
public class KeysTests
{
    static readonly string Root = RepoRoot.Dir;

    [Fact]
    public void ClassifyCoversArrowsNamedKeysAndChars()
    {
        string output = RunWithKeys(string.Join('\n',
            "Include \"machines/keys.shoddy\"",
            "",
            "Def Name(k As GameKey) As String",
            "    Select Case k",
            "        Case ArrowLeft",
            "            \"LEFT\"",
            "        Case ArrowRight",
            "            \"RIGHT\"",
            "        Case ArrowUp",
            "            \"UP\"",
            "        Case ArrowDown",
            "            \"DOWN\"",
            "        Case SpaceBar",
            "            \"SPACE\"",
            "        Case EnterKey",
            "            \"ENTER\"",
            "        Case EscKey",
            "            \"ESC\"",
            "        Case CharKey(c)",
            "            \"CHAR:\" & Chr(c)",
            "        Case Else",
            "            \"OTHER\"",
            "",
            "Def Main()",
            "    Print(Name(ClassifyKey(262)))",   // Left
            "    Print(Name(ClassifyKey(263)))",   // Right
            "    Print(Name(ClassifyKey(264)))",   // Up
            "    Print(Name(ClassifyKey(265)))",   // Down
            "    Print(Name(ClassifyKey(32)))",    // Space
            "    Print(Name(ClassifyKey(13)))",    // Enter
            "    Print(Name(ClassifyKey(27)))",    // Escape
            "    Print(Name(ClassifyKey(81)))",    // 'Q'
            "    Print(Name(ClassifyKey(48)))",    // '0'
            "    Print(Name(ClassifyKey(999)))",   // unmapped
            ""));
        Assert.Equal(string.Join('\n',
            "LEFT", "RIGHT", "UP", "DOWN", "SPACE", "ENTER", "ESC",
            "CHAR:Q", "CHAR:0", "OTHER",
            ""), output);
    }

    [Fact]
    public void GlyphAndIsKey()
    {
        string output = RunWithKeys(string.Join('\n',
            "Include \"machines/keys.shoddy\"",
            "",
            "Def YN(b As Boolean) As String",
            "    If b Then",
            "        \"Y\"",
            "    Else",
            "        \"N\"",
            "",
            "Def Main()",
            "    Print(KeyGlyph(ClassifyKey(65)))",           // "A"
            "    Print(KeyGlyph(ClassifyKey(262)))",          // "" (named key)
            "    Print(YN(IsKey(ClassifyKey(81), \"q\")))",   // physical Q vs 'q' -> Y
            "    Print(YN(IsKey(ClassifyKey(81), \"Z\")))",   // -> N
            ""));
        Assert.Equal("A\n\nY\nN\n", output);
    }

    // ---- helper --------------------------------------------------------

    static string RunWithKeys(string src)
    {
        string ws = Path.Combine(Path.GetTempPath(), "shoddy-keys", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(ws, "machines"));
        foreach (string f in Directory.GetFiles(Path.Combine(Root, "machines"), "*.shoddy"))
            File.Copy(f, Path.Combine(ws, "machines", Path.GetFileName(f)));
        string sb = Path.Combine(ws, "prog.shoddy");
        File.WriteAllText(sb, src);

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
