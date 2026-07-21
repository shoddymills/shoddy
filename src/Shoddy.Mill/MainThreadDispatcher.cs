// Copyright (c) 2026 Stephen Vincent Foster. All rights reserved.
//
// This file is part of the Shoddy Language project.
// Licensed under the Shoddy Language License 1.0.0 (PolyForm Noncommercial
// License 1.0.0 with Additional Use Grant). See the LICENSE file in the
// project root for full terms.

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Shoddy.Mill;

/// <summary>The one bit of framework glue the scribbler design needs —
/// what a UI toolkit's Dispatcher.UIThread gives for free. The Shoddy
/// program runs on a background thread; GLFW requires windows, GL and
/// event pumping on the process's main thread; this queue is the bridge.
/// Post/Invoke from any thread; Drain/WaitForWork on the main thread
/// only.</summary>
public sealed class MainThreadDispatcher
{
    readonly ConcurrentQueue<Action> queue = new();
    readonly SemaphoreSlim work = new(0);       // released once per wake

    /// <summary>Extra wake hook for when the main thread is blocked in
    /// glfwWaitEventsTimeout rather than on the semaphore — set (on the
    /// main thread) to glfwPostEmptyEvent once the first window exists.
    /// Both wakes always fire; whichever wait is active hears its own.</summary>
    public Action? Waker;

    public void Post(Action a)
    {
        queue.Enqueue(a);
        Wake();
    }

    /// <summary>Wake the main thread without queueing work — used for
    /// program completion and dirty-blit notification.</summary>
    public void Wake()
    {
        work.Release();
        Waker?.Invoke();
    }

    /// <summary>Blocking marshal: run <paramref name="a"/> on the main
    /// thread and wait for it. The ManualResetEventSlim publishes both
    /// completion and any captured exception across the thread boundary;
    /// the exception is rethrown here on the calling thread.</summary>
    public void Invoke(Action a)
    {
        using var done = new ManualResetEventSlim(false);
        Exception? failed = null;
        Post(() =>
        {
            try { a(); }
            catch (Exception e) { failed = e; }
            finally { done.Set(); }
        });
        done.Wait();
        if (failed != null) ExceptionDispatchInfo.Capture(failed).Throw();
    }

    /// <summary>Main thread: run everything queued so far.</summary>
    public void Drain()
    {
        while (queue.TryDequeue(out Action? a)) a();
    }

    /// <summary>Main thread: block at zero CPU until something wakes it.
    /// Stale wake counts (work consumed by an earlier Drain) cause a
    /// spurious return, never a missed wake — callers loop.</summary>
    public void WaitForWork() => work.Wait();
}
