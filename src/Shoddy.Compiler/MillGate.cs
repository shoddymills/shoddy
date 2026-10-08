// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Security.Cryptography;
using System.Text;
using Shoddy.Runtime;

namespace Shoddy.Compiler;

/// <summary>
/// Cross-process serialization for the verbs that write build artifacts.
///
/// ShoddyWeave runs the mill once per referencing project, and parallel
/// MSBuild runs those projects together — so two mill processes can reach
/// File.Create on the same output at the same moment, and whichever loses
/// dies with the file in use. It happened twice on one release day, in
/// two different lanes: an app and its tests both driving `manifest`
/// against one mill, and a server and its tests both weaving one core.
///
/// A named mutex per normalized target path makes the second writer wait
/// instead. Distinct targets never contend, a waiter that inherits the
/// lock from a crashed holder proceeds (the weave is deterministic, so
/// re-writing is always safe), and named mutexes are cross-process on
/// every OS .NET runs on.
///
/// The wait is bounded. A holder that crashes releases the lock, but one
/// that is alive and stuck never does, and an unbounded wait turned that
/// one process into every later build hanging with no message. Ten
/// minutes is far beyond the longest legitimate weave.
/// </summary>
static class MillGate
{
    static readonly TimeSpan Patience = TimeSpan.FromMinutes(10);

    /// <summary>Hold the gate for <paramref name="path"/> until the
    /// returned handle is disposed.</summary>
    public static IDisposable Hold(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { full = path; }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
        var gate = new Mutex(false, "ShoddyMill-" + Convert.ToHexString(hash, 0, 16));
        try
        {
            if (!gate.WaitOne(Patience))
            {
                gate.Dispose();
                throw new ShoddyError(0,
                    $"mill: waited {Patience.TotalMinutes:0} minutes for another mill process working on " +
                    $"{full}; it has not finished. Look for a stuck mill process and end it, then build again.");
            }
        }
        catch (AbandonedMutexException) { /* prior holder died; the lock is ours */ }
        return new Held(gate);
    }

    sealed class Held(Mutex gate) : IDisposable
    {
        public void Dispose() { gate.ReleaseMutex(); gate.Dispose(); }
    }
}
