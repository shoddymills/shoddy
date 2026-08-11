// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Runtime;

namespace Shoddy.Mcp;

/// <summary>One captured frame: the pixels as they stood at the moment
/// of the blit, and the size they were. A copy, never the live buffer —
/// see SparkyCanvas.OnBlit for why that is the whole design.</summary>
public sealed record SparkyFrame(byte[] Rgba, int Width, int Height)
{
    /// <summary>The PNG, encoded from the snapshot. Png lives in the
    /// runtime precisely so this works with no window anywhere.</summary>
    public byte[] Png() => Runtime.Png.Encode(Rgba, Width, Height);
}

/// <summary>
/// The headless canvas: a real pixel buffer with no window, no Silk.NET
/// and no main-thread hop, plus the capture table that lets a drawing
/// reach a client that has no screen.
///
/// THE BLIT WORDS ARE THE CAPTURE SEAM. `PLOTBLIT`, `SCRIBBLIT` and
/// `TURTLEBLIT` all bottom out in the runtime's ScribblerBlit, which
/// invokes handle.OnBlit. A host that sets OnBlit gets every drawing
/// without knowing, or caring, which word drew it — so this class needs
/// no list of drawing words and cannot fall behind one.
///
/// COPYING INSIDE THE BLIT IS THE POINT. The alternative — remembering
/// the handle and reading its live buffer when a tool asks — would race
/// the drawing thread and hand back a torn frame. Copying while the
/// blitting thread is inside OnBlit removes the tear window entirely,
/// and makes every captured frame immutable thereafter.
///
/// THE TABLE OUTLIVES THE SURFACE (R4.4). `CLOSE` shuts the scribbler
/// and the dictionary can no longer reach it, but the capture table is
/// the HOST's, not the runtime's — so a student who closes before asking
/// can still be shown what they made.
///
/// NOTHING HERE POLLS OR WAITS. No seed registers a wait, poll or event
/// word, ScribblerRegistry.WaitAllClosed is never called (it blocks
/// until every scribbler closes, which for a long-lived server is a
/// hang), and OnSetInterval is left null. There is no live window, no
/// frame loop and no keystroke.
/// </summary>
public sealed class SparkyCanvas
{
    readonly object sync = new();
    readonly List<ScribblerHandle> opened = new();
    readonly Dictionary<ScribblerHandle, SparkyFrame> frames = new();
    ScribblerHandle? latest;
    bool blittedThisTurn;

    /// <summary>Install this canvas as the process's scribbler factory.
    /// The registry is process-global static state, which is why a
    /// server creates exactly one of these and every session shares
    /// it.</summary>
    public void Install() => ScribblerRegistry.CreateScribbler = Create;

    public static void Uninstall() => ScribblerRegistry.CreateScribbler = null;

    ScribblerHandle Create(int w, int h)
    {
        var handle = new ScribblerHandle
        {
            Width = w,
            Height = h,
            Pixels = new byte[w * h * 4],
        };
        // OnSetTitle and OnPlace are no-ops: there is no window to title
        // or to place, and the handle records both itself for anyone who
        // asks. OnClose does the bookkeeping the registry expects and
        // nothing more — notably it does NOT drop the captured frame.
        handle.OnBlit = (px, bw, bh) => Captured(handle, px, bw, bh);
        handle.OnClose = () => { if (handle.MarkClosed()) ScribblerRegistry.NoteClosed(); };
        lock (sync)
        {
            opened.Add(handle);
            latest = handle;
        }
        return handle;
    }

    void Captured(ScribblerHandle handle, byte[] pixels, int w, int h)
    {
        // The copy happens HERE, on the blitting thread, while nothing
        // else can be writing: that is what makes the snapshot safe to
        // hand out later without a lock on the drawing.
        var copy = new byte[w * h * 4];
        Array.Copy(pixels, copy, Math.Min(pixels.Length, copy.Length));
        lock (sync)
        {
            frames[handle] = new SparkyFrame(copy, w, h);
            latest = handle;
            blittedThisTurn = true;
        }
    }

    /// <summary>Called by the session on either side of a turn: clears
    /// the flag before, reads it after. A turn that blitted carries its
    /// picture back inline, which is what makes a chart arrive in the
    /// same answer as the numbers behind it.</summary>
    public void BeginTurn()
    {
        lock (sync) blittedThisTurn = false;
    }

    public bool BlittedThisTurn
    {
        get { lock (sync) return blittedThisTurn; }
    }

    /// <summary>How many surfaces this session has opened, captured or
    /// not. Reported alongside a frame so a client that opened several
    /// knows there are others to ask for.</summary>
    public int Count
    {
        get { lock (sync) return opened.Count; }
    }

    /// <summary>The frame a `canvas` call answers.
    ///
    /// WITH NO ARGUMENT it is the most recently drawn surface, which for
    /// the session that opened one — very nearly every session — is the
    /// only sensible reading of the question. WITH A 1-BASED INDEX it is
    /// that surface in the order they were opened.
    ///
    /// The PULL path exists because a turtle's picture has no natural
    /// finish: seedturtle's own header warns that a session which draws
    /// and sees nothing has forgotten TURTLEBLIT. At a desk the window
    /// is the reminder; here there is none, so forgetting must cost
    /// nothing — and this reads the LIVE buffer when no blit has
    /// happened, which is safe because the session's turn gate
    /// guarantees no Shoddy word is running while it does.</summary>
    public SparkyFrame? Latest(int? index = null)
    {
        lock (sync)
        {
            ScribblerHandle? handle =
                index is null ? latest
                : index >= 1 && index <= opened.Count ? opened[index.Value - 1]
                : null;
            if (handle is null) return null;
            if (frames.TryGetValue(handle, out SparkyFrame? captured)) return captured;
            return Live(handle);
        }
    }

    /// <summary>A surface that was drawn on but never blitted. Read
    /// under the caller's lock AND under the session's turn gate — the
    /// two together are what make reading a live buffer honest here.
    /// A closed surface whose frame was never captured cannot be
    /// recovered: its pixels went with the handle.</summary>
    static SparkyFrame? Live(ScribblerHandle handle)
    {
        if (handle.Width < 1 || handle.Height < 1) return null;
        int want = handle.Width * handle.Height * 4;
        if (handle.Pixels.Length < want) return null;
        var copy = new byte[want];
        Array.Copy(handle.Pixels, copy, want);
        return new SparkyFrame(copy, handle.Width, handle.Height);
    }

    /// <summary>Forget the surfaces of a session that has been reset or
    /// abandoned. The frames go because the session they belonged to is
    /// gone; a new engine has drawn nothing yet.</summary>
    public void Forget()
    {
        lock (sync)
        {
            opened.Clear();
            frames.Clear();
            latest = null;
            blittedThisTurn = false;
        }
    }
}
