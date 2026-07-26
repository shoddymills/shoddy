// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

namespace Shoddy.Compiler;

using Shoddy.Runtime;

/// <summary>Weave-time warnings.
///
/// A word resolves through local binding, top-level Let, program Def,
/// machine Def, type constructor, builtin, and only then field accessor.
/// Accessors therefore lose every contest -- and unlike every other name in
/// that chain, they are never written down: declaring a Type puts one in
/// scope everywhere the type is visible, often from another file. So a
/// parameter named `motion` does not read like a shadowing declaration, it
/// reads like a parameter.
///
/// Nothing else catches this. Annotations are runtime-only, so there is no
/// check at weave time; and the shadowing name almost always has the type
/// the accessor would have returned, so there is no error at run time
/// either. The program just computes the wrong answer.</summary>
public static class Lint
{
    readonly record struct Owner(string Type, string Field);

    /// <summary>Names shadowing a field accessor, as human-readable lines.
    /// Warnings, never errors: shadowing a builtin or an ordinary Def is
    /// legitimate and common, and only accessors are reported.</summary>
    public static List<string> ShadowedAccessors(ShoddyProgram prog)
    {
        var owner = new Dictionary<string, Owner>();
        foreach (TypeDef t in prog.Types)
        {
            // A one-field type is a union variant carrying a payload --
            // Died(Msg), KNumeric(N). Those payloads are read by pattern
            // matching rather than by calling the accessor, so shadowing one
            // is harmless, and `n`, `msg`, `id` are names any program wants
            // for a parameter. Warning on them buries the records, which is
            // where the damage actually happens.
            if (t.Fields.Count < 2) continue;
            for (int i = 0; i < t.Fields.Count; i++)
            {
                string folded = t.Fields[i];
                if (owner.ContainsKey(folded)) continue;
                owner[folded] = new Owner(t.Disp ?? t.Name,
                                          i < t.FDisp.Count ? t.FDisp[i] : folded);
            }
        }

        var hits = new List<string>();
        var seen = new HashSet<string>();
        foreach ((string defName, Quot body) in prog.Defs)
        {
            if (owner.TryGetValue(defName, out Owner d))
                hits.Add($"warning: Def '{defName}' shadows field accessor " +
                         $"'{d.Field}' of type '{d.Type}' — the accessor is unreachable");
            Walk(body, owner, hits, seen);
        }
        hits.Sort(StringComparer.Ordinal);
        return hits;
    }

    static void Walk(Quot q, Dictionary<string, Owner> owner,
                     List<string> hits, HashSet<string> seen)
    {
        foreach (Node n in q.Items)
        {
            // Take binds parameters, Bind binds a Let. With also carries
            // Names, but those are field names being written, not bindings.
            if (n.T is NType.Take or NType.Bind)
            {
                foreach (string name in n.Names)
                {
                    if (!owner.TryGetValue(name, out Owner d)) continue;
                    if (!seen.Add($"{n.File}:{n.Line}:{name}")) continue;
                    hits.Add($"{n.File ?? "?"}:{n.Line}: warning: '{name}' shadows " +
                             $"field accessor '{d.Field}' of type '{d.Type}' — " +
                             "the accessor is unreachable in this scope");
                }
            }
            if (n.Q != null) Walk(n.Q, owner, hits, seen);
            if (n.ElseQ != null) Walk(n.ElseQ, owner, hits, seen);
        }
    }
}
