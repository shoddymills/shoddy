// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

namespace Shoddy.Runtime;

/// <summary>A debug sink observes execution at source-line granularity.
/// OnLine may block — that is how the perch pauses the program.</summary>
public interface IDebugSink
{
    void OnLine(Engine rt);

    /// <summary>A runtime error is about to unwind the program. Called
    /// once per error, from the innermost instrumented frame, BEFORE any
    /// frame is popped: the frames, their bindings and the value stack are
    /// exactly as they were at the failing line. May block, as OnLine
    /// does; the error continues on its way when this returns.</summary>
    void OnError(Engine rt, Exception error) { }
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

    /// <summary>The debugger set one of this frame's variables while it
    /// was stopped; the woven code reloads its locals at the next line
    /// (<see cref="Engine.TakeEdits"/>).</summary>
    public bool Edited;
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

    /// <summary>Per-assembly file registration: each debug-woven
    /// assembly's table, keyed by the table's own identity (a static
    /// array per woven class), mapped to its offset in
    /// <see cref="DebugFiles"/>. A debug program registers first —
    /// its preamble runs before anything else — so its ids stay
    /// absolute at offset 0; each instrumented machine appends on
    /// first entry and carries its offset in every frame.</summary>
    readonly Dictionary<string[], int> fileBases = new();

    /// <summary>Debug preamble: register the source file table and the
    /// root frame (top-level Lets run here, before Main).</summary>
    public void Files(string[] files)
    {
        if (Frames.Count == 0) Frames.Add(new DebugFrame { Name = "(program)" });
        FileBase(files);
    }

    /// <summary>An instrumented machine's source files, where they are
    /// now rather than where they were when it was woven.
    ///
    /// <para>A machine is woven in one place and run in another: the
    /// VS Code extension ships a library woven on the build machine, and
    /// a user's install puts it somewhere else entirely. A path fixed at
    /// weave time then names a file that does not exist here, and the
    /// debugger can neither show a machine's lines nor stop at a
    /// breakpoint in one. So the weave records each source relative to the
    /// folder its DLL will be loaded from (<paramref name="relative"/>:
    /// ../../machines/x.shoddy from a toolchain's lib/debug/), and this
    /// resolves them against the folder the DLL was actually loaded from.
    /// The toolchain carries its library and the sources together, so
    /// the relation holds wherever it lands.</para>
    ///
    /// <para>A pinned source library wins: when SHODDYLIB names a folder
    /// holding the file (or its seeds/ companion does), that is the file
    /// shown, so a checkout debugs its own sources rather than the copy
    /// its build carries. The same variable is what the Include resolver
    /// reads, so the two agree.</para>
    ///
    /// <para>An assembly loaded from memory has no folder (its Location
    /// is empty), and then the path recorded at weave time
    /// (<paramref name="woven"/>) is the only one there is.</para></summary>
    public static string[] SourcesBeside(System.Reflection.Assembly asm, string[] relative, string[] woven)
    {
        string dir = asm.Location.Length == 0 ? "" : Path.GetDirectoryName(asm.Location) ?? "";
        string? pinned = Environment.GetEnvironmentVariable("SHODDYLIB");
        var now = new string[relative.Length];
        for (int i = 0; i < relative.Length; i++)
        {
            if (!string.IsNullOrEmpty(pinned))
            {
                string name = Path.GetFileName(relative[i]);
                bool seed = relative[i].Replace('\\', '/').Contains("/seeds/", StringComparison.OrdinalIgnoreCase);
                string at = seed ? Path.Combine(pinned, "seeds", name) : Path.Combine(pinned, name);
                if (File.Exists(at)) { now[i] = Path.GetFullPath(at); continue; }
            }
            now[i] = dir.Length == 0 ? woven[i] : Path.GetFullPath(Path.Combine(dir, relative[i]));
        }
        return now;
    }

