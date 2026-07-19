// Copyright (c) 2026 Stephen Vincent Foster. All rights reserved.
//
// This file is part of the Shoddy Language project.
// Licensed under the Shoddy Language License 1.0.0 (PolyForm Noncommercial
// License 1.0.0 with Additional Use Grant). See the LICENSE file in the
// project root for full terms.

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

/// <summary>A tokenized source line that survived lexing (blank and
/// comment-only lines are dropped). LineNo is within its own file,
/// exactly as the C reports errors.</summary>
public sealed record Line(string File, int LineNo, int Indent, List<Token> Toks);

/// <summary>
/// The devil: grinds source files to token lines. A straight port of the
/// C's tokenizeLine/readProgram. Tokens are runs of characters delimited
/// only by whitespace and the self-delimiting set "[](){},"; operators
/// like &lt;= are ordinary words and must be space-separated in source.
/// </summary>
public static class Lexer
{
    const string SelfDelim = "[](){},";

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
            TokenizeLine(raw[lineno - 1], path, lineno, lines);

            // INCLUDE "FILE" — splice another source file in, right here.
            if (lines.Count > 0 && !lines[^1].Toks[0].IsStr &&
                lines[^1].Toks[0].Text == "INCLUDE")
            {
                Line l = lines[^1];
                lines.RemoveAt(lines.Count - 1);     // consume the directive
                if (l.Indent != 0 || l.Toks.Count != 2 || !l.Toks[1].IsStr)
                    throw new ShoddyError(lineno, "expected INCLUDE \"FILE\" at left margin");
                string name = l.Toks[1].Text;
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
            }
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

    static void TokenizeLine(string src, string file, int lineno, List<Line> lines)
    {
        var toks = new List<Token>();
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

        if (toks.Count > 0)
            lines.Add(new Line(file, lineno, indent, toks));
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
