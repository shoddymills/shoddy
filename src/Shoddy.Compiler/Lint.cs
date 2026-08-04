// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

namespace Shoddy.Compiler;

using Shoddy.Devil;
using Shoddy.Runtime;

/// <summary>Weave-time diagnostics. Doctrine: warnings, never errors —
/// except unknown words, which would die at runtime anyway — on stderr,
/// never stdout, never the exit code. Warn only on the provable: any Def
/// the checker cannot model is skipped, not guessed, and the coverage is
/// reported under --lint-verbose so erosion stays visible.</summary>
public static class Lint
{
    /// <summary>Everything one lint run found. Unknown words are errors
    /// (the caller throws); the rest are warnings.</summary>
    public sealed class Result
    {
        public readonly List<string> Warnings = new();
        public readonly List<(string Msg, string? File, int Line)> UnknownWords = new();
        public int DefsChecked;
        public int DefsTotal;
    }

    // ================= the entry point =================

    /// <summary>"unknown word: X" — and, when X is only unknown because a
    /// machine keeps it to itself, which Include would reach it.
    ///
    /// A machine exports what it declares, so including stats does not
    /// hand you seq's words. Without this the scoping rule reads as an
    /// arbitrary refusal, and the reader has to go and learn the include
    /// graph to find out that seq is where ZipWith lives.</summary>
    static string Unknown(ShoddyProgram prog, string name, string written)
    {
        if (!prog.Unseeded.TryGetValue(name, out (string Declares, string? Via) a))
            return $"unknown word: {written}";
        string via = a.Via == null ? "" : $", which {a.Via} includes but does not export";
        return $"unknown word: {written} — declared in {a.Declares}{via}. " +
               $"Add: Include \"{a.Declares}\"";
    }

    /// <summary>An Include whose machine contributes no name the program
    /// uses. Now that a machine's includes are its own business, an
    /// include is a declared interface rather than an incidental splice,
    /// so one that earns nothing is simply noise — and the four
    /// re-export includes this warning would have caught had it existed
    /// (stats→dict, net→seq, buzzer→seq, scribbler→clock) are exactly the
    /// habit the scoping rule ends.
    ///
    /// Warning only. An unused include is untidy, never wrong, and a
    /// build should not fail over tidiness.</summary>
    static void UnusedMachineIncludes(ShoddyProgram prog, IReadOnlyList<Line>? lines, Result r)
    {
        if (lines == null) return;
        // Machine class -> the names it contributed to this program.
        var byClass = new Dictionary<string, List<string>>();
        void note(string name, string target)
        {
            int cut = target.LastIndexOf('.');
            if (cut < 0) return;
            string cls = target[..cut];
            if (!byClass.TryGetValue(cls, out List<string>? l)) byClass[cls] = l = new();
            l.Add(name);
        }
        foreach ((string n, string t) in prog.ExternalDefs) note(n, t);
        foreach ((string n, ExternalType t) in prog.ExternalTypes) note(n, t.FieldRef);

        HashSet<string> used = UsedWords(prog);
        foreach ((string cls, List<string> names) in byClass)
        {
            if (names.Any(used.Contains)) continue;
            string src = cls[(cls.LastIndexOf('.') + 1)..].ToLowerInvariant() + ".shoddy";
            r.Warnings.Add($"warning: Include \"{src}\" — no word or type from it is used");
        }
    }

    /// <summary>Every word named anywhere in the program's bodies.</summary>
    static HashSet<string> UsedWords(ShoddyProgram prog)
    {
        var used = new HashSet<string>();
        void walk(Quot? q)
        {
            if (q == null) return;
            foreach (Node n in q.Items)
            {
                if (n.T == NType.Word && n.Str != null) used.Add(n.Str);
                walk(n.Q); walk(n.ElseQ);
            }
        }
        walk(prog.InitQuot);
        foreach (Quot b in prog.Defs.Values) walk(b);
        return used;
    }

    public static Result Run(ShoddyProgram prog, IReadOnlyList<Line>? lines)
    {
        var r = new Result();
        UnusedMachineIncludes(prog, lines, r);
        r.Warnings.AddRange(ShadowedAccessors(prog));
        WordAccessorCollisions(prog, r);
        NamespacedTopLevelLets(prog, r);
        if (lines != null) WrappedInfix(lines, r);
        StackEffects(prog, r);
        r.Warnings.Sort(StringComparer.Ordinal);
        return r;
    }

    // ================= shadowed accessors (the original check) =========

