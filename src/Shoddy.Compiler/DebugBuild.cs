// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.CodeAnalysis;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Compiler;

/// <summary>
/// A program woven for the debugger: its entry point, and an evaluator
/// for any Shoddy expression while it is stopped.
///
/// <para><b>How an expression is evaluated.</b> It is compiled, never
/// interpreted: the language has one execution path and the debugger does
/// not get a second. The expression becomes the body of a Def of its own
/// whose parameters are the bindings in view, woven against the running
/// program's own assembly (its Defs and types are public in a debug
/// weave) and the machines it was built with. So it can call the
/// program's Defs, any machine word it included, any builtin, build its
/// records and read their fields, exactly as a line of the program could.
/// It runs on the stopped program's own engine, with the debugger's line
/// hooks switched off for the duration, and leaves the value stack as it
/// found it.</para>
/// </summary>
public sealed class DebugBuild
{
    readonly ShoddyProgram prog;
    readonly IReadOnlyList<MachineInfo> machines;
    readonly byte[] image;
    readonly IReadOnlyDictionary<string, string> defMembers;
    readonly IReadOnlyDictionary<string, string> typeMembers;
    readonly Dictionary<string, MethodInfo> compiled = new(StringComparer.Ordinal);

    /// <summary>The program's entry point: output, input, arguments.</summary>
    public Func<TextWriter, TextReader, string[], int> Run { get; }

    /// <summary>The name the expression's Def is woven under. A plain
    /// word, so it lexes like any other.</summary>
    const string EvalWord = "PERCHEVALUATION";

    internal DebugBuild(ShoddyProgram prog, IReadOnlyList<MachineInfo> machines, byte[] image,
                        IReadOnlyDictionary<string, string> defMembers,
                        IReadOnlyDictionary<string, string> typeMembers,
                        Func<TextWriter, TextReader, string[], int> run)
    {
        this.prog = prog;
        this.machines = machines;
        this.image = image;
        this.defMembers = defMembers;
        this.typeMembers = typeMembers;
        Run = run;
    }

    /// <summary>
    /// Evaluate <paramref name="expression"/> with <paramref name="bindings"/>
    /// in view (later entries shadow earlier ones of the same name) on the
    /// stopped engine <paramref name="rt"/>. Answers every value the
    /// expression left, in order: one for an ordinary expression, none for
    /// a call that returns nothing.
    ///
    /// <para>A program error, a compile error or an unknown word throws
    /// <see cref="ShoddyError"/> with the language's own message. The
    /// engine is restored either way.</para>
    /// </summary>
    public Value[] Evaluate(Engine rt, IReadOnlyList<(string Name, Value V)> bindings, string expression)
    {
        // Last wins, in first-seen order: the parameter list.
        var order = new List<string>();
        var values = new Dictionary<string, Value>(StringComparer.Ordinal);
        foreach ((string name, Value v) in bindings)
        {
            if (!IsParameterName(name)) continue;
            if (!values.ContainsKey(name)) order.Add(name);
            values[name] = v;
        }

        MethodInfo method = Compile(expression.Trim(), order);

        Value[]? result = null;
        Exception? failure = null;
        // Its own thread with the program stack, as every launcher gives a
        // program: an expression may recurse as deeply as the program can.
        var thread = new Thread(() =>
        {
            int depth = rt.Depth;
            int frames = rt.Frames.Count;
            IDebugSink? sink = rt.Sink;
            rt.Sink = null;                 // no stops inside an evaluation
            try
            {
                foreach (string name in order) rt.Push(values[name]);
                try { method.Invoke(null, new object?[] { rt }); }
                catch (TargetInvocationException e) when (e.InnerException != null)
                {
                    ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                }
                var got = new Value[rt.Depth - depth];
                for (int k = 0; k < got.Length; k++) got[k] = rt.StackView[depth + k];
                result = got;
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                rt.DbgRestore(depth);
                if (rt.Frames.Count > frames) rt.Frames.RemoveRange(frames, rt.Frames.Count - frames);
                rt.Sink = sink;
            }
        }, Engine.ProgramStackBytes) { IsBackground = true, Name = "shoddy-evaluate" };
        thread.Start();
        thread.Join();

        if (failure is ShoddyError se) throw new ShoddyError(0, se.Message);
        if (failure != null) throw new ShoddyError(0, failure.Message);
        return result!;
    }

