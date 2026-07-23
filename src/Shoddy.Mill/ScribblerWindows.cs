// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Runtime.ExceptionServices;
using Shoddy.Runtime;
using Silk.NET.GLFW;

namespace Shoddy.Mill;

/// <summary>The window registry and the main-thread loop that drives it.
/// GLFW requires its event loop — and on macOS, window creation itself —
/// on the process's main thread, so the Shoddy program runs on a
/// background thread while this loop alternates draining the dispatcher
/// queue and pumping windows. It exits once the program has finished and
/// no windows remain open. The registry is mutated only from the main
/// thread; pumping tolerates a window unregistering itself
/// mid-iteration.</summary>
public static class ScribblerWindows
{
    static readonly List<ScribblerWindow> windows = new();   // main thread only
    static Glfw? glfw;      // for WaitEventsTimeout/PostEmptyEvent; set with the first window

    public static void Register(ScribblerWindow w) => windows.Add(w);
    public static void Unregister(ScribblerWindow w) => windows.Remove(w);

    /// <summary>Run a Shoddy program with the window backend available:
    /// the program on a background thread, this (the main) thread serving
    /// window creation, pumping and frame timing. Returns the program's
    /// exit code once it has finished and every window is closed. A
    /// console program that never opens a scribbler costs one blocked
    /// wait here — no GLFW init, no polling.
    ///
    /// With <paramref name="linger"/> false, windows do not outlive the
    /// program: the moment it returns, any still open are torn down. The
    /// perch runs its serve loop this way — its "program" is the DAP
    /// session, and a window with no session behind it is an orphan.</summary>
    public static int Run(Func<int> program, bool linger = true)
    {
        var dispatcher = new MainThreadDispatcher();

        // Called from the Shoddy thread; blocks until the window exists —
        // SCRIBBLEROPEN returns a handle the program draws into on the
        // very next word, so a post-and-forget queue would be wrong here.
        ScribblerRegistry.CreateScribbler = (w, h) =>
        {
            ScribblerWindow? win = null;
            try
            {
                dispatcher.Invoke(() =>
                {
                    win = new ScribblerWindow(dispatcher, w, h);
                    if (glfw == null)       // first window: GLFW is now live
                    {
                        glfw = Glfw.GetApi();
                        dispatcher.Waker = () => glfw.PostEmptyEvent();
                    }
                });
            }
            catch (ShoddyError) { throw; }
            catch (Exception e)
            {
                throw new ShoddyError(0, $"ScribblerOpen: could not create a window: {e.Message}");
            }
            return win!.Handle;
        };

        // The buzzer serves both `mill run` and `mill dap` (this method is
        // both paths). Init is lazy — installing costs nothing, and sound
        // works under the debugger: the scribbler's dap restriction is
        // about the window loop and does not apply here.
        Buzzer.Install();

        // The exit code and completion flag cross a thread boundary: the
        // ManualResetEventSlim publishes both (and any exception).
        int exitCode = 0;
        Exception? failure = null;
        var done = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try { exitCode = program(); }
            catch (Exception e) { failure = e; }
            finally { done.Set(); dispatcher.Wake(); }
        })
        { IsBackground = true, Name = "shoddy-program" };
        thread.Start();

        while (true)
        {
            dispatcher.Drain();
            if (done.IsSet && (failure != null || !linger)) break;   // error or no-linger: tear down now
            if (windows.Count == 0)
            {
                if (done.IsSet) break;
                // No window: block on completion or posted work — never
                // poll. This is every `mill run` of a console program.
                dispatcher.WaitForWork();
                continue;
            }

            foreach (ScribblerWindow win in windows.ToArray())
                win.Pump();
            if (windows.Count == 0) continue;            // last window just closed

            // Sleep inside the GLFW pump until input arrives, a wake is
            // posted (glfwPostEmptyEvent), or the nearest interval tick
            // falls due — the frame timer is this check, not a separate
            // timer thread, and nothing spins while idle.
            double now = Ticker.Now, timeout = 3600;
            foreach (ScribblerWindow win in windows)
                if (win.NextDeadline is double at)
                    timeout = Math.Min(timeout, Math.Max(0, (at - now) / 1000.0));
            if (timeout > 0) glfw!.WaitEventsTimeout(timeout);
        }

        // A program error while windows are up: tear them down rather than
        // sit on an undelivered error message, then rethrow so the mill's
        // ShoddyError handler prints it as usual.
        foreach (ScribblerWindow win in windows.ToArray())
            win.ForceClose();
        dispatcher.Drain();
        ScribblerRegistry.CreateScribbler = null;
        // Held notes stop now (they would never end); queued notes finish
        // unless the program failed — "queue a tune, return from Main" is a
        // complete program, the audio sibling of "draw a picture, return".
        Buzzer.Shutdown(drain: failure == null);
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        return exitCode;
    }
}
