// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Reflection;
using Shoddy.Hosting;

namespace Shoddy.Maui;

/// <summary>
/// One submitted line's outcome: the lines to append to the transcript,
/// the prompt the next entry should wear, and whether the line was QUIT
/// at an ordinary prompt — which the terminal treats as the end of the
/// session and the app treats however its window model wants.
/// </summary>
public sealed record HalifaxTurn(IReadOnlyList<string> Shown, string Prompt, bool Quit);

/// <summary>
/// The calculator's Mode N loop, no view anywhere near it: exactly what
/// halifax.shoddy's own HxLoop does, less the prompt printing and the
/// Print (B4.7). One submitted line goes to HxDo; HxShown yields the
/// transcript lines; HxAfter yields the next state; HxJoined carries an
/// unfinished definition forward so a `: NAME … ;` typed across three
/// entries works for the same reason it works at the terminal prompt.
///
/// The four file words and RESET are dispatched by HxRead before the
/// turn, as the shell dispatches them — the shell owns effects, and in
/// Mode N this class is the shell. Its file I/O resolves under the
/// session's root, which the host application maps to app storage
/// (B3.5): the mill never chooses where the filesystem begins.
///
/// Every engine call runs on a worker under a gate — the core runs off
/// the UI thread from the first commit, not as a later fix (D9, B4.11).
/// Abandon is the cancel path: a NETGET against a peer that will never
/// reply blocks its engine by design, so giving up means walking away.
/// Walking away RESTARTS the session — the state cannot follow, because
/// the dictionary inside it holds quotations whose closures are bound
/// to the engine that built them, and running those against a second
/// engine executes on the first one's (occupied) stack. So cancel has
/// the terminal's own QUIT-and-relaunch semantics: halifaxrc loads
/// again, and unsaved words are gone unless SAVE put them somewhere.
/// The generation counter is what makes the walk-away safe: a stale
/// turn that eventually returns commits nothing.
/// </summary>
public sealed class HalifaxSession
{
    readonly ShoddyHostOptions options;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly object sync = new();
    ShoddyHost host;
    ShoddyValue state;
    string buf = "";
    int generation;

    HalifaxSession(ShoddyHostOptions options)
    {
        this.options = options;
        host = LoadHost(options);
        state = host.Word("HxFresh").Call();
        Prompt = host.Word("HxPrompt").Call().AsStr();
    }

    static ShoddyHost LoadHost(ShoddyHostOptions options) =>
        ShoddyHost.Load(options,
            Assembly.Load("Shoddy.Machines.Halifax-core"),
            Assembly.Load("Shoddy.Machines.Reckoner"));

    /// <summary>Open a session. The root is where the mill's declared
    /// files live — halifaxrc, SAVE and LOAD paths — and it must exist;
    /// a granted `file` capability with no real directory behind it is
    /// C2.4a's startup failure, not a place to write beside the exe.</summary>
    public static HalifaxSession Open(ShoddyHostOptions options) => new(options);

    /// <summary>The prompt for the next entry: HxPrompt at rest,
    /// HxMorePrompt while a definition is open. A cached string,
    /// recomputed only inside a turn — reading it never touches the
    /// engine from a second thread.</summary>
    public string Prompt { get; private set; }

    /// <summary>The lines the terminal prints before the first prompt:
    /// the banner, the halifaxrc report if there is one, and the opening
    /// stack line — same order, same words. Call once, before the first
    /// turn.</summary>
    public IReadOnlyList<string> Opening()
    {
        var lines = new List<string>(Lines(host.Word("HxBanner").Call()));
        string? rc = LoadRc();
        if (rc != null) lines.Add(rc);
        lines.AddRange(Lines(host.Word("RckShow").Call(state)));
        return lines;
    }