    /// <summary>A binding the expression can see as a parameter: a plain
    /// word. Hidden temporaries (and anything else that could not be
    /// written as a name) are left out.</summary>
    static bool IsParameterName(string name)
    {
        if (name.Length == 0 || !char.IsAsciiLetter(name[0])) return false;
        foreach (char c in name)
            if (!char.IsAsciiLetterOrDigit(c) && c != '_') return false;
        return true;
    }

    MethodInfo Compile(string expression, IReadOnlyList<string> parameters)
    {
        string key = expression + "\0" + string.Join(",", parameters);
        lock (compiled)
            if (compiled.TryGetValue(key, out MethodInfo? known)) return known;

        if (expression.Length == 0) throw new ShoddyError(0, "there is no expression to evaluate");

        // The expression as the body of a Def, one line per line of the
        // expression, read by the same lexer and parser as any program.
        var src = new System.Text.StringBuilder();
        src.Append("Def ").Append(EvalWord).Append('(').Append(string.Join(", ", parameters)).Append(")\n");
        foreach (string line in expression.Split('\n'))
            src.Append("    ").Append(line.TrimEnd('\r')).Append('\n');

        string dir = Path.Combine(Path.GetTempPath(), "shoddy-perch-eval");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".shoddy");
        File.WriteAllText(file, src.ToString());
        List<Line> lines;
        try { lines = Lexer.ReadProgram(file); }
        finally { File.Delete(file); }

        // The running program's surface: its machines' words and types
        // as it was woven against them, and its own Defs and types by the
        // public members of its debug weave.
        var seeded = new ShoddyProgram();
        foreach ((string name, string target) in prog.ExternalDefs) seeded.ExternalDefs[name] = target;
        foreach ((string name, ExternalType t) in prog.ExternalTypes) seeded.ExternalTypes[name] = t;
        foreach ((string name, (int, int) fx) in prog.ExternalEffects) seeded.ExternalEffects[name] = fx;
        foreach ((string name, (string, string?) advice) in prog.Unseeded) seeded.Unseeded[name] = advice;
        foreach ((string word, string field) in prog.Accessors) seeded.Accessors.TryAdd(word, field);
        foreach ((string name, string member) in defMembers)
            seeded.ExternalDefs[name] = $"global::Woven.{member}";
        foreach (TypeDef t in prog.Types)
            if (!Prelude.Declares(t.Name) && typeMembers.TryGetValue(t.Name, out string? field))
                seeded.ExternalTypes[t.Name] = new ExternalType(t, $"global::Woven.{field}");

        ShoddyProgram parsed;
        try { parsed = Parser.Parse(lines, seeded); }
        catch (ShoddyError e) { throw new ShoddyError(0, e.Message); }
        Lint.Result lint = Lint.Run(parsed, lines);
        if (lint.UnknownWords.Count > 0) throw new ShoddyError(0, lint.UnknownWords[0].Item1);

        var gen = new CodeGen(parsed, "PerchEvaluation", debug: false, deps: machines);
        string code;
        try { code = gen.Generate(); }
        catch (ShoddyError e) { throw new ShoddyError(0, e.Message); }

        Assembly asm;
        using (var pe = new MemoryStream())
        {
            Weaver.Compile(code, pe, OutputKind.DynamicallyLinkedLibrary, machines,
                           "perch-eval-" + Guid.NewGuid().ToString("N"),
                           new[] { MetadataReference.CreateFromImage(image) });
            pe.Position = 0;
            asm = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(pe);
        }
        MethodInfo method = asm.GetType("Shoddy.Machines.PerchEvaluation")!
            .GetMethod(gen.DefMembers[EvalWord])!;
        lock (compiled) compiled[key] = method;
        return method;
    }
}
