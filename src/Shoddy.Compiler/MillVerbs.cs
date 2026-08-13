// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Reflection;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Compiler;

/// <summary>
/// The mill's shared verb surface — one dispatch, two builds. The full
/// mill wraps `run` and `dap` in the window and audio stack it alone
/// references; the headless build carries no such reference and refuses
/// those two verbs by name. Everything else — flag stripping, command
/// parsing, the pure verbs (lex, gen, weave, machine, manifest), the
/// error report — lives here, written once. That single implementation
/// is what makes the two builds one mill: byte-identical artifacts for
/// every verb they share is an acceptance criterion, and code that
/// cannot diverge is how it is met.
/// </summary>
public static class MillVerbs
{
    /// <summary>Flags every build understands, stripped before the
    /// positional command/file parse.</summary>
    public sealed class Options
    {
        public bool LintOff;
        public bool LintVerbose;
        /// <summary>--no-window: the full mill opens every scribbler
        /// hidden. The headless build has no windows to hide, so for it
        /// the flag is honoured vacuously rather than refused — the
        /// flag's meaning is "show nothing", and nothing is shown.</summary>
        public bool NoWindow;
    }

    public static bool WantsVersion(string[] args) =>
        args.Length == 1 && args[0] == "--version";

    /// <summary>C3.6: `mill --version` is the one place the two builds
    /// surface their difference to a user.</summary>
    public static int Version(string build)
    {
        string? ver = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        Console.WriteLine(ver is null ? $"mill ({build})" : $"mill {ver} ({build})");
        return 0;
    }

    /// <summary>Strip the flags common to every verb, recording them in
    /// <paramref name="opt"/> so they never disturb the positional
    /// command/file parsing, nor a program's own arguments.</summary>
    public static string[] StripFlags(string[] args, Options opt)
    {
        // --allow-net arms the gated TCP/IP builtins for this run (and any
        // woven program it spawns in-process) by setting the ambient switch
        // the Engine reads at construction.
        if (args.Contains("--allow-net"))
        {
            Environment.SetEnvironmentVariable("SHODDY_ALLOW_NET", "1");
            args = args.Where(a => a != "--allow-net").ToArray();
        }

        opt.NoWindow = args.Contains("--no-window");

        // The linter is on by default; --no-lint (or SHODDY_LINT=0) silences
        // the warnings. The unknown-word error is a correctness error — the
        // program would die at runtime anyway — and no flag silences it.
        opt.LintOff = args.Contains("--no-lint") ||
            Environment.GetEnvironmentVariable("SHODDY_LINT") == "0";
        opt.LintVerbose = args.Contains("--lint-verbose");

        return args.Where(a => a is not ("--no-window" or "--no-lint" or "--lint-verbose"))
                   .ToArray();
    }

    /// <summary>One parsed command line: the verb, its file (or folder,
    /// for manifest), and the program arguments (run only; --write for
    /// manifest).</summary>
    public sealed record Command(string Verb, string File, string[] ProgArgs);

    /// <summary>Null when the line is unusable; the caller prints
    /// <see cref="Usage"/> and exits 2. `dap` is not parsed here — it
    /// takes no file, and each entry point owns what it means.</summary>
    public static Command? Parse(string[] args)
    {
        string? cmd = null, file = null;
        string[] progArgs = Array.Empty<string>();
        if (args.Length >= 1)
        {
            if (args[0] is "run" or "lex" or "weave" or "machine" or "gen" or "manifest")
            {
                cmd = args[0];
                if (args.Length >= 2) { file = args[1]; progArgs = args[2..]; }
            }
            else
            {
                (cmd, file, progArgs) = ("run", args[0], args[1..]);
            }
        }
        if (cmd == "manifest")
        {
            // Flags may come before or after the target; both orders are
            // seen in the docs and neither deserves a usage error.
            // --write regenerates; --inputs and --gate serve the build
            // (ShoddyWeave's incrementality and its capability gate).
            var flags = new List<string>();
            string? target = null;
            string[] rest = args[1..];
            for (int i = 0; i < rest.Length; i++)
            {
                switch (rest[i])
                {
                    case "--write" or "--inputs":
                        flags.Add(rest[i]);
                        break;
                    case "--gate" or "--mode":
                        if (i + 1 >= rest.Length) return null;
                        flags.Add(rest[i]);
                        flags.Add(rest[++i]);
                        break;
                    default:
                        if (target != null) return null;
                        target = rest[i];
                        break;
                }
            }
            if (target is null) return null;
            int actions = flags.Count(f => f is "--write" or "--inputs" or "--gate");
            if (actions > 1) return null;
            return new Command(cmd, target, flags.ToArray());
        }
        if (cmd == "machine")
        {
            // --debug — the instrumented machine weave (A6.2) — in
            // either position, like manifest's flags.
            string[] rest = args[1..];
            string[] flags = rest.Where(a => a == "--debug").ToArray();
            string[] files = rest.Where(a => a != "--debug").ToArray();
            if (files.Length != 1 || flags.Length > 1) return null;
            return new Command(cmd, files[0], flags);
        }
        if (file is null) return null;
        if (cmd != "run" && progArgs.Length > 0) return null;
        return new Command(cmd!, file, progArgs);
    }

