// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Compiler;
using Shoddy.Runtime;

// mill — the Shoddy toolchain, the headless build: the pure verbs
// (weave, machine, manifest, gen, lex) with no window or audio stack.
// `run` and `dap` exist and refuse by name — a missing verb sends a
// user to the documentation; a refusing verb sends them to the fix.
// The verbs themselves live in Shoddy.Compiler's MillVerbs, shared
// with the full mill, which is what keeps the two builds one mill:
// byte-identical artifacts for every verb they share.

if (MillVerbs.WantsVersion(args)) return MillVerbs.Version("headless");

var opt = new MillVerbs.Options();
args = MillVerbs.StripFlags(args, opt);
// --no-window is honoured vacuously: this build shows nothing anyway.

if (args.Length == 1 && args[0] == "dap") return Refuse("dap");

MillVerbs.Command? cmd = MillVerbs.Parse(args);
if (cmd is null) return MillVerbs.Usage();
if (cmd.Verb == "run") return Refuse("run");

try
{
    return MillVerbs.RunPure(cmd, opt);
}
catch (ShoddyError e)
{
    return MillVerbs.Report(e);
}

// A compile-time stub, deliberately: no probing, no dynamic assembly
// loading, nothing that could drag the graphics or audio stack back in.
static int Refuse(string verb)
{
    Console.Error.WriteLine(
        $"mill: `mill {verb}` needs windows and audio, and this is the headless " +
        "build (Shoddy.Mill.Headless — pure verbs only).");
    Console.Error.WriteLine(
        "Install the full mill (the Shoddy.Mill package, or the VS Code " +
        "extension's bundled copy) to run or debug programs.");
    return 1;
}
