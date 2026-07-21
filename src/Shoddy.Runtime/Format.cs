// Copyright (c) 2026 Stephen Vincent Foster. All rights reserved.
//
// This file is part of the Shoddy Language project.
// Licensed under the Shoddy Language License 1.0.0 (PolyForm Noncommercial
// License 1.0.0 with Additional Use Grant). See the LICENSE file in the
// project root for full terms.

using System.Globalization;

namespace Shoddy.Runtime;

/// <summary>
/// Number and value formatting — critical for byte-identical output.
/// A double prints as C's %.0f when integral and |d| &lt; 1e15, else as
/// C's %.10g. Records print with their declared spellings; quotations as
/// space-separated items in brackets; top-level Print of a string is raw.
/// </summary>
public static class Format
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Num(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsInfinity(d)) return d > 0 ? "inf" : "-inf";
        return d == Math.Floor(d) && Math.Abs(d) < 1e15 ? d.ToString("F0", Inv) : G10(d);
    }

    /// <summary>C's %.10g: 10 significant digits, trailing zeros trimmed,
    /// scientific notation when the decimal exponent is &lt; -4 or &gt;= 10.</summary>
    static string G10(double d)
    {
        string e9 = d.ToString("E9", Inv);           // d.dddddddddE+xxx
        int ei = e9.IndexOf('E');
        int exp = int.Parse(e9[(ei + 1)..], Inv);
        if (exp < -4 || exp >= 10)
        {
            string mant = TrimZeros(e9[..ei]);
            return $"{mant}e{(exp < 0 ? '-' : '+')}{Math.Abs(exp):00}";
        }
        return TrimZeros(d.ToString("F" + (9 - exp), Inv));
    }

    static string TrimZeros(string s) =>
        s.Contains('.') ? s.TrimEnd('0').TrimEnd('.') : s;
}

public static class Printer
{
    /// <summary>Top-level Print: strings are raw (unquoted).</summary>
    public static void PrintValue(TextWriter o, Value v)
    {
        if (v.T == VType.Str) { o.Write(v.Str); return; }
        Repr(o, v);
    }

    /// <summary>Repr form: strings quoted, records and arrays as valid
    /// input syntax.</summary>
    public static void Repr(TextWriter o, Value v)
    {
        switch (v.T)
        {
            case VType.Num: o.Write(Format.Num(v.Num)); break;
            case VType.Str: o.Write('"'); o.Write(v.Str); o.Write('"'); break;
            case VType.Bool: o.Write(v.B ? "True" : "False"); break;
            case VType.Quot: CQuotRepr(o, v.CItems!); break;
            case VType.Rec:
                o.Write(v.RType!.Disp ?? v.RType.Name);
                o.Write('(');
                for (int k = 0; k < v.Elems!.Length; k++)
                {
                    if (k > 0) o.Write(", ");
                    o.Write(k < v.RType.FDisp.Count ? v.RType.FDisp[k] : v.RType.Fields[k]);
                    o.Write(" = ");
                    Repr(o, v.Elems[k]);
                }
                o.Write(')');
                break;
            case VType.Arr:
                o.Write("Array(");
                for (int k = 0; k < v.Elems!.Length; k++)
                {
                    if (k > 0) o.Write(", ");
                    Repr(o, v.Elems[k]);
                }
                o.Write(')');
                break;
            case VType.Scribbler:
                o.Write($"Scribbler({v.Scribbler!.Width}, {v.Scribbler.Height})");
                break;
        }
    }

    /// <summary>Compiled-representation quotation: value items print as
    /// their repr, code items as their captured source spelling.</summary>
    public static void CQuotRepr(TextWriter o, QItem[] items)
    {
        o.Write('[');
        foreach (QItem it in items)
        {
            o.Write(' ');
            if (it.Lit != null) Repr(o, it.Lit);
            else o.Write(it.Disp);
        }
        o.Write(" ]");
    }

    public static void QuotRepr(TextWriter o, Quot q)
    {
        o.Write('[');
        foreach (Node n in q.Items)
        {
            o.Write(' ');
            NodeRepr(o, n);
        }
        o.Write(" ]");
    }

    public static void NodeRepr(TextWriter o, Node n)
    {
        switch (n.T)
        {
            case NType.Num: o.Write(Format.Num(n.Num)); break;
            case NType.Str: o.Write('"'); o.Write(n.Str); o.Write('"'); break;
            case NType.Bool: o.Write(n.BVal ? "True" : "False"); break;
            case NType.Word: o.Write(n.Str); break;
            case NType.Quot: QuotRepr(o, n.Q!); break;
            case NType.List: o.Write("{...}"); break;
            case NType.Val: Repr(o, n.Val!); break;
            case NType.If: o.Write("IF"); break;
            case NType.Take: o.Write("TAKE"); break;
            case NType.With: o.Write("WITH"); break;
            case NType.IsType: o.Write($"ISTYPE:{n.Str}"); break;
            case NType.Bind: o.Write($"BIND:{n.Str}"); break;
            case NType.Pat: o.Write($"MATCH:{n.P!.Type ?? n.P.Name}"); break;
        }
    }
}
