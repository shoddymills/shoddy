// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Runtime;

namespace Shoddy.Devil;

/// <summary>
/// The parser, a 1:1 port of the C reference. Two body dialects chosen by
/// the Def header: expression bodies (infix, calls, Let, Fn, Select Case)
/// and concatenative postfix bodies. Both compile to the same Quot/Node
/// set. The expression compiler tracks a scope set of known bindings so
/// that bare unknown names in argument position auto-quote to function
/// values; quotations close over their creation environment at runtime.
/// </summary>
public sealed class Parser
{
    readonly List<Line> lines;
    readonly ShoddyProgram prog;
    readonly HashSet<string> globalScope = new();
    int i;
    int selCounter;

    Parser(List<Line> lines, ShoddyProgram prog)
    {
        this.lines = lines;
        this.prog = prog;
    }

    /// <summary>Parse a program. Pass a pre-seeded ShoddyProgram (with
    /// ExternalDefs/ExternalTypes filled from machine manifests) when
    /// weaving against precompiled machines.</summary>
    public static ShoddyProgram Parse(List<Line> lines, ShoddyProgram? seeded = null)
    {
        var p = new Parser(lines, seeded ?? new ShoddyProgram());
        p.ParseProgram();
        return p.prog;
    }

    // ---- token helpers ------------------------------------------------

    const string SelfDelim = "[](){},";

    static bool Is(Token t, string s) => !t.IsStr && t.Text == s;
    static bool IsPunct(Token t) => !t.IsStr && t.Text.Length == 1 && SelfDelim.Contains(t.Text[0]);
    static bool IsNum(Token t, out double d)
    {
        d = 0;
        return !t.IsStr && Lexer.IsNumber(t.Text, out d);
    }

    static int OpPrec(Token t) => t.IsStr ? 0 : OpPrec(t.Text);

    static int OpPrec(string s) => s switch
    {
        "OR" => 1,
        "AND" => 2,
        "=" or "<>" or "<" or ">" or "<=" or ">=" => 3,
        "+" or "-" or "&" => 4,
        "*" or "/" or "MOD" => 5,
        "^" => 6,               // right-associative
        _ => 0,
    };

    static ShoddyError Die(int line, string msg) => new(line, msg);

    static ShoddyError Die(Line l, string msg) => new(l.LineNo, l.File, msg);

    /// <summary>A type named from within <paramref name="l"/>, resolved
    /// through that file's namespace. Constructors and patterns need the
    /// TypeDef at parse time, so they cannot wait for CodeGen's resolver.
    /// </summary>
    TypeDef? FindTypeIn(Line l, string name) =>
        prog.FindType(prog.ResolveName(name, l.File));

    /// <summary>Reject a top-level name that is already spoken for, and
    /// record where this one was declared so the *next* collision can point
    /// back here. Defs, Types, and top-level Lets share one namespace, and
    /// so do the builtins: a Def outranks a builtin in the resolution chain
    /// (CodeGen.EmitWord), so `Def Length` silently retires the builtin for
    /// the whole program. That is the same silent-overwrite failure the
    /// duplicate check already prevents between two Defs, so it is refused
    /// on the same terms. REDEF says you meant it.
    ///
    /// Returns the name as actually declared: a file included AS Q declares
    /// QNAME, so everything downstream — Defs, Types, globalScope — keys on
    /// the qualified spelling and nothing below here needs to know about
    /// namespaces at all.</summary>
    string ClaimName(Line l, string name, string kind, bool redef = false)
    {
        string full = ShoddyProgram.Qualify(l.Qual, name);
        if (!redef)
        {
            if (prog.HasDef(full) || prog.FindType(full) != null ||
                globalScope.Contains(full))
                throw Die(l, $"duplicate definition of {full} — " +
                             $"already declared as {prog.DescribeSite(full)}");
            // A namespace does not license shadowing a builtin: the fused
            // name is what the program says, and QLENGTH is not LENGTH.
            if (Engine.BuiltinWords.Contains(full))
                throw Die(l, $"{full} is a builtin — a Def of that name would " +
                             "shadow it everywhere; rename it, or write " +
                             $"'Redef {full}' if that is deliberate");
        }
        prog.RecordSite(full, kind, l.File, l.LineNo);
        prog.NoteBare(name, full);
        return full;
    }

    static Node LiteralOrWord(Token t, int lineno, string? file)
    {
        if (t.IsStr) return new Node(NType.Str, lineno) { File = file,  Str = t.Text };
        if (Lexer.IsNumber(t.Text, out double v)) return new Node(NType.Num, lineno) { File = file,  Num = v };
        return Node.Word(t.Text, lineno, file);
    }

    static void ExpectTok(Line l, ref int pos, string what)
    {
        if (pos >= l.Toks.Count || !Is(l.Toks[pos], what))
            throw Die(l.LineNo, pos < l.Toks.Count
                ? $"expected '{what}', got {l.Toks[pos].Text}"
                : $"expected '{what}' at end of line");
        pos++;
    }

    // ---- [ ... ] quotation literals ----------------------------------

