// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Compiler;
using Shoddy.Mill;
using Shoddy.Devil;
using Shoddy.Runtime;

// mill — the Shoddy toolchain. 100% compiled: every path weaves to C#
// and compiles with Roslyn; `run` just keeps the assembly in memory.
//   mill run FILE.shoddy [a...]  compile in memory and run (default command);
//                             extra arguments reach the program via Args
//   mill weave FILE.shoddy    compile to a .NET assembly (run: dotnet FILE.dll)
//   mill machine LIB.shoddy   compile a library to a machine DLL with manifest
//   mill lex FILE.shoddy      dump the token lines (devil output; debug aid)
//   mill gen FILE.shoddy      print the generated C# (debug aid)
//   mill dap                  serve the Debug Adapter Protocol (the perch;
//                             launched by the editor, not by hand)
//
// An Include resolves to Shoddy.Machines.<Name>.dll beside the source if
// present — compiled include-once — and splices the source otherwise. A
// DLL older than its source (or than a dependency's DLL) is rebuilt in
// place before loading.
//
// The linter runs on every run/weave/machine: warnings to stderr,
// --no-lint (or SHODDY_LINT=0) to silence them, --lint-verbose for the
// coverage line. Unknown words are weave errors no flag overrides.

// --allow-net arms the gated TCP/IP builtins for this run (and any woven
// program it spawns in-process) by setting the ambient switch the Engine
// reads at construction. Stripped from args so it never disturbs the
// positional command/file parsing below, nor a program's own arguments.
if (args.Contains("--allow-net"))
{
    Environment.SetEnvironmentVariable("SHODDY_ALLOW_NET", "1");
    args = args.Where(a => a != "--allow-net").ToArray();
}

// --no-window opens every scribbler hidden: the program draws, blits and
// saves exactly as usual, but nothing appears on screen. For a run whose
// output is a file — ScribblerSave in a script, a chart captured by a
// build — a window that flashes up and vanishes is noise. Stripped from
// args like --allow-net, and for the same reason.
if (args.Contains("--no-window"))
{
    ScribblerWindows.Hidden = true;
    args = args.Where(a => a != "--no-window").ToArray();
}

// The linter is on by default; --no-lint (or SHODDY_LINT=0) silences the
// warnings. The unknown-word error is a correctness error — the program
// would die at runtime anyway — and no flag silences it.
bool lintOff = args.Contains("--no-lint") ||
    Environment.GetEnvironmentVariable("SHODDY_LINT") == "0";
bool lintVerbose = args.Contains("--lint-verbose");
args = args.Where(a => a is not ("--no-lint" or "--lint-verbose")).ToArray();

if (args.Length == 1 && args[0] == "dap")
    // The perch: DAP over stdio for the editor. The serve loop runs on a
    // background thread so this (the main) thread can serve scribbler
    // windows for the debugged program, exactly as `run` does; no linger,
    // because a window must not outlive its debug session.
    return ScribblerWindows.Run(DapServer.Serve, linger: false);

string? cmd = null, file = null;
string[] progArgs = Array.Empty<string>();
if (args.Length >= 1)
{
    if (args[0] is "run" or "lex" or "weave" or "machine" or "gen")
    {
        cmd = args[0];
        if (args.Length >= 2) { file = args[1]; progArgs = args[2..]; }
    }
    else
    {
        (cmd, file, progArgs) = ("run", args[0], args[1..]);
    }
}
if (file is null || (cmd != "run" && progArgs.Length > 0))
{
    Console.Error.WriteLine("usage: mill [run|weave|machine|lex] FILE.shoddy [program args...]");
    return 2;
}

