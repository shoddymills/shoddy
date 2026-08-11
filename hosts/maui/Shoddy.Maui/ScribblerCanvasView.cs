// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Runtime.InteropServices;
using Shoddy.Runtime;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace Shoddy.Maui;

/// <summary>
/// The scribbler's picture inside a MAUI layout (B1.4b): a view that
/// presents the seam's RGBA buffer — a picture inside the app's UI,
/// never the app's UI. The blit is SkiaSharp (D10): InstallPixels wraps
/// the buffer zero-copy for the draw, which is the decision the spike
/// measures and the falsifier would revert.
///
/// The buffer belongs to the runtime and mutates on the Shoddy thread
/// while this view reads it on the UI thread. That is the mill's own
/// presentation model — the desktop window blits from the same live
/// buffer — and at worst a frame shows a half-drawn update that the
/// next invalidate replaces.
///
/// <see cref="Attach"/> adds the games' half (B5.5), mirroring
/// ScribblerWindow's event production exactly: pointer events in buffer
/// coordinates (inverse aspect-fit), keystrokes through a hidden key
/// sink as KeyDown/KeyUp (ScribblerKeys codes) and Typed (characters),
/// interval ticks with the one-pending-Tick rule, MouseMove coalescing,
/// and the same bounded lossy queue. A view never Attached — the
/// calculator's chart — presents and stays silent.
/// </summary>
public sealed class ScribblerCanvasView : SKCanvasView
{
    const int QueueCap = 2048;      // bounded and lossy: drop oldest on overflow

    byte[]? pixels;
    int width, height;
    int invalidatePending;

    ScribblerHandle? handle;
    ScribblerEvent? lastEnqueued;
    ScribblerEvent? lastTick;
    Timer? tick;

    public ScribblerCanvasView()
    {
        PaintSurface += Paint;
    }

    /// <summary>Wire the games' event plumbing to the handle. Call
    /// before the view joins the visual tree (the presenter does); the
    /// interval callback lands here too, so SetFps works.</summary>
    public void Attach(ScribblerHandle h)
    {
        handle = h;
        h.OnSetInterval = ms =>
        {
            tick?.Dispose();
            tick = null;
            if (ms > 0)
                tick = new Timer(_ => EmitTick(), null, ms, ms);
        };
    }

    /// <summary>Called on the Shoddy thread by the handle's OnBlit:
    /// note the buffer and coalesce a redraw onto the main thread —
    /// many blits between frames cost one invalidate.</summary>
    public void Present(byte[] rgba, int w, int h)
    {
        pixels = rgba;
        width = w;
        height = h;
        if (Interlocked.Exchange(ref invalidatePending, 1) == 0)
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Interlocked.Exchange(ref invalidatePending, 0);
                InvalidateSurface();
            });
    }

    // ---- events (B5.5) --------------------------------------------------

    /// <summary>At most one pending Tick: if the handler cannot keep
    /// up, missed ticks drop rather than queue — one slow frame must
    /// not spiral into a backlog. The program sees drops in the At
    /// timestamps, exactly as at the desktop window.</summary>
    void EmitTick()
    {
        ScribblerHandle? h = handle;
        if (h is null) return;
        if (h.Closed) { tick?.Dispose(); tick = null; return; }
        if (lastTick != null && !lastTick.Taken) return;
        var t = new ScribblerEvent { Type = ScribblerEvent.Kind.Tick, At = Ticker.Now };
        lastTick = t;
        Enqueue(t);
    }

    /// <summary>Consecutive MouseMoves coalesce: a still-queued
    /// MouseMove updates in place — only the newest position matters.</summary>
    void EnqueueMove(int x, int y, ScribblerEvent.Mod mods)
    {
        ScribblerEvent? last = lastEnqueued;
        if (last != null && last.Type == ScribblerEvent.Kind.MouseMove && !last.Taken)
        {
            last.X = x; last.Y = y; last.Mods = mods; last.At = Ticker.Now;
            return;
        }
        Enqueue(new ScribblerEvent
        {
            Type = ScribblerEvent.Kind.MouseMove,
            X = x, Y = y, Mods = mods, At = Ticker.Now,
        });
    }

    void Enqueue(ScribblerEvent ev)
    {
        ScribblerHandle? h = handle;
        if (h is null || h.Closed) return;
        while (h.Events.Count >= QueueCap && h.Events.TryDequeue(out _)) { }
        h.Events.Enqueue(ev);
        lastEnqueued = ev;
        h.Signal.Release();                 // once per enqueue, always
    }

    /// <summary>View coordinates (DIPs) to buffer pixels through the
    /// inverse of the aspect-fit the paint applies. Outside the picture
    /// clamps to its edge — a drag that strays keeps its slider.</summary>
    (int X, int Y) ToBuffer(double vx, double vy)
    {
        int w = width, h = height;
        double vw = Width, vh = Height;
        if (w < 1 || h < 1 || vw <= 0 || vh <= 0) return (0, 0);
        double scale = Math.Min(vw / w, vh / h);
        double x0 = (vw - w * scale) / 2, y0 = (vh - h * scale) / 2;
        int bx = (int)((vx - x0) / scale);
        int by = (int)((vy - y0) / scale);
        return (Math.Clamp(bx, 0, w - 1), Math.Clamp(by, 0, h - 1));
    }