    Quot ParseBracket(Line l, ref int pos)
    {
        var q = new Quot();
        while (pos < l.Toks.Count)
        {
            Token tok = l.Toks[pos];
            if (Is(tok, "]")) { pos++; return q; }
            if (Is(tok, "["))
            {
                pos++;
                var n = new Node(NType.Quot, l.LineNo) { File = l.File,  Q = ParseBracket(l, ref pos) };
                q.Add(n);
                continue;
            }
            if (Is(tok, "IF") || Is(tok, "THEN") || Is(tok, "ELSE") || Is(tok, "TAKE"))
                throw Die(l.LineNo, $"{tok.Text} is not allowed inside [ ] — use IFTE for inline conditionals");
            q.Add(LiteralOrWord(tok, l.LineNo, l.File));
            pos++;
        }
        throw Die(l.LineNo, "missing ] (quotations may not span lines)");
    }

    // ---- concatenative (postfix) body parser — the core --------------

    void ParseStatementLine(int indent, Quot outq)
    {
        Line l = lines[i];

        if (Is(l.Toks[0], "ELSE"))
            throw Die(l.LineNo, "ELSE without matching IF");

        if (Is(l.Toks[0], "TAKE"))
        {
            var n = new Node(NType.Take, l.LineNo) { File = l.File };
            for (int k = 1; k < l.Toks.Count; k++)
            {
                Token t = l.Toks[k];
                string s = t.Text;
                while (s.Length > 0 && s[^1] == ',') s = s[..^1];
                if (s.Length == 0) continue;
                if (t.IsStr || Lexer.IsNumber(s, out _) || IsPunct(t))
                    throw Die(l.LineNo, $"TAKE expects names, got '{t.Text}'");
                n.Names.Add(s);
            }
            if (n.Names.Count == 0) throw Die(l.LineNo, "TAKE needs at least one name");
            outq.Add(n);
            i++;
            return;
        }

        int pos = 0;
        while (pos < l.Toks.Count)
        {
            Token tok = l.Toks[pos];

            if (Is(tok, "["))
            {
                pos++;
                outq.Add(new Node(NType.Quot, l.LineNo) { File = l.File,  Q = ParseBracket(l, ref pos) });
                continue;
            }
            if (Is(tok, "]"))
                throw Die(l.LineNo, "] without matching [");

            if (Is(tok, "IF"))
            {
                pos++;
                if (pos < l.Toks.Count && Is(l.Toks[pos], "THEN")) pos++;
                if (pos != l.Toks.Count)
                    throw Die(l.LineNo, "IF must end its line");

                var n = new Node(NType.If, l.LineNo) { File = l.File };
                i++;
                if (i >= lines.Count || lines[i].Indent <= indent)
                    throw Die(l.LineNo, "expected indented block after IF");
                n.Q = ParseBlock(lines[i].Indent);

                n.ElseQ = new Quot();
                // An ELSE at *our* indent is ours; at a shallower indent it
                // belongs to an enclosing IF, so leave it for the caller.
                if (i < lines.Count && lines[i].Indent == indent && Is(lines[i].Toks[0], "ELSE"))
                {
                    if (lines[i].Toks.Count != 1)
                        throw Die(lines[i].LineNo, "ELSE must be alone on its line");
                    i++;
                    if (i >= lines.Count || lines[i].Indent <= indent)
                        throw Die(l.LineNo, "expected indented block after ELSE");
                    n.ElseQ = ParseBlock(lines[i].Indent);
                }
                outq.Add(n);
                return;                          // line fully consumed
            }

            outq.Add(LiteralOrWord(tok, l.LineNo, l.File));
            pos++;
        }
        i++;
    }

    Quot ParseBlock(int indent)
    {
        var q = new Quot();
        while (i < lines.Count)
        {
            Line l = lines[i];
            if (l.Indent < indent) break;
            if (l.Indent > indent)
                throw Die(l.LineNo, "unexpected indent");
            if (Is(l.Toks[0], "ELSE")) break;    // caller handles
            ParseStatementLine(indent, q);
        }
        return q;
    }

    // ---- applicative (expression) body parser — the surface ----------

    /* FN(X, ...) => expression — an inline lambda, compiled to a quotation */
    void CompileFn(Line l, ref int pos, Quot outq, HashSet<string> sc)
    {
        int line = l.LineNo;
        pos++;                                   // FN
        ExpectTok(l, ref pos, "(");
        var take = new Node(NType.Take, line) { File = l.File };
        while (pos < l.Toks.Count && !Is(l.Toks[pos], ")"))
        {
            Token nm = l.Toks[pos];
            if (Is(nm, ",")) { pos++; continue; }
            if (nm.IsStr || IsNum(nm, out _) || IsPunct(nm))
                throw Die(line, $"FN expects parameter names, got '{nm.Text}'");
            take.Names.Add(nm.Text);
            pos++;
        }
        ExpectTok(l, ref pos, ")");
        ExpectTok(l, ref pos, "=>");

        var q = new Node(NType.Quot, line) { File = l.File,  Q = new Quot() };
        if (take.Names.Count > 0) q.Q.Add(take);

        var inner = new HashSet<string>(sc);
        foreach (string nm in take.Names) inner.Add(nm);
        CompileExpr(l, ref pos, 1, q.Q, inner);
        outq.Add(q);
    }

