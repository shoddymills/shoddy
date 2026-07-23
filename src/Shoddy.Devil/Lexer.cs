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
/// indentation of continuation lines is ignored.</summary>
public sealed record Line(string File, int LineNo, int Indent, List<Token> Toks);

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
    /// spliced. The interpreter never passes it — it always splices.</summary>
    public static List<Line> ReadProgram(string path, Func<string, bool>? externalInclude = null)
    {
        var lines = new List<Line>();
        var included = new List<string>();
        ReadFile(path, lines, included, externalInclude);
        return lines;
    }

    static void ReadFile(string path, List<Line> lines, List<string> included,
                         Func<string, bool>? externalInclude = null)
    {
        if (included.Contains(path)) return;         // include-once semantics
        included.Add(path);

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
                if (toks.Count > before && contIndent == 0 &&
                    !toks[before].IsStr && toks[before].Text == "DEF")
                    throw new ShoddyError(at, $"unclosed '{c}'");
            }

            // INCLUDE "FILE" — splice another source file in, right here.
            if (!toks[0].IsStr && toks[0].Text == "INCLUDE")
            {
                if (indent != 0 || toks.Count != 2 || !toks[1].IsStr)
                    throw new ShoddyError(start, "expected INCLUDE \"FILE\" at left margin");
                string name = toks[1].Text;
                string cand = ResolveInclude(path, name);
                if (!File.Exists(cand))
                {
                    string? libdir = Environment.GetEnvironmentVariable("SHODDYLIB");
                    if (libdir != null && File.Exists($"{libdir}/{name}"))
                        cand = $"{libdir}/{name}";
                }
                if (externalInclude != null && !included.Contains(cand) &&
                    externalInclude(cand))
                {
                    included.Add(cand);          // satisfied by a machine DLL
                    continue;
                }
                ReadFile(cand, lines, included, externalInclude);
                continue;                        // the directive itself is consumed
            }

            lines.Add(new Line(path, start, indent, toks));
        }
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