try
{
    switch (cmd)
    {
        case "lex":
            foreach (Line l in Lexer.ReadProgram(file))
                Console.WriteLine(
                    $"{Path.GetFileName(l.File)}:{l.LineNo,4} |{new string(' ', l.Indent)}{string.Join(' ', l.Toks)}");
            return 0;
        case "gen":
        {
            (ShoddyProgram prog, _) = ParseWithMachines(file, lintOff, lintVerbose);
            Console.Write(Weaver.GenerateSource(prog));
            return 0;
        }
        case "weave":
        {
            (ShoddyProgram prog, MachineSet machines) = ParseWithMachines(file, lintOff, lintVerbose);
            string outDll = Path.ChangeExtension(file, ".dll");
            Weaver.Weave(prog, outDll, machines.Machines);
            string via = machines.Machines.Count > 0
                ? $" (against {machines.Machines.Count} machine{(machines.Machines.Count == 1 ? "" : "s")})"
                : "";
            Console.Error.WriteLine($"mill: wove {file} -> {outDll}{via}   (run: dotnet {outDll})");
            return 0;
        }
        case "machine":
        {
            (ShoddyProgram prog, MachineSet machines) = ParseWithMachines(file, lintOff, lintVerbose);
            string outDll = Weaver.WeaveMachine(prog, file, machines.Machines);
            Console.Error.WriteLine($"mill: built machine {file} -> {outDll}");
            return 0;
        }
        default:
        {
            (ShoddyProgram prog, MachineSet machines) = ParseWithMachines(file, lintOff, lintVerbose);
            // The program runs on a background thread; this (the main)
            // thread serves scribbler windows, because GLFW requires them
            // here. A console program costs one blocked wait — and if the
            // program returns while a window is open, the process stays
            // alive until the user closes it ("draw a picture, return" is
            // a complete program).
            // ...unless the windows are hidden. Lingering exists so a
            // picture stays on screen until the user dismisses it, and
            // under --no-window there is no screen and no user: a program
            // that draws, saves and returns without calling ScribblerClose
            // would otherwise wait forever on a window nobody can close.
            // Same reasoning as the perch's linger: false.
            return ScribblerWindows.Run(
                () => Weaver.Execute(prog, machines.Machines, Console.Out, Console.In, progArgs),
                linger: !ScribblerWindows.Hidden);
        }
    }
}
catch (ShoddyError e)
{
    Console.Error.Write("ERROR");
    if (e.Line > 0 && e.File != null)
        Console.Error.Write($" ({Path.GetFileName(e.File)}:{e.Line})");
    else if (e.Line > 0) Console.Error.Write($" (line {e.Line})");
    Console.Error.WriteLine($": {e.Message}");
    return 1;
}

static (ShoddyProgram, MachineSet) ParseWithMachines(
    string file, bool lintOff = false, bool lintVerbose = false)
{
    var machines = new MachineSet();
    List<Line> lines = Lexer.ReadProgram(file, (p, q) => ResolveMachine(machines, p, q));
    var prog = new ShoddyProgram();
    machines.SeedInto(prog);
    ShoddyProgram parsed = Parser.Parse(lines, prog);
    // The linter: warnings on stderr — they never stop a build, and
    // stdout belongs to the program. Unknown words are errors: nothing
    // legitimate survives resolution, and runtime would die at the same
    // spot with less context.
    Lint.Result lint = Lint.Run(parsed, lines);
    if (!lintOff)
        foreach (string w in lint.Warnings)
            Console.Error.WriteLine(w);
    if (lintVerbose)
        Console.Error.WriteLine(
            $"lint: checked {lint.DefsChecked} of {lint.DefsTotal} Defs " +
            "(the rest touch dynamic constructs and are skipped, not guessed)");
    if (lint.UnknownWords.Count > 0)
    {
        foreach ((string msg, string? f, int ln) in lint.UnknownWords)
            Console.Error.WriteLine(
                $"{(f == null ? "?" : Path.GetFileName(f))}:{ln}: error: {msg}");
        (string m0, string? f0, int l0) = lint.UnknownWords[0];
        throw new ShoddyError(l0, f0, m0);
    }
    return (parsed, machines);
}

// A machine DLL that is older than its source (or than any machine DLL it
// depends on) is rebuilt in place before it is loaded — the stale-DLL trap
// cost two debugging rounds in one week, and the mill already knows how to
// build a machine. Rebuild failures fall through to the normal error path.
static bool ResolveMachine(MachineSet machines, string sbPath, string? qual)
{
    string dll = MachineSet.DllPathFor(sbPath);
    if (File.Exists(dll) && IsStale(sbPath, dll))
    {
        Console.Error.WriteLine(
            $"mill: machine {Path.GetFileName(sbPath)} is newer than its DLL — rebuilding");
        (ShoddyProgram mprog, MachineSet msub) = ParseWithMachines(sbPath, lintOff: true);
        Weaver.WeaveMachine(mprog, sbPath, msub.Machines);
    }
    return machines.TryResolve(sbPath, qual);
}

static bool IsStale(string sbPath, string dll)
{
    DateTime built = File.GetLastWriteTimeUtc(dll);
    if (File.GetLastWriteTimeUtc(sbPath) > built) return true;
    // A dependency rebuilt after this DLL also staled it: its calls bind
    // against the dependency's surface. Includes are cheap to scan.
    string dir = Path.GetDirectoryName(Path.GetFullPath(sbPath))!;
    foreach (string line in File.ReadLines(sbPath))
    {
        System.Text.RegularExpressions.Match m =
            System.Text.RegularExpressions.Regex.Match(
                line, "^Include \"([A-Za-z0-9_.-]+\\.shoddy)\"");
        if (!m.Success) continue;
        string depDll = MachineSet.DllPathFor(Path.Combine(dir, m.Groups[1].Value));
        if (File.Exists(depDll) && File.GetLastWriteTimeUtc(depDll) > built) return true;
    }
    return false;
}
