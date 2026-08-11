// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Shoddy.Compiler;

/// <summary>
/// Cross-process serialization for the verbs that write build artifacts.
///
/// ShoddyWeave runs the mill once per referencing project, and parallel
/// MSBuild runs those projects together — so two mill processes can reach
/// File.Create on the same output at the same moment, and whichever loses
/// dies with the file in use. It happened twice on one release day: the
/// MAUI app and its tests both driving `manifest mills/halifax`, and the
/// sparky server and its tests both weaving the sparky core.
///
/// A named mutex per normalized target path makes the second writer wait
/// instead. Distinct targets never contend, a waiter that inherits the
/// lock from a crashed holder proceeds (the weave is deterministic, so
/// re-writing is always safe), and named mutexes are cross-process on
/// every OS .NET runs on.
/// </summary>
static class MillGate
{
    /// <summary>Hold the gate for <paramref name="path"/> until the
    /// returned handle is disposed.</summary>
    public static IDisposable Hold(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { full = path; }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
        var gate = new Mutex(false, "ShoddyMill-" + Convert.ToHexString(hash, 0, 16));
        try { gate.WaitOne(); }
        catch (AbandonedMutexException) { /* prior holder died; the lock is ours */ }
        return new Held(gate);
    }

    sealed class Held(Mutex gate) : IDisposable
    {
        public void Dispose() { gate.ReleaseMutex(); gate.Dispose(); }
    }
}
