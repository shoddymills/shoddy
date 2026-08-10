// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Runtime;

namespace Shoddy.Maui;

/// <summary>
/// The MAUI scribbler backend (D10), calculator scope: presentation and
/// close, nothing else. The calculator's dictionary DRAWS but does not
/// POLL (B4.9) — no seeded word reads events, so no pointer, key or
/// timer plumbing exists here. All of that is a games requirement
/// (B5.5) and arrives with the shelf.
///
/// SCRIBBLEROPEN runs on the Shoddy thread and blocks until the surface
/// exists; the app's presenter runs on the UI thread and decides where
/// the canvas lives (a page, a pane — B1.4b). The two meet in Install's
/// synchronous main-thread hop, which is safe precisely because the
/// core never runs on the UI thread (D9).
/// </summary>
public static class MauiScribblerBackend
{
    /// <summary>Install the backend. <paramref name="present"/> is
    /// called on the UI thread with each newly opened canvas view and
    /// its handle — put the view on screen and return. <paramref
    /// name="dismiss"/> is called on the UI thread when the mill closes
    /// the scribbler — take the view off screen.</summary>
    public static void Install(
        Action<ScribblerCanvasView, ScribblerHandle> present,
        Action<ScribblerCanvasView> dismiss)
    {
        ScribblerRegistry.CreateScribbler = (w, h) =>
        {
            var handle = new ScribblerHandle
            {
                Width = w,
                Height = h,
                Pixels = new byte[w * h * 4],
            };

            ScribblerCanvasView view =
                MainThread.InvokeOnMainThreadAsync(() =>
                {
                    var v = new ScribblerCanvasView();
                    // The games' event plumbing (B5.5), wired before the
                    // view joins the tree. A drawing nobody polls just
                    // fills a bounded queue nobody reads — harmless.
                    v.Attach(handle);
                    present(v, handle);
                    return v;
                }).GetAwaiter().GetResult();

            handle.OnBlit = view.Present;
            handle.OnSetTitle = _ => { };          // the page owns its chrome (B1.4b)
            handle.OnClose = () =>
            {
                // SCRIBBLERCLOSE from the mill: MarkClosed arbitrates
                // with any user-initiated dismissal, the winner does the
                // bookkeeping, and the view leaves the screen.
                if (handle.MarkClosed())
                {
                    ScribblerRegistry.NoteClosed();
                    handle.Signal.Release();
                }
                MainThread.BeginInvokeOnMainThread(() => dismiss(view));
            };
            return handle;
        };
    }

    /// <summary>The app-side close — the user dismissed the page while
    /// the mill still holds the scribbler. A Quit event goes first so a
    /// Wait loop reaches its exit branch (exactly the desktop window's
    /// close), then the same arbitration; a later SCRIBBLERCLOSE finds
    /// it already closed and does nothing twice.</summary>
    public static void NoteDismissed(ScribblerHandle handle)
    {
        handle.Events.Enqueue(new ScribblerEvent
        {
            Type = ScribblerEvent.Kind.Quit,
            At = Ticker.Now,
        });
        handle.Signal.Release();
        if (handle.MarkClosed())
        {
            ScribblerRegistry.NoteClosed();
            handle.Signal.Release();
        }
    }
}
