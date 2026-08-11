// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Compiler;
using Shoddy.Mill;
using Shoddy.Runtime;

// mill — the Shoddy toolchain, the full build: every verb, window
// serving, audio. 100% compiled: every path weaves to C# and compiles
// with Roslyn; `run` just keeps the assembly in memory.
//   mill run FILE.shoddy [a...]  compile in memory and run (default command);
//                             extra arguments reach the program via Args
//   mill weave FILE.shoddy    compile to a .NET assembly (run: dotnet FILE.dll)
//   mill machine LIB.shoddy   compile a library to a machine DLL with manifest
//   mill manifest MILL        verify a mill.manifest (--write: generate)
//   mill lex FILE.shoddy      dump the token lines (devil output; debug aid)
//   mill gen FILE.shoddy      print the generated C# (debug aid)
//   mill dap                  serve the Debug Adapter Protocol (the perch;
//                             launched by the editor, not by hand)
//
// The verbs themselves live in Shoddy.Compiler's MillVerbs, shared with
// the headless build; this entry point owns only what needs the window
// and audio stack — `run`'s serving loop and `dap`.

if (MillVerbs.WantsVersion(args)) return MillVerbs.Version("full");

var opt = new MillVerbs.Options();
args = MillVerbs.StripFlags(args, opt);
if (opt.NoWindow) ScribblerWindows.Hidden = true;

if (args.Length == 1 && args[0] == "dap")
    // The perch: DAP over stdio for the editor. The serve loop runs on a
    // background thread so this (the main) thread can serve scribbler
    // windows for the debugged program, exactly as `run` does; no linger,
    // because a window must not outlive its debug session.
    return ScribblerWindows.Run(DapServer.Serve, linger: false);

MillVerbs.Command? cmd = MillVerbs.Parse(args);
if (cmd is null) return MillVerbs.Usage();

try
{
    if (cmd.Verb != "run") return MillVerbs.RunPure(cmd, opt);

    (ShoddyProgram prog, MachineSet machines) = MillVerbs.ParseWithMachines(cmd.File, opt);
    // The program runs on a background thread; this (the main) thread
    // serves scribbler windows, because GLFW requires them here. A
    // console program costs one blocked wait — and if the program
    // returns while a window is open, the process stays alive until the
    // user closes it ("draw a picture, return" is a complete program).
    // ...unless the windows are hidden. Lingering exists so a picture
    // stays on screen until the user dismisses it, and under --no-window
    // there is no screen and no user: a program that draws, saves and
    // returns without calling ScribblerClose would otherwise wait
    // forever on a window nobody can close. Same reasoning as the
    // perch's linger: false.
    return ScribblerWindows.Run(
        () => Weaver.Execute(prog, machines.Machines, Console.Out, Console.In, cmd.ProgArgs),
        linger: !ScribblerWindows.Hidden);
}
catch (ShoddyError e)
{
    return MillVerbs.Report(e);
}