    /// <summary>The offset this assembly's file ids live at — appended
    /// on first sight, constant thereafter. Called by instrumented
    /// machine words on entry; O(1) after the first call.</summary>
    public int FileBase(string[] files)
    {
        if (fileBases.TryGetValue(files, out int at)) return at;
        at = DebugFiles.Length;
        if (files.Length > 0)
        {
            var grown = new string[at + files.Length];
            DebugFiles.CopyTo(grown, 0);
            files.CopyTo(grown, at);
            DebugFiles = grown;
        }
        fileBases[files] = at;
        return at;
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

    /// <summary>
    /// The exception filter every instrumented frame carries. A filter
    /// runs before the frames inside it unwind, so the first one to see an
    /// error (the innermost) hands the sink a program still standing at the
    /// failing line. It always answers false: the error is reported, never
    /// caught, and goes on exactly as it would without a debugger.
    ///
    /// <para>Reported once: the outer frames' filters see the mark and stay
    /// silent.</para>
    /// </summary>
    public bool DbgError(Exception error)
    {
        if (Sink == null || error.Data.Contains(ReportedKey)) return false;
        error.Data[ReportedKey] = true;
        Sink.OnError(this, error);
        return false;
    }

    const string ReportedKey = "shoddy.debug.reported";

    // ---- the debugger changing the program's state ---------------------
    //
    // A variable's value lives in the woven code's own C# local; the
    // frame's Binds is the debugger's view of it. Setting a variable
    // writes the view and marks the frame, and the instrumented code reads
    // the view back into its locals after the line hook it is paused in
    // (TakeEdits/Reload), so the program computes with the new value from
    // the next thing it does. Globals work the same way through their own
    // flag. All of this runs only while the program is stopped.

    bool globalsEdited;

    /// <summary>Set the newest binding of <paramref name="name"/> in a
    /// frame, counted from the bottom of <see cref="Frames"/>.</summary>
    public void DbgSetLocal(int frameIndex, string name, Value v)
    {
        List<(string Name, Value V)> binds = Frames[frameIndex].Binds;
        for (int k = binds.Count - 1; k >= 0; k--)
            if (binds[k].Name == name)
            {
                binds[k] = (name, v);
                Frames[frameIndex].Edited = true;
                return;
            }
        throw new ShoddyError(0, $"there is no variable {name} in {Frames[frameIndex].Name}");
    }

    /// <summary>Set a top-level Let.</summary>
    public void DbgSetGlobal(string name, Value v)
    {
        for (int k = Globals.Count - 1; k >= 0; k--)
            if (Globals[k].Name == name)
            {
                Globals[k] = (name, v);
                globalsEdited = true;
                return;
            }
        throw new ShoddyError(0, $"there is no global {name}");
    }

    /// <summary>Replace a value on the value stack, counted from the
    /// bottom.</summary>
    public void DbgSetStack(int index, Value v)
    {
        if (index < 0 || index >= Stk.Count)
            throw new ShoddyError(0, $"the value stack has no slot {index + 1}");
        Stk[index] = v;
    }

    /// <summary>Did the debugger set a variable of the running frame?
    /// Clears the mark: the woven code reloads its locals once.</summary>
    public bool TakeEdits()
    {
        DebugFrame f = Frames[^1];
        if (!f.Edited) return false;
        f.Edited = false;
        return true;
    }

    /// <summary>Did the debugger set a top-level Let? Clears the mark.</summary>
    public bool TakeGlobalEdits()
    {
        if (!globalsEdited) return false;
        globalsEdited = false;
        return true;
    }

    /// <summary>The running frame's current value for a local, as the
    /// debugger left it; <paramref name="current"/> when it has none.</summary>
    public Value Reload(string name, Value current)
    {
        List<(string Name, Value V)> binds = Frames[^1].Binds;
        for (int k = binds.Count - 1; k >= 0; k--)
            if (binds[k].Name == name) return binds[k].V;
        return current;
    }

    /// <summary>A top-level Let's current value, as the debugger left it.</summary>
    public Value ReloadGlobal(string name, Value current)
    {
        for (int k = Globals.Count - 1; k >= 0; k--)
            if (Globals[k].Name == name) return Globals[k].V;
        return current;
    }

    /// <summary>Put the value stack back to <paramref name="depth"/>
    /// entries: an expression the debugger evaluated is gone without a
    /// trace, whether it finished or failed.</summary>
    public void DbgRestore(int depth)
    {
        if (Stk.Count > depth) Stk.RemoveRange(depth, Stk.Count - depth);
    }

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