    readonly record struct Owner(string Type, string Field);

    static Dictionary<string, Owner> AccessorOwners(ShoddyProgram prog)
    {
        var owner = new Dictionary<string, Owner>();
        foreach (TypeDef t in AllTypes(prog))
        {
            // One-field types are union variants read by pattern matching;
            // shadowing their payload accessor is harmless and common.
            if (t.Fields.Count < 2) continue;
            for (int i = 0; i < t.Fields.Count; i++)
            {
                string folded = t.Fields[i];
                if (owner.ContainsKey(folded)) continue;
                if (!prog.Accessors.ContainsKey(folded)) continue;
                owner[folded] = new Owner(t.Disp ?? t.Name,
                                          i < t.FDisp.Count ? t.FDisp[i] : folded);
            }
        }
        return owner;
    }

    /// <summary>Field names actually read through their accessor word
    /// somewhere in the program. A shadow of a field nobody reads by
    /// accessor costs nothing — graphics code names every x and y after
    /// the event fields it destructures, and warning there buries the
    /// real hazard (the Landed incident: a field the program *does*
    /// read by accessor, shadowed in one scope).</summary>
    /// <summary>Two views of the program's word traffic: fields actually
    /// read through their accessor (nothing else claimed the word), and
    /// every non-local word mention regardless of what won it. The shadow
    /// check filters on the first; the collision check needs the second —
    /// in a collision the word *wins*, so the accessor never resolves,
    /// and the mention is exactly the evidence of confusable intent.</summary>
    static (HashSet<string> UsedFields, HashSet<string> WordMentions)
        AccessorTraffic(ShoddyProgram prog)
    {
        var used = new HashSet<string>();
        var mentions = new HashSet<string>();
        var globals = new HashSet<string>();
        if (prog.InitQuot != null)
            foreach (Node n in prog.InitQuot.Items)
                if (n.T == NType.Take)
                    foreach (string g in n.Names) globals.Add(g);

        void Walk(Quot q, HashSet<string> locals)
        {
            foreach (Node n in q.Items)
            {
                if (n.T is NType.Take or NType.Bind)
                    foreach (string nm in n.Names) locals.Add(nm);
                if (n.T == NType.Pat && n.P != null) PatBinders(n.P, locals);
                if (n.T == NType.Word)
                {
                    string w = n.Str!;
                    if (locals.Contains(w)) continue;
                    mentions.Add(w);
                    string name = prog.ResolveName(w, n.File);
                    if (globals.Contains(name) || prog.HasDef(name) ||
                        prog.FindType(name) != null ||
                        Engine.BuiltinWords.Contains(name)) continue;
                    if (prog.Accessors.TryGetValue(name, out string? fld)) used.Add(fld);
                    else if (prog.AnyTypeHasField(name)) used.Add(name);
                }
                if (n.Q != null) Walk(n.Q, new HashSet<string>(locals));
                if (n.ElseQ != null) Walk(n.ElseQ, new HashSet<string>(locals));
            }
        }
        foreach ((_, Quot body) in prog.Defs) Walk(body, new HashSet<string>());
        if (prog.InitQuot != null) Walk(prog.InitQuot, new HashSet<string>());
        return (used, mentions);
    }

    static HashSet<string> UsedAccessorFields(ShoddyProgram prog) =>
        AccessorTraffic(prog).UsedFields;

    static void PatBinders(Pat p, HashSet<string> locals)
    {
        if (p.Type == null && p.Name != null) locals.Add(p.Name);
        foreach (Pat s in p.Subs) PatBinders(s, locals);
    }

    static IEnumerable<TypeDef> AllTypes(ShoddyProgram prog)
    {
        foreach (TypeDef t in prog.Types) yield return t;
        foreach (ExternalType t in prog.ExternalTypes.Values) yield return t.Shape;
    }

    /// <summary>Names shadowing a field accessor, as human-readable lines.
    /// A word resolves through local, Let, Def, machine Def, constructor,
    /// builtin, and only then accessor — accessors lose every contest, and
    /// they are never written down, so the shadow never reads like one.</summary>
    public static List<string> ShadowedAccessors(ShoddyProgram prog)
    {
        Dictionary<string, Owner> owner = AccessorOwners(prog);
        HashSet<string> used = UsedAccessorFields(prog);
        foreach (string k in owner.Keys.Where(k => !used.Contains(k)).ToList())
            owner.Remove(k);
        var hits = new List<string>();
        var seen = new HashSet<string>();
        foreach ((string defName, Quot body) in prog.Defs)
        {
            if (owner.TryGetValue(defName, out Owner d))
                hits.Add($"warning: Def '{defName}' shadows field accessor " +
                         $"'{d.Field}' of type '{d.Type}' — the accessor is unreachable");
            WalkBinds(body, owner, hits, seen);
        }
        hits.Sort(StringComparer.Ordinal);
        return hits;
    }