    /* WITH(record, FIELD = expr, ...) — functional record update */
    void CompileWith(Line l, ref int pos, Quot outq, HashSet<string> sc)
    {
        int line = l.LineNo;
        pos += 2;                                // WITH (
        CompileExpr(l, ref pos, 1, outq, sc);    // the record
        var n = new Node(NType.With, line) { File = l.File };
        while (pos < l.Toks.Count && !Is(l.Toks[pos], ")"))
        {
            if (Is(l.Toks[pos], ",")) { pos++; continue; }
            Token fn = l.Toks[pos];
            if (fn.IsStr || IsNum(fn, out _) || IsPunct(fn))
                throw Die(line, $"WITH expects FIELD = value, got '{fn.Text}'");
            pos++;
            ExpectTok(l, ref pos, "=");
            CompileExpr(l, ref pos, 1, outq, sc);
            n.Names.Add(fn.Text);
        }
        ExpectTok(l, ref pos, ")");
        if (n.Names.Count == 0) throw Die(line, "WITH needs at least one FIELD = value");
        outq.Add(n);
    }

    /* TYPENAME(...) — record construction, positional or named fields */
    void CompileCtor(Line l, ref int pos, Quot outq, HashSet<string> sc, TypeDef t)
    {
        int line = l.LineNo;
        pos += 2;                                // NAME (
        bool named = pos + 1 < l.Toks.Count && !l.Toks[pos].IsStr &&
                     t.FieldIndex(l.Toks[pos].Text) >= 0 && Is(l.Toks[pos + 1], "=");
        if (named)
        {
            var fq = new Quot?[Math.Max(t.Fields.Count, 1)];
            while (pos < l.Toks.Count && !Is(l.Toks[pos], ")"))
            {
                if (Is(l.Toks[pos], ",")) { pos++; continue; }
                Token fn = l.Toks[pos];
                int fi = fn.IsStr ? -1 : t.FieldIndex(fn.Text);
                if (fi < 0) throw Die(line, $"{t.Name} has no field {fn.Text}");
                if (fq[fi] != null) throw Die(line, $"duplicate field {fn.Text}");
                pos++;
                ExpectTok(l, ref pos, "=");
                fq[fi] = new Quot();
                CompileExpr(l, ref pos, 1, fq[fi]!, sc);
            }
            ExpectTok(l, ref pos, ")");
            for (int k = 0; k < t.Fields.Count; k++)
            {
                if (fq[k] == null) throw Die(line, $"{t.Name}: missing field {t.Fields[k]}");
                foreach (Node item in fq[k]!.Items) outq.Add(item);
            }
        }
        else
        {
            int argc = 0;
            while (pos < l.Toks.Count && !Is(l.Toks[pos], ")"))
            {
                if (Is(l.Toks[pos], ",")) { pos++; continue; }
                CompileExpr(l, ref pos, 1, outq, sc);
                argc++;
            }
            ExpectTok(l, ref pos, ")");
            if (argc != t.Fields.Count)
                throw Die(line, $"{t.Name} expects {t.Fields.Count} fields, got {argc}");
        }
        outq.Add(Node.Word(t.Name, line, l.File));
    }

    /* OP(args) — a section: pre-loads trailing arguments of an operator,
     * yielding a quotation, e.g. >=(100) becomes [ 100 >= ]. */
    void CompileSection(Line l, ref int pos, Quot outq, HashSet<string> sc)
    {
        string op = l.Toks[pos].Text;
        pos += 2;                                // OP (
        var n = new Node(NType.Quot, l.LineNo) { File = l.File,  Q = new Quot() };
        while (pos < l.Toks.Count && !Is(l.Toks[pos], ")"))
        {
            if (Is(l.Toks[pos], ",")) { pos++; continue; }
            CompileExpr(l, ref pos, 1, n.Q, sc);
        }
        ExpectTok(l, ref pos, ")");
        n.Q.Add(Node.Word(op, l.LineNo, l.File));
        outq.Add(n);
    }

    /* Compile one argument of a call. Bare non-local names and bare
     * operators are passed as quotations (function values); everything
     * else is an ordinary expression. Parenthesize a name to force
     * evaluation. */
    void CompileArg(Line l, ref int pos, Quot outq, HashSet<string> sc)
    {
        Token tok = l.Toks[pos];
        Token? next = pos + 1 < l.Toks.Count ? l.Toks[pos + 1] : null;
        bool atEnd = next != null && (Is(next, ",") || Is(next, ")"));

        if (Is(tok, "FN")) { CompileFn(l, ref pos, outq, sc); return; }

        bool isop = !tok.IsStr && (OpPrec(tok.Text) != 0 || tok.Text == "NOT");
        if (isop && next != null && Is(next, "("))
        {
            CompileSection(l, ref pos, outq, sc);
            return;
        }
        if ((isop && atEnd) ||
            (atEnd && !IsPunct(tok) && !tok.IsStr && !IsNum(tok, out _) &&
             !sc.Contains(tok.Text) && tok.Text != "TRUE" && tok.Text != "FALSE"))
        {
            pos++;
            var n = new Node(NType.Quot, l.LineNo) { File = l.File,  Q = new Quot() };
            n.Q.Add(Node.Word(tok.Text, l.LineNo, l.File));
            outq.Add(n);
            return;
        }
        CompileExpr(l, ref pos, 1, outq, sc);
    }