    public static int Usage()
    {
        Console.Error.WriteLine(
            "usage: mill [run|weave|machine|manifest|lex] FILE.shoddy [program args...]");
        return 2;
    }

    /// <summary>The pure verbs — every verb but `run` and `dap`, which
    /// are the callers' own because only the full mill can serve them.</summary>
    public static int RunPure(Command cmd, Options opt)
    {
        // Two projects in one solution both drive the mill at the same
        // target under parallel MSBuild; serialize per target so the
        // second writer waits instead of crashing on a file in use.
        using IDisposable gate = MillGate.Hold(cmd.File);
        switch (cmd.Verb)
        {
            case "lex":
                foreach (Line l in Lexer.ReadProgram(cmd.File))
                    Console.WriteLine(
                        $"{Path.GetFileName(l.File)}:{l.LineNo,4} |{new string(' ', l.Indent)}{string.Join(' ', l.Toks)}");
                return 0;
            case "gen":
            {
                (ShoddyProgram prog, _) = ParseWithMachines(cmd.File, opt);
                Console.Write(Weaver.GenerateSource(prog));
                return 0;
            }
            case "weave":
            {
                (ShoddyProgram prog, MachineSet machines) = ParseWithMachines(cmd.File, opt);
                string outDll = Path.ChangeExtension(cmd.File, ".dll");
                Weaver.Weave(prog, outDll, machines.Machines);
                string via = machines.Machines.Count > 0
                    ? $" (against {machines.Machines.Count} machine{(machines.Machines.Count == 1 ? "" : "s")})"
                    : "";
                Console.Error.WriteLine(
                    $"mill: wove {cmd.File} -> {outDll}{via}   (run: dotnet {outDll})");
                return 0;
            }
            case "machine":
            {
                bool debug = cmd.ProgArgs.Contains("--debug");
                (ShoddyProgram prog, MachineSet machines) = ParseWithMachines(cmd.File, opt);
                string outDll = Weaver.WeaveMachine(prog, cmd.File, machines.Machines, debug);
                Console.Error.WriteLine(
                    $"mill: built {(debug ? "instrumented " : "")}machine {cmd.File} -> {outDll}");
                return 0;
            }
            case "manifest":
            {
                if (cmd.ProgArgs.Contains("--inputs"))
                    return Manifest.Inputs(cmd.File);
                if (cmd.ProgArgs.Contains("--gate"))
                {
                    int g = Array.IndexOf(cmd.ProgArgs, "--gate");
                    int m = Array.IndexOf(cmd.ProgArgs, "--mode");
                    return Manifest.Gate(cmd.File, cmd.ProgArgs[g + 1],
                                         m >= 0 ? cmd.ProgArgs[m + 1] : null);
                }
                return Manifest.Run(cmd.File, write: cmd.ProgArgs.Contains("--write"));
            }
            default:
                throw new ShoddyError(0, $"internal: '{cmd.Verb}' is not a pure verb");
        }
    }

    /// <summary>The mill's own pipeline: read with machine resolution,
    /// seed, parse, lint. Warnings go to stderr — they never stop a
    /// build, and stdout belongs to the program. Unknown words are
    /// errors: nothing legitimate survives resolution, and runtime would
    /// die at the same spot with less context.</summary>
    public static (ShoddyProgram, MachineSet) ParseWithMachines(string file, Options opt)
    {
        var machines = new MachineSet();
        List<Line> lines = Lexer.ReadProgram(file, (p, q) => MachineResolve.Resolve(machines, p, q));
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        ShoddyProgram parsed = Parser.Parse(lines, prog);
        Lint.Result lint = Lint.Run(parsed, lines);
        if (!opt.LintOff)
        {
            foreach (string w in lint.Warnings)
                Console.Error.WriteLine(w);
            // The capability lint (A5.5): only speaks for a mill that
            // carries a manifest, and warns — never fails — like the rest.
            foreach (string w in CapabilityLint.Run(parsed, machines, file))
                Console.Error.WriteLine(w);
        }
        if (opt.LintVerbose)
        {
            // The advisory tier: findings real enough to record and too
            // common on healthy code to shout — dormant builtin shadows.
            foreach (string w in lint.Verbose)
                Console.Error.WriteLine(w);
            Console.Error.WriteLine(
                $"lint: checked {lint.DefsChecked} of {lint.DefsTotal} Defs " +
                "(the rest touch dynamic constructs and are skipped, not guessed)");
        }
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

    /// <summary>The one error report both builds print.</summary>
    public static int Report(ShoddyError e)
    {
        Console.Error.Write("ERROR");
        if (e.Line > 0 && e.File != null)
            Console.Error.Write($" ({Path.GetFileName(e.File)}:{e.Line})");
        else if (e.Line > 0) Console.Error.Write($" (line {e.Line})");
        Console.Error.WriteLine($": {e.Message}");
        return 1;
    }
}