    static void WalkBinds(Quot q, Dictionary<string, Owner> owner,
                          List<string> hits, HashSet<string> seen)
    {
        // A Let compiles to a Take like everything else; what marks a
        // parameter list is POSITION - the leading Take of a body. Params
        // named after fields are the codebase idiom (every helper takes x
        // and y) and stay silent; a Let mid-body is a fresh declaration
        // the writer chose, and shadowing a read accessor there is the
        // Landed incident.
        for (int i = 0; i < q.Items.Count; i++)
        {
            Node n = q.Items[i];
            if (n.T is NType.Take or NType.Bind && i > 0)
            {
                foreach (string name in n.Names)
                {
                    if (name.Length > 0 && name[0] == '\u0001') continue;
                    if (!owner.TryGetValue(name, out Owner d)) continue;
                    if (!seen.Add($"{n.File}:{n.Line}:{name}")) continue;
                    hits.Add($"{n.File ?? "?"}:{n.Line}: warning: '{name}' shadows " +
                             $"field accessor '{d.Field}' of type '{d.Type}' — " +
                             "the accessor is unreachable in this scope");
                }
            }
            if (n.Q != null) WalkBinds(n.Q, owner, hits, seen);
            if (n.ElseQ != null) WalkBinds(n.ElseQ, owner, hits, seen);
        }
    }

    // ================= word / accessor collisions ======================

    /// <summary>A Def or machine word sharing a case-folded name with a
    /// record field accessor. The word wins silently — no ambiguity
    /// diagnostic fires — and the mis-resolution surfaces as wrong-shaped
    /// stack traffic far from the site (str's Fixed vs mungo's Fixed
    /// field, 2026-08-01). Warned at the definition creating it.</summary>
    static void WordAccessorCollisions(ShoddyProgram prog, Result r)
    {
        Dictionary<string, Owner> owner = AccessorOwners(prog);
        HashSet<string> mentions = AccessorTraffic(prog).WordMentions;
        foreach (string k in owner.Keys.Where(k => !mentions.Contains(k)).ToList())
            owner.Remove(k);
        foreach ((string name, Owner d) in owner)
        {
            string kind =
                prog.Defs.ContainsKey(name) ? "Def" :
                prog.ExternalDefs.ContainsKey(name) ? "machine word" : "";
            if (kind == "") continue;
            string site = prog.DescribeSite(name);
            r.Warnings.Add($"warning: {kind} '{name}' ({site}) collides with field " +
                           $"accessor '{d.Field}' of type '{d.Type}' — the word wins " +
                           "and the accessor is unreachable; rename one of them");
        }
    }

    // ================= namespaced top-level Lets =======================

    /// <summary>A file-scope Let inside `Include … As Q` resolves wrongly
    /// (known live resolver defect) — warn on the pattern by name until
    /// the resolver is fixed.</summary>
    static void NamespacedTopLevelLets(ShoddyProgram prog, Result r)
    {
        if (prog.InitQuot == null) return;
        foreach (Node n in prog.InitQuot.Items)
        {
            if (n.T != NType.Take) continue;
            string? qual = prog.QualOf(n.File);
            if (qual == null) continue;
            foreach (string full in n.Names)
            {
                // A unique name resolves fine in practice; the hazard is a
                // bare spelling that more than one namespace claims, where
                // the resolver picks silently. Warn only there.
                string bare = full.StartsWith(qual) ? full[qual.Length..] : full;
                if (!prog.ByBare.TryGetValue(bare, out List<string>? cands) ||
                    cands.Count <= 1) continue;
                r.Warnings.Add($"{n.File ?? "?"}:{n.Line}: warning: top-level Let " +
                               $"'{bare}' in a namespaced include shares its name " +
                               "with another namespace — resolution here is known " +
                               "to misfire; move the value into a Def");
            }
        }
    }

    // ================= wrapped-infix continuation ======================

