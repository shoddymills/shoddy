// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Net;
using System.Net.Sockets;

namespace Shoddy.Perch;

/// <summary>
/// The host-side attach hook (A6.3, C6.2): a TCP listener that hands
/// accepted connections to a <see cref="PerchServer"/>. Shoddy.Build
/// compiles a one-line arming call into debug configurations of a
/// consuming project — release builds carry neither the call nor this
/// assembly, and B7.4's check asserts the absence.
///
/// Off by default even in debug builds: the listener starts only when
/// SHODDY_PERCH names a port, which is how the compound launch
/// configuration turns it on for exactly the session being debugged. A
/// debug build run outside the debugger listens on nothing.
/// </summary>
public static class PerchService
{
    static TcpListener? listener;

    /// <summary>Called by generated wiring at module load. Reads
    /// SHODDY_PERCH; silently does nothing when it is unset or not a
    /// port number — a debug build must run normally outside a debug
    /// session.</summary>
    public static void StartFromEnvironment()
    {
        if (listener != null) return;
        if (!int.TryParse(Environment.GetEnvironmentVariable("SHODDY_PERCH"), out int port))
            return;
        Start(port);
    }

    /// <summary>Serve DAP attach on the loopback at <paramref name="port"/>.
    /// One debuggee per process — the sink seam is process-global — so
    /// connections are served one at a time; a second client waits for
    /// the first to disconnect.</summary>
    public static void Start(int port)
    {
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Task.Run(() =>
        {
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (SocketException) { return; }     // listener stopped
                try
                {
                    using (client)
                    {
                        NetworkStream s = client.GetStream();
                        new PerchServer(s, s).Serve();
                    }
                }
                catch { /* a dropped client must not kill the host */ }
            }
        });
    }
}