    void CompilePrimary(Line l, ref int pos, Quot outq, HashSet<string> sc)
    {
        if (pos >= l.Toks.Count)
            throw Die(l.LineNo, "unexpected end of line in expression");
        Token tok = l.Toks[pos];

        if (tok.IsStr)
        {
            outq.Add(new Node(NType.Str, l.LineNo) { File = l.File,  Str = tok.Text });
            pos++;
            return;
        }
        if (IsNum(tok, out double d))
        {
            outq.Add(new Node(NType.Num, l.LineNo) { File = l.File,  Num = d });
            pos++;
            return;
        }
        if (Is(tok, "("))                        // grouping
        {
            pos++;
            CompileExpr(l, ref pos, 1, outq, sc);
            ExpectTok(l, ref pos, ")");
            return;
        }
        if (Is(tok, "{"))                        // list literal
        {
            pos++;
            var n = new Node(NType.List, l.LineNo) { File = l.File,  Q = new Quot() };
            while (pos < l.Toks.Count && !Is(l.Toks[pos], "}"))
            {
                if (Is(l.Toks[pos], ",")) { pos++; continue; }
                var el = new Node(NType.Quot, l.LineNo) { File = l.File,  Q = new Quot() };
                CompileExpr(l, ref pos, 1, el.Q, sc);
                n.Q.Add(el);
            }
            ExpectTok(l, ref pos, "}");
            outq.Add(n);
            return;
        }
        if (Is(tok, "["))                        // raw core quotation
        {
            pos++;
            outq.Add(new Node(NType.Quot, l.LineNo) { File = l.File,  Q = ParseBracket(l, ref pos) });
            return;
        }
        if (Is(tok, "FN")) { CompileFn(l, ref pos, outq, sc); return; }

        if (IsPunct(tok) || Is(tok, "IF") || Is(tok, "THEN") || Is(tok, "ELSE") ||
            Is(tok, "LET") || Is(tok, "DEF") || Is(tok, "REDEF") ||
            Is(tok, "AS") || Is(tok, "=>"))
            throw Die(l.LineNo, $"unexpected '{tok.Text}' in expression");

        Token? next = pos + 1 < l.Toks.Count ? l.Toks[pos + 1] : null;
        if (next != null && Is(next, "("))       // call: F(a, b)
        {
            if (Is(tok, "WITH")) { CompileWith(l, ref pos, outq, sc); return; }
            TypeDef? ct = FindTypeIn(l, tok.Text);
            if (ct != null) { CompileCtor(l, ref pos, outq, sc, ct); return; }
            string name = tok.Text;
            pos += 2;
            /* CALL(F, args...) is sugar for args... F CALL, so its first
             * argument (the function) is emitted last. */
            bool isCall = name == "CALL";
            /* IFTE(cond, t, f): the two arms are lazy. Wrap each as a
             * quotation so IFTE calls only the taken one — exactly the
             * c [ t ] [ f ] Ifte the block-If form desugars to. Without
             * this the arms push bare values and IFTE pops a non-quotation. */
            bool isIfte = name == "IFTE";
            Quot? first = null;
            int argIdx = 0;
            while (pos < l.Toks.Count && !Is(l.Toks[pos], ")"))
            {
                if (Is(l.Toks[pos], ",")) { pos++; continue; }
                if (isCall && argIdx == 0)
                {
                    first = new Quot();
                    CompileArg(l, ref pos, first, sc);
                }
                else if (isIfte && argIdx >= 1)
                {
                    var arm = new Node(NType.Quot, l.LineNo) { File = l.File, Q = new Quot() };
                    CompileArg(l, ref pos, arm.Q, sc);
                    outq.Add(arm);
                }
                else
                {
                    CompileArg(l, ref pos, outq, sc);
                }
                argIdx++;
            }
            ExpectTok(l, ref pos, ")");
            if (first != null)
                foreach (Node item in first.Items) outq.Add(item);
            outq.Add(Node.Word(name, l.LineNo, l.File));
            return;
        }

        /* bare word: a local pushes its value; a def or builtin executes */
        outq.Add(Node.Word(tok.Text, l.LineNo, l.File));
        pos++;
    }

    void CompileExpr(Line l, ref int pos, int minPrec, Quot outq, HashSet<string> sc)
    {
        if (pos < l.Toks.Count && Is(l.Toks[pos], "-"))          // unary -
        {
            pos++;
            CompileExpr(l, ref pos, 6, outq, sc);
            outq.Add(Node.Word("NEGATE", l.LineNo, l.File));
        }
        else if (pos < l.Toks.Count && Is(l.Toks[pos], "NOT"))
        {
            pos++;
            CompileExpr(l, ref pos, 3, outq, sc);
            outq.Add(Node.Word("NOT", l.LineNo, l.File));
        }
        else
        {
            CompilePrimary(l, ref pos, outq, sc);
        }
        while (pos < l.Toks.Count)
        {
            Token tok = l.Toks[pos];
            int p = OpPrec(tok);
            if (p == 0 || p < minPrec) break;
            pos++;
            /* AND/OR short-circuit: compile as a conditional so the right
             * side only evaluates when it has to */
            if (Is(tok, "AND") || Is(tok, "OR"))
            {
                var n = new Node(NType.If, l.LineNo) { File = l.File };
                var rhs = new Quot();
                CompileExpr(l, ref pos, p + 1, rhs, sc);
                var konst = new Quot();
                konst.Add(new Node(NType.Bool, l.LineNo) { File = l.File,  BVal = Is(tok, "OR") });
                if (Is(tok, "AND")) { n.Q = rhs; n.ElseQ = konst; }
                else { n.Q = konst; n.ElseQ = rhs; }
                outq.Add(n);
                continue;
            }
            /* ^ is right-associative: recurse at the same precedence */
            CompileExpr(l, ref pos, Is(tok, "^") ? p : p + 1, outq, sc);
            outq.Add(Node.Word(tok.Text, l.LineNo, l.File));
        }
    }