    /// <summary>Submit one line, off the caller's thread. Turns are
    /// serialized — the engine is a single value stack, not a service.
    /// Answers null when the turn was abandoned mid-flight.</summary>
    public async Task<HalifaxTurn?> SubmitAsync(string line)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        int myGen;
        lock (sync) myGen = generation;
        try
        {
            return await Task.Run(() => Turn(line, myGen)).ConfigureAwait(false);
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

    /// <summary>Give up on a turn that is not coming back (B4.11). The
    /// blocked engine is abandoned where it stands — its thread parks
    /// until the OS gives up; one thread, once, is the price — and the
    /// session restarts fresh: new engine, HxFresh, halifaxrc reloaded.
    /// Answers the restart's opening report (the rc line if there is
    /// one, then the opening stack), for the transcript to show.</summary>
    public IReadOnlyList<string> Abandon()
    {
        var lines = new List<string>();
        lock (sync)
        {
            generation++;
            host = LoadHost(options);
            state = host.Word("HxFresh").Call();
            buf = "";
            string? rc = LoadRc();
            if (rc != null) lines.Add(rc);
            lines.AddRange(Lines(host.Word("RckShow").Call(state)));
            Prompt = host.Word("HxPrompt").Call().AsStr();
        }
        gate.Release();
        return lines;
    }

    HalifaxTurn? Turn(string line, int myGen)
    {
        ShoddyHost h;
        ShoddyValue st;
        string b;
        lock (sync)
        {
            if (myGen != generation) return null;
            h = host; st = state; b = buf;
        }

        IReadOnlyList<string> shown;
        ShoddyValue next;
        string nextBuf = "";
        bool quit = false;

        if (b.Length == 0 && h.Word("HxQuits").Call(ShoddyValue.Str(line)).AsBool())
        {
            shown = Array.Empty<string>();
            next = h.Word("RckShutAll").Call(st);
            quit = true;
        }
        else
        {
            ShoddyValue joined = h.Word("HxJoined").Call(ShoddyValue.Str(b), ShoddyValue.Str(line));
            if (Is(joined, "HxMore"))
            {
                shown = Array.Empty<string>();
                next = st;
                nextBuf = joined.Field("MoreBuf").AsStr();
            }
            else
            {
                string whole = joined.Field("ReadyLine").AsStr();
                ShoddyValue ask = h.Word("HxRead").Call(ShoddyValue.Str(whole));
                (shown, next) =
                    Is(ask, "HxEval") ? Lined(h, st, h.Word("HxDo").Call(st, ShoddyValue.Str(ask.Field("AskLine").AsStr())))
                    : Is(ask, "HxPath") ? Filed(h, st, whole, ask.Field("PathWord").AsStr(), ask.Field("PathName").AsStr())
                    : Is(ask, "HxPlain") ? Plained(h, st, whole, ask.Field("PlainWord").AsStr())
                    : Refused(h, st, whole, ask.Field("AskWhy").AsStr());
            }
        }

        string prompt = h.Word(nextBuf.Length == 0 ? "HxPrompt" : "HxMorePrompt").Call().AsStr();
        lock (sync)
        {
            if (myGen != generation) return null;
            state = next;
            buf = nextBuf;
            Prompt = prompt;
        }
        return new HalifaxTurn(shown, prompt, quit);
    }

    /// <summary>RESET is the one intercepted word that performs no
    /// effect: a fresh state, not taped — mirroring the shell's
    /// HxPlained arm exactly, "reset" on the transcript included.</summary>
    static (IReadOnlyList<string>, ShoddyValue) Plained(ShoddyHost h, ShoddyValue st, string line, string word)
    {
        if (!word.Equals(h.Word("HxResetWord").Call().AsStr(), StringComparison.OrdinalIgnoreCase))
            return Refused(h, st, line, word + " is decided before the turn and should never arrive here");
        return (new[] { "reset" }, h.Word("HxFresh").Call());
    }

    // ---- the four words that name a file (the shell's HxFiled, with
    // this class holding the pen) ----

    (IReadOnlyList<string>, ShoddyValue) Filed(ShoddyHost h, ShoddyValue st, string line, string word, string path)
    {
        if (Named(h, "HxSaveWord", word)) return SavedWords(h, st, line, path);
        if (Named(h, "HxTapeSaveWord", word)) return SavedTape(h, st, line, path);
        return Loaded(h, st, line, path);
    }

    (IReadOnlyList<string>, ShoddyValue) SavedWords(ShoddyHost h, ShoddyValue st, string line, string path)
    {
        IReadOnlyList<string> rows = Lines(h.Word("RckSave").Call(st));
        if (!TryWrite(path, rows)) return Refused(h, st, line, "cannot write " + path);
        double n = h.Word("HxUserCount").Call(st).AsNum();
        return Said(h, st, line, "saved " + Counted(h, n, "word", "words"));
    }

    (IReadOnlyList<string>, ShoddyValue) SavedTape(ShoddyHost h, ShoddyValue st, string line, string path)
    {
        IReadOnlyList<string> rows = Lines(st.Field("RckTape"));
        if (!TryWrite(path, rows)) return Refused(h, st, line, "cannot write " + path);
        return Said(h, st, line, "wrote " + Counted(h, rows.Count, "row", "rows"));
    }

    (IReadOnlyList<string>, ShoddyValue) Loaded(ShoddyHost h, ShoddyValue st, string line, string path)
    {
        string full = Resolve(path);
        if (!File.Exists(full)) return Refused(h, st, line, "there is no file called " + path);
        string text;
        try { text = File.ReadAllText(full); }
        catch { return Refused(h, st, line, "cannot read " + path); }

        ShoddyValue r = h.Word("RckLoad").Call(st, TextLines(text));
        if (!Is(r, "RckNext"))
            return Refused(h, st, line, Is(r, "RckStop") ? r.Field("StopWhy").AsStr() : "the file did not load");

        ShoddyValue st2 = r.Field("NextState");
        var said = new List<string>(Lines(r.Field("NextOut")));
        double added = h.Word("HxUserCount").Call(st2).AsNum()
                     - h.Word("HxUserCount").Call(st).AsNum();
        said.Add("loaded " + Counted(h, added, "word", "words"));
        ShoddyValue saidList = ShoddyValue.ListOf(said.Select(ShoddyValue.Str));
        ShoddyValue taped = h.Word("RckTapedOk").Call(st, st2, ShoddyValue.Str(line), saidList);
        return (said, taped);
    }

    /// <summary>The shell's HxStart: a missing halifaxrc is silence, a
    /// present one loads through RckLoad — no tape rows, nobody typed
    /// it — and a bad one says so and starts anyway.</summary>
    string? LoadRc()
    {
        const string rc = "halifaxrc";
        string full = Resolve(rc);
        if (!File.Exists(full)) return null;
        string text;
        try { text = File.ReadAllText(full); }
        catch { return rc + ": cannot read it"; }

        ShoddyValue r = host.Word("RckLoad").Call(state, TextLines(text));
        if (Is(r, "RckNext"))
        {
            ShoddyValue st2 = r.Field("NextState");
            double added = host.Word("HxUserCount").Call(st2).AsNum()
                         - host.Word("HxUserCount").Call(state).AsNum();
            string said = rc + ": loaded " + Counted(host, added, "word", "words");
            state = st2;
            return said;
        }
        return Is(r, "RckStop") ? rc + ": " + r.Field("StopWhy").AsStr() : null;
    }

    // ---- small mirrors of the shell's text helpers ----

    static (IReadOnlyList<string>, ShoddyValue) Lined(ShoddyHost h, ShoddyValue st, ShoddyValue r) =>
        (Lines(h.Word("HxShown").Call(r)), h.Word("HxAfter").Call(st, r));

    static (IReadOnlyList<string>, ShoddyValue) Said(ShoddyHost h, ShoddyValue st, string line, string said) =>
        Lined(h, st, h.Word("HxSaid").Call(st, ShoddyValue.Str(line), ShoddyValue.Str(said)));

    static (IReadOnlyList<string>, ShoddyValue) Refused(ShoddyHost h, ShoddyValue st, string line, string why) =>
        Lined(h, st, h.Word("HxRefused").Call(st, ShoddyValue.Str(line), ShoddyValue.Str(why)));

    static string Counted(ShoddyHost h, double n, string one, string many) =>
        h.Word("HxCounted").Call(ShoddyValue.Num(n), ShoddyValue.Str(one), ShoddyValue.Str(many)).AsStr();

    static bool Named(ShoddyHost h, string namer, string word) =>
        word.Equals(h.Word(namer).Call().AsStr(), StringComparison.OrdinalIgnoreCase);

    static bool Is(ShoddyValue v, string typeName) =>
        v.TypeName().Equals(typeName, StringComparison.OrdinalIgnoreCase);

    static IReadOnlyList<string> Lines(ShoddyValue list) =>
        list.AsList().Select(v => v.AsStr()).ToArray();

    /// <summary>One line per row and a closing newline (the shell's
    /// HxFileText); an empty tape writes an empty file.</summary>
    bool TryWrite(string path, IReadOnlyList<string> rows)
    {
        try
        {
            File.WriteAllText(Resolve(path),
                rows.Count == 0 ? "" : string.Join("\n", rows) + "\n");
            return true;
        }
        catch { return false; }
    }

    /// <summary>Split on the newline and Trim what is left (the shell's
    /// HxTextLines) — which takes the carriage return off a file written
    /// on Windows and read on anything else.</summary>
    static ShoddyValue TextLines(string text) =>
        ShoddyValue.ListOf(text.Split('\n').Select(s => ShoddyValue.Str(s.Trim())));

    /// <summary>Every path the session touches resolves under the root
    /// (B3.5): from inside the mill there is nothing outside it.</summary>
    string Resolve(string path) =>
        options.FileRoot is null ? path : Path.Combine(options.FileRoot, path);
}
