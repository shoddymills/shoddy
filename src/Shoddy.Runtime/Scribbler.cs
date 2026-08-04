// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;

namespace Shoddy.Runtime;

// The scribbler seam: the contract between the runtime (which owns the
// pixel buffer and never references a windowing library) and the mill
// (which owns the window and only ever *reads* the buffer to blit it).
// Follows the Engine.PendingSink precedent for a static seam.

/// <summary>The monotonic clock behind the TICKS builtin and every event
/// timestamp — one Stopwatch, started at type load. Never wall-clock,
/// never runs backwards.</summary>
public static class Ticker
{
    static readonly Stopwatch Sw = Stopwatch.StartNew();
    public static double Now => Sw.Elapsed.TotalMilliseconds;
}

/// <summary>A scribbler: a software pixel buffer the runtime draws into,
/// optionally backed by a window the mill blits it to. The one place
/// Shoddy departs from pure functional values — equality is reference
/// identity and the buffer mutates in place.</summary>
public sealed class ScribblerHandle
{
    public byte[] Pixels = Array.Empty<byte>();   // RGBA, row-major, top-down
    public int Width, Height;
    public string Title = "";

    // Where SCRIBBLERPLACE last asked the window to sit, in desktop pixels.
    // Recorded here as well as posted to the window so that a headless
    // handle still answers the question, and so a placement made before the
    // window exists is not lost. Both -1 until asked; a window manager is
    // free to ignore the request and nothing reads these back from it.
    public int PlaceX = -1, PlaceY = -1;

    public readonly ConcurrentQueue<ScribblerEvent> Events = new();
    // Released by the window whenever it enqueues, and once on teardown.
    // SCRIBBLERWAIT blocks on this; nothing else may.
    public readonly SemaphoreSlim Signal = new(0);

    // Set by the mill's rendering layer when a window backs this handle;
    // null when headless.
    public Action<byte[], int, int>? OnBlit;
    public Action? OnClose;
    public Action<string>? OnSetTitle;
    public Action<int>? OnSetInterval;            // (milliseconds, 0 = off)
    public Action<int, int>? OnPlace;             // (desktop x, desktop y)

    int closed;

    /// <summary>True once the window is gone (or SCRIBBLERCLOSE ran).
    /// A wait that wakes to an empty queue on a closed handle yields
    /// Quit instead of blocking forever.</summary>
    public bool Closed => Volatile.Read(ref closed) != 0;

    /// <summary>Marks the handle closed; true for the first caller only.
    /// Whoever wins does the OpenCount decrement and the teardown Signal
    /// release — this is what makes SCRIBBLERCLOSE and a user-initiated
    /// window close idempotent against each other.</summary>
    public bool MarkClosed() => Interlocked.Exchange(ref closed, 1) == 0;

    /// <summary>Every pixel write in the runtime goes through here —
    /// SCRIBBLERPIXEL, SCRIBBLERFILL and Font8x8 all share it.
    /// Out-of-bounds writes are dropped silently.</summary>
    public void SetPixelClamped(int x, int y, byte r, byte g, byte b)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;
        int i = (y * Width + x) * 4;
        Pixels[i] = r; Pixels[i + 1] = g; Pixels[i + 2] = b; Pixels[i + 3] = 255;
    }

    /// <summary>Dequeue one event, marking it taken. The window layer's
    /// producer-side coalescing (MouseMove, Tick) checks the flag before
    /// reusing an event it enqueued earlier.</summary>
    public bool TryTake(out ScribblerEvent ev)
    {
        if (Events.TryDequeue(out ev!)) { ev.Taken = true; return true; }
        return false;
    }
}

public sealed class ScribblerEvent
{
    public enum Kind { None, MouseDown, MouseMove, MouseUp,
                       KeyDown, KeyUp, Typed, Tick, Quit }

    [Flags] public enum Mod { None = 0, Shift = 1, Ctrl = 2, Alt = 4, Super = 8 }

    public Kind Type;
    public int X, Y, Button;     // Button: 1=left 2=right 3=middle
    public int Key;              // KeyDown/KeyUp: a ScribblerKeys code
    public string KeyChar = "";  // Typed: the character; "" for every other kind
    public Mod Mods;
    public double At;            // TICKS value when the event was generated

    /// <summary>Set by the runtime the moment the event is dequeued. The
    /// window layer coalesces by updating a still-queued MouseMove in
    /// place and by skipping a Tick whose predecessor is still queued;
    /// this flag is how it knows. The mutate-vs-dequeue overlap is a few
    /// nanoseconds wide and at worst yields one transiently mixed mouse
    /// coordinate — accepted, and it never corrupts a decoded event.</summary>
    public volatile bool Taken;
}

/// <summary>Shoddy's own key codes — stable numbers, never renumbered,
/// never a windowing library's enum leaked through the seam. Letters,
/// digits and the classic control keys sit on their ASCII codes; named
/// keys live at 256 and up. Anything unmapped reports 0.</summary>
public static class ScribblerKeys
{
    // Letters are 'A'..'Z' (65..90), digits '0'..'9' (48..57).
    public const int Backspace = 8;
    public const int Tab = 9;
    public const int Enter = 13;
    public const int Escape = 27;
    public const int Space = 32;

    public const int Insert = 256;
    public const int Delete = 257;
    public const int Home = 258;
    public const int End = 259;
    public const int PageUp = 260;
    public const int PageDown = 261;
    public const int Left = 262;
    public const int Right = 263;
    public const int Up = 264;
    public const int Down = 265;

    public const int F1 = 281;
    public const int F2 = 282;
    public const int F3 = 283;
    public const int F4 = 284;
    public const int F5 = 285;
    public const int F6 = 286;
    public const int F7 = 287;
    public const int F8 = 288;
    public const int F9 = 289;
    public const int F10 = 290;
    public const int F11 = 291;
    public const int F12 = 292;

    public const int LeftShift = 301;
    public const int RightShift = 302;
    public const int LeftCtrl = 303;
    public const int RightCtrl = 304;
    public const int LeftAlt = 305;
    public const int RightAlt = 306;
    public const int LeftSuper = 307;
    public const int RightSuper = 308;
}

public static class ScribblerRegistry
{
    // Set once by the mill at startup. Null when no window backend is running
    // (a woven program run via dotnet, mill machine, headless tests).
    public static Func<int, int, ScribblerHandle>? CreateScribbler;

    // Incremented by SCRIBBLEROPEN, decremented by NoteClosed (via
    // MarkClosed, exactly once per handle). Read by the INPUT builtin —
    // the console and the window are separate input channels and reading
    // the blocked one is refused.
    public static int OpenCount;

    // Released once per NoteClosed; WaitAllClosed drains it. Stale counts
    // (closes nobody was waiting for) cause spurious wakes, never missed
    // ones — the waiter loops on OpenCount.
    static readonly SemaphoreSlim closedTick = new(0);

    /// <summary>The MarkClosed winner's bookkeeping: decrement OpenCount
    /// and wake anyone waiting for the last scribbler to close. Called by
    /// SCRIBBLERCLOSE and by window teardown — never both for one handle;
    /// MarkClosed arbitrates.</summary>
    public static void NoteClosed()
    {
        Interlocked.Decrement(ref OpenCount);
        closedTick.Release();
    }

    /// <summary>Block at zero CPU until no scribbler is open. Returns
    /// immediately when none is. The perch uses this to keep a debug
    /// session alive while a returned program's window is still up —
    /// the same "draw a picture, return" semantics `mill run` gives.</summary>
    public static void WaitAllClosed()
    {
        while (Volatile.Read(ref OpenCount) > 0) closedTick.Wait();
    }
}
