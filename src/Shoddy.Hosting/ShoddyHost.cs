// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Reflection;
using Shoddy.Runtime;

namespace Shoddy.Hosting;

/// <summary>
/// Options a host supplies at load: where the engine's I/O goes, the
/// program arguments, and the resources behind granted capabilities.
/// The grant itself is build-time permission (ShoddyWeave's gate); the
/// resource is this run-time argument — the two are deliberately
/// separate things (C2.4a).
/// </summary>
public sealed class ShoddyHostOptions
{
    public TextWriter? Output { get; set; }
    public TextReader? Input { get; set; }
    public string[]? Args { get; set; }

    /// <summary>Arm the gated TCP/IP builtins for this host's engine —
    /// and only this one. Engine construction is serialized process-wide
    /// so two hosts with different grants cannot race the ambient
    /// switch the runtime reads at construction (D17).</summary>
    public bool AllowNet { get; set; }

    /// <summary>The directory the mill's files live under, and may not
    /// reach outside of. Validated to exist at load — a granted `file`
    /// capability with no root must fail here, at startup, not by
    /// writing beside the executable.
    ///
    /// IT IS A BOUNDARY, not merely a base. Every file word in the
    /// engine resolves its path against this directory and is refused if
    /// the result lands outside it — `..`, an absolute path elsewhere,
    /// and a symbolic link along the way all included. Before this it
    /// was only a base, and only for the paths a host resolved itself:
    /// the runtime resolved everything else against the process working
    /// directory, so `"../../elsewhere.png" PLOTSAVE` left the root
    /// behind. A host no longer needs to chdir for containment, and one
    /// that does gets the same answer.
    ///
    /// The boundary is not a defence against a hostile process sharing
    /// the root — see Shoddy.Runtime.FileRoot for exactly what it does
    /// and does not promise.</summary>
    public string? FileRoot { get; set; }
}

/// <summary>
/// Manifest-driven word lookup over woven machine assemblies: the Mode N
/// surface. Load reads each assembly's ShoddyDef manifest — attribute
/// reading only, no Roslyn, nothing from the toolchain (B2.4) — and owns
/// the one Engine the words run against. Words go one at a time: the
/// engine is a single value stack, not a thread-safe service.
/// </summary>
public sealed class ShoddyHost
{
    /// <summary>Serializes engine construction process-wide. The `net`
    /// switch is ambient and read once at construction, so construction
    /// is the one window where two hosts could race it (D17). The lock
    /// contains the race with zero runtime changes; an Engine
    /// constructor option is the named graduation if contention ever
    /// measures.</summary>
    static readonly object ConstructionGate = new();

    readonly Engine rt;
    readonly Dictionary<string, ShoddyWord> words = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mills whose build granted `file`: registered by the
    /// wiring ShoddyWeave generates, so a Load that supplies no root
    /// fails here, at startup, with the mill named — never by silently
    /// writing beside the executable (C2.4a).</summary>
    static readonly List<string> FileRootNeeds = new();

    /// <summary>Called by generated wiring, not by hand: declares that
    /// this process's build granted `file` to <paramref name="mill"/>,
    /// so every Load must supply a FileRoot.</summary>
    public static void RequireFileRoot(string mill)
    {
        lock (FileRootNeeds) FileRootNeeds.Add(mill);
    }

    public static ShoddyHost Load(params Assembly[] machines) =>
        Load(new ShoddyHostOptions(), machines);

