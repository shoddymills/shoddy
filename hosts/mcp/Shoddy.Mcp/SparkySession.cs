// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Reflection;
using System.Text;
using Shoddy.Hosting;

namespace Shoddy.Mcp;

/// <summary>
/// One submitted line's outcome, with the three things that are not the
/// same thing kept apart — and they shall never be merged.
///
///   Printed  what PRINT wrote, drained from the session's output stream
///   Said     what the evaluation ANSWERED (RckLine's NextOut / StopOut)
///   Stack    what the stack became, after the line
///
/// PRINT writes the moment it runs, so `1 [ DUP PRINT 2 * ] 3 TIMES`
/// reports each pass in Printed while Said and Stack describe only where
/// the line finished. A client shown one merged blob cannot tell a
/// running commentary from a result, and a model shown one will explain
/// the wrong thing.
///
/// A REFUSED LINE IS A RESULT, NOT AN ERROR. Error carries RckStop's
/// StopWhy and the state is the one the line started from. A
/// protocol-level error is reserved for a malformed request — never for
/// a Shoddy line that did not work, because a refusal is the engine
/// working correctly and a client told otherwise will retry rather than
/// read.
///
/// Unfinished is the third case: a definition, list or program still
/// open at the end of the line. Nothing ran, nothing was refused, and
/// the next line continues it.
/// </summary>
public sealed record SparkyTurn(
    string Line,
    IReadOnlyList<string> Printed,
    IReadOnlyList<string> Said,
    IReadOnlyList<string> Stack,
    string? Error,
    bool Unfinished);

