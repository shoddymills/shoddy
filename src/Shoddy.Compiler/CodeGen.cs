// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Globalization;
using System.Text;
using Shoddy.Runtime;

namespace Shoddy.Compiler;

/// <summary>
/// The weave: emits C# source from a parsed program. Word resolution
/// (case-folded env → globals → defs → machine defs → constructors →
/// builtins → field accessors) happens here, at compile time, mirroring
/// the interpreter's runtime order. TAKE bindings become C# locals — CLR
/// closures replace the captured-environment machinery — and a rebind
/// allocates a fresh local so existing closures keep seeing the old
/// value, exactly like the immutable env chain. Self-tail-recursion
/// compiles to `continue` in a `while (true)` loop: arguments travel on
/// the value stack, so the tail call is just a jump back to the def's
/// own TAKE.
///
/// Two output shapes: a program (static class Woven with Run/Main) or a
/// machine (a public static class under Shoddy.Machines with its manifest
/// as assembly attributes). Every def takes the Engine as a parameter,
/// so machine methods run against whatever program instantiates them.
/// </summary>
public sealed class CodeGen
{
    readonly ShoddyProgram prog;
    readonly string? machineClass;      // short class name; null = program
    readonly StringBuilder sb = new();
    int ind;

    readonly Dictionary<string, string> defIds = new();     // internal defs
    readonly Dictionary<string, string> typeExpr = new();   // any type -> C# expr
    readonly Dictionary<string, string> typeField = new();  // internal type -> member
    readonly Dictionary<string, string> globalIds = new();
    readonly HashSet<string> usedIds = new() { "MT", "Run", "Main", "rt" };
    readonly List<string> siteDecls = new();

    // during initQuot emission: which globals are assigned so far, so a
    // forward reference resolves the way the interpreter's env would
    readonly HashSet<string> assignedGlobals = new();
    bool inInit;

    int siteN, localN, tmpN;

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Debug (perch) mode: emit rt.Dbg line hooks, Enter/Exit frames,
    // Bind/Mark/Release for the variables view. Output is unchanged —
    // only the instrumentation calls are added.
    readonly bool debug;
    readonly IReadOnlyList<MachineInfo>? deps;
    // A machine's own source file, when weaving one. Only names declared
    // *here* go in the manifest: what a machine includes is its own
    // business, and a spliced dependency's words are not this machine's
    // to export. Without this, whether a dependency happened to be
    // compiled decided the exported surface — the same source producing
    // two different public interfaces.
    readonly string? ownFile;
    readonly Dictionary<string, int> fileIds = new();
    readonly List<string> fileList = new();

    /// <summary>Was this name declared in the machine's own source, rather
    /// than in a file it included? Only those belong in the manifest.
    ///
    /// Unknown names answer true: every Def, Type and Let is recorded by
    /// the parser, so a miss means a name this rule was never meant to
    /// judge, and the safe direction is to keep exporting it.
    ///
    /// Note this treats *any* spliced include as a dependency. Machines in
    /// the tree are single-file, so that is exact today; a machine later
    /// split across sibling files would need its own files distinguished
    /// from the machines it includes.</summary>
    bool DeclaredHere(string name)
    {
        if (ownFile == null) return true;                     // not weaving a machine
        if (!prog.Sites.TryGetValue(name, out DeclSite s)) return true;
        if (s.File == null) return true;
        return string.Equals(Path.GetFullPath(s.File), ownFile,
                             StringComparison.OrdinalIgnoreCase);
    }

    public CodeGen(ShoddyProgram prog, string? machineClass, bool debug,
                   IReadOnlyList<MachineInfo>? deps = null, string? ownFile = null)
    {
        this.prog = prog;
        this.machineClass = machineClass;
        this.debug = debug;
        this.deps = deps;
        this.ownFile = ownFile == null ? null : Path.GetFullPath(ownFile);
        if (debug)
        {
            if (prog.InitQuot != null) CollectFiles(prog.InitQuot);
            foreach (Quot body in prog.Defs.Values) CollectFiles(body);
        }
    }

