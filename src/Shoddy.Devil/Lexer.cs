// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Globalization;
using Shoddy.Runtime;

namespace Shoddy.Devil;

/// <summary>
/// One token. Words are folded to uppercase in <see cref="Text"/> (the
/// language is case-insensitive; all matching happens on the fold) with
/// the as-typed spelling kept in <see cref="Orig"/> for display. String
/// literals keep their case: <see cref="Text"/> is the unescaped value.
/// Numbers are NOT a token kind — like the C, a number is any word that
/// satisfies <see cref="Lexer.IsNumber"/>, decided at parse time.
/// </summary>
public sealed record Token(bool IsStr, string Text, string Orig)
{
    public override string ToString() => IsStr ? $"\"{Text}\"" : Text;
}

/// <summary>A tokenized logical line that survived lexing (blank and
/// comment-only lines are dropped). A logical line is one physical line,
/// plus any following physical lines pulled in by an unclosed bracket.
/// LineNo is the first physical line, within its own file, exactly as
/// the C reports errors. Indent is the first physical line's — the
/// indentation of continuation lines is ignored.
///
/// Qual is the namespace prefix this line's file was included under
/// (INCLUDE "F" AS Q), or null at the top level. Include splices every
/// file into one flat line list, so this tag is the only surviving trace
/// of where a line came from once parsing begins.</summary>
public sealed record Line(string File, int LineNo, int Indent, List<Token> Toks,
                          string? Qual = null);

/// <summary>
/// The devil: grinds source files to token lines. A straight port of the
/// C's tokenizeLine/readProgram. Tokens are runs of characters delimited
/// only by whitespace and the self-delimiting set "[](){},"; operators
/// like &lt;= are ordinary words and must be space-separated in source.
/// A line whose brackets are still open continues onto the next physical
/// line — any long expression can be wrapped by parenthesizing it.
/// </summary>
public static class Lexer
{
    const string SelfDelim = "[](){},";
    const string Openers = "([{";
    const string Closers = ")]}";

    /// <summary>Reads a program with Include splicing (include-once,
    /// resolved relative to the including file, then $SHODDYLIB).
    /// When <paramref name="externalInclude"/> is given, it is offered
    /// each resolved include path first; returning true means the include
    /// is satisfied externally (a precompiled machine) and is not
    /// spliced. The interpreter never passes it — it always splices.
    ///
    /// <paramref name="onQualified"/> is told (path, qualifier) for every
    /// include that carried an AS and was satisfied externally, so the
    /// machine layer can qualify a DLL's surface it never sees as source.</summary>
    public static List<Line> ReadProgram(string path, Func<string, bool>? externalInclude = null,
                                         Action<string, string>? onQualified = null)
    {
        var lines = new List<Line>();
        var included = new Dictionary<string, string?>();
        ReadFile(path, lines, included, externalInclude, null, onQualified);
        return lines;
    }

