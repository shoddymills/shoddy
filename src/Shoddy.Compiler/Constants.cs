// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Globalization;
using Shoddy.Runtime;

namespace Shoddy.Compiler;

/// <summary>
/// A machine's top-level Lets. A machine has no Run, so it has no init
/// phase to assign a global in — which is the whole content of the rule
/// that used to refuse the Let outright. A constant needs no init phase:
/// it is built by a C# static field initializer, exactly as every machine
/// type already is, and the value exists once per process rather than
/// once per call.
///
/// THE RULE IS A PROHIBITION: the initializer contains no word calls.
/// Literals, { } list literals and type constructors, nested to any
/// depth, with four folded exceptions and the arithmetic and
/// concatenation operators over constant operands. Everything else keeps
/// the refusal, and the message names the offending word rather than the
/// feature.
///
/// The four exceptions are one exception twice over: a value kind with no
/// literal has no other spelling. ToArray and Dim are there because the
/// language has no array literal, and True and False because it has no
/// boolean one — they are runtime builtins, so without them a boolean
/// could not appear in a constant at all, which is what the spec has
/// always said it could. Both fold to a value like any other word.
///
/// A word that reaches the whitelist is folded to the builtin even where
/// the file carries a Redef of that name. That is the standing behaviour
/// for Mod, ToArray and Dim rather than anything new here, and a plain
/// Def of a builtin's name is refused by the parser long before this.
///
/// The prohibition is not conservatism. A quotation captures the
/// environment it was made in, and a captured environment shared between
/// two Engines in one process is a real hazard — the conformance suite
/// runs its goldens in-process. A literal has no free variables, so
/// there is nothing to capture, and the no-words rule is what guarantees
/// it. The emitter enforces the same thing from the other side: a woven
/// quotation body is a C# lambda closing over rt, and a static field
/// initializer has no rt, so a function-valued constant is not merely
/// unwise but inexpressible.
///
/// The fold does not reimplement the language's arithmetic — it runs the
/// initializer on a scratch Engine at compile time and serializes the
/// value that comes out. Mod and ^ therefore cannot drift, and a
/// constant division by zero is refused where it is written instead of
/// aborting a run months later.
/// </summary>
static class MachineConstants
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The words a constant initializer may name. The four folded
    /// builtins are recognised HERE, ahead of the rejection branch, so none
    /// can ever be reported as an offender: the first person to write
    /// `Let A = ToArray({1,2,3})` must not be told their constant "calls
    /// TOARRAY", which is true, useless and contradicts the
    /// documentation.</summary>
    static readonly HashSet<string> Folded = new()
    {
        "+", "-", "*", "/", "MOD", "^", "NEGATE", "&",   // R2.6 operators
        "TOARRAY", "DIM",                                // R2.2 builtins
        "TRUE", "FALSE",                                 // no boolean literal
    };

    /// <summary>One machine constant: the binding, and the C# expression
    /// that builds its value in a static field initializer.</summary>
    internal readonly record struct Constant(string Name, string Expr);

    /// <summary>Validate and fold every top-level Let in a machine.
    /// Throws the refusal, naming the offending word or the binding, for
    /// anything the rule does not admit.</summary>
    /// <param name="typeRef">A declared TypeDef to the C# expression that
    /// reaches it — a field of this class, of Prelude, or of another
    /// machine's class.</param>
    internal static List<Constant> Fold(ShoddyProgram prog, Func<TypeDef, string?> typeRef)
    {
        var done = new Dictionary<string, Value>();     // bound so far, in textual order
        var outp = new List<Constant>();
        var rt = new Engine(TextWriter.Null, TextReader.Null, Array.Empty<string>());
        var pending = new List<Node>();

        foreach (Node n in prog.InitQuot!.Items)
        {
            if (n.T != NType.Take) { pending.Add(n); continue; }

            string binding = n.Names[0];
            // Words first, quotations second, and the order matters:
            // `Let w = Map(xs, Upper)` auto-quotes Upper into a quotation
            // literal, so a single walk would report the lambda and never
            // mention MAP — which is the word the writer has to remove.
            foreach (Node e in pending) CheckWords(prog, e, done, binding);
            foreach (Node e in pending) CheckQuots(e);
            var values = new Value[n.Names.Count];
            int d0 = rt.Depth;
            try
            {
                foreach (Node e in pending) Run(prog, rt, e, done);
            }
            catch (ShoddyError ex)
            {
                // Division by zero, DIM of a negative size, a power that
                // overflows: computable in principle, wrong in fact, and
                // better said here than in the middle of a run months later.
                throw new ShoddyError(n.Line, n.File,
                    $"the constant '{binding}' cannot be computed: {ex.Message}");
            }
            // An invariant, not a diagnostic: the parser admits one
            // expression per Let and every word left is arity-checked.
            if (rt.Depth - d0 != n.Names.Count)
                throw new ShoddyError(n.Line, n.File,
                    $"internal: cannot render constant {binding} from {rt.Depth - d0} values");
            // Rightmost name on top, as the woven init phase takes them.
            for (int k = n.Names.Count - 1; k >= 0; k--) values[k] = rt.Pop(n.Line);
            for (int k = 0; k < n.Names.Count; k++)
            {
                done[n.Names[k]] = values[k];
                outp.Add(new Constant(n.Names[k], Render(values[k], typeRef, n.Line, n.File)));
            }
            pending.Clear();
        }
        return outp;
    }

    // ---- the walk, which is the specification of the rule -------------

    static void CheckWords(ShoddyProgram prog, Node n, Dictionary<string, Value> done, string binding)
    {
        switch (n.T)
        {
            case NType.Num:
            case NType.Str:
            case NType.Bool:
            case NType.Val:
            case NType.Quot:            // left to CheckQuots
                return;
            case NType.List:
                // A list literal's items are one wrapper quotation each,
                // holding the element's own expression.
                foreach (Node el in n.Q!.Items)
                    foreach (Node inner in el.Q!.Items)
                        CheckWords(prog, inner, done, binding);
                return;
            case NType.Word:
            {
                string name = prog.ResolveName(n.Str!, n.File);
                if (Folded.Contains(name)) return;              // ahead of the refusal
                if (prog.FindType(name) != null) return;         // a type constructor
                if (done.ContainsKey(name)) return;              // an earlier constant
                if (prog.Sites.TryGetValue(name, out DeclSite s) && s.Kind == "Let")
                    throw new ShoddyError(n.Line, n.File,
                        $"the constant '{binding}' cannot be computed: " +
                        $"'{name}' is not bound until later in the file");
                throw new ShoddyError(n.Line, n.File,
                    "a machine's top-level Let must bind a constant; " +
                    $"this one calls {name}");
            }
            case NType.If:
                // R2.4's complaint in a third costume. And and Or compile
                // to a short-circuiting conditional, so the writer of
                // `Let ok = True And False` typed no If at all and the
                // offending-word branch would name a word not in their
                // file. Unreachable until True and False began to fold,
                // because the condition was refused ahead of it.
                throw new ShoddyError(n.Line, n.File,
                    "a machine's top-level Let cannot hold a conditional; " +
                    "And and Or compile to one, so neither folds");
            default:
                throw new ShoddyError(n.Line, n.File,
                    "a machine's top-level Let must bind a constant; " +
                    $"this one calls {n.T.ToString().ToUpperInvariant()}");
        }
    }

    /// <summary>The quotation refusal, by its own message and never by
    /// falling through to the offending-word branch, which would report
    /// `Let f = Fn(x) => x + 1` as "calls FN". A woven quotation body is a
    /// C# lambda closing over rt, and a static field initializer has no rt,
    /// so this is what the emitter can express rather than a preference.
    /// The use case is served already: a dispatch table of functions is a
    /// zero-arg Def returning the quotation, which captures the right
    /// Engine and loses nothing, since nobody compares functions.</summary>
    static void CheckQuots(Node n)
    {
        if (n.T == NType.List)
        {
            foreach (Node el in n.Q!.Items)
                foreach (Node inner in el.Q!.Items)
                    CheckQuots(inner);
            return;
        }
        if (n.T == NType.Quot)
            throw new ShoddyError(n.Line, n.File,
                "a machine's top-level Let cannot hold a quotation; " +
                "return it from a Def instead");
    }

    // ---- the fold, on the language's own arithmetic --------------------

    static void Run(ShoddyProgram prog, Engine rt, Node n, Dictionary<string, Value> done)
    {
        switch (n.T)
        {
            case NType.Num: rt.PushNum(n.Num); return;
            case NType.Str: rt.PushStr(n.Str!); return;
            case NType.Bool: rt.PushBool(n.BVal); return;
            case NType.Val: rt.Push(n.Val!); return;
            case NType.List:
            {
                var els = new List<Value>();
                foreach (Node el in n.Q!.Items)
                {
                    int d = rt.Depth;
                    foreach (Node inner in el.Q!.Items) Run(prog, rt, inner, done);
                    els.Add(rt.TakeOne(d, n.Line));
                }
                rt.PushList(els, n.Line);
                return;
            }
            case NType.Word:
            {
                string name = prog.ResolveName(n.Str!, n.File);
                if (done.TryGetValue(name, out Value? v)) { rt.Push(v); return; }
                TypeDef? t = prog.FindType(name);
                if (t != null) { rt.Ctor(t, n.Line); return; }
                rt.Op(name, n.Line);
                return;
            }
            default:
                throw new ShoddyError(n.Line, n.File, $"internal: cannot fold node {n.T}");
        }
    }

    // ---- the value, as C# source ---------------------------------------

    /// <summary>Every factory here is static and Engine-free, which is
    /// what makes a static field initializer possible at all. A list
    /// reaches for CList (emitted beside MT), whose identity convention —
    /// the items array is the id — is the runtime's own, so a constant
    /// list is indistinguishable from one a program built.</summary>
    static string Render(Value v, Func<TypeDef, string?> typeRef, int line, string? file)
    {
        switch (v.T)
        {
            case VType.Num: return $"Value.OfNum({v.Num.ToString("R", Inv)}d)";
            case VType.Str: return $"Value.OfStr({CodeGen.StrLit(v.Str!)})";
            case VType.Bool: return $"Value.OfBool({(v.B ? "true" : "false")})";
            case VType.Arr:
                return $"Value.OfArr(new Value[] {{ {Elems(v, typeRef, line, file)} }})";
            case VType.Quot:
                return $"CList({Items(v, typeRef, line, file)})";
            case VType.Rec:
            {
                string? t = typeRef(v.RType!);
                // Both of these are invariants rather than diagnostics: the
                // walk has already refused everything that could reach them,
                // and a type that constructed a value is by definition one
                // this weave can name.
                if (t == null)
                    throw new ShoddyError(line, file,
                        $"internal: cannot render constant {v.RType!.Name} (type not reachable here)");
                return $"Value.OfRec({t}, new Value[] {{ {Elems(v, typeRef, line, file)} }})";
            }
            default:
                throw new ShoddyError(line, file,
                    $"internal: cannot render constant {Value.TypeName(v.T)}");
        }
    }

    static string Elems(Value v, Func<TypeDef, string?> typeRef, int line, string? file) =>
        string.Join(", ", v.Elems!.Select(e => Render(e, typeRef, line, file)));

    static string Items(Value v, Func<TypeDef, string?> typeRef, int line, string? file) =>
        string.Join(", ", v.CItems!.Select(it => Render(it.Lit!, typeRef, line, file)));
}