    /// <summary>A logical line beginning with And/Or outside brackets is
    /// almost always a wrapped condition: it parses as a fresh statement
    /// whose infix grabs a stray stack value, and the wrong answer lands
    /// far away. The direct message names the actual fix.</summary>
    static void WrappedInfix(IReadOnlyList<Line> lines, Result r)
    {
        foreach (Line l in lines)
        {
            if (l.Toks.Count == 0 || l.Toks[0].IsStr) continue;
            string t = l.Toks[0].Text.ToUpperInvariant();
            if (t is "AND" or "OR")
                r.Warnings.Add($"{Path.GetFileName(l.File)}:{l.LineNo}: warning: this " +
                               $"line begins with '{t}' — a wrapped condition must be " +
                               "parenthesized to continue the previous line");
        }
    }

    // ================= stack effects (the headline) ====================

    enum Kind { Num, Str, Bool, List, Quot, Rec, Unknown }

    /// <summary>(pops, pushes) for one Def, derived from a depth
    /// simulation: pops = the deepest dip below entry, pushes = the final
    /// height above that dip.</summary>
    public readonly record struct DefEffect(int Pops, int Pushes);

    /// <summary>Fixpoint over the program: every Def's stack effect, for
    /// the Defs the checker can model. Machine manifests are written from
    /// this, so a machine word carries its effect to its users.</summary>
    public static Dictionary<string, DefEffect> DefEffects(ShoddyProgram prog) =>
        Fixpoint(prog, null);

    static Dictionary<string, DefEffect> Fixpoint(ShoddyProgram prog, Result? r)
    {
        var known = new Dictionary<string, DefEffect>();
        // Outer loop: the assumption round below can ground a recursive
        // clique, whose CALLERS then become checkable in another plain
        // pass. Iterate the whole thing until nothing moves.
        int before = -1;
        while (known.Count != before)
        {
            before = known.Count;
            bool moved = true;
            while (moved)
            {
                moved = false;
                foreach ((string name, Quot body) in prog.Defs)
                {
                    if (known.ContainsKey(name)) continue;
                    var sim = new Sim(prog, known, null)
                        { Globals = new HashSet<string>(GlobalNames(prog)) };
                    Sim.Frame? f = sim.Body(body, new HashSet<string>());
                    if (f is { } fr)
                    {
                        known[name] = new DefEffect(-fr.Min, fr.Net - fr.Min);
                        moved = true;
                    }
                }
            }

        // Mutual recursion never grounds in the plain iteration — Even
        // calls Odd calls Even, and neither completes first. Assume each
        // leftover call-style Def yields one value at its declared arity,
        // then iterate: keep an assumption only while recomputation agrees
        // with it, correcting toward the computed effect. Anything still
        // unstable after a bounded number of rounds stays unknown.
        var assumed = new Dictionary<string, DefEffect>();
        var fellBack = new HashSet<string>();     // already retried as void
        foreach ((string name, Quot body) in prog.Defs)
            if (!known.ContainsKey(name) &&
                body.Items.Count > 0 && body.Items[0].T == NType.Take)
                assumed[name] = new DefEffect(body.Items[0].Names.Count, 1);
        for (int round = 0; round < 12 && assumed.Count > 0; round++)
        {
            var merged = new Dictionary<string, DefEffect>(known);
            foreach ((string n2, DefEffect a) in assumed) merged[n2] = a;
            bool stable = true;
            foreach (string name in assumed.Keys.ToList())
            {
                var sim = new Sim(prog, merged, null)
                    { Globals = new HashSet<string>(GlobalNames(prog)) };
                Sim.Frame? f = sim.Body(prog.Defs[name], new HashSet<string>());
                if (f is not { } fr)
                {
                    // The value assumption can be self-refuting for a void
                    // recursive Def (branch disagreement): retry as void
                    // once before giving up.
                    if (fellBack.Add(name))
                        assumed[name] = new DefEffect(assumed[name].Pops, 0);
                    else assumed.Remove(name);
                    stable = false;
                    continue;
                }
                var got = new DefEffect(-fr.Min, fr.Net - fr.Min);
                if (got != assumed[name])
                {
                    if (got.Pushes <= 1) { assumed[name] = got; stable = false; }
                    else { assumed.Remove(name); stable = false; }
                }
            }
            if (stable)
            {
                foreach ((string n2, DefEffect a) in assumed) known[n2] = a;
                break;
            }
        }
        }
        return known;
    }

