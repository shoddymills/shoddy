// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Shoddy.Hosting;
using Shoddy.Maui;
using Xunit;

namespace Shoddy.Maui.Tests;

/// <summary>
/// B4.11's two teeth, and C2.4a's, at app level:
///
/// The network never blocks the UI — a NETGET against a peer that
/// accepts and never replies leaves the caller free (SubmitAsync is a
/// task, not a block) and cancellable (Abandon walks away and the next
/// turn answers on a fresh engine from the last committed state).
///
/// The fail-fast — this project's build granted `file` to halifax, so
/// a Load that supplies no root dies at startup naming the mill, never
/// by writing beside the executable.
/// </summary>
public class NetworkTests
{
    [Fact]
    public async Task ANeverReplyingPeerLeavesTheSessionResponsiveAndCancellable()
    {
        // A peer that accepts and never sends: the socket read blocks
        // by design (a TLS read especially — B4.11), which is exactly
        // the case the busy state and cancel exist for.
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        int port = ((IPEndPoint)silent.LocalEndpoint).Port;
        _ = silent.AcceptTcpClientAsync();   // accept, then say nothing, forever

        string root = Path.Combine(Path.GetTempPath(), "shoddy-maui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var session = HalifaxSession.Open(new ShoddyHostOptions { FileRoot = root, AllowNet = true });
        _ = session.Opening();

        try
        {
            // A saved word and an unsaved one: cancel restarts the
            // session, so the difference between them is the whole
            // story the transcript has to tell.
            Assert.NotNull(await session.SubmitAsync(": DOZEN 12 * ;"));
            Assert.NotNull(await session.SubmitAsync("SAVE \"halifaxrc\""));
            Assert.NotNull(await session.SubmitAsync(": UNSAVED 9 * ;"));

            Task<HalifaxTurn?> stuck = session.SubmitAsync($"\"http://127.0.0.1:{port}/\" NETGET");

            // Responsive: the caller has its thread back immediately;
            // the turn is genuinely parked on the socket.
            Task done = await Task.WhenAny(stuck, Task.Delay(1500));
            Assert.NotSame(stuck, done);

            // Cancellable: walk away. The session relaunches — its
            // opening report says halifaxrc came back.
            IReadOnlyList<string> reopened = session.Abandon();
            Assert.Contains(reopened, l => l.Contains("halifaxrc") && l.Contains("loaded"));

            HalifaxTurn? next = await session.SubmitAsync("2 3 +");
            Assert.NotNull(next);
            Assert.Contains(next!.Shown, l => l.Contains("5"));

            // The saved word survived the relaunch; the unsaved one is
            // gone — QUIT-and-relaunch semantics, honestly reported.
            HalifaxTurn? saved = await session.SubmitAsync("3 DOZEN");
            Assert.NotNull(saved);
            Assert.Contains(saved!.Shown, l => l.Contains("36"));
            HalifaxTurn? lost = await session.SubmitAsync("2 UNSAVED");
            Assert.NotNull(lost);
            Assert.Contains(lost!.Shown, l => l.StartsWith("?"));

            // The abandoned task resolves to null (stale generation)
            // if the OS ever releases it; it must never throw into
            // nowhere. Not awaited further — that is the point.
            Assert.False(stuck.IsFaulted);
        }
        finally
        {
            silent.Stop();
        }
    }

    [Fact]
    public void AGrantedFileCapabilityWithNoRootFailsFastNamingTheMill()
    {
        // The generated wiring in THIS test project's build registered
        // halifax's `file` grant (C2.4a): Load with no FileRoot must
        // die at startup, by name, with the fix in the message.
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() =>
            ShoddyHost.Load(Assembly.Load("Shoddy.Machines.Halifax-core")));
        Assert.Contains("halifax", e.Message);
        Assert.Contains("FileRoot", e.Message);
    }
}