    public static ShoddyHost Load(ShoddyHostOptions options, params Assembly[] machines)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (machines is null || machines.Length == 0)
            throw new ArgumentException("at least one machine assembly is required", nameof(machines));
        if (options.FileRoot != null && !Directory.Exists(options.FileRoot))
            throw new DirectoryNotFoundException(
                $"the file root '{options.FileRoot}' does not exist — a granted `file` " +
                "capability needs a real directory behind it");
        if (options.FileRoot is null)
        {
            lock (FileRootNeeds)
                if (FileRootNeeds.Count > 0)
                    throw new InvalidOperationException(
                        $"this project granted `file` to mill '{FileRootNeeds[0]}', so every " +
                        "ShoddyHost.Load must supply a FileRoot — the grant is build-time " +
                        "permission, the root is this run-time argument (C2.4a). Set " +
                        "ShoddyHostOptions.FileRoot to the directory the mill's declared " +
                        "paths live under.");
        }
        return new ShoddyHost(options, machines);
    }

    ShoddyHost(ShoddyHostOptions options, Assembly[] machines)
    {
        lock (ConstructionGate)
        {
            // Set-construct-restore: the engine snapshots both ambient
            // switches at construction, so inside the gate this arms (or
            // disarms) exactly this engine. Clearing when not granted
            // matters too: a process a Mode T mill armed for its lifetime
            // must still yield unarmed engines for ungranted mills — and
            // the same holds for the file root, so a host that loads two
            // mills with different roots gets two engines each confined
            // to its own.
            string? ambientNet = Environment.GetEnvironmentVariable("SHODDY_ALLOW_NET");
            string? ambientRoot = Environment.GetEnvironmentVariable(FileRoot.Variable);
            Environment.SetEnvironmentVariable("SHODDY_ALLOW_NET", options.AllowNet ? "1" : null);
            Environment.SetEnvironmentVariable(FileRoot.Variable, options.FileRoot);
            try
            {
                rt = new Engine(options.Output, options.Input, options.Args);
            }
            finally
            {
                Environment.SetEnvironmentVariable("SHODDY_ALLOW_NET", ambientNet);
                Environment.SetEnvironmentVariable(FileRoot.Variable, ambientRoot);
            }
        }

        // Mirror the woven preamble's debug half: register an (empty)
        // file table — which also roots the frame stack, so a word from
        // an INSTRUMENTED machine weave can call rt.Dbg without dying —
        // and pick up a pending sink if a launcher installed one. A
        // release-woven machine touches none of this and pays nothing.
        rt.Files(Array.Empty<string>());
        if (Engine.PendingSink != null) rt.Sink = Engine.PendingSink;

        foreach (Assembly asm in machines)
        {
            var machine = asm.GetCustomAttribute<ShoddyMachineAttribute>()
                ?? throw new ArgumentException(
                    $"'{asm.GetName().Name}' is not a Shoddy machine (no manifest)");
            Type klass = asm.GetType(machine.ClassName)
                ?? throw new ArgumentException(
                    $"'{asm.GetName().Name}' names class {machine.ClassName} in its manifest, " +
                    "but the assembly does not contain it");
            foreach (ShoddyDefAttribute d in asm.GetCustomAttributes<ShoddyDefAttribute>())
            {
                MethodInfo? m = klass.GetMethod(d.Method,
                    BindingFlags.Public | BindingFlags.Static);
                if (m is null) continue;
                words[d.Name] = new ShoddyWord(rt, d.Name, m, d.Pops, d.Pushes);
            }
        }
    }

    /// <summary>The word, case-folded like every Shoddy name. Unknown
    /// words throw with the machine-manifest vocabulary in mind — a
    /// null here would only defer the failure to a worse place.</summary>
    public ShoddyWord Word(string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        return words.TryGetValue(name, out ShoddyWord? w)
            ? w
            : throw new KeyNotFoundException(
                $"no word '{name}' in the loaded machines' manifests");
    }

    /// <summary>Every word the loaded machines export, folded.</summary>
    public IReadOnlyCollection<string> Words => words.Keys;

    // ---- Mode T ----

    /// <summary>Run a woven console mill through pipes: the assembly's
    /// <c>Woven.Run(TextWriter, TextReader, string[])</c>, on a worker
    /// task. The piped-reader contract is the host's half: a reader
    /// whose Peek() answers -1 when nothing is pending (INKEY's
    /// redirected path then yields ""), and whose Read/ReadLine block
    /// until input arrives. Cancellation is cooperative — the mill
    /// unblocks when the host's pipe does, so cancel by ending the
    /// input stream; a token cancelled before the run starts prevents
    /// it.</summary>
    public static Task<int> RunWovenAsync(Assembly woven,
        TextWriter output, TextReader input, string[] args,
        CancellationToken cancellationToken = default)
    {
        if (woven is null) throw new ArgumentNullException(nameof(woven));
        if (output is null) throw new ArgumentNullException(nameof(output));
        if (input is null) throw new ArgumentNullException(nameof(input));
        // The Woven type is found per-assembly by name, never referenced
        // in code — which is what lets several woven game assemblies
        // coexist in one host.
        Type entry = woven.GetType("Woven")
            ?? throw new ArgumentException(
                $"'{woven.GetName().Name}' is not a woven console mill (no Woven type)");
        MethodInfo run = entry.GetMethod("Run",
                BindingFlags.Public | BindingFlags.Static,
                new[] { typeof(TextWriter), typeof(TextReader), typeof(string[]) })
            ?? throw new ArgumentException(
                $"'{woven.GetName().Name}': Woven has no Run(TextWriter, TextReader, string[])");
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (int)run.Invoke(null,
                new object[] { output, input, args ?? Array.Empty<string>() })!;
        }, cancellationToken);
    }

    /// <summary>The R2 resolution, explicit and documented: a woven Mode
    /// T program constructs its own engine inside its preamble, so the
    /// per-engine arming that Load performs cannot reach it. Granting
    /// `net` to a Mode T mill therefore arms the WHOLE PROCESS for its
    /// lifetime — all-or-nothing, exactly as `mill run --allow-net`
    /// behaves at the desktop. Two concurrent Mode T mills in one
    /// process share this switch; a host that needs them isolated runs
    /// them in separate processes. Mode N engines are unaffected: Load
    /// overrides the ambient switch, in both directions, for the span
    /// of each construction.</summary>
    public static void ArmNetForProcess() =>
        Environment.SetEnvironmentVariable("SHODDY_ALLOW_NET", "1");
}