    static IEnumerable<string> GlobalNames(ShoddyProgram prog)
    {
        if (prog.InitQuot == null) yield break;
        foreach (Node n in prog.InitQuot.Items)
            if (n.T == NType.Take)
                foreach (string g in n.Names) yield return g;
    }

    static void StackEffects(ShoddyProgram prog, Result r)
    {
        Dictionary<string, DefEffect> known = Fixpoint(prog, r);
        Dictionary<string, bool[]> recParams = RecordExpectingParams(prog);
        r.DefsTotal = prog.Defs.Count;
        r.DefsChecked = known.Count;

        // Reporting pass: re-simulate each Def with warnings on.
        foreach ((string name, Quot body) in prog.Defs)
        {
            var sim = new Sim(prog, known, r)
                { RecParams = recParams, DefName = name,
                  Globals = new HashSet<string>(GlobalNames(prog)) };
            Sim.Frame? f = sim.Body(body, new HashSet<string>());
            if (f is not { } fr) continue;

            int arity = body.Items.Count > 0 && body.Items[0].T == NType.Take
                ? body.Items[0].Names.Count : 0;
            int pushes = fr.Net - fr.Min;
            Node at = body.Items.Count > 0 ? body.Items[0] : new Node(NType.Take, 0);
            if (pushes >= 2)
                r.Warnings.Add($"{at.File ?? "?"}:{at.Line}: warning: Def '{name}' " +
                               $"leaves {pushes} values on the stack — a Def yields " +
                               "one value, or none");
            else if (arity > 0 && -fr.Min > arity)
                r.Warnings.Add($"{at.File ?? "?"}:{at.Line}: warning: Def '{name}' " +
                               $"reaches {-fr.Min - arity} value(s) below its own " +
                               "parameters — something consumed more than it was given");
        }

        // Unknown words in InitQuot too (top-level Lets call words).
        if (prog.InitQuot != null)
        {
            var simInit = new Sim(prog, known, r)
                { Globals = new HashSet<string>(GlobalNames(prog)) };
            simInit.Body(prog.InitQuot, new HashSet<string>());
        }
    }

    /// <summary>Params a Def scrutinizes against sum constructors: the
    /// KnobHi(7) inference. A Select Case compiles to `Word(param),
    /// Take(hidden)` followed by conds mentioning nullary constructors;
    /// such a param expects a record, and a literal number, string, or
    /// boolean at a call site can only fall to Case Else.</summary>
    static Dictionary<string, bool[]> RecordExpectingParams(ShoddyProgram prog)
    {
        var map = new Dictionary<string, bool[]>();
        foreach ((string name, Quot body) in prog.Defs)
        {
            if (body.Items.Count == 0 || body.Items[0].T != NType.Take) continue;
            List<string> ps = body.Items[0].Names;
            var flags = new bool[ps.Count];
            MarkScrutinized(prog, body, ps, flags);
            if (flags.Any(f => f)) map[name] = flags;
        }
        return map;
    }

    static void MarkScrutinized(ShoddyProgram prog, Quot q, List<string> ps, bool[] flags)
    {
        for (int i = 0; i + 1 < q.Items.Count; i++)
        {
            Node a = q.Items[i], b = q.Items[i + 1];
            if (a.T == NType.Word && b.T == NType.Take && b.Names.Count == 1 &&
                b.Names[0].StartsWith('\u0001'))
            {
                int p = ps.IndexOf(a.Str!);
                if (p >= 0 && ScrutinyIsConstructors(prog, q, i + 2, b.Names[0]))
                    flags[p] = true;
            }
        }
        foreach (Node n in q.Items)
        {
            if (n.Q != null) MarkScrutinized(prog, n.Q, ps, flags);
            if (n.ElseQ != null) MarkScrutinized(prog, n.ElseQ, ps, flags);
        }
    }

