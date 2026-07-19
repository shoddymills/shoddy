// Copyright (c) 2026 Stephen Vincent Foster. All rights reserved.
//
// This file is part of the Shoddy Language project.
// Licensed under the Shoddy Language License 1.0.0 (PolyForm Noncommercial
// License 1.0.0 with Additional Use Grant). See the LICENSE file in the
// project root for full terms.

namespace Shoddy.Runtime;

/// <summary>A debug sink observes execution at source-line granularity.
/// OnLine may block — that is how the perch pauses the program.</summary>
public interface IDebugSink
{
    void OnLine(Engine rt);
}

/// <summary>One call-stack frame as the debugger sees it: the def's name,
/// its current source position, and the bindings made so far. Bindings
/// are last-wins (shadowing) and truncated on block exit — the same
/// discipline the immutable environment chain gave the language.</summary>
public sealed class DebugFrame
{
    public string Name = "";
    public int FileId;
    public int Line;
    public readonly List<(string Name, Value V)> Binds = new();
}

// The debug surface. Only debug-woven code calls any of this; a release
// weave carries no instrumentation and pays nothing.
public sealed partial class Engine
{
    /// <summary>Installed by the launcher before invoking a debug-woven
    /// Run; the generated preamble copies it onto the instance.</summary>
    public static IDebugSink? PendingSink;

    public IDebugSink? Sink;
    public string[] DebugFiles = Array.Empty<string>();
    public readonly List<DebugFrame> Frames = new();
    public readonly List<(string Name, Value V)> Globals = new();

    /// <summary>The value stack, read-only — half the story in a
    /// concatenative language.</summary>
    public IReadOnlyList<Value> StackView => Stk;

    /// <summary>Debug preamble: register the source file table and the
    /// root frame (top-level Lets run here, before Main).</summary>
    public void Files(string[] files)
    {
        DebugFiles = files;
        Frames.Add(new DebugFrame { Name = "(program)" });
    }

    /// <summary>Source-line boundary: update position, offer the sink a
    /// chance to pause.</summary>
    public void Dbg(int fileId, int line)
    {
        DebugFrame f = Frames[^1];
        f.FileId = fileId;
        f.Line = line;
        Sink?.OnLine(this);
    }

    public void Enter(string name, int fileId, int line) =>
        Frames.Add(new DebugFrame { Name = name, FileId = fileId, Line = line });

    public void Exit() => Frames.RemoveAt(Frames.Count - 1);

    public void Bind(string name, Value v) => Frames[^1].Binds.Add((name, v));

    public void BindGlobal(string name, Value v) => Globals.Add((name, v));

    /// <summary>Block scoping for bindings: Mark on entry, Release on
    /// exit truncates back — bindings inside an If branch or a quotation
    /// body go out of scope exactly as the env chain's did.</summary>
    public int Mark() => Frames[^1].Binds.Count;

    public void Release(int mark)
    {
        List<(string, Value)> b = Frames[^1].Binds;
        if (b.Count > mark) b.RemoveRange(mark, b.Count - mark);
    }
}