    /* Parse a pattern: a binder name, or TYPE(sub, sub, ...) which may
     * nest. '_' is an ordinary binder, used by convention for ignored
     * fields. */
    Pat ParsePat(Line c, ref int cp, int line)
    {
        if (cp >= c.Toks.Count) throw Die(line, "incomplete pattern");
        Token tok = c.Toks[cp];
        if (tok.IsStr || IsNum(tok, out _) || IsPunct(tok))
            throw Die(line, $"pattern expects names or TYPE(...), got '{tok.Text}'");
        Token? next = cp + 1 < c.Toks.Count ? c.Toks[cp + 1] : null;
        var p = new Pat();
        if (next != null && Is(next, "("))
        {
            // Key on the resolved *name*, not the shape's own: a machine's
            // TypeDef carries the name the machine was built under, while
            // the program reaches it by the name its INCLUDE gave it.
            string tn = prog.ResolveName(tok.Text, c.File);
            TypeDef? t = prog.FindType(tn);
            if (t == null) throw Die(line, $"unknown type '{tok.Text}' in pattern");
            p.Type = tn;
            cp += 2;
            while (cp < c.Toks.Count && !Is(c.Toks[cp], ")"))
            {
                if (Is(c.Toks[cp], ",")) { cp++; continue; }
                p.Subs.Add(ParsePat(c, ref cp, line));
            }
            if (cp >= c.Toks.Count) throw Die(line, "missing ) in pattern");
            cp++;
            if (p.Subs.Count != t.Fields.Count)
                throw Die(line, $"{t.Name} has {t.Fields.Count} fields but the pattern names {p.Subs.Count}");
        }
        else
        {
            p.Name = tok.Text;
            cp++;
        }
        return p;
    }

    static void CollectPatNames(Pat p, HashSet<string> sc)
    {
        if (p.Type != null)
            foreach (Pat sub in p.Subs) CollectPatNames(sub, sc);
        else
            sc.Add(p.Name!);
    }

    /* SELECT CASE — classic BASIC value matching, as an expression.
     * Compiled to nested IFs over a hidden binding of the scrutinee, so
     * the tested expression evaluates exactly once. */
    void ParseSelectStatement(int indent, Quot outq, HashSet<string> sc)
    {
        Line l = lines[i];
        int line = l.LineNo;
        if (l.Toks.Count < 3 || !Is(l.Toks[1], "CASE"))
            throw Die(line, "expected SELECT CASE expression");
        int pos = 2;
        CompileExpr(l, ref pos, 1, outq, sc);
        if (pos != l.Toks.Count) throw Die(line, "malformed SELECT CASE expression");

        string hidden = "\u0001SEL" + selCounter++;  // unspellable name
        var t0 = new Node(NType.Take, line) { File = l.File };
        t0.Names.Add(hidden);
        outq.Add(t0);

        i++;
        if (i >= lines.Count || lines[i].Indent <= indent)
            throw Die(line, "expected indented CASE clauses after SELECT CASE");
        int caseIndent = lines[i].Indent;

        var conds = new List<Quot>();
        var bodies = new List<Quot>();
        bool hasElse = false;

        while (i < lines.Count && lines[i].Indent == caseIndent)
        {
            Line c = lines[i];
            if (!Is(c.Toks[0], "CASE"))
                throw Die(c.LineNo, "expected CASE inside SELECT CASE");
            if (hasElse) throw Die(c.LineNo, "CASE after CASE ELSE");
            int cline = c.LineNo;
            Quot? cond = null;
            if (c.Toks.Count == 2 && Is(c.Toks[1], "ELSE"))
            {
                hasElse = true;
            }
            else if (c.Toks.Count > 2 && !c.Toks[1].IsStr &&
                     FindTypeIn(c, c.Toks[1].Text) != null && Is(c.Toks[2], "("))
            {
                /* destructuring pattern: CASE TYPE(pat, ...) [WHERE expr]
                 * Binders are in scope for the guard and the body; a
                 * failing WHERE falls through to the next clause. */
                int cp = 1;
                Pat pat = ParsePat(c, ref cp, cline);

                var psc = new HashSet<string>(sc);
                CollectPatNames(pat, psc);

                /* guard body: runs only if the pattern matched */
                var inner = new Quot();
                if (cp < c.Toks.Count && Is(c.Toks[cp], "WHERE"))
                {
                    cp++;
                    CompileExpr(c, ref cp, 1, inner, psc);
                }
                else
                {
                    inner.Add(new Node(NType.Bool, cline) { File = c.File,  BVal = true });
                }
                if (cp != c.Toks.Count) throw Die(cline, "malformed CASE pattern");

                /* condition: pattern matches AND guard passes */
                cond = new Quot();
                cond.Add(Node.Word(hidden, cline, c.File));
                cond.Add(new Node(NType.Pat, cline) { File = c.File,  P = pat });
                var gif = new Node(NType.If, cline) { File = c.File,  Q = inner, ElseQ = new Quot() };
                gif.ElseQ.Add(new Node(NType.Bool, cline) { File = c.File,  BVal = false });
                cond.Add(gif);

                /* body: rebind via the same pattern, drop its TRUE */
                i++;
                if (i >= lines.Count || lines[i].Indent <= caseIndent)
                    throw Die(cline, "expected indented block after CASE");
                Quot ub = ParseExprBlock(lines[i].Indent, psc);
                var pbody = new Quot();
                pbody.Add(Node.Word(hidden, cline, c.File));
                pbody.Add(new Node(NType.Pat, cline) { File = c.File,  P = pat });
                pbody.Add(Node.Word("DROP", cline, c.File));
                foreach (Node item in ub.Items) pbody.Add(item);
                conds.Add(cond);
                bodies.Add(pbody);
                continue;                        // clause fully handled
            }
            else
            {
                cond = new Quot();
                int cp = 1;
                if (cp < c.Toks.Count && Is(c.Toks[cp], "IS")) cp++;
                if (cp < c.Toks.Count && OpPrec(c.Toks[cp]) == 3)
                {
                    /* CASE [IS] <op> expr */
                    string op = c.Toks[cp++].Text;
                    cond.Add(Node.Word(hidden, cline, c.File));
                    CompileExpr(c, ref cp, 1, cond, sc);
                    cond.Add(Node.Word(op, cline, c.File));
                }
                else
                {
                    cond.Add(Node.Word(hidden, cline, c.File));
                    CompileExpr(c, ref cp, 1, cond, sc);
                    if (cp < c.Toks.Count && Is(c.Toks[cp], "TO"))
                    {
                        cp++;                    /* CASE a TO b */
                        cond.Add(Node.Word(">=", cline, c.File));
                        cond.Add(Node.Word(hidden, cline, c.File));
                        CompileExpr(c, ref cp, 1, cond, sc);
                        cond.Add(Node.Word("<=", cline, c.File));
                        cond.Add(Node.Word("AND", cline, c.File));
                    }
                    else
                    {
                        cond.Add(Node.Word("=", cline, c.File));
                        while (cp < c.Toks.Count && Is(c.Toks[cp], ","))
                        {
                            cp++;                /* CASE e1, e2, ... */
                            cond.Add(Node.Word(hidden, cline, c.File));
                            CompileExpr(c, ref cp, 1, cond, sc);
                            cond.Add(Node.Word("=", cline, c.File));
                            cond.Add(Node.Word("OR", cline, c.File));
                        }
                    }
                }
                if (cp != c.Toks.Count) throw Die(cline, "malformed CASE");
            }
            i++;
            if (i >= lines.Count || lines[i].Indent <= caseIndent)
                throw Die(cline, "expected indented block after CASE");
            Quot body = ParseExprBlock(lines[i].Indent, sc);
            if (cond != null) conds.Add(cond);
            bodies.Add(body);
        }
        if (bodies.Count == 0) throw Die(line, "SELECT CASE needs at least one CASE");

        /* fold the clauses into nested IFs, back to front */
        Quot elseq;
        int ncases = conds.Count;
        if (hasElse)
        {
            elseq = bodies[^1];
        }
        else
        {
            elseq = new Quot();
            elseq.Add(new Node(NType.Str, line) { File = l.File,  Str = "SELECT CASE: no matching CASE" });
            elseq.Add(Node.Word("ERROR", line, l.File));
        }
        for (int k = ncases - 1; k >= 0; k--)
        {
            var wrap = new Quot();
            foreach (Node item in conds[k].Items) wrap.Add(item);
            var n = new Node(NType.If, line) { File = l.File,  Q = bodies[k], ElseQ = elseq };
            wrap.Add(n);
            elseq = wrap;
        }
        foreach (Node item in elseq.Items) outq.Add(item);
    }

