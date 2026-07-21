// Copyright (c) 2026 Stephen Vincent Foster. All rights reserved.
//
// This file is part of the Shoddy Language project.
// Licensed under the Shoddy Language License 1.0.0 (PolyForm Noncommercial
// License 1.0.0 with Additional Use Grant). See the LICENSE file in the
// project root for full terms.

namespace Shoddy.Runtime;

// The shared object model, a 1:1 port of the C reference's data
// structures. Both back-ends (tree-walk interpreter now, CLR compiler in
// phase 2) consume this. Names are folded to uppercase everywhere; the
// as-declared spellings live in TypeDef.Disp/FDisp for display only.

public enum NType { Num, Str, Bool, Word, Quot, If, Take, List, Val, With, IsType, Bind, Pat }

/// <summary>A parsed quotation body: a list of nodes. Front-end only —
/// at runtime quotations are CLR closures (see Value.CItems/Body).</summary>
public sealed class Quot
{
    public readonly List<Node> Items = new();
    public int Len => Items.Count;
    public void Add(Node n) => Items.Add(n);
}

public sealed class Node
{
    public NType T;
    public double Num;          // Num
    public string? Str;         // Str literal, Word name
    public bool BVal;           // Bool
    public Quot? Q;             // Quot body, If then-branch, List elements
    public Quot? ElseQ;         // If else-branch
    public List<string> Names = new();   // Take, With field names
    public Value? Val;          // Val: a runtime value quoted into code
    public Pat? P;              // Pat: match pattern
    public int Line;
    public string? File;        // source file (lines are per-file) — the
                                // debugger's breakpoints need both

    public Node(NType t, int line) { T = t; Line = line; }

    public static Node Word(string name, int line, string? file = null) =>
        new(NType.Word, line) { Str = name, File = file };
}

public enum VType { Num, Str, Bool, Quot, Rec, Arr, Scribbler }

public sealed class TypeDef
{
    public string Name = "";            // folded — used for matching
    public string? Disp;                // as-declared spelling, for display
    public readonly List<string> Fields = new();   // folded field names
    public readonly List<string> FDisp = new();    // as-declared field spellings

    public int FieldIndex(string f) => Fields.IndexOf(f);
}

public sealed class Value
{
    public VType T;
    public double Num;
    public string? Str;
    public bool B;
    public TypeDef? RType;      // Rec: which TYPE this record is
    public Value[]? Elems;      // Rec field values / Arr elements
    public ScribblerHandle? Scribbler;  // Scribbler: opaque mutable reference

    // Quotations are CLR closures: a body Action (null = push each item),
    // a QItem array for sequence ops and printing, and an identity object
    // — the literal site or the items array — which plays the role the
    // parse-time Quot pointer played in the C (`=` compares identities).
    public QItem[]? CItems;
    public Action? Body;
    public object? CId;

    public static Value OfNum(double d) => new() { T = VType.Num, Num = d };
    public static Value OfStr(string s) => new() { T = VType.Str, Str = s };
    public static Value OfBool(bool b) => new() { T = VType.Bool, B = b };
    public static Value OfCQuot(object id, QItem[] items, Action? body = null) =>
        new() { T = VType.Quot, CId = id, CItems = items, Body = body };
    public static Value OfRec(TypeDef t, Value[] fields) => new() { T = VType.Rec, RType = t, Elems = fields };
    public static Value OfArr(Value[] elems) => new() { T = VType.Arr, Elems = elems };
    public static Value OfScribbler(ScribblerHandle h) => new() { T = VType.Scribbler, Scribbler = h };

    public static string TypeName(VType t) => t switch
    {
        VType.Num => "NUMBER",
        VType.Str => "STRING",
        VType.Bool => "BOOLEAN",
        VType.Quot => "QUOTATION",
        VType.Rec => "RECORD",
        VType.Arr => "ARRAY",
        VType.Scribbler => "SCRIBBLER",
        _ => "?",
    };
}

/// <summary>One item of a compiled (woven) quotation: either a plain
/// value (pushed when run, printed as its repr) or a code item (a closure
/// to execute, printed as its source word). Mirrors the interpreter's
/// Node items at the level sequence ops and printing observe.</summary>
public sealed class QItem
{
    public readonly Value? Lit;     // value item
    public readonly Action? Act;    // code item
    public readonly string? Disp;   // code item's printed form

    QItem(Value? lit, Action? act, string? disp) { Lit = lit; Act = act; Disp = disp; }

    public static QItem OfValue(Value v) => new(v, null, null);
    public static QItem OfCode(Action act, string disp) => new(null, act, disp);
}

/// <summary>A match pattern: either a plain binder name, or a constructor
/// pattern with sub-patterns for each field (which may nest).</summary>
public sealed class Pat
{
    public string? Type;        // null for a plain binder
    public string? Name;        // binder name when Type is null
    public readonly List<Pat> Subs = new();
}

/// <summary>A def or type exported by a precompiled machine: the parse-
/// time shape plus the C# expression the weave emits to reach it.</summary>
public sealed record ExternalType(TypeDef Shape, string FieldRef);

/// <summary>A parsed program: what the front-end hands to a back-end.
/// The External* maps carry the surface of referenced machine DLLs when
/// weaving; they are empty when interpreting (which always splices).</summary>
public sealed class ShoddyProgram
{
    public readonly List<TypeDef> Types = new();
    public readonly Dictionary<string, Quot> Defs = new();   // keys folded
    public Quot? InitQuot;      // top-level Lets, run once before Main

    public readonly Dictionary<string, string> ExternalDefs = new();      // name -> call target
    public readonly Dictionary<string, ExternalType> ExternalTypes = new();

    public TypeDef? FindType(string name)
    {
        foreach (TypeDef t in Types)
            if (t.Name == name) return t;
        return ExternalTypes.GetValueOrDefault(name)?.Shape;
    }

    public Quot? FindDef(string name) => Defs.GetValueOrDefault(name);

    public bool HasDef(string name) =>
        Defs.ContainsKey(name) || ExternalDefs.ContainsKey(name);

    public bool AnyTypeHasField(string f)
    {
        foreach (TypeDef t in Types)
            if (t.FieldIndex(f) >= 0) return true;
        foreach (ExternalType t in ExternalTypes.Values)
            if (t.Shape.FieldIndex(f) >= 0) return true;
        return false;
    }
}
