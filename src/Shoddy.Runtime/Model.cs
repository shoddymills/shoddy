// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

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

/// <summary>Where a top-level name was declared. Kind reads as a noun
/// phrase in the duplicate message: "Def", "Type", "Let".</summary>
public readonly record struct DeclSite(string Kind, string? File, int Line);

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

    /// <summary>Machine word -> its declared stack effect (pops, pushes),
    /// for the words whose manifest carries one. The linter checks call
    /// sites against these; words absent here are skipped, not guessed.</summary>
    public readonly Dictionary<string, (int Pops, int Pushes)> ExternalEffects = new();

    /// <summary>Where each top-level name was declared, for the duplicate
    /// diagnostic. Include splices every file into one line list, so a
    /// collision usually spans two files and the second one alone does not
    /// tell you what you collided with.</summary>
    public readonly Dictionary<string, DeclSite> Sites = new();

    /// <summary>Source file -> the namespace it was included under. Word
    /// nodes carry their file, so this is how resolution recovers which
    /// namespace a use site is written in without threading the qualifier
    /// through every Node.</summary>
    public readonly Dictionary<string, string?> FileQual = new();

    /// <summary>Unqualified name -> every qualified name declared under it.
    /// A bare word resolves while this holds exactly one entry; more than
    /// one is the ambiguity that forces an explicit IN.</summary>
    public readonly Dictionary<string, List<string>> ByBare = new();

    /// <summary>A name that exists in a machine the program did not
    /// include, mapped to the advice for reaching it: the source file that
    /// declares it, and the machine that dragged it in. A machine exports
    /// what it declares, so its own includes are not nameable by whoever
    /// includes it — this is what lets the refusal say which Include to
    /// add instead of only that the word is unknown.</summary>
    public readonly Dictionary<string, (string Declares, string? Via)> Unseeded = new();

    /// <summary>Accessor word -> the field it reads. A type declared in a
    /// file included AS Q is read with QSHORT, not SHORT, so field names
    /// stop being words the whole program can see.
    ///
    /// Only the *word* is namespaced. The field's identity inside the
    /// record is untouched, because WITH names fields directly rather than
    /// resolving them as words — `With(g, Dflag = 3)` keeps working with no
    /// namespace on Dflag, and the runtime still dispatches on the record's
    /// own type.</summary>
    public readonly Dictionary<string, string> Accessors = new();

    public void NoteAccessor(string? qual, string field)
    {
        string word = Qualify(qual, field);
        Accessors.TryAdd(word, field);
        NoteBare(field, word);
    }

    /// <summary>A namespace is pasted straight onto the name, so a file
    /// included AS MSG declaring CAVENEARBY yields MSGCAVENEARBY — the same
    /// name a hand-written prefix would have produced.</summary>
    public static string Qualify(string? qual, string name) =>
        qual == null ? name : qual + name;

    public void NoteBare(string bare, string full)
    {
        if (bare == full) return;
        if (!ByBare.TryGetValue(bare, out List<string>? l))
            ByBare[bare] = l = new List<string>();
        if (!l.Contains(full)) l.Add(full);
    }

    /// <summary>The namespace a word written in <paramref name="file"/> is
    /// spelled in, or null at the top level.</summary>
    public string? QualOf(string? file) =>
        file != null && FileQual.TryGetValue(file, out string? q) ? q : null;

    /// <summary>What a bare word written in <paramref name="file"/> names,
    /// as actually declared — or the word itself when nothing claims it
    /// (builtins and field accessors are resolved further down the chain).
    ///
    /// Qualification is not required at the use site. A bare word resolves
    /// while its meaning is unambiguous, and only a genuine collision
    /// between two namespaces forces an explicit IN. The order is:
    ///
    ///   1. the writer's own namespace  — a file's own names come first, so
    ///      qualifying two files that share a helper name breaks neither
    ///   2. an exact declaration        — what is literally written wins
    ///   3. a unique namespaced name    — the convenience case
    ///   4. ambiguous                   — reported by <see cref="Ambiguity"/>
    /// </summary>
    public string ResolveName(string name, string? file)
    {
        string? q = QualOf(file);
        if (q != null)
        {
            string own = q + name;
            if (Declares(own)) return own;
        }
        if (Declares(name)) return name;
        if (ByBare.TryGetValue(name, out List<string>? cands) && cands.Count == 1)
            return cands[0];
        return name;
    }

    /// <summary>The candidates when a bare word names more than one thing,
    /// or null when it does not. Checked before falling through to builtins
    /// so that an ambiguity is never silently resolved as something else.</summary>
    public List<string>? Ambiguity(string name, string? file)
    {
        string? q = QualOf(file);
        if (q != null && Declares(q + name)) return null;
        if (Declares(name)) return null;
        return ByBare.TryGetValue(name, out List<string>? c) && c.Count > 1 ? c : null;
    }

    public bool Declares(string name) =>
        HasDef(name) || FindType(name) != null || Sites.ContainsKey(name) ||
        Accessors.ContainsKey(name);

    public void RecordSite(string name, string kind, string? file, int line) =>
        Sites[name] = new DeclSite(kind, file, line);

    /// <summary>"Def 'HINTS' (cave-tables.shoddy:14)", or a bare kind when
    /// the name came from a machine DLL and has no source line.</summary>
    public string DescribeSite(string name)
    {
        if (!Sites.TryGetValue(name, out DeclSite s))
            return ExternalDefs.ContainsKey(name) || ExternalTypes.ContainsKey(name)
                ? "a machine" : "an earlier declaration";
        string where = s.File == null ? "" : $" ({Path.GetFileName(s.File)}:{s.Line})";
        return $"{s.Kind}{where}";
    }

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