    static bool ScrutinyIsConstructors(ShoddyProgram prog, Quot q, int from, string hidden)
    {
        // A constructor word is evidence ONLY where a pattern can appear.
        // A constructor word in a result position is evidence of nothing.
        //
        // ParseSelect folds each clause as: the cond's items as flat
        // siblings, then one If carrying the clause body in Q and the rest
        // of the chain in ElseQ. So the conds are reachable without shape-
        // matching them at all - walk the siblings, follow ElseQ, and never
        // descend into Q. Q is the branch body, and a Def that classifies a
        // Number INTO a sum type puts its constructors exactly there.
        //
        // Scanning bodies is what made Classify(t As Number) - Select on t,
        // Cold() / Mild() / Warm() in the branches - look like a Def that
        // matches its parameter against constructors, and warned at every
        // literal call site that the call always falls to Case Else, which
        // the program's own output refuted.
        //
        // Skipping Q also skips a destructuring clause's WHERE guard, which
        // rides in the Q of its own If. That is the safe direction: a guard
        // is not a pattern either.
        //
        // ElseQ needs the same care, because when the Select has a Case Else
        // the fold seeds the chain with that clause's BODY - so following
        // ElseQ to the end walks straight into it. Every cond begins with
        // the hidden binder and nothing else does, so that is the test for
        // "another clause follows" as against "this is the else body".
        bool sawHidden = false, sawCtor = false;
        bool IsClause(Quot qq) =>
            qq.Items.Count > 0 && qq.Items[0].T == NType.Word && qq.Items[0].Str == hidden;
        void Scan(Quot qq, int start)
        {
            for (int i = start; i < qq.Items.Count; i++)
            {
                Node it = qq.Items[i];
                if (it.T == NType.Word)
                {
                    if (it.Str == hidden) sawHidden = true;
                    else
                    {
                        TypeDef? t = prog.FindType(prog.ResolveName(it.Str!, it.File));
                        if (t != null && t.Fields.Count == 0) sawCtor = true;
                    }
                }
                // A Pat node is built only by the pattern parser, so it is
                // sound evidence wherever it is found.
                if (it.T == NType.Pat && sawHidden) sawCtor = true;
                if (it.T == NType.If)
                {
                    if (it.ElseQ != null && IsClause(it.ElseQ)) Scan(it.ElseQ, 0);
                }
                else
                {
                    if (it.Q != null) Scan(it.Q, 0);
                    if (it.ElseQ != null) Scan(it.ElseQ, 0);
                }
            }
        }
        Scan(q, from);
        return sawHidden && sawCtor;
    }

    // ================= the simulator ===================================

    /// <summary>One pass over a Def body: tracks net depth, the deepest
    /// dip, and (when depth is exact) a symbolic stack of value kinds for
    /// the contextual checks. Returns null when the body touches anything
    /// dynamic — the enclosing Def is then skipped, not guessed.</summary>
    sealed class Sim
    {
        public readonly record struct Frame(int Net, int Min, bool Diverges = false);

        readonly ShoddyProgram prog;
        readonly Dictionary<string, DefEffect> known;
        readonly Result? r;
        public Dictionary<string, bool[]>? RecParams;
        public string? DefName;
        public HashSet<string> Globals = new();   // qualified top-level Let names

        public Sim(ShoddyProgram prog, Dictionary<string, DefEffect> known, Result? r)
        { this.prog = prog; this.known = known; this.r = r; }

        static readonly HashSet<string> NumericConsumers = new()
        {
            "+", "-", "*", "/", "MOD", "WRAP", "NEGATE", "ABS", "SGN", "MIN", "MAX",
            "SQR", "FLOOR", "CEIL", "ROUND", "FIX", "^", "SIN", "COS", "TAN", "ATN",
            "ATN2", "ASIN", "ACOS", "TANH", "EXP", "LOG", "LOG10",
            "<", ">", "<=", ">=",
        };
        static readonly HashSet<string> BoolConsumers = new() { "AND", "OR", "NOT" };

