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
/// parsing, the pure verbs (lex, gen, weave, machine, library,
/// manifest), the error report — lives here, written once. That single
/// implementation is what makes the two builds one mill: byte-identical
/// artifacts for every verb they share is an acceptance criterion, and
/// code that cannot diverge is how it is met.
/// </summary>
public static class MillVerbs
{
    /// <summary>Where a verb writes when no -o says otherwise: a bin/
    /// folder beside the source. Build output never lands in a source
    /// folder itself.</summary>
    public const string DefaultOutDir = "bin";

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
        /// <summary>--lib: the compiled libraries Includes resolve
        /// against, in order. Named, they replace the default search
        /// ($SHODDYLIBBIN, then lib/ beside the toolchain): a build that
        /// says what it links gets exactly that. ShoddyWeave passes a
        /// consumer's references here.</summary>
        public readonly List<string> Libraries = new();

        /// <summary>The libraries a set should search: the named ones,
        /// else the toolchain's own.</summary>
        public IEnumerable<string> LibrariesFor(bool debug) =>
            Libraries.Count > 0 ? Libraries : MachineSet.DefaultLibraries(debug);
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

        // --lib PATH and --libsrc DIR, repeatable; a value may carry
        // several joined with ';' as MSBuild joins an item list. --lib
        // names a compiled library; --libsrc names a library's source
        // folder, so a file reached there by relative path counts as a
        // machine. A build passes both for every library it references.
        var kept = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a is "--no-window" or "--no-lint" or "--lint-verbose") continue;
            string? listed = null;
            List<string>? into = null;
            if (a is "--lib" or "--libsrc")
            {
                if (i + 1 >= args.Length) { kept.Add(a); continue; }
                listed = args[++i];
                into = a == "--lib" ? opt.Libraries : Lexer.NamedLibDirs;
            }
            else if (a.StartsWith("--lib=", StringComparison.Ordinal))
            {
                listed = a[6..];
                into = opt.Libraries;
            }
            else if (a.StartsWith("--libsrc=", StringComparison.Ordinal))
            {
                listed = a[9..];
                into = Lexer.NamedLibDirs;
            }
            if (listed != null)
            {
                foreach (string p in listed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    into!.Add(Path.GetFullPath(p));
                continue;
            }
            kept.Add(a);
        }
        return kept.ToArray();
    }

    /// <summary>One parsed command line: the verb, its file (or folder,
    /// for manifest and library), and the rest — the program arguments
    /// for run; the verb's own flags and any further folders for the
    /// others.</summary>
    public sealed record Command(string Verb, string File, string[] ProgArgs)
    {
        /// <summary>`run --perch PORT`: this is a debugged program's
        /// process, started by the editor's adapter, and PORT is where
        /// it connects back. Null for every other run.</summary>
        public string? Perch { get; init; }

        /// <summary>The value after a flag (-o DIR), or null.</summary>
        public string? Flag(params string[] names)
        {
            for (int i = 0; i + 1 < ProgArgs.Length; i++)
                if (names.Contains(ProgArgs[i])) return ProgArgs[i + 1];
            return null;
        }

        public bool Has(string flag) => ProgArgs.Contains(flag);

        /// <summary>The targets after the first: more library folders.</summary>
        public IEnumerable<string> Targets
        {
            get
            {
                yield return File;
                for (int i = 0; i < ProgArgs.Length; i++)
                {
                    if (ProgArgs[i] is "-o" or "--out" or "--gate" or "--mode" or "--sources") { i++; continue; }
                    if (ProgArgs[i].StartsWith('-')) continue;
                    yield return ProgArgs[i];
                }
            }
        }
    }

    /// <summary>Null when the line is unusable; the caller prints
    /// <see cref="Usage"/> and exits 2. `dap` is not parsed here — it
    /// takes no file, and each entry point owns what it means.</summary>
    public static Command? Parse(string[] args)
    {
        if (args.Length == 0) return null;
        string cmd;
        string[] rest;
        if (args[0] is "run" or "lex" or "weave" or "machine" or "gen" or "manifest" or "library")
        {
            cmd = args[0];
            rest = args[1..];
        }
        else
        {
            (cmd, rest) = ("run", args);
        }
        if (cmd == "run")
        {
            // --perch PORT before the file: the debugged program's process,
            // as the adapter starts it. Everything after the file is the
            // program's own.
            string? perch = null;
            if (rest.Length >= 2 && rest[0] == "--perch") { perch = rest[1]; rest = rest[2..]; }
            return rest.Length == 0 ? null : new Command("run", rest[0], rest[1..]) { Perch = perch };
        }

        // Every other verb: its flags in either position, like a .NET
        // tool's, and one target — or several folders for a library.
        var flags = new List<string>();
        var targets = new List<string>();
        for (int i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "-o" or "--out" or "--gate" or "--mode" or "--sources":
                    if (i + 1 >= rest.Length) return null;
                    flags.Add(rest[i]);
                    flags.Add(rest[++i]);
                    break;
                case "--write" or "--inputs" or "--debug" or "--library":
                    flags.Add(rest[i]);
                    break;
                default:
                    if (rest[i].StartsWith('-')) return null;
                    targets.Add(rest[i]);
                    break;
            }
        }
        if (targets.Count == 0) return null;
        bool many = cmd == "library" || (cmd == "gen" && flags.Contains("--library"));
        if (!many && targets.Count != 1) return null;
        if (cmd == "manifest" && flags.Count(f => f is "--write" or "--inputs" or "--gate") > 1) return null;
        return new Command(cmd, targets[0], flags.Concat(targets.Skip(1)).ToArray());
    }

    public static int Usage()
    {
        Console.Error.WriteLine(
            "usage: mill [run] FILE.shoddy [program args...]\n" +
            "       mill dap                 the debug adapter, for an editor (full mill)\n" +
            "       mill weave FILE.shoddy [-o DIR]\n" +
            "       mill machine [--debug] LIB.shoddy [-o DIR|FILE.dll]\n" +
            "       mill library [--debug] FOLDER... -o FILE.dll [--sources REL]\n" +
            "       mill gen FILE.shoddy\n" +
            "       mill gen --library [--debug] FOLDER... [-o FILE.cs] [--sources REL]\n" +
            "       mill lex FILE.shoddy\n" +
            "       mill manifest [--write | --inputs [-o DIR] | --gate MODE] [--mode MODE] TARGET\n" +
            "       any verb: --lib FILE.dll names a compiled library Includes resolve against;\n" +
            "                 --libsrc DIR names a library's source folder (both repeatable)");
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
                if (cmd.Has("--library"))
                {
                    List<string> sources = LibraryGen.SourcesIn(cmd.Targets);
                    if (sources.Count == 0) throw new ShoddyError(0, "no machine sources to generate");
                    string? outFile = cmd.Flag("-o", "--out");
                    string rel = cmd.Flag("--sources")
                        ?? (outFile == null ? "." : Weaver.RelativeDir(outFile, sources[0]));
                    bool debug = cmd.Has("--debug");
                    LibraryGen lib = LibraryGen.Generate(sources, debug, opt.LibrariesFor(debug), rel);
                    string text = lib.ToSource();
                    if (outFile == null)
                    {
                        Console.Write(text);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
                        // Unchanged text is left alone: the file is a build
                        // input downstream, and a moved timestamp is a
                        // recompile.
                        if (!File.Exists(outFile) || File.ReadAllText(outFile) != text)
                            File.WriteAllText(outFile, text);
                        Console.Error.WriteLine(
                            $"mill: generated {lib.Units.Count} machine{(lib.Units.Count == 1 ? "" : "s")} -> {outFile}");
                    }
                    return 0;
                }
                (ShoddyProgram prog, _) = ParseWithMachines(cmd.File, opt);
                Console.Write(Weaver.GenerateSource(prog));
                return 0;
            }
            case "weave":
            {
                (ShoddyProgram prog, MachineSet machines) = ParseWithMachines(cmd.File, opt);
                string outDir = cmd.Flag("-o", "--out")
                    ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cmd.File))!, DefaultOutDir);
                string outDll = Path.Combine(outDir, Path.GetFileNameWithoutExtension(cmd.File) + ".dll");
                Weaver.Weave(prog, outDll, machines.Machines);
                int n = Weaver.LibrariesOf(machines.Machines).Count();
                string via = n > 0 ? $" (against {n} librar{(n == 1 ? "y" : "ies")})" : "";
                Console.Error.WriteLine(
                    $"mill: wove {cmd.File} -> {outDll}{via}   (run: dotnet \"{outDll}\")");
                return 0;
            }
            case "machine":
            {
                bool debug = cmd.Has("--debug");
                // An instrumented machine links against the instrumented
                // weaves of the machines it includes, so a machine built
                // here and one the debugger loads are the same artifact.
                (ShoddyProgram prog, MachineSet machines) = ParseWithMachines(cmd.File, opt, debug);
                string outDll = MachineOut(cmd.File, cmd.Flag("-o", "--out"), debug);
                Weaver.WeaveMachine(prog, cmd.File, outDll, machines.Machines, debug);
                Console.Error.WriteLine(
                    $"mill: built {(debug ? "instrumented " : "")}machine {cmd.File} -> {outDll}");
                return 0;
            }
            case "library":
            {
                bool debug = cmd.Has("--debug");
                string? outDll = cmd.Flag("-o", "--out");
                if (outDll == null || !outDll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    throw new ShoddyError(0, "library needs -o FILE.dll: the assembly to write");
                List<string> sources = LibraryGen.SourcesIn(cmd.Targets);
                Weaver.WeaveLibrary(sources, outDll, Path.GetFileNameWithoutExtension(outDll), debug,
                                    opt.LibrariesFor(debug), cmd.Flag("--sources"));
                Console.Error.WriteLine(
                    $"mill: built {(debug ? "instrumented " : "")}library of {sources.Count} machine{(sources.Count == 1 ? "" : "s")} -> {outDll}");
                return 0;
            }
            case "manifest":
            {
                if (cmd.Has("--inputs"))
                    return Manifest.Inputs(cmd.File, cmd.Flag("-o", "--out"), opt.LibrariesFor(false));
                if (cmd.Has("--gate"))
                    return Manifest.Gate(cmd.File, cmd.Flag("--gate")!, cmd.Flag("--mode"));
                return Manifest.Run(cmd.File, write: cmd.Has("--write"), opt.LibrariesFor(false));
            }
            default:
                throw new ShoddyError(0, $"internal: '{cmd.Verb}' is not a pure verb");
        }
    }

    /// <summary>Where `mill machine` writes: the -o given (a .dll path,
    /// or a folder to put the conventionally named DLL in), else bin/
    /// beside the source — bin/debug/ for the instrumented weave, so the
    /// two twins never share a path.</summary>
    public static string MachineOut(string sbPath, string? outArg, bool debug)
    {
        string file = MachineSet.AssemblyNameFor(sbPath) + ".dll";
        if (outArg != null)
            return outArg.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? outArg
                : Path.Combine(outArg, file);
        string bin = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sbPath))!, DefaultOutDir);
        return Path.Combine(debug ? Path.Combine(bin, "debug") : bin, file);
    }

    /// <summary>The mill's own pipeline: read with machine resolution,
    /// seed, parse, lint. Warnings go to stderr — they never stop a
    /// build, and stdout belongs to the program. Unknown words are
    /// errors: nothing legitimate survives resolution, and runtime would
    /// die at the same spot with less context. With <paramref name="debug"/>,
    /// every Include resolves to its instrumented weave.</summary>
    public static (ShoddyProgram, MachineSet) ParseWithMachines(string file, Options opt, bool debug = false)
    {
        var machines = new MachineSet(debug, opt.LibrariesFor(debug));
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