    void CollectFiles(Quot q)
    {
        foreach (Node n in q.Items)
        {
            if (n.File != null && !fileIds.ContainsKey(n.File))
            {
                fileIds[n.File] = fileList.Count;
                fileList.Add(n.File);
            }
            if (n.Q != null) CollectFiles(n.Q);
            if (n.ElseQ != null) CollectFiles(n.ElseQ);
        }
    }

    // ---- emission helpers ---------------------------------------------

    void W(string line)
    {
        if (line.Length > 0) sb.Append(' ', ind * 4);
        sb.Append(line).Append('\n');
    }

    void Open() { W("{"); ind++; }
    void Close(string suffix = "") { ind--; W("}" + suffix); }

    string Mangle(string prefix, string name)
    {
        var b = new StringBuilder(prefix);
        foreach (char c in name)
        {
            if (c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_') b.Append(c);
            else b.Append('x').Append(((int)c).ToString("X2"));
        }
        if (b.Length == 0 || b[0] is >= '0' and <= '9') b.Insert(0, '_');
        string id = b.ToString();
        string final = id;
        int n = 2;
        while (!usedIds.Add(final)) final = id + "_" + n++;
        return final;
    }

    static string StrLit(string s)
    {
        var b = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                case '\n': b.Append("\\n"); break;
                case '\t': b.Append("\\t"); break;
                case '\r': b.Append("\\r"); break;
                default:
                    if (c < ' ' || c > '\x7e') b.Append("\\u").Append(((int)c).ToString("X4"));
                    else b.Append(c);
                    break;
            }
        }
        return b.Append('"').ToString();
    }

    static string StrArr(IEnumerable<string> ss) =>
        $"new string[] {{ {string.Join(", ", ss.Select(StrLit))} }}";

    static string NumLit(double d) => d.ToString("R", Inv) + "d";

    static string Render(Node n)
    {
        var sw = new StringWriter();
        Printer.NodeRepr(sw, n);
        return sw.ToString();
    }

    // ---- scopes --------------------------------------------------------

    sealed class Scope
    {
        public readonly Dictionary<string, string> Map = new();
        public readonly Scope? Parent;
        public Scope(Scope? parent = null) => Parent = parent;

        public string? Find(string name)
        {
            for (Scope? s = this; s != null; s = s.Parent)
                if (s.Map.TryGetValue(name, out string? id)) return id;
            return null;
        }
    }

    bool GlobalVisible(string name) =>
        globalIds.ContainsKey(name) && (!inInit || assignedGlobals.Contains(name));

    // ---- top level -----------------------------------------------------

    public string Generate()
    {
        bool machine = machineClass != null;
        if (!machine && prog.FindDef("MAIN") == null)
            throw new ShoddyError(0, "no DEF MAIN found");
        if (machine && prog.InitQuot != null)
            throw new ShoddyError(0, "machines cannot have top-level Let");
        if (machine) usedIds.Add(machineClass!);

        foreach (TypeDef t in prog.Types)
        {
            string f = machine ? Mangle("", t.Name) : Mangle("T_", t.Name);
            typeField[t.Name] = f;
            typeExpr[t.Name] = f;
        }
        foreach ((string name, ExternalType et) in prog.ExternalTypes)
            typeExpr[name] = et.FieldRef;
        foreach (string name in prog.Defs.Keys)
            defIds[name] = machine ? Mangle("", name) : Mangle("D_", name);
        if (prog.InitQuot != null)
            foreach (Node n in prog.InitQuot.Items)
                if (n.T == NType.Take)
                    foreach (string name in n.Names)
                        globalIds[name] = Mangle("G_", name);

        W("// woven by mill — generated C#; do not edit");
        W("#pragma warning disable CS0162, CS0164, CS0219, CS8321");
        W("using System;");
        W("using System.Collections.Generic;");
        W("using System.IO;");
        W("using Shoddy.Runtime;");
        W("");
        if (machine)
        {
            string full = $"Shoddy.Machines.{machineClass}";
            W($"[assembly: ShoddyMachine({StrLit(full)})]");
            // Declared dependencies survive even when Roslyn drops an
            // unused assembly reference (a pure re-export Include).
            if (deps != null)
                foreach (MachineInfo dm in deps)
                    W($"[assembly: ShoddyDependsOn({StrLit(dm.AssemblyName)})]");
            // The manifest carries each word's stack effect when the
            // linter could infer one, so downstream programs get their
            // machine calls depth-checked without reading the source.
            Dictionary<string, Lint.DefEffect> fx = Lint.DefEffects(prog);
            foreach ((string name, string id) in defIds)
            {
                if (!DeclaredHere(name)) continue;
                W(fx.TryGetValue(name, out Lint.DefEffect e)
                    ? $"[assembly: ShoddyDef({StrLit(name)}, {StrLit(id)}, Pops = {e.Pops}, Pushes = {e.Pushes})]"
                    : $"[assembly: ShoddyDef({StrLit(name)}, {StrLit(id)})]");
            }
            foreach (TypeDef t in prog.Types)
            {
                if (!DeclaredHere(t.Name)) continue;
                W($"[assembly: ShoddyType({StrLit(t.Name)}, {StrLit(t.Disp ?? t.Name)}, " +
                  $"{StrLit(typeField[t.Name])}, {StrArr(t.Fields)}, {StrArr(t.FDisp)})]");
            }
            W("");
            W("namespace Shoddy.Machines;");
            W("");
            W($"public static class {machineClass}");
        }
        else
        {
            W("public static class Woven");
        }
        Open();
        W("static TypeDef MT(string name, string disp, string[] fields, string[] fdisp)");
        Open();
        W("var t = new TypeDef { Name = name, Disp = disp };");
        W("for (int i = 0; i < fields.Length; i++) { t.Fields.Add(fields[i]); t.FDisp.Add(fdisp[i]); }");
        W("return t;");
        Close();
        W("");
        foreach (TypeDef t in prog.Types)
            W($"{(machine ? "public " : "")}static readonly TypeDef {typeField[t.Name]} = " +
              $"MT({StrLit(t.Name)}, {StrLit(t.Disp ?? t.Name)}, {StrArr(t.Fields)}, {StrArr(t.FDisp)});");
        if (prog.Types.Count > 0) W("");
        foreach (string g in globalIds.Values)
            W($"static Value {g};");
        if (globalIds.Count > 0) W("");

        foreach ((string name, Quot body) in prog.Defs)
        {
            W($"{(machine ? "public " : "")}static void {defIds[name]}(Engine rt)  // {name}");
            Open();
            if (debug)
            {
                Node? first = body.Items.Count > 0 ? body.Items[0] : null;
                int fid = first?.File != null ? fileIds[first.File] : 0;
                W($"rt.Enter({StrLit(name)}, {fid}, {first?.Line ?? 0});");
                W("try");
                Open();
            }
            W("while (true)");
            Open();
            if (debug) W("rt.Release(0);   // fresh bindings per (tail-call) iteration");
            EmitQuotBody(body, new Scope(), name);
            W("return;");
            Close();
            if (debug)
            {
                Close();
                W("finally { rt.Exit(); }");
            }
            Close();
            W("");
        }

        if (!machine)
        {
            W("public static int Run(TextWriter o, TextReader i, string[] args)");
            Open();
            W("var rt = new Engine(o, i, args);");
            if (debug)
            {
                W("rt.Sink = Engine.PendingSink;");
                W($"rt.Files({StrArr(fileList)});");
            }
            W("try");
            Open();
            EmitInit();
            W($"{defIds["MAIN"]}(rt);");
            W("return 0;");
            Close();
            W("catch (ShoddyError e)");
            Open();
            W("Console.Error.Write(\"ERROR\");");
            W("if (e.Line > 0) Console.Error.Write(\" (line \" + e.Line + \")\");");
            W("Console.Error.WriteLine(\": \" + e.Message);");
            W("return 1;");
            Close();
            Close();
            W("");
            W("public static int Main() => Run(Console.Out, Console.In, System.Environment.GetCommandLineArgs()[1..]);");
            W("");
        }
        foreach (string s in siteDecls) W(s);
        Close();
        return sb.ToString();
    }

    void EmitInit()
    {
        if (prog.InitQuot == null) return;
        inInit = true;
        var sc = new Scope();
        foreach (Node n in prog.InitQuot.Items)
        {
            if (n.T == NType.Take)      // a top-level Let binding
            {
                for (int k = n.Names.Count - 1; k >= 0; k--)
                {
                    W($"{globalIds[n.Names[k]]} = rt.Pop({n.Line});");
                    if (debug)
                        W($"rt.BindGlobal({StrLit(n.Names[k])}, {globalIds[n.Names[k]]});");
                    assignedGlobals.Add(n.Names[k]);
                }
            }
            else
            {
                EmitNode(n, sc, null);
            }
        }
        inInit = false;
    }

    // ---- statement emission --------------------------------------------

    void EmitQuotBody(Quot q, Scope sc, string? tailDef)
    {
        (string File, int Line)? last = null;    // per-block: branches re-hook
        for (int k = 0; k < q.Items.Count; k++)
        {
            Node n = q.Items[k];
            if (debug && n.File != null && (last == null || last.Value != (n.File, n.Line)))
            {
                W($"rt.Dbg({fileIds[n.File]}, {n.Line});");
                last = (n.File, n.Line);
            }
            EmitNode(n, sc, k == q.Items.Count - 1 ? tailDef : null);
        }
    }

    void EmitNode(Node n, Scope sc, string? tailDef)
    {
        switch (n.T)
        {
            case NType.Num: W($"rt.PushNum({NumLit(n.Num)});"); return;
            case NType.Str: W($"rt.PushStr({StrLit(n.Str!)});"); return;
            case NType.Bool: W($"rt.PushBool({(n.BVal ? "true" : "false")});"); return;
            case NType.Quot: EmitQuotLiteral(n, sc); return;
            case NType.List: EmitList(n, sc); return;
            case NType.Take:
                for (int k = n.Names.Count - 1; k >= 0; k--)
                {
                    string id = $"v{localN++}_{Ident(n.Names[k])}";
                    W($"var {id} = rt.Pop({n.Line});");
                    sc.Map[n.Names[k]] = id;    // rebind = fresh local; old
                                                // closures keep the old one
                    if (debug && n.Names[k][0] != '\u0001')
                        W($"rt.Bind({StrLit(n.Names[k])}, {id});");
                }
                return;
            case NType.If:
            {
                W($"if (rt.PopBool({n.Line}, \"IF\"))");
                Open();
                int bm = tmpN++;
                if (debug) W($"int bm{bm} = rt.Mark();");
                EmitQuotBody(n.Q!, new Scope(sc), tailDef);
                if (debug) W($"rt.Release(bm{bm});");
                Close();
                W("else");
                Open();
                if (debug) W($"int bm{bm}e = rt.Mark();");
                EmitQuotBody(n.ElseQ!, new Scope(sc), tailDef);
                if (debug) W($"rt.Release(bm{bm}e);");
                Close();
                return;
            }
            case NType.With:
                W($"rt.With({n.Line}, {StrArr(n.Names)});");
                return;
            case NType.Pat: EmitPat(n, sc); return;
            case NType.Word: EmitWord(n, sc, tailDef); return;
            default:
                throw new ShoddyError(n.Line, $"internal: cannot weave node {n.T}");
        }
    }

    static string Ident(string name)
    {
        var b = new StringBuilder();
        foreach (char c in name)
            if (c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_') b.Append(c);
        return b.ToString();
    }

    /// <summary>A word that names more than one namespace's declaration is
    /// refused before it can fall through to a builtin or a field accessor,
    /// which would silently pick a meaning the writer did not choose.</summary>
    void RefuseAmbiguity(Node n)
    {
        List<string>? amb = prog.Ambiguity(n.Str!, n.File);
        if (amb == null) return;
        throw new ShoddyError(n.Line, n.File,
            $"'{n.Str}' is ambiguous — it names " + string.Join(" and ",
                amb.Select(c => $"{c} ({prog.DescribeSite(c)})")) +
            $"; write '{n.Str} In <namespace>' to choose");
    }

    void EmitWord(Node n, Scope sc, string? tailDef)
    {
        string written = n.Str!;
        // Locals are looked up as written: a parameter is never namespaced.
        string? loc = sc.Find(written);
        if (loc != null) { W($"rt.Push({loc});"); return; }

        // Everything below is a top-level name, so it resolves through the
        // namespace the use site is written in.
        RefuseAmbiguity(n);
        string name = prog.ResolveName(written, n.File);

        if (GlobalVisible(name)) { W($"rt.Push({globalIds[name]});"); return; }
        if (defIds.TryGetValue(name, out string? d))
        {
            if (name == tailDef) W("continue;  // self tail call — the loop is the TCO");
            else W($"{d}(rt);");
            return;
        }
        if (prog.ExternalDefs.TryGetValue(name, out string? x)) { W($"{x}(rt);"); return; }
        if (typeExpr.TryGetValue(name, out string? t)) { W($"rt.Ctor({t}, {n.Line});"); return; }
        if (Engine.BuiltinWords.Contains(name)) { W($"rt.Op({StrLit(name)}, {n.Line});"); return; }
        // The accessor word may be namespaced; the field it reads is not.
        if (prog.Accessors.TryGetValue(name, out string? fld) || prog.AnyTypeHasField(name))
            { W($"rt.Field({StrLit(fld ?? name)}, {n.Line});"); return; }
        // A word a machine declares but does not export is neither a typo
        // nor a name that might turn up at runtime — it is a missing
        // Include, and we know which one. Refuse it here rather than
        // deferring to rt.UnknownWord, because the linter only reports
        // what it can model: an unknown word inside a quotation of unknown
        // effect reaches this line unmentioned and dies mid-run instead.
        if (prog.Unseeded.TryGetValue(name, out (string Declares, string? Via) a))
        {
            string via = a.Via == null ? "" : $", which {a.Via} includes but does not export";
            throw new ShoddyError(n.Line, n.File,
                $"unknown word: {name} — declared in {a.Declares}{via}. " +
                $"Add: Include \"{a.Declares}\"");
        }
        W($"rt.UnknownWord({StrLit(name)}, {n.Line});");
    }

    void EmitList(Node n, Scope sc)
    {
        int t = tmpN++;
        Open();
        W($"var els{t} = new List<Value>();");
        foreach (Node el in n.Q!.Items)         // N_QUOT wrappers
        {
            Open();
            W($"int d{t} = rt.Depth;");
            EmitQuotBody(el.Q!, new Scope(sc), null);
            W($"els{t}.Add(rt.TakeOne(d{t}, {n.Line}));");
            Close();
        }
        W($"rt.PushList(els{t}, {n.Line});");
        Close();
    }

    /// <summary>A quotation literal: a per-site identity object (mirrors
    /// the C's parse-time Quot pointer, so `=` works the same), an item
    /// array for sequence ops and printing, and the whole body as one
    /// closure — items resolve without locals, exactly as the interpreter
    /// evaluates extracted items outside their quotation's environment.</summary>
    void EmitQuotLiteral(Node n, Scope sc)
    {
        int s = siteN++;
        siteDecls.Add($"static readonly object site{s} = new object();");
        Open();
        W($"var it{s} = new QItem[{n.Q!.Items.Count}];");
        for (int k = 0; k < n.Q.Items.Count; k++)
            EmitQItem(n.Q.Items[k], $"it{s}[{k}]", sc);
        W($"rt.PushCQuot(site{s}, it{s}, () =>");
        Open();
        if (debug) W($"int qm{s} = rt.Mark();");
        EmitQuotBody(n.Q, new Scope(sc), null);
        if (debug) W($"rt.Release(qm{s});");
        Close(");");
        Close();
    }

    void EmitQItem(Node item, string tgt, Scope sc)
    {
        switch (item.T)
        {
            case NType.Num: W($"{tgt} = QItem.OfValue(Value.OfNum({NumLit(item.Num)}));"); return;
            case NType.Str: W($"{tgt} = QItem.OfValue(Value.OfStr({StrLit(item.Str!)}));"); return;
            case NType.Bool: W($"{tgt} = QItem.OfValue(Value.OfBool({(item.BVal ? "true" : "false")}));"); return;
            case NType.Word:
            {
                RefuseAmbiguity(item);
                string name = prog.ResolveName(item.Str!, item.File);
                string act;
                if (GlobalVisible(name)) act = $"() => rt.Push({globalIds[name]})";
                else if (defIds.TryGetValue(name, out string? d)) act = $"() => {d}(rt)";
                else if (prog.ExternalDefs.TryGetValue(name, out string? x)) act = $"() => {x}(rt)";
                else if (typeExpr.TryGetValue(name, out string? t)) act = $"() => rt.Ctor({t}, {item.Line})";
                else if (Engine.BuiltinWords.Contains(name)) act = $"() => rt.Op({StrLit(name)}, {item.Line})";
                else if (prog.Accessors.TryGetValue(name, out string? qf))
                    act = $"() => rt.Field({StrLit(qf)}, {item.Line})";
                else if (prog.AnyTypeHasField(name)) act = $"() => rt.Field({StrLit(name)}, {item.Line})";
                else act = $"() => rt.UnknownWord({StrLit(name)}, {item.Line})";
                W($"{tgt} = QItem.OfCode({act}, {StrLit(name)});");
                return;
            }
            case NType.Quot:
                W($"{tgt} = QItem.OfCode(() =>");
                Open();
                EmitQuotLiteral(item, sc);
                Close($", {StrLit(Render(item))});");
                return;
            default:
                // IF/TAKE/... as extracted items are only meaningful through
                // the body closure; standalone evaluation is an error.
                W($"{tgt} = QItem.OfCode(() => rt.UnknownWord({StrLit(Render(item))}, {item.Line}), " +
                  $"{StrLit(Render(item))});");
                return;
        }
    }

    /// <summary>Pattern match: ( v -- bool ), binding binder names as
    /// fresh locals for the nodes that follow (guard and body).</summary>
    void EmitPat(Node n, Scope sc)
    {
        int t = tmpN++;
        var binders = new List<(string Name, string Id)>();
        CollectBinders(n.P!, binders);
        foreach ((string _, string id) in binders)
            W($"Value {id} = null;");
        W($"Value s{t} = rt.Pop({n.Line});");
        W($"bool m{t} = true;");
        W("do");
        Open();
        int bi = 0;
        GenMatch(n.P!, $"s{t}", t, binders, ref bi);
        Close(" while (false);");
        W($"rt.PushBool(m{t});");
        if (debug && binders.Count > 0)
        {
            W($"if (m{t})");
            Open();
            foreach ((string name, string id) in binders)
                W($"rt.Bind({StrLit(name)}, {id});");
            Close();
        }
        foreach ((string name, string id) in binders)
            sc.Map[name] = id;
    }

    void CollectBinders(Pat p, List<(string, string)> binders)
    {
        if (p.Type != null)
            foreach (Pat sub in p.Subs) CollectBinders(sub, binders);
        else
            binders.Add((p.Name!, $"v{localN++}_{Ident(p.Name!)}"));
    }

    void GenMatch(Pat p, string src, int t, List<(string Name, string Id)> binders, ref int bi)
    {
        if (p.Type == null)
        {
            W($"{binders[bi++].Id} = {src};");
            return;
        }
        W($"if ({src}.T != VType.Rec || !ReferenceEquals({src}.RType, {typeExpr[p.Type]})) {{ m{t} = false; break; }}");
        for (int k = 0; k < p.Subs.Count; k++)
        {
            Pat sub = p.Subs[k];
            if (sub.Type == null)
            {
                W($"{binders[bi++].Id} = {src}.Elems[{k}];");
            }
            else
            {
                string sv = $"s{t}_{tmpN++}";
                W($"var {sv} = {src}.Elems[{k}];");
                GenMatch(sub, sv, t, binders, ref bi);
            }
        }
    }
}