/// <summary>One word of a loaded machine, callable from any .NET
/// language. Call is the marshalling layer: machine words are emitted as
/// <c>static void M(Engine rt)</c> working the engine's stack, and this
/// pushes the arguments, invokes, and pops the answer so a caller never
/// sees the stack at all.</summary>
public sealed class ShoddyWord
{
    readonly Engine rt;
    readonly string name;
    readonly MethodInfo method;
    readonly int pops, pushes;

    internal ShoddyWord(Engine rt, string name, MethodInfo method, int pops, int pushes)
    {
        this.rt = rt;
        this.name = name;
        this.method = method;
        this.pops = pops;
        this.pushes = pushes;
    }

    /// <summary>The word's declared stack effect, when its manifest
    /// carries one; (-1, -1) when the linter could not infer it.</summary>
    public (int Pops, int Pushes) Effect => (pops, pushes);

    /// <summary>Push the arguments in order, run the word, pop the
    /// answer. A word that answers nothing returns an empty value; a
    /// word that leaves more than one value is refused by name, because
    /// a silently dropped value is a debugging session.</summary>
    public ShoddyValue Call(params ShoddyValue[] args)
    {
        if (args is null) throw new ArgumentNullException(nameof(args));
        if (pops >= 0 && args.Length != pops)
            throw new ArgumentException(
                $"{name} takes {pops} argument{(pops == 1 ? "" : "s")}, got {args.Length}");
        int before = rt.Depth;
        foreach (ShoddyValue a in args)
            rt.Push(a.V ?? throw new ArgumentException(
                "an argument is empty — it was default-constructed, not built or returned"));
        try
        {
            method.Invoke(null, new object[] { rt });
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            // The word's own abort (Error(msg) → ShoddyError) reaches the
            // caller undecorated, line number and all.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(e.InnerException).Throw();
            throw;   // unreachable
        }
        int produced = rt.Depth - before;
        if (produced == 0) return default;
        if (produced == 1) return new ShoddyValue(rt.Pop(0));
        // Restore balance before refusing, so one bad call does not
        // poison the next.
        while (rt.Depth > before) rt.Pop(0);
        throw new InvalidOperationException(
            $"{name} left {produced} values on the stack — Call answers exactly one");
    }
}
