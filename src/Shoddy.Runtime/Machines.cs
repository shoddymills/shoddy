// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

namespace Shoddy.Runtime;

// A compiled machine is a public static class under Shoddy.Machines that
// carries its manifest as attributes ON THE CLASS: the source it came
// from, the machines its Include chain reaches, the exported def names
// with their method names, and the record types with their field lists
// and declared spellings. The weaver reads these to resolve calls,
// constructors, accessors, With, and patterns against precompiled code;
// a host reads them to find the words it may call.
//
// An assembly holds one such class (a mill's core, woven by `mill
// machine`) or many (a machine library, woven whole by `mill library`
// or built from its generated C# by the library's own project). Reading
// the manifest from the class rather than the assembly is what lets the
// two shapes read alike.

[AttributeUsage(AttributeTargets.Class)]
public sealed class ShoddyMachineAttribute : Attribute
{
    /// <summary>The source file the machine was woven from, by name:
    /// seq.shoddy. What a program writes in an Include to reach it.</summary>
    public string Source { get; }
    public ShoddyMachineAttribute(string source) => Source = source;
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ShoddyDefAttribute : Attribute
{
    public string Name { get; }
    public string Method { get; }

    /// <summary>Stack effect, when the linter could infer one at machine
    /// build time: how many values the word pops and pushes. -1 = not
    /// declared (a word the checker could not model) — callers of such a
    /// word are skipped, not guessed.</summary>
    public int Pops { get; set; } = -1;
    public int Pushes { get; set; } = -1;

    public ShoddyDefAttribute(string name, string method) { Name = name; Method = method; }
}

/// <summary>A machine this machine's Include chain reaches, by class
/// name (Shoddy.Machines.Seq). Assembly references alone cannot carry
/// this: a pure re-export Include (isam's seq) calls no dependency code,
/// Roslyn drops the unused reference, and the dependency's surface
/// silently vanishes from programs that include the DLL. The manifest
/// states what the source said — and inside a library, every dependency
/// is in the same assembly anyway.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ShoddyDependsOnAttribute : Attribute
{
    public string ClassName { get; }
    public ShoddyDependsOnAttribute(string className) => ClassName = className;
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ShoddyTypeAttribute : Attribute
{
    public string Name { get; }
    public string Disp { get; }
    public string Field { get; }
    public string[] Fields { get; }
    public string[] FDisp { get; }

    public ShoddyTypeAttribute(string name, string disp, string field,
                               string[] fields, string[] fdisp)
    {
        Name = name; Disp = disp; Field = field; Fields = fields; FDisp = fdisp;
    }
}
