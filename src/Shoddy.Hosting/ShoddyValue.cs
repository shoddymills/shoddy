// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Runtime;

namespace Shoddy.Hosting;

/// <summary>
/// A Shoddy value as a host sees it: construction for what a caller
/// hands in (numbers, strings, booleans, lists), destructuring for what
/// a word hands back (including record fields by name). Wraps the
/// runtime's own <c>Value</c> so nothing is copied and record identity
/// — which is reference identity on the shared TypeDef — survives the
/// crossing intact.
///
/// Records are minted by the mill's own constructor words, never by the
/// host: a host that needs a fresh RckState calls HxFresh. That is what
/// keeps one set of rules for what a record is.
/// </summary>
public readonly struct ShoddyValue
{
    internal readonly Value? V;

    internal ShoddyValue(Value v) => V = v;

    Value Val => V ?? throw new InvalidOperationException(
        "this ShoddyValue is empty — it was default-constructed, not returned by a word");

    // ---- construction ----

    public static ShoddyValue Num(double d) => new(Value.OfNum(d));

    public static ShoddyValue Str(string s) =>
        new(Value.OfStr(s ?? throw new ArgumentNullException(nameof(s))));

    public static ShoddyValue Bool(bool b) => new(Value.OfBool(b));

    /// <summary>A list, built the way the runtime builds one: the items
    /// array is its own identity, so the result is indistinguishable
    /// from a list a program made.</summary>
    public static ShoddyValue ListOf(IEnumerable<ShoddyValue> xs)
    {
        if (xs is null) throw new ArgumentNullException(nameof(xs));
        QItem[] items = xs.Select(x => QItem.OfValue(x.Val)).ToArray();
        return new ShoddyValue(Value.OfCQuot(items, items));
    }

    // ---- destructuring ----

    public double AsNum() => Val.T == VType.Num ? Val.Num
        : throw Mismatch("a NUMBER");

    public string AsStr() => Val.T == VType.Str ? Val.Str!
        : throw Mismatch("a STRING");

    public bool AsBool() => Val.T == VType.Bool ? Val.B
        : throw Mismatch("a BOOLEAN");

    /// <summary>The record's type name (SOME, ERR, RCKSTATE, …), for
    /// dispatching on what a word answered — the host-side echo of
    /// Select Case.</summary>
    public string TypeName() => Val.T == VType.Rec
        ? Val.RType!.Name
        : Value.TypeName(Val.T);

    /// <summary>A record field, by its declared name, folded the way the
    /// language folds every name.</summary>
    public ShoddyValue Field(string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        Value v = Val;
        if (v.T != VType.Rec) throw Mismatch("a RECORD");
        int i = v.RType!.FieldIndex(name.ToUpperInvariant());
        if (i < 0)
            throw new InvalidOperationException(
                $"{v.RType.Disp ?? v.RType.Name} has no field {name}");
        return new ShoddyValue(v.Elems![i]);
    }

    /// <summary>The items of a list (or the elements of an array). A
    /// quotation holding code rather than data refuses — a host reads
    /// values, it does not execute fragments.</summary>
    public IReadOnlyList<ShoddyValue> AsList()
    {
        Value v = Val;
        switch (v.T)
        {
            case VType.Arr:
                return v.Elems!.Select(e => new ShoddyValue(e)).ToArray();
            case VType.Quot when v.CItems != null:
                var result = new ShoddyValue[v.CItems.Length];
                for (int i = 0; i < v.CItems.Length; i++)
                {
                    Value? lit = v.CItems[i].Lit;
                    if (lit is null)
                        throw new InvalidOperationException(
                            "this quotation holds code, not data — AsList reads value lists only");
                    result[i] = new ShoddyValue(lit);
                }
                return result;
            default:
                throw Mismatch("a LIST or ARRAY");
        }
    }

    /// <summary>The value's printed form — the same repr the language
    /// prints, for diagnostics and logs.</summary>
    public override string ToString()
    {
        if (V is null) return "(empty)";
        var sw = new StringWriter();
        Printer.Repr(sw, V);
        return sw.ToString();
    }

    InvalidOperationException Mismatch(string wanted) => new(
        $"expected {wanted}, got {Value.TypeName(Val.T)}: {this}");
}