        public Frame? Body(Quot q, HashSet<string> locals)
        {
            var kinds = new List<Kind>();       // symbolic stack, top = end
            bool lastDiverges = false;          // body ends in ERROR (or an IF that always does)
            int net = 0, min = 0;
            bool exact = true;                  // kinds valid only while exact

            void Push(Kind k) { net++; kinds.Add(k); }
            Kind Pop()
            {
                net--; if (net < min) min = net;
                if (kinds.Count == 0) return Kind.Unknown;
                Kind k = kinds[^1]; kinds.RemoveAt(kinds.Count - 1); return k;
            }

            bool Apply(int pops, int pushes, Node n, string? word)
            {
                var popped = new List<Kind>();
                for (int i = 0; i < pops; i++) popped.Add(Pop());
                if (r != null && word != null && exact)
                {
                    bool numeric = NumericConsumers.Contains(word);
                    bool boolean = BoolConsumers.Contains(word);
                    if ((numeric || boolean) && popped.Contains(Kind.Quot))
                        r.Warnings.Add($"{n.File ?? "?"}:{n.Line}: warning: a quotation " +
                            $"reaches '{word}', which needs a {(numeric ? "number" : "boolean")} " +
                            "— an operator called function-style is a section; write " +
                            "`a Op b` (infix) or parenthesize the operand");
                    if (word == "IFTE" && popped.Count == 3 && popped[2] == Kind.Quot)
                        r.Warnings.Add($"{n.File ?? "?"}:{n.Line}: warning: IFTE's " +
                            "condition is a quotation — an operator called " +
                            "function-style is a section; write `a Op b` (infix)");
                }
                for (int i = 0; i < pushes; i++) kinds.Add(Kind.Unknown);
                net += pushes;
                return true;
            }

            foreach (Node n in q.Items)
            {
                lastDiverges = false;
                switch (n.T)
                {
                    case NType.Num: Push(Kind.Num); break;
                    case NType.Str: Push(Kind.Str); break;
                    case NType.Bool: Push(Kind.Bool); break;
                    case NType.Val: Push(Kind.Unknown); break;
                    case NType.List: Push(Kind.List); break;
                    case NType.Quot:
                        CheckQuotBody(n, locals);
                        Push(Kind.Quot);
                        break;
                    case NType.Take:
                        foreach (string nm in n.Names) locals.Add(nm);
                        for (int i = 0; i < n.Names.Count; i++) Pop();
                        break;
                    case NType.Bind:
                        foreach (string nm in n.Names) locals.Add(nm);
                        Pop();
                        break;
                    case NType.With:
                        Apply(n.Names.Count + 1, 1, n, null);
                        break;
                    case NType.Pat:
                        if (n.P != null) AddBinders(n.P, locals);
                        Pop(); Push(Kind.Bool);
                        break;
                    case NType.IsType:
                        Pop(); Push(Kind.Bool);
                        break;
                    case NType.If:
                    {
                        Kind c = Pop();
                        if (r != null && exact && c == Kind.Quot)
                            r.Warnings.Add($"{n.File ?? "?"}:{n.Line}: warning: IF's " +
                                "condition is a quotation — an operator called " +
                                "function-style is a section; write `a Op b` (infix)");
                        Frame? a = Body(n.Q!, new HashSet<string>(locals));
                        Frame? b = n.ElseQ != null
                            ? Body(n.ElseQ, new HashSet<string>(locals))
                            : new Frame(0, 0);
                        if (a is not { } fa || b is not { } fb) return null;
                        // An empty arm adopts its sibling's net: a Select
                        // Case with no Case Else compiles to an empty final
                        // else that never runs when the arms are exhaustive
                        // — warning there would cry wolf on the idiom.
                        if (fa.Diverges && fb.Diverges) lastDiverges = true;
                        // Only a DIVERGING arm adopts its sibling's net (a
                        // Select without Case Else lowers to an ERROR arm).
                        // An empty else is a reachable net-0 path: a value
                        // branch against it is a genuine missing value.
                        if (fa.Diverges && !fb.Diverges) fa = fb;
                        else if (fb.Diverges && !fa.Diverges) fb = fa;
                        if (fa.Net != fb.Net)
                        {
                            r?.Warnings.Add($"{n.File ?? "?"}:{n.Line}: warning: the " +
                                $"branches of this IF disagree — one nets {fa.Net} " +
                                $"value(s), the other {fb.Net}; every branch must " +
                                "leave the same number of values");
                            return null;
                        }
                        int dip = net + Math.Min(fa.Min, fb.Min);
                        if (dip < min) min = dip;
                        net += fa.Net;
                        kinds.Clear(); exact = false;   // merged kinds unknown
                        for (int i = 0; i < Math.Max(net, 0); i++) kinds.Add(Kind.Unknown);
                        break;
                    }
                    case NType.Word:
                    {
                        string written = n.Str!;
                        if (locals.Contains(written)) { Push(Kind.Unknown); break; }
                        string name = prog.ResolveName(written, n.File);

                        if (Globals.Contains(name)) { Push(Kind.Unknown); break; }
                        if (prog.Defs.ContainsKey(name))
                        {
                            if (!known.TryGetValue(name, out DefEffect de)) return null;
                            CheckRecArgs(name, de.Pops, kinds, n);
                            Apply(de.Pops, de.Pushes, n, null);
                        }
                        else if (prog.ExternalDefs.ContainsKey(name))
                        {
                            if (!prog.ExternalEffects.TryGetValue(name, out (int Pops, int Pushes) fx))
                                return null;
                            Apply(fx.Pops, fx.Pushes, n, null);
                        }
                        else if (prog.FindType(name) is { } t)
                        {
                            Apply(t.Fields.Count, 0, n, null);
                            Push(Kind.Rec);
                        }
                        else if (Engine.BuiltinWords.Contains(name))
                        {
                            if (Effects.Dynamic.Contains(name)) return null;
                            Effects.Effect e = Effects.Builtin[name];
                            Apply(e.Pops, e.Pushes, n, name);
                            if (name == "ERROR") lastDiverges = true;
                        }
                        else if (prog.Accessors.ContainsKey(name) || prog.AnyTypeHasField(name))
                        {
                            Pop(); Push(Kind.Unknown);
                        }
                        else
                        {
                            r?.UnknownWords.Add((
                                Unknown(prog, name, written), n.File, n.Line));
                            return null;
                        }
                        break;
                    }
                    default:
                        return null;
                }
            }
            return new Frame(net, min, lastDiverges);
        }

