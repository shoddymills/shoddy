// Copyright (c) 2026 Stephen Vincent Foster. All rights reserved.
//
// This file is part of the Shoddy Language project.
// Licensed under the Shoddy Language License 1.0.0 (PolyForm Noncommercial
// License 1.0.0 with Additional Use Grant). See the LICENSE file in the
// project root for full terms.

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
// present — compiled include-once — and splices the source otherwise.

// --allow-net arms the gated TCP/IP builtins for this run (and any woven
// program it spawns in-process) by setting the ambient switch the Engine
// reads at construction. Stripped from args so it never disturbs the
// positional command/file parsing below, nor a program's own arguments.
if (args.Contains("--allow-net"))
{
    Environment.SetEnvironmentVariable("SHODDY_ALLOW_NET", "1");
    args = args.Where(a => a != "--allow-net").ToArray();
}

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
            (ShoddyProgram prog, _) = ParseWithMachines(file);
            Console.Write(Weaver.GenerateSource(prog));
            return 0;
        }
        case "weave":
        {
            (ShoddyProgram prog, MachineSet machines) = ParseWithMachines(file);
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
            (ShoddyProgram prog, MachineSet machines) = ParseWithMachines(file);
            string outDll = Weaver.WeaveMachine(prog, file, machines.Machines);
            Console.Error.WriteLine($"mill: built machine {file} -> {outDll}");
            return 0;
        }
        default:
        {
            (ShoddyProgram prog, MachineSet machines) = ParseWithMachines(file);
            // The program runs on a background thread; this (the main)
            // thread serves scribbler windows, because GLFW requires them
            // here. A console program costs one blocked wait — and if the
            // program returns while a window is open, the process stays
            // alive until the user closes it ("draw a picture, return" is
            // a complete program).
            return ScribblerWindows.Run(() =>
                Weaver.Execute(prog, machines.Machines, Console.Out, Console.In, progArgs));
        }
    }
}
catch (ShoddyError e)
{
    Console.Error.Write("ERROR");
    if (e.Line > 0) Console.Error.Write($" (line {e.Line})");
    Console.Error.WriteLine($": {e.Message}");
    return 1;
}

static (ShoddyProgram, MachineSet) ParseWithMachines(string file)
{
    var machines = new MachineSet();
    List<Line> lines = Lexer.ReadProgram(file, machines.TryResolve);
    var prog = new ShoddyProgram();
    machines.SeedInto(prog);
    return (Parser.Parse(lines, prog), machines);
}
