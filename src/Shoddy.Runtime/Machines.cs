// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

namespace Shoddy.Runtime;

// A compiled machine (a lib/*.shoddy woven to a class library) carries its
// manifest as assembly attributes: the exported def names with their
// method names, and the record types with their field lists and declared
// spellings. The weaver reads these to resolve calls, constructors,
// accessors, With, and patterns against precompiled code.

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ShoddyMachineAttribute : Attribute
{
    public string ClassName { get; }
    public ShoddyMachineAttribute(string className) => ClassName = className;
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class ShoddyDefAttribute : Attribute
{
    public string Name { get; }
    public string Method { get; }

    /// <summary>Stack effect, when the linter could infer one at machine
    /// build time: how many values the word pops and pushes. -1 = not
    /// declared (an older DLL, or a word the checker could not model) —
    /// callers of such a word are skipped, not guessed.</summary>
    public int Pops { get; set; } = -1;
    public int Pushes { get; set; } = -1;

    public ShoddyDefAttribute(string name, string method) { Name = name; Method = method; }
}

/// <summary>A machine this machine's Include chain reaches. Assembly
/// references alone cannot carry this: a pure re-export Include (isam's
/// seq) calls no dependency code, Roslyn drops the unused reference, and
/// the dependency's surface silently vanishes from programs that include
/// the DLL. The manifest states what the source said.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class ShoddyDependsOnAttribute : Attribute
{
    public string AssemblyName { get; }
    public ShoddyDependsOnAttribute(string assemblyName) => AssemblyName = assemblyName;
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
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