    /// <summary><paramref name="qual"/> is the namespace this file was
    /// included under. Qualifiers do not compose through a chain of
    /// includes: a file that carries its own AS wins for its whole subtree,
    /// so a name never depends on who included the includer.</summary>
    static void ReadFile(string path, List<Line> lines, Dictionary<string, string?> included,
                         Func<string, bool>? externalInclude = null, string? qual = null,
                         Action<string, string>? onQualified = null)
    {
        if (included.ContainsKey(path)) return;      // include-once semantics
        included[path] = qual;

        string[] raw;
        try { raw = File.ReadAllLines(path); }
        catch (IOException) { throw new ShoddyError(0, $"cannot open '{path}'"); }
        catch (UnauthorizedAccessException) { throw new ShoddyError(0, $"cannot open '{path}'"); }
        catch (SystemException) when (!File.Exists(path)) { throw new ShoddyError(0, $"cannot open '{path}'"); }

        for (int lineno = 1; lineno <= raw.Length; lineno++)
        {
            var toks = new List<Token>();
            var open = new Stack<(char C, int Line)>();
            int start = lineno;
            int indent = TokenizeLine(raw[lineno - 1], lineno, toks, open);
            if (toks.Count == 0) continue;

            // An unclosed bracket pulls the following physical lines into
            // this logical line; their indentation is not significant.
            while (open.Count > 0)
            {
                (char c, int at) = open.Peek();
                if (lineno >= raw.Length)
                    throw new ShoddyError(at, $"unclosed '{c}'");
                int before = toks.Count;
                int contIndent = TokenizeLine(raw[lineno], lineno + 1, toks, open);
                lineno++;
                // A margin-level Def mid-continuation is a missing close
                // bracket eating the next function, not a continuation.
                if (toks.Count > before && contIndent == 0 && !toks[before].IsStr &&
                    (toks[before].Text == "DEF" || toks[before].Text == "REDEF"))
                    throw new ShoddyError(at, $"unclosed '{c}'");
            }

            // INCLUDE "FILE" [AS Q] — splice another source file in, right
            // here, optionally under a namespace prefix.
            if (!toks[0].IsStr && toks[0].Text == "INCLUDE")
            {
                bool bare = toks.Count == 2;
                bool qualified = toks.Count == 4 && !toks[2].IsStr &&
                                 toks[2].Text == "AS" && !toks[3].IsStr;
                if (indent != 0 || !toks[1].IsStr || !(bare || qualified))
                    throw new ShoddyError(start, path,
                        "expected INCLUDE \"FILE\" or INCLUDE \"FILE\" AS NAMESPACE "
                        + "at left margin");
                string name = toks[1].Text;
                // Inner wins: a file's own AS governs its whole subtree.
                string? sub = qualified ? toks[3].Text : qual;
                if (qualified && !IsPlainWord(toks[3].Text))
                    throw new ShoddyError(start, path,
                        $"'{toks[3].Orig}' is not usable as a namespace — "
                        + "a namespace is a plain word");
                string cand = ResolveInclude(path, name);
                if (!File.Exists(cand))
                    foreach (string dir in LibDirs())
                    {
                        string alt = Path.Combine(dir, name);
                        if (!File.Exists(alt)) continue;
                        // Normalized, so the ../machines route and a direct
                        // one spell the same file identically — include-once
                        // is keyed on the path.
                        cand = Path.GetFullPath(alt);
                        break;
                    }
                // Include-once is silent when the namespace agrees. When it
                // does not, one of the two spellings the program uses would
                // resolve to nothing, and which one depends on include order.
                if (included.TryGetValue(cand, out string? had) && had != sub)
                    throw new ShoddyError(start, path,
                        $"'{name}' is included twice under different namespaces "
                        + $"({Show(had)} and {Show(sub)})");
                if (externalInclude != null && !included.ContainsKey(cand) &&
                    externalInclude(cand))
                {
                    included[cand] = sub;        // satisfied by a machine DLL
                    if (qualified) onQualified?.Invoke(cand, sub!);
                    continue;
                }
                ReadFile(cand, lines, included, externalInclude, sub, onQualified);
                continue;                        // the directive itself is consumed
            }

            FuseQualified(toks, start, path);
            lines.Add(new Line(path, start, indent, toks, qual));
        }
    }

    /// <summary>Where an INCLUDE that isn't beside its includer is looked
    /// for, in order: $SHODDYLIB, then the machine library shipped with
    /// the running mill — beside it, then one level up, beside its bin/.
    ///
    /// The mill-relative pair is what lets a program keep a bare
    /// Include "seq.shoddy" wherever it is copied: the repo's bin/mill
    /// finds machines/ at the root, and the mill bundled in the VS Code
    /// extension finds the machines/ staged beside it — neither needs
    /// anything set. SHODDYLIB comes first so a checkout can still be
    /// pinned to its own machines.</summary>
    static IEnumerable<string> LibDirs()
    {
        string? env = Environment.GetEnvironmentVariable("SHODDYLIB");
        if (env != null) yield return env;
        string mill = AppContext.BaseDirectory;
        yield return Path.Combine(mill, "machines");
        yield return Path.Combine(mill, "..", "machines");
    }

    /// <summary>Resolve an INCLUDE name relative to the including file's
    /// directory; absolute names and dirless includers pass through.</summary>
    static string ResolveInclude(string from, string name)
    {
        int slash = Math.Max(from.LastIndexOf('/'), from.LastIndexOf('\\'));
        if (slash < 0 || name.StartsWith('/') || name.StartsWith('\\'))
            return name;
        return from[..(slash + 1)] + name;
    }

