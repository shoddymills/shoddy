// Copyright (c) 2026 Stephen Vincent Foster. All rights reserved.
//
// This file is part of the Shoddy Language project.
// Licensed under the Shoddy Language License 1.0.0 (PolyForm Noncommercial
// License 1.0.0 with Additional Use Grant). See the LICENSE file in the
// project root for full terms.

using System.Reflection;
using Shoddy.Runtime;

namespace Shoddy.Compiler;

/// <summary>The weave-time surface of one compiled machine DLL, read
/// from its assembly-attribute manifest.</summary>
public sealed class MachineInfo
{
    public required string AssemblyName;
    public required string DllPath;
    public required string ClassName;   // e.g. "Shoddy.Machines.Seq"
    public readonly Dictionary<string, string> Defs = new();   // name -> method
    public readonly Dictionary<string, (string Field, TypeDef Shape)> Types = new();

    public static MachineInfo From(Assembly asm, string dllPath)
    {
        var m = asm.GetCustomAttribute<ShoddyMachineAttribute>()
            ?? throw new ShoddyError(0, $"'{dllPath}' is not a Shoddy machine (no manifest)");
        var info = new MachineInfo
        {
            AssemblyName = asm.GetName().Name!,
            DllPath = dllPath,
            ClassName = m.ClassName,
        };
        foreach (var d in asm.GetCustomAttributes<ShoddyDefAttribute>())
            info.Defs[d.Name] = d.Method;
        foreach (var t in asm.GetCustomAttributes<ShoddyTypeAttribute>())
        {
            var shape = new TypeDef { Name = t.Name, Disp = t.Disp };
            for (int k = 0; k < t.Fields.Length; k++)
            {
                shape.Fields.Add(t.Fields[k]);
                shape.FDisp.Add(t.FDisp[k]);
            }
            info.Types[t.Name] = (t.Field, shape);
        }
        return info;
    }
}

/// <summary>
/// Resolves Include paths to precompiled machine DLLs. A machine for
/// lib/seq.shoddy is Shoddy.Machines.Seq.dll beside it; loading one pulls in
/// the manifests of the machines it references (compiled include-once —
/// an assembly is loaded at most once, however many routes reach it).
/// </summary>
public sealed class MachineSet
{
    public readonly List<MachineInfo> Machines = new();
    readonly HashSet<string> loaded = new();

    public static string ClassNameFor(string sbPath)
    {
        string stem = Path.GetFileNameWithoutExtension(sbPath);
        return char.ToUpperInvariant(stem[0]) + stem[1..].ToLowerInvariant();
    }

    public static string DllPathFor(string sbPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sbPath))!,
                     $"Shoddy.Machines.{ClassNameFor(sbPath)}.dll");

    /// <summary>The Lexer's externalInclude hook: true = a machine DLL
    /// satisfies this include, don't splice the source.</summary>
    public bool TryResolve(string sbPath)
    {
        string dll = DllPathFor(sbPath);
        if (!File.Exists(dll)) return false;
        LoadRecursive(dll);
        return true;
    }

    void LoadRecursive(string dllPath)
    {
        Assembly asm = Assembly.LoadFrom(dllPath);
        if (!loaded.Add(asm.GetName().Name!)) return;
        Machines.Add(MachineInfo.From(asm, dllPath));
        foreach (AssemblyName rn in asm.GetReferencedAssemblies())
        {
            if (!rn.Name!.StartsWith("Shoddy.Machines.", StringComparison.Ordinal))
                continue;
            if (loaded.Contains(rn.Name)) continue;
            string dep = Path.Combine(Path.GetDirectoryName(dllPath)!, rn.Name + ".dll");
            if (!File.Exists(dep))
                throw new ShoddyError(0,
                    $"machine {asm.GetName().Name} references {rn.Name}, but {dep} is missing");
            LoadRecursive(dep);
        }
    }

    /// <summary>Seed a program with every loaded machine's surface so the
    /// parser and code generator resolve against precompiled code.</summary>
    public void SeedInto(ShoddyProgram prog)
    {
        foreach (MachineInfo m in Machines)
        {
            foreach ((string name, string method) in m.Defs)
            {
                if (prog.ExternalDefs.ContainsKey(name))
                    throw new ShoddyError(0, $"duplicate definition of {name} across machines");
                prog.ExternalDefs[name] = $"{m.ClassName}.{method}";
            }
            foreach ((string name, (string field, TypeDef shape)) in m.Types)
            {
                if (prog.ExternalTypes.ContainsKey(name))
                    throw new ShoddyError(0, $"duplicate definition of {name} across machines");
                prog.ExternalTypes[name] = new ExternalType(shape, $"{m.ClassName}.{field}");
            }
        }
    }
}