    void ParseExprStatement(int indent, Quot outq, HashSet<string> sc)
    {
        Line l = lines[i];

        if (Is(l.Toks[0], "ELSE"))
            throw Die(l.LineNo, "ELSE without matching IF");

        if (Is(l.Toks[0], "SELECT"))
        {
            ParseSelectStatement(indent, outq, sc);
            return;
        }

        if (Is(l.Toks[0], "TAKE"))
            throw Die(l.LineNo, "TAKE belongs to concatenative bodies; use LET or named parameters here");

        if (Is(l.Toks[0], "LET"))
        {
            if (l.Toks.Count < 4 || !Is(l.Toks[2], "="))
                throw Die(l.LineNo, "expected LET NAME = expression");
            string name = l.Toks[1].Text;
            int lpos = 3;
            CompileExpr(l, ref lpos, 1, outq, sc);
            if (lpos != l.Toks.Count)
                throw Die(l.LineNo, "unexpected tokens after LET expression");
            var t = new Node(NType.Take, l.LineNo) { File = l.File };
            t.Names.Add(name);
            outq.Add(t);
            sc.Add(name);
            i++;
            return;
        }

        if (Is(l.Toks[0], "IF"))
        {
            if (!Is(l.Toks[^1], "THEN"))
                throw Die(l.LineNo, "IF line must end with THEN");
            int ipos = 1;
            if (l.Toks.Count > 2)                // IF cond THEN
            {
                CompileExpr(l, ref ipos, 1, outq, sc);
                if (ipos != l.Toks.Count - 1)
                    throw Die(l.LineNo, "malformed IF condition");
            }
            var n = new Node(NType.If, l.LineNo) { File = l.File };
            i++;
            if (i >= lines.Count || lines[i].Indent <= indent)
                throw Die(l.LineNo, "expected indented block after IF");
            n.Q = ParseExprBlock(lines[i].Indent, sc);
            n.ElseQ = new Quot();
            if (i < lines.Count && lines[i].Indent == indent && Is(lines[i].Toks[0], "ELSE"))
            {
                if (lines[i].Toks.Count != 1)
                    throw Die(lines[i].LineNo, "ELSE must be alone on its line");
                i++;
                if (i >= lines.Count || lines[i].Indent <= indent)
                    throw Die(l.LineNo, "expected indented block after ELSE");
                n.ElseQ = ParseExprBlock(lines[i].Indent, sc);
            }
            outq.Add(n);
            return;
        }

        /* one or more expression units, left to right (adjacency = stack
         * flow, enabling pipeline chaining like RANGE(1, N) FILTER(ISEVEN)) */
        int pos = 0;
        while (pos < l.Toks.Count)
            CompileExpr(l, ref pos, 1, outq, sc);
        i++;

        /* deeper-indented lines continue the statement */
        while (i < lines.Count && lines[i].Indent > indent)
        {
            Line c = lines[i];
            if (Is(c.Toks[0], "LET") || Is(c.Toks[0], "IF") || Is(c.Toks[0], "ELSE"))
                throw Die(c.LineNo, "unexpected indent");
            int cp = 0;
            while (cp < c.Toks.Count)
                CompileExpr(c, ref cp, 1, outq, sc);
            i++;
        }
    }