#if WINDOWS
    /// <summary>The same hidden-key-sink trick the terminal uses: the
    /// canvas is a Panel and panels do not reliably take focus, so a
    /// 1x1 transparent TextBox parked offscreen inside it owns the
    /// keyboard. KeyDown/KeyUp map VirtualKeys onto ScribblerKeys
    /// codes; TextChanged harvests Typed characters (layout and shift
    /// are the platform's, exactly as the desktop's char callback).</summary>
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (handle is null) return;
        if (Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.Panel panel) return;

        var box = new Microsoft.UI.Xaml.Controls.TextBox
        {
            Opacity = 0,
            Width = 1,
            Height = 1,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
        };
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(box, -200);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(box, -200);
        panel.Children.Add(box);

        box.KeyDown += (_, e) =>
        {
            int code = MapKey(e.Key);
            if (code == 0) return;
            Enqueue(new ScribblerEvent
            {
                Type = ScribblerEvent.Kind.KeyDown,
                Key = code, Mods = CurrentMods(), At = Ticker.Now,
            });
            e.Handled = true;
        };
        box.KeyUp += (_, e) =>
        {
            int code = MapKey(e.Key);
            if (code == 0) return;
            Enqueue(new ScribblerEvent
            {
                Type = ScribblerEvent.Kind.KeyUp,
                Key = code, Mods = CurrentMods(), At = Ticker.Now,
            });
            e.Handled = true;
        };
        box.TextChanged += (_, _) =>
        {
            string typed = box.Text;
            if (typed.Length == 0) return;
            box.Text = "";
            foreach (char ch in typed)
                Enqueue(new ScribblerEvent
                {
                    Type = ScribblerEvent.Kind.Typed,
                    KeyChar = ch.ToString(), Mods = CurrentMods(), At = Ticker.Now,
                });
        };
        box.Loaded += (_, _) => box.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);

        panel.PointerPressed += (_, e) =>
        {
            box.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            var p = e.GetCurrentPoint(panel);
            (int x, int y) = ToBuffer(p.Position.X, p.Position.Y);
            Enqueue(new ScribblerEvent
            {
                Type = ScribblerEvent.Kind.MouseDown,
                X = x, Y = y, Button = ButtonOf(p.Properties),
                Mods = CurrentMods(), At = Ticker.Now,
            });
            e.Handled = true;
        };
        panel.PointerMoved += (_, e) =>
        {
            var p = e.GetCurrentPoint(panel);
            (int x, int y) = ToBuffer(p.Position.X, p.Position.Y);
            EnqueueMove(x, y, CurrentMods());
        };
        panel.PointerReleased += (_, e) =>
        {
            var p = e.GetCurrentPoint(panel);
            (int x, int y) = ToBuffer(p.Position.X, p.Position.Y);
            Enqueue(new ScribblerEvent
            {
                Type = ScribblerEvent.Kind.MouseUp,
                X = x, Y = y, Button = 1,
                Mods = CurrentMods(), At = Ticker.Now,
            });
            e.Handled = true;
        };
    }

    static int ButtonOf(Microsoft.UI.Input.PointerPointProperties p) =>
        p.IsRightButtonPressed ? 2 : p.IsMiddleButtonPressed ? 3 : 1;

    static ScribblerEvent.Mod CurrentMods()
    {
        ScribblerEvent.Mod m = ScribblerEvent.Mod.None;
        if (IsDown(Windows.System.VirtualKey.Shift)) m |= ScribblerEvent.Mod.Shift;
        if (IsDown(Windows.System.VirtualKey.Control)) m |= ScribblerEvent.Mod.Ctrl;
        if (IsDown(Windows.System.VirtualKey.Menu)) m |= ScribblerEvent.Mod.Alt;
        if (IsDown(Windows.System.VirtualKey.LeftWindows)
            || IsDown(Windows.System.VirtualKey.RightWindows)) m |= ScribblerEvent.Mod.Super;
        return m;
    }

    static bool IsDown(Windows.System.VirtualKey k) =>
        (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    /// <summary>WinUI's VirtualKey mapped onto Shoddy's own stable
    /// ScribblerKeys codes — the platform enum never crosses the seam.
    /// Anything unmapped reports 0, exactly as at the desktop.</summary>
    static int MapKey(Windows.System.VirtualKey k)
    {
        int v = (int)k;
        if (v is >= 'A' and <= 'Z') return v;               // letters sit on ASCII
        if (v is >= '0' and <= '9') return v;               // top-row digits too
        if (k >= Windows.System.VirtualKey.NumberPad0
            && k <= Windows.System.VirtualKey.NumberPad9)
            return '0' + (k - Windows.System.VirtualKey.NumberPad0);
        if (k >= Windows.System.VirtualKey.F1 && k <= Windows.System.VirtualKey.F12)
            return ScribblerKeys.F1 + (k - Windows.System.VirtualKey.F1);
        return k switch
        {
            Windows.System.VirtualKey.Enter => ScribblerKeys.Enter,
            Windows.System.VirtualKey.Escape => ScribblerKeys.Escape,
            Windows.System.VirtualKey.Back => ScribblerKeys.Backspace,
            Windows.System.VirtualKey.Tab => ScribblerKeys.Tab,
            Windows.System.VirtualKey.Space => ScribblerKeys.Space,
            Windows.System.VirtualKey.Delete => ScribblerKeys.Delete,
            Windows.System.VirtualKey.Insert => ScribblerKeys.Insert,
            Windows.System.VirtualKey.Left => ScribblerKeys.Left,
            Windows.System.VirtualKey.Right => ScribblerKeys.Right,
            Windows.System.VirtualKey.Up => ScribblerKeys.Up,
            Windows.System.VirtualKey.Down => ScribblerKeys.Down,
            Windows.System.VirtualKey.Home => ScribblerKeys.Home,
            Windows.System.VirtualKey.End => ScribblerKeys.End,
            Windows.System.VirtualKey.PageUp => ScribblerKeys.PageUp,
            Windows.System.VirtualKey.PageDown => ScribblerKeys.PageDown,
            Windows.System.VirtualKey.LeftShift or Windows.System.VirtualKey.Shift
                => ScribblerKeys.LeftShift,
            Windows.System.VirtualKey.RightShift => ScribblerKeys.RightShift,
            Windows.System.VirtualKey.LeftControl or Windows.System.VirtualKey.Control
                => ScribblerKeys.LeftCtrl,
            Windows.System.VirtualKey.RightControl => ScribblerKeys.RightCtrl,
            Windows.System.VirtualKey.LeftMenu or Windows.System.VirtualKey.Menu
                => ScribblerKeys.LeftAlt,
            Windows.System.VirtualKey.RightMenu => ScribblerKeys.RightAlt,
            Windows.System.VirtualKey.LeftWindows => ScribblerKeys.LeftSuper,
            Windows.System.VirtualKey.RightWindows => ScribblerKeys.RightSuper,
            _ => 0,
        };
    }
#endif

    // ---- presentation ---------------------------------------------------

    void Paint(object? sender, SKPaintSurfaceEventArgs e)
    {
        SKCanvas canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Black);
        byte[]? px = pixels;
        int w = width, h = height;
        if (px is null || w < 1 || h < 1 || px.Length < w * h * 4) return;

        // Zero-copy: pin the runtime's buffer, wrap it as an SKBitmap
        // for exactly the duration of the draw, scale to fit.
        GCHandle pin = GCHandle.Alloc(px, GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using var bmp = new SKBitmap();
            if (!bmp.InstallPixels(info, pin.AddrOfPinnedObject(), info.RowBytes)) return;

            SKRect dest = FitRect(e.Info.Width, e.Info.Height, w, h);
            using var paint = new SKPaint();
            canvas.DrawBitmap(bmp, dest, paint);
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>Aspect-fit: the drawing keeps its shape whatever shape
    /// MAUI gives the view.</summary>
    static SKRect FitRect(float viewW, float viewH, int w, int h)
    {
        float scale = Math.Min(viewW / w, viewH / h);
        float dw = w * scale, dh = h * scale;
        float x = (viewW - dw) / 2, y = (viewH - dh) / 2;
        return new SKRect(x, y, x + dw, y + dh);
    }
}