        /// <summary>Words inside a quotation literal run later, so an
        /// unresolvable one is the auto-quoted-typo warning, not the
        /// unknown-word error — a linking machine may still supply it.</summary>
        void CheckQuotBody(Node quot, HashSet<string> outerLocals)
        {
            if (r == null || quot.Q == null) return;
            // A quotation body binds its own locals — Fn parameters arrive
            // as a leading Take — so the walk carries a growing scope.
            var locals = new HashSet<string>(outerLocals);
            WalkQuot(quot.Q, locals);
        }

        void WalkQuot(Quot q, HashSet<string> locals)
        {
            if (r == null) return;
            foreach (Node it in q.Items)
            {
                switch (it.T)
                {
                    case NType.Take or NType.Bind:
                        foreach (string nm in it.Names) locals.Add(nm);
                        continue;
                    case NType.Pat:
                        if (it.P != null) AddBinders(it.P, locals);
                        continue;
                    case NType.Quot:
                        CheckQuotBody(it, locals);
                        continue;
                    case NType.If:
                        if (it.Q != null) WalkQuot(it.Q, new HashSet<string>(locals));
                        if (it.ElseQ != null) WalkQuot(it.ElseQ, new HashSet<string>(locals));
                        continue;
                    case NType.Word:
                        break;
                    default:
                        continue;
                }
                string w = it.Str!;
                if (locals.Contains(w)) continue;
                string name = prog.ResolveName(w, it.File);
                bool resolves = Globals.Contains(name) || prog.HasDef(name) ||
                    prog.FindType(name) != null ||
                    Engine.BuiltinWords.Contains(name) || prog.Accessors.ContainsKey(name) ||
                    prog.AnyTypeHasField(name) || locals.Contains(name);
                if (resolves) continue;
                // A name a machine declares but keeps to itself is not a
                // typo, and must not be softened into one: auto-quoting
                // would turn a missing Include into a warning and defer
                // the failure to runtime, at whatever line happened to
                // reach it. Refuse it with the advice instead.
                if (prog.Unseeded.ContainsKey(name))
                    r.UnknownWords.Add((Unknown(prog, name, w), it.File, it.Line));
                else
                    r.Warnings.Add($"{it.File ?? "?"}:{it.Line}: warning: bare name " +
                        $"'{w}' was passed as a function value, but nothing defines " +
                        "it — a typo is auto-quoted silently in argument position");
            }
        }

        void CheckRecArgs(string callee, int pops, List<Kind> kinds, Node n)
        {
            if (r == null || RecParams == null) return;
            if (!RecParams.TryGetValue(callee, out bool[]? flags)) return;
            // Caller pushed args left-to-right; param i sits pops-1-i from top.
            for (int i = 0; i < flags.Length && i < pops; i++)
            {
                if (!flags[i]) continue;
                int idx = kinds.Count - pops + i;
                if (idx < 0 || idx >= kinds.Count) continue;
                Kind k = kinds[idx];
                if (k is Kind.Num or Kind.Str or Kind.Bool)
                    r.Warnings.Add($"{n.File ?? "?"}:{n.Line}: warning: '{callee}' " +
                        $"matches this parameter against sum constructors, but a " +
                        $"{k.ToString().ToLowerInvariant()} can never match one — " +
                        "this call always falls to Case Else");
            }
        }

        static void AddBinders(Pat p, HashSet<string> locals)
        {
            if (p.Type == null && p.Name != null) locals.Add(p.Name);
            foreach (Pat s in p.Subs) AddBinders(s, locals);
        }
    }
}