    Quot ParseExprBlock(int indent, HashSet<string> sc)
    {
        var q = new Quot();
        while (i < lines.Count)
        {
            Line l = lines[i];
            if (l.Indent < indent) break;
            if (l.Indent > indent)
                throw Die(l.LineNo, "unexpected indent");
            if (Is(l.Toks[0], "ELSE")) break;    // caller handles
            ParseExprStatement(indent, q, sc);
        }
        return q;
    }

    // ---- top-level parser --------------------------------------------

    /* DEF NAME(P1 AS T1, ...) AS R — applicative definition. `name` is the
       already-claimed (namespace-qualified) spelling. */
    void ParseApplicativeDef(string name)
    {
        Line l = lines[i];
        int pos = 3;                             // after '('
        var take = new Node(NType.Take, l.LineNo) { File = l.File };
        var sc = new HashSet<string>(globalScope);   // constants are in scope
        while (pos < l.Toks.Count && !Is(l.Toks[pos], ")"))
        {
            Token t = l.Toks[pos];
            if (Is(t, ",")) { pos++; continue; }
            if (Is(t, "AS"))                     // skip the type
            {
                pos++;
                SkipType(l, ref pos);
                continue;
            }
            if (t.IsStr || IsNum(t, out _) || IsPunct(t))
                throw Die(l.LineNo, $"expected parameter name, got '{t.Text}'");
            take.Names.Add(t.Text);
            sc.Add(t.Text);
            pos++;
        }
        if (pos >= l.Toks.Count)
            throw Die(l.LineNo, "missing ) in DEF header");
        /* return-type annotation after ')' is parsed and ignored */

        i++;
        if (i >= lines.Count || lines[i].Indent == 0)
            throw Die(l.LineNo, $"DEF {name} has an empty body (body must be indented)");
        var body = new Quot();
        if (take.Names.Count > 0) body.Add(take);
        Quot b = ParseExprBlock(lines[i].Indent, sc);
        foreach (Node item in b.Items) body.Add(item);
        prog.Defs[name] = body;
    }

    /* skip a type annotation after AS: up to a bracket-depth-0 ',' or ')' */
    static void SkipType(Line l, ref int pos)
    {
        int bd = 0;
        while (pos < l.Toks.Count)
        {
            Token tt = l.Toks[pos];
            if (bd == 0 && (Is(tt, ",") || Is(tt, ")"))) break;
            if (Is(tt, "[")) bd++;
            else if (Is(tt, "]")) bd--;
            pos++;
        }
    }

    void ParseProgram()
    {
        // Every Node carries the file it was written in, so recording the
        // file's namespace once here is all resolution needs to tell which
        // namespace a use site is spelled in.
        foreach (Line ln in lines) prog.FileQual[ln.File] = ln.Qual;

        i = 0;
        while (i < lines.Count)
        {
            Line l = lines[i];
            if (Is(l.Toks[0], "ELSE"))
                throw Die(l.LineNo, "ELSE without matching IF");
            if (l.Indent != 0)
                throw Die(l.LineNo, "unexpected indent at top level (expected DEF at left margin)");

            if (Is(l.Toks[0], "LET"))            // top-level constant
            {
                if (l.Toks.Count < 4 || !Is(l.Toks[2], "="))
                    throw Die(l.LineNo, "expected Let NAME = expression");
                Token gtok = l.Toks[1];
                if (gtok.IsStr || IsNum(gtok, out _) || IsPunct(gtok))
                    throw Die(l.LineNo, $"Let expects a name, got '{gtok.Text}'");
                string gname = ClaimName(l, gtok.Text, "Let");
                prog.InitQuot ??= new Quot();
                int pos = 3;
                CompileExpr(l, ref pos, 1, prog.InitQuot, globalScope);
                if (pos != l.Toks.Count)
                    throw Die(l.LineNo, "unexpected tokens after Let expression");
                var t = new Node(NType.Take, l.LineNo) { File = l.File };
                t.Names.Add(gname);
                prog.InitQuot.Add(t);
                globalScope.Add(gname);
                i++;
                continue;
            }

            if (Is(l.Toks[0], "TYPE"))           // record declaration
            {
                ParseTypeDecl();
                continue;
            }

            // REDEF is DEF with the duplicate/builtin check waived.
            bool redef = Is(l.Toks[0], "REDEF");
            if (!Is(l.Toks[0], "DEF") && !redef)
                throw Die(l.LineNo, "expected DEF at top level");
            if (l.Toks.Count < 2)
                throw Die(l.LineNo, "DEF needs a name");
            string name = ClaimName(l, l.Toks[1].Text, "Def", redef);

            /* Dispatch on the header:
             *   DEF NAME                   -> concatenative body
             *   DEF NAME ( a b -- c )      -> concatenative body
             *   DEF NAME(params) [AS type] -> applicative body */
            bool applicative = false;
            if (l.Toks.Count > 2)
            {
                if (!Is(l.Toks[2], "("))
                    throw Die(l.LineNo, "unexpected tokens after DEF name");
                applicative = true;
                int bd = 0;
                for (int k = 3; k < l.Toks.Count; k++)
                {
                    Token t = l.Toks[k];
                    if (Is(t, "[")) bd++;
                    else if (Is(t, "]")) bd--;
                    else if (bd == 0 && Is(t, "--")) { applicative = false; break; }
                    else if (bd == 0 && Is(t, ")")) break;
                }
            }

            if (applicative)
            {
                ParseApplicativeDef(name);
                continue;
            }

            i++;
            if (i >= lines.Count || lines[i].Indent == 0)
                throw Die(l.LineNo, $"DEF {name} has an empty body (body must be indented)");
            prog.Defs[name] = ParseBlock(lines[i].Indent);
        }
    }