/// <summary>
/// One engine, one dictionary, one capture table, and turns serialized
/// through a gate. `HalifaxSession` is the working precedent and this is
/// its near-twin; where they differ, the difference is that Halifax's
/// caller is a person at a prompt and this one's is a model calling
/// tools.
///
/// TURNS ARE SERIALIZED because the engine is a single value stack, not
/// a thread-safe service. Two clients on one session queue; two sessions
/// do not interfere, because each holds its own ShoddyHost.
///
/// EVERY ENGINE HAS ITS OWN OUTPUT STREAM, and that is what makes
/// Abandon safe. An abandoned engine is not stopped — it cannot be; it
/// is blocked in a socket read by design — so it may still PRINT for as
/// long as it lives. Handing the new engine a NEW stream means the old
/// one writes into a buffer nobody will ever read, rather than leaking a
/// line into a later turn's Printed.
///
/// THE GENERATION COUNTER is the other half of that safety: a stale turn
/// that eventually returns commits nothing, because it checks its
/// generation before writing state back.
/// </summary>
public sealed class SparkySession : IDisposable
{
    /// <summary>The engine's output stream, drainable and safe to write
    /// from the engine thread while the session thread reads it.</summary>
    sealed class Pen : TextWriter
    {
        readonly StringBuilder sb = new();
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char c) { lock (sb) sb.Append(c); }
        public override void Write(string? s) { lock (sb) sb.Append(s); }
        public string Drain()
        {
            lock (sb)
            {
                string s = sb.ToString();
                sb.Clear();
                return s;
            }
        }
    }

    readonly string root;
    readonly bool allowNet;
    readonly SparkyCanvas canvas;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly object sync = new();

    ShoddyHost host;
    Pen pen;
    ShoddyValue state;
    string buf = "";
    int generation;

    public string Name { get; }

    SparkySession(string name, string root, bool allowNet, SparkyCanvas canvas)
    {
        Name = name;
        this.root = root;
        this.allowNet = allowNet;
        this.canvas = canvas;
        (host, pen) = LoadHost();
        state = host.Word("SpkFresh").Call();
    }

    public static SparkySession Open(string name, string root, bool allowNet, SparkyCanvas canvas) =>
        new(name, root, allowNet, canvas);

    /// <summary>A new engine, armed for this host only. ShoddyHost.Load
    /// sets and restores the ambient net switch inside a construction
    /// gate, so the grant applies to Sparky's Mode N engines and nothing
    /// else in the process. ArmNetForProcess is a Mode T concern and is
    /// never called.</summary>
    (ShoddyHost, Pen) LoadHost()
    {
        var p = new Pen();
        ShoddyHost h = ShoddyHost.Load(
            new ShoddyHostOptions { FileRoot = root, AllowNet = allowNet, Output = p },
            Assembly.Load("Shoddy.Machines.Sparky-core"),
            Assembly.Load("Shoddy.Machines.Reckoner"));
        return (h, p);
    }

    // ---- what a session says on the way in ----

    /// <summary>The banner, the sparkyrc report if there is one, and the
    /// opening stack — the same three things, in the same order, a
    /// person gets at the console shell.</summary>
    public IReadOnlyList<string> Opening()
    {
        var lines = new List<string>(Lines(host.Word("SpkBanner").Call()));
        string? rc = LoadRc();
        if (rc != null) lines.Add(rc);
        lines.AddRange(Lines(host.Word("RckShow").Call(state)));
        return lines;
    }

    // ---- a turn ----

    /// <summary>Submit lines in order, off the caller's thread, one turn
    /// each. Answers one result per line; a line that only continued a
    /// definition answers Unfinished and nothing else.</summary>
    public async Task<IReadOnlyList<SparkyTurn>> SubmitAsync(IReadOnlyList<string> lines)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        int myGen;
        lock (sync) myGen = generation;
        try
        {
            return await Task.Run(() =>
            {
                var turns = new List<SparkyTurn>();
                foreach (string line in lines)
                {
                    SparkyTurn? t = Turn(line, myGen);
                    if (t is null) break;          // abandoned mid-flight
                    turns.Add(t);
                }
                return (IReadOnlyList<SparkyTurn>)turns;
            }).ConfigureAwait(false);
        }
        finally
        {
            // An abandoned turn's gate was already reopened by Abandon;
            // releasing again would let two live turns overlap.
            lock (sync)
                if (myGen == generation)
                    gate.Release();
        }
    }

    SparkyTurn? Turn(string line, int myGen)
    {
        ShoddyHost h;
        ShoddyValue st;
        Pen p;
        string b;
        lock (sync)
        {
            if (myGen != generation) return null;
            h = host; st = state; p = pen; b = buf;
        }

        // Drain first, so anything a previous turn's PRINT left behind
        // belongs to that turn and not to this one.
        p.Drain();
        canvas.BeginTurn();

        ShoddyValue joined = h.Word("SpkJoined").Call(ShoddyValue.Str(b), ShoddyValue.Str(line));
        if (Is(joined, "SpkMore"))
        {
            string more = joined.Field("MoreBuf").AsStr();
            lock (sync)
            {
                if (myGen != generation) return null;
                buf = more;
            }
            return new SparkyTurn(line, Array.Empty<string>(), Array.Empty<string>(),
                                  Lines(h.Word("RckShow").Call(st)), null, true);
        }

        string whole = joined.Field("ReadyLine").AsStr();
        ShoddyValue r = h.Word("SpkDo").Call(st, ShoddyValue.Str(whole));

        bool stopped = Is(r, "RckStop");
        ShoddyValue next = h.Word("SpkAfter").Call(st, r);
        IReadOnlyList<string> said = Lines(r.Field(stopped ? "StopOut" : "NextOut"));
        string? why = stopped ? r.Field("StopWhy").AsStr() : null;
        IReadOnlyList<string> stack = Lines(h.Word("RckShow").Call(next));
        IReadOnlyList<string> printed = Split(p.Drain());

        lock (sync)
        {
            if (myGen != generation) return null;
            state = next;
            buf = "";
        }
        return new SparkyTurn(whole, printed, said, stack, why, false);
    }

    // ---- restarting ----

    /// <summary>`reset`: a fresh dictionary and nothing else, which is
    /// what RESET means at the Halifax prompt. sparkyrc is NOT reloaded
    /// — a known discrepancy inherited rather than introduced, because
    /// fixing it on one side would give two shells two meanings for one
    /// word. The engine is kept: nothing is wrong with it.</summary>
    public async Task<IReadOnlyList<string>> ResetAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (sync)
            {
                state = host.Word("SpkFresh").Call();
                buf = "";
            }
            canvas.Forget();
            pen.Drain();
            return Lines(host.Word("RckShow").Call(state));
        }
        finally { gate.Release(); }
    }

    /// <summary>`abandon`: give up on a turn that is not coming back.
    ///
    /// A NETGET against a peer that never replies blocks its engine by
    /// design, and the state cannot follow to a new one: the dictionary
    /// holds quotations whose closures are bound to the engine that
    /// built them, and running those against a second engine would
    /// execute on the first one's occupied stack. So abandon means a NEW
    /// ENGINE — SpkFresh, sparkyrc reloaded, unsaved words gone. The
    /// blocked engine's thread parks until the OS gives up; one thread,
    /// once, is the price.</summary>
    public IReadOnlyList<string> Abandon()
    {
        var lines = new List<string>();
        lock (sync)
        {
            generation++;
            (host, pen) = LoadHost();
            state = host.Word("SpkFresh").Call();
            buf = "";
            string? rc = LoadRc();
            if (rc != null) lines.Add(rc);
            lines.AddRange(Lines(host.Word("RckShow").Call(state)));
        }
        canvas.Forget();
        gate.Release();
        return lines;
    }

    // ---- what the reading tools ask ----

    public IReadOnlyList<string> Stack() => Read(h => h.Word("RckShow").Call(state));

    public IReadOnlyList<string> Help(string word) =>
        Read(h => h.Word("RckHelp").Call(state, ShoddyValue.Str(word)));

    public IReadOnlyList<string> View(string word) =>
        Read(h => h.Word("RckView").Call(state, ShoddyValue.Str(word)));

    public IReadOnlyList<string> Words() => Read(h => h.Word("RckWords").Call(state));

    public IReadOnlyList<string> WordsByEffect() =>
        Read(h => h.Word("RckWordsEffect").Call(state));

    public IReadOnlyList<string> Tape() => Read(_ => state.Field("RckTape"));

    public IReadOnlyList<string> Definitions() => Read(h => h.Word("RckSave").Call(state));

    /// <summary>Every reading tool goes through the same gate the turns
    /// do. These calls run Shoddy words — RckShow renders, RckWords
    /// packs columns — so they touch the one value stack and may not
    /// overlap a turn.</summary>
    IReadOnlyList<string> Read(Func<ShoddyHost, ShoddyValue> of)
    {
        gate.Wait();
        try { return Lines(of(host)); }
        finally { gate.Release(); }
    }

    /// <summary>The canvas, read while no turn is running. This is the
    /// guarantee that makes SparkyCanvas.Latest safe to read a live,
    /// never-blitted buffer.</summary>
    public SparkyFrame? Canvas(int? index)
    {
        gate.Wait();
        try { return canvas.Latest(index); }
        finally { gate.Release(); }
    }

    public bool BlittedLastTurn => canvas.BlittedThisTurn;

    public int CanvasCount => canvas.Count;

    // ---- the three that name a file ----

    public string SaveWords(string path)
    {
        gate.Wait();
        try
        {
            IReadOnlyList<string> rows = Lines(host.Word("RckSave").Call(state));
            Write(path, rows);
            double n = host.Word("SpkUserCount").Call(state).AsNum();
            return "saved " + Counted(n, "word", "words");
        }
        finally { gate.Release(); }
    }

    public string SaveTape(string path)
    {
        gate.Wait();
        try
        {
            IReadOnlyList<string> rows = Lines(state.Field("RckTape"));
            Write(path, rows);
            return "wrote " + Counted(rows.Count, "row", "rows");
        }
        finally { gate.Release(); }
    }

    /// <summary>LOAD: one bad line refuses the whole file, because
    /// RckLoad validates a saved line exactly as a typed one. A
    /// dictionary half-populated by a truncated download is the failure
    /// this prevents.</summary>
    public string LoadWords(string path)
    {
        gate.Wait();
        try
        {
            string full = Resolve(path);
            if (!File.Exists(full)) throw new FileNotFoundException("there is no file called " + path);
            ShoddyValue r = host.Word("RckLoad").Call(state, TextLines(File.ReadAllText(full)));
            if (!Is(r, "RckNext"))
                throw new InvalidOperationException(
                    Is(r, "RckStop") ? r.Field("StopWhy").AsStr() : "the file did not load");

            ShoddyValue st2 = r.Field("NextState");
            double added = host.Word("SpkUserCount").Call(st2).AsNum()
                         - host.Word("SpkUserCount").Call(state).AsNum();
            lock (sync) state = st2;
            return "loaded " + Counted(added, "word", "words");
        }
        finally { gate.Release(); }
    }

    /// <summary>sparkyrc, with halifaxrc's exact semantics: absent is
    /// silence, present is loaded through RckLoad and reported, bad says
    /// so and the session starts anyway. No tape rows — the tape is a
    /// record of a session, and nobody typed this.</summary>
    string? LoadRc()
    {
        string rc = host.Word("SpkRcName").Call().AsStr();
        string full = Resolve(rc);
        if (!File.Exists(full)) return null;
        string text;
        try { text = File.ReadAllText(full); }
        catch { return rc + ": cannot read it"; }

        ShoddyValue r = host.Word("RckLoad").Call(state, TextLines(text));
        if (Is(r, "RckNext"))
        {
            ShoddyValue st2 = r.Field("NextState");
            double added = host.Word("SpkUserCount").Call(st2).AsNum()
                         - host.Word("SpkUserCount").Call(state).AsNum();
            state = st2;
            return rc + ": loaded " + Counted(added, "word", "words");
        }
        return Is(r, "RckStop") ? rc + ": " + r.Field("StopWhy").AsStr() : null;
    }

    // ---- small helpers, mirroring the shell's own ----

    string Counted(double n, string one, string many) =>
        host.Word("SpkCounted").Call(ShoddyValue.Num(n), ShoddyValue.Str(one), ShoddyValue.Str(many)).AsStr();

    static bool Is(ShoddyValue v, string typeName) =>
        v.TypeName().Equals(typeName, StringComparison.OrdinalIgnoreCase);

    static IReadOnlyList<string> Lines(ShoddyValue list) =>
        list.AsList().Select(v => v.AsStr()).ToArray();

    /// <summary>What PRINT wrote, as lines. A trailing newline is the
    /// end of the last line and not an empty one after it.</summary>
    static IReadOnlyList<string> Split(string text) =>
        text.Length == 0
            ? Array.Empty<string>()
            : text.TrimEnd('\n').Replace("\r", "").Split('\n');

    static ShoddyValue TextLines(string text) =>
        ShoddyValue.ListOf(text.Split('\n').Select(s => ShoddyValue.Str(s.Trim())));

    /// <summary>One line per row and a closing newline; an empty tape
    /// writes an empty file.</summary>
    void Write(string path, IReadOnlyList<string> rows) =>
        File.WriteAllText(Resolve(path),
            rows.Count == 0 ? "" : string.Join("\n", rows) + "\n");

    /// <summary>The paths the HOST resolves itself — sparkyrc, and the
    /// save/load/tape tools. The paths the DICTIONARY resolves are the
    /// runtime's business and are contained there, against the same
    /// root, by ShoddyHostOptions.FileRoot.</summary>
    string Resolve(string path) => Path.Combine(root, path);

    /// <summary>Shut whatever the session left open. RckShutAll, never
    /// ScribblerRegistry.WaitAllClosed — the latter blocks until every
    /// scribbler closes, which for a long-lived server is a hang.</summary>
    public void Dispose()
    {
        try
        {
            gate.Wait(TimeSpan.FromSeconds(5));
            state = host.Word("RckShutAll").Call(state);
        }
        catch { /* a blocked engine cannot be tidied; let it go */ }
    }
}