    /// <summary>Tokenize one physical line, appending to <paramref name="toks"/>
    /// and maintaining the open-bracket stack that drives line continuation.
    /// Returns the line's indent (spaces; a tab counts 4).</summary>
    static int TokenizeLine(string src, int lineno, List<Token> toks,
                            Stack<(char C, int Line)> open)
    {
        int i = 0, indent = 0;
        while (i < src.Length && (src[i] == ' ' || src[i] == '\t'))
            indent += src[i++] == '\t' ? 4 : 1;

        while (i < src.Length)
        {
            char c = src[i];
            if (c == ' ' || c == '\t') { i++; continue; }
            if (c == '\'') break;                    // comment to end of line

            if (c == '"')
            {
                i++;
                var sb = new System.Text.StringBuilder();
                while (i < src.Length && src[i] != '"')
                {
                    if (src[i] == '\\' && i + 1 < src.Length)
                    {
                        char e = src[++i];
                        sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => e });
                        i++;
                    }
                    else sb.Append(src[i++]);
                }
                if (i >= src.Length)
                    throw new ShoddyError(lineno, "unterminated string literal");
                i++;
                string s = sb.ToString();
                toks.Add(new Token(true, s, s));
            }
            else if (SelfDelim.Contains(c))
            {
                i++;
                if (Openers.Contains(c)) open.Push((c, lineno));
                else if (Closers.Contains(c) && open.Count > 0)
                    open.Pop();                  // mismatches are the parser's
                toks.Add(new Token(false, c.ToString(), c.ToString()));
            }
            else
            {
                int start = i;
                while (i < src.Length && !char.IsWhiteSpace(src[i]) &&
                       !SelfDelim.Contains(src[i]))
                    i++;
                string orig = src[start..i];
                string tok = Fold(orig);
                if (tok == "REM") break;             // comment to end of line
                // All BASIC type sigils are reserved: types live in
                // declarations, not names. Predicates use the IS prefix.
                if (tok.IndexOfAny(['!', '%', '#', '$', '?']) >= 0)
                    throw new ShoddyError(lineno,
                        $"'{tok}': the characters ! % # $ ? are reserved and may not appear in words");
                // -N is unary minus on N (but -3 is a number literal and
                // -- belongs to stack-effect signatures)
                if (tok.Length > 1 && tok[0] == '-' && tok[1] != '-' &&
                    !IsNumber(tok, out _))
                {
                    toks.Add(new Token(false, "-", "-"));
                    tok = tok[1..];
                    orig = orig[1..];
                }
                toks.Add(new Token(false, tok, orig));
            }
        }

        return indent;
    }

    /// <summary>NAME IN NAMESPACE collapses to the single word NAMESPACENAME
    /// — the same name the include already built, so nothing downstream
    /// learns a new shape and both spellings mean one thing.
    ///
    /// This runs here, on the assembled logical line, because the expression
    /// parser decides a word is a call by peeking at the very next token: if
    /// IN were still sitting between the name and its '(', `Split In Str(s)`
    /// would not read as a call at all.</summary>
    static void FuseQualified(List<Token> toks, int lineno, string path)
    {
        for (int k = 1; k + 1 < toks.Count; k++)
        {
            if (toks[k].IsStr || toks[k].Text != "IN") continue;
            Token nm = toks[k - 1], q = toks[k + 1];
            if (nm.IsStr || q.IsStr || !IsPlainWord(nm.Text) || !IsPlainWord(q.Text))
                throw new ShoddyError(lineno, path,
                    "IN wants a plain name on each side, as in 'CaveNearby In Msg'");
            toks[k - 1] = new Token(false, q.Text + nm.Text, q.Orig + nm.Orig);
            toks.RemoveRange(k, 2);
            k--;                         // fuse again from the same slot
        }
        // A survivor is an IN with nothing usable beside it.
        foreach (Token t in toks)
            if (!t.IsStr && t.Text == "IN")
                throw new ShoddyError(lineno, path,
                    "IN wants a plain name on each side, as in 'CaveNearby In Msg'");
    }

    static string Show(string? qual) => qual == null ? "unqualified" : $"AS {qual}";

    /// <summary>A namespace must be a plain word: it is pasted onto every
    /// name the file declares, so anything the lexer treats specially — a
    /// number, an operator, a bracket — would build names nothing can
    /// spell.</summary>
    static bool IsPlainWord(string s)
    {
        if (s.Length == 0 || IsNumber(s, out _)) return false;
        foreach (char c in s)
            if (!(c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_')) return false;
        return true;
    }

    /// <summary>ASCII-only case fold, matching C toupper in the C locale.</summary>
    static string Fold(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        for (int i = 0; i < s.Length; i++)
            buf[i] = s[i] is >= 'a' and <= 'z' ? (char)(s[i] - 32) : s[i];
        return new string(buf);
    }

    /// <summary>Is this word a number literal? Mirrors the C's strtod
    /// whole-token check for the formats Shoddy actually uses (sign,
    /// decimal point, exponent — not hex/inf/nan).</summary>
    public static bool IsNumber(string tok, out double v) =>
        double.TryParse(tok,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint |
            NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out v);
}