    void ParseTypeDecl()
    {
        Line l = lines[i];
        if (l.Toks.Count != 2 && !(l.Toks.Count > 3 && Is(l.Toks[2], "=")))
            throw Die(l.LineNo, "expected TYPE NAME or TYPE NAME = VARIANT | ...");
        string tname = ClaimName(l, l.Toks[1].Text, "Type");

        if (l.Toks.Count > 2 && Is(l.Toks[2], "="))
        {
            /* sum type: TYPE NAME = V1(F, ...) | V2(...) | V3
             * Each variant becomes its own constructor/pattern; the union
             * name is documentation. A bare variant has zero fields. */
            int p = 3, nvars = 0;
            while (p < l.Toks.Count)
            {
                Token vt0 = l.Toks[p];
                if (vt0.IsStr || IsNum(vt0, out _) || IsPunct(vt0) || Is(vt0, "|"))
                    throw Die(l.LineNo, $"expected variant name, got '{vt0.Text}'");
                // Each variant is its own constructor, so each claims a name
                // of its own — and each gets the file's namespace.
                string vn = ClaimName(l, vt0.Text, "Type");
                var vt = new TypeDef { Name = vn, Disp = vt0.Orig };
                prog.Types.Add(vt);
                p++;
                if (p < l.Toks.Count && Is(l.Toks[p], "("))
                {
                    p++;
                    while (p < l.Toks.Count && !Is(l.Toks[p], ")"))
                    {
                        Token fn = l.Toks[p];
                        if (Is(fn, ",")) { p++; continue; }
                        if (Is(fn, "AS")) { p++; SkipType(l, ref p); continue; }
                        if (fn.IsStr || IsNum(fn, out _) || IsPunct(fn))
                            throw Die(l.LineNo, $"expected field name, got '{fn.Text}'");
                        if (vt.FieldIndex(fn.Text) >= 0)
                            throw Die(l.LineNo, $"duplicate field {fn.Text}");
                        vt.FDisp.Add(fn.Orig);
                        vt.Fields.Add(fn.Text);
                        prog.NoteAccessor(l.Qual, fn.Text);
                        p++;
                    }
                    if (p >= l.Toks.Count)
                        throw Die(l.LineNo, "missing ) in variant");
                    p++;
                }
                nvars++;
                if (p < l.Toks.Count)
                {
                    if (!Is(l.Toks[p], "|"))
                        throw Die(l.LineNo, "expected | between variants");
                    p++;
                    if (p >= l.Toks.Count)
                        throw Die(l.LineNo, "trailing | in sum type");
                }
            }
            if (nvars == 0)
                throw Die(l.LineNo, "sum type needs at least one variant");
            i++;
            return;
        }

        var t = new TypeDef { Name = tname, Disp = l.Toks[1].Orig };
        prog.Types.Add(t);
        i++;
        if (i >= lines.Count || lines[i].Indent == 0)
            throw Die(l.LineNo, $"TYPE {tname} has no fields (fields must be indented)");
        int find = lines[i].Indent;
        while (i < lines.Count && lines[i].Indent == find)
        {
            Line f = lines[i];
            Token fn = f.Toks[0];
            if (fn.IsStr || IsNum(fn, out _) || IsPunct(fn))
                throw Die(f.LineNo, $"expected field name, got '{fn.Text}'");
            if (t.FieldIndex(fn.Text) >= 0)
                throw Die(f.LineNo, $"duplicate field {fn.Text}");
            if (f.Toks.Count > 1 && !Is(f.Toks[1], "AS"))
                throw Die(f.LineNo, "expected AS after field name");
            t.FDisp.Add(fn.Orig);
            t.Fields.Add(fn.Text);
            prog.NoteAccessor(f.Qual, fn.Text);
            i++;
        }
        if (i < lines.Count && lines[i].Indent != 0)
            throw Die(lines[i].LineNo, "unexpected indent");
    }
}
