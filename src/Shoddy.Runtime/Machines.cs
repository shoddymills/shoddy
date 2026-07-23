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
    public ShoddyDefAttribute(string name, string method) { Name = name; Method = method; }
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
