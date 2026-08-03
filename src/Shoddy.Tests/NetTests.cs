// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Net.Sockets;
using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// The TCP/IP builtins on the loopback path. The whole round trip runs in
/// one single-threaded Shoddy program because the readiness ops never
/// block: after a synchronous Connect the listener's Accept finds the
/// pending client at once, so listen / connect / accept / send / recv /
/// echo interleave on one thread with no second party to schedule. The
/// port is handed in as a program argument so the test owns an ephemeral
/// one and never collides. Both tests live in one class so the process-
/// global --allow-net switch is set and cleared without racing itself
/// (xUnit runs a class's tests sequentially).
/// </summary>
public class NetTests
{
    [Fact]
    public void LoopbackEchoRoundTrip()
    {
        string src =
            "Include \"net.shoddy\"\n" +
            "\n" +
            "Def Main()\n" +
            "    Let port = Val(First(Args()))\n" +
            "    Let srv = Listen(port)\n" +
            "    Let cli = Connect(\"127.0.0.1\", port)\n" +
            "    Let conn = WaitAccept(srv)\n" +
            "    Send(cli, \"PING\")\n" +
            "    Let got = WaitRecv(conn)\n" +
            "    Send(conn, \"PONG:\" & got)\n" +
            "    Let reply = WaitRecv(cli)\n" +
            "    Print(reply)\n" +
            "    Close(cli)\n" +
            "    Close(conn)\n" +
            "    Close(srv)\n";

        string output = Run(src, allowNet: true, out int exit, port: FreePort());
        Assert.True(exit == 0, $"expected exit 0, got {exit}");
        Assert.Equal("PONG:PING\n", output);
    }

    [Fact]
    public void RefusedWithoutAllowNet()
    {
        // Same first move, but the mill was not armed: the first TCP word
        // must raise instead of touching the network. Execute catches the
        // ShoddyError inside the woven Run and returns exit 1.
        string src =
            "Include \"net.shoddy\"\n" +
            "\n" +
            "Def Main()\n" +
            "    Let srv = Listen(Val(First(Args())))\n" +
            "    Print(\"unreachable\")\n";

        string output = Run(src, allowNet: false, out int exit, port: FreePort());
        Assert.True(exit == 1, $"expected exit 1 (network disabled), got {exit}");
        Assert.Equal("", output);
    }

    // ---- TLS -----------------------------------------------------------
    // These cannot use the single-threaded trick above. A TLS handshake is
    // a multi-round-trip conversation, so AuthenticateAsClient blocks until
    // something pumps the other side: every test here stands up a real
    // server on a background task, with a certificate minted in-process.
    // No files, no certificate store, nothing machine-global, and no test
    // touches the internet.

    [Fact]
    public void TlsLoopbackEchoRoundTrip()
    {
        int port = FreePort();
        using var server = TlsEchoServer(port);
        string src =
            "Include \"net.shoddy\"\n" +
            "\n" +
            "Def Main()\n" +
            "    Let port = Val(First(Args()))\n" +
            "    Let s = TCPConnect(\"127.0.0.1\", port)\n" +
            "    TCPSecure(s, \"localhost\")\n" +
            // A byte above 127 proves Latin-1 survived the TLS stream: it
            // is the transport that changed, never the representation.
            "    TCPSend(s, \"PING\" & Chr(200))\n" +
            "    Print(TCPRecv(s, 4096))\n" +
            "    TCPClose(s)\n";

        string output = Run(src, allowNet: true, out int exit, port: port, tlsInsecure: true);
        Assert.True(exit == 0, $"expected exit 0, got {exit}: {output}");
        Assert.Equal("ECHO:PING" + (char)200 + "\n", output);
    }

    [Fact]
    public void TlsRecvReturnsEmptyAtEofAndTcpEofReportsIt()
    {
        int port = FreePort();
        using var server = TlsEchoServer(port);
        string src =
            "Include \"net.shoddy\"\n" +
            "\n" +
            "Def Main()\n" +
            "    Let s = TCPConnect(\"127.0.0.1\", Val(First(Args())))\n" +
            "    TCPSecure(s, \"localhost\")\n" +
            "    TCPSend(s, \"BYE\")\n" +
            "    Print(TCPRecv(s, 4096))\n" +      // the echo
            "    Print(TCPRecv(s, 4096) = \"\")\n" +   // now at EOF
            "    Print(TCPEof(s))\n" +
            "    TCPClose(s)\n";

        string output = Run(src, allowNet: true, out int exit, port: port, tlsInsecure: true);
        Assert.True(exit == 0, $"expected exit 0, got {exit}: {output}");
        Assert.Contains("True\nTrue", output.Replace("\r\n", "\n"));
    }

    [Fact]
    public void TcpPollOnASecuredHandleIsRefused()
    {
        int port = FreePort();
        using var server = TlsEchoServer(port);
        string src =
            "Include \"net.shoddy\"\n" +
            "\n" +
            "Def Main()\n" +
            "    Let s = TCPConnect(\"127.0.0.1\", Val(First(Args())))\n" +
            "    TCPSecure(s, \"localhost\")\n" +
            "    Print(TCPPoll(s))\n";

        Run(src, allowNet: true, out int exit, port: port, tlsInsecure: true);
        Assert.True(exit == 1, $"expected exit 1 (poll refused), got {exit}");
    }

    [Fact]
    public void TcpSecureOnAListenerIsRefused()
    {
        string src =
            "Include \"net.shoddy\"\n" +
            "\n" +
            "Def Main()\n" +
            "    Let srv = TCPListen(\"127.0.0.1\", Val(First(Args())))\n" +
            "    TCPSecure(srv, \"localhost\")\n";

        Run(src, allowNet: true, out int exit, port: FreePort());
        Assert.True(exit == 1, $"expected exit 1 (listener refused), got {exit}");
    }

    [Fact]
    public void TcpSecureWithoutAllowNetIsRefused()
    {
        string src =
            "Include \"net.shoddy\"\n" +
            "\n" +
            "Def Main()\n" +
            "    TCPSecure(1, \"localhost\")\n";

        Run(src, allowNet: false, out int exit, port: FreePort());
        Assert.True(exit == 1, $"expected exit 1 (network disabled), got {exit}");
    }

    [Fact]
    public void TcpSecureAgainstAPlainServerFailsTheHandshake()
    {
        // A server that accepts and closes speaks no TLS, so the handshake
        // cannot complete. The handle must die with it.
        int port = FreePort();
        using var server = PlainCloseServer(port);
        string src =
            "Include \"net.shoddy\"\n" +
            "\n" +
            "Def Main()\n" +
            "    Let s = TCPConnect(\"127.0.0.1\", Val(First(Args())))\n" +
            "    TCPSecure(s, \"localhost\")\n";

        Run(src, allowNet: true, out int exit, port: port, tlsInsecure: true);
        Assert.True(exit == 1, $"expected exit 1 (handshake failed), got {exit}");
    }

    /// <summary>A server that accepts and immediately closes. It speaks no
    /// TLS at all, which is what makes the client's handshake fail.</summary>
    static IDisposable PlainCloseServer(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var task = Task.Run(() =>
        {
            try { using Socket s = listener.AcceptSocket(); }
            catch (Exception) { }
        });
        return new ServerHandle(listener, task, null, null);
    }

    [Fact]
    public void MachineWordsAgreeWithTheBuiltinsAboutBlocking()
    {
        // The machine layer, not the raw builtins: ConnectTls secures with
        // the same host it connected to, and RecvAllTls drains a blocking
        // socket without ever reaching for TCPPoll — which would abort.
        int port = FreePort();
        using var server = TlsHttpServer(port);
        string src =
            "Include \"https.shoddy\"\n" +
            "\n" +
            "Def Main()\n" +
            "    Let s = ConnectTls(\"localhost\", Val(First(Args())))\n" +
            "    Send(s, HttpRequestText(\"GET\", \"/hi\", \"localhost\", { Pair(\"User-Agent\", \"mill\") }))\n" +
            "    Let reply = RecvAllTls(s)\n" +
            "    Close(s)\n" +
            "    Print(HttpStatus(reply))\n" +
            "    Print(HttpHeader(reply, \"Content-Type\", \"?\"))\n" +
            "    Print(HttpBody(reply))\n";

        string output = Run(src, allowNet: true, out int exit, port: port, tlsInsecure: true);
        Assert.True(exit == 0, $"expected exit 0, got {exit}: {output}");
        string[] lines = output.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        Assert.Equal("200", lines[0]);
        Assert.Equal("text/plain", lines[1]);
        // The server echoes the request line back, proving HttpRequestText's
        // bytes crossed the TLS stream and arrived as written.
        Assert.Equal("you asked for /hi", lines[2]);
    }

    /// <summary>A one-shot TLS server that speaks just enough HTTP: read a
    /// request, answer HTTP/1.0 with a Content-Type and a body naming the
    /// path, then close so read-to-EOF terminates.</summary>
    static IDisposable TlsHttpServer(int port) => TlsServer(port, req =>
    {
        string path = "?";
        string first = req.Split('\n')[0].TrimEnd('\r');
        string[] bits = first.Split(' ');
        if (bits.Length > 1) path = bits[1];
        return "HTTP/1.0 200 OK\r\nContent-Type: text/plain\r\n\r\nyou asked for " + path;
    });

    /// <summary>A one-shot TLS echo server on the loopback: accept, do the
    /// server handshake with a self-signed certificate minted here, echo one
    /// message back with an ECHO: prefix, then close so the client sees a
    /// clean EOF. Disposing it stops the listener.</summary>
    static IDisposable TlsEchoServer(int port) =>
        TlsServer(port, req => "ECHO:" + req);

    /// <summary>The shared TLS server: accept one connection, handshake as
    /// server with a certificate minted here, read one message, hand it to
    /// <paramref name="answer"/>, write the reply and close.</summary>
    static IDisposable TlsServer(int port, Func<string, string> answer)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        X509Certificate2 cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // On Windows an SslStream server needs a certificate with an
        // exportable private key attached, which round-tripping through
        // PFX guarantees.
        X509Certificate2 usable = X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx, "x"), "x");

        var task = Task.Run(() =>
        {
            try
            {
                using Socket s = listener.AcceptSocket();
                using var ssl = new SslStream(new NetworkStream(s, ownsSocket: false), false);
                ssl.AuthenticateAsServer(usable, false, checkCertificateRevocation: false);
                var buf = new byte[4096];
                int n = ssl.Read(buf, 0, buf.Length);
                byte[] reply = System.Text.Encoding.Latin1.GetBytes(
                    answer(System.Text.Encoding.Latin1.GetString(buf, 0, n)));
                ssl.Write(reply, 0, reply.Length);
                ssl.Flush();
            }
            catch (Exception) { /* the client's test asserts the outcome */ }
        });
        return new ServerHandle(listener, task, cert, usable);
    }

    /// <summary>Stops the listener and joins the pump. The wait lives here
    /// rather than in a test body so the blocking join is not inside a test
    /// method, which the xUnit analyzer rightly objects to.</summary>
    sealed class ServerHandle(TcpListener l, Task t, X509Certificate2? a, X509Certificate2? b) : IDisposable
    {
        public void Dispose()
        {
            try { l.Stop(); } catch (Exception) { }
            t.Wait(5000);
            a?.Dispose();
            b?.Dispose();
        }
    }

    // ---- helpers -------------------------------------------------------

    static string Run(string src, bool allowNet, out int exit, int port,
                      bool tlsInsecure = false)
    {
        string ws = Path.Combine(Path.GetTempPath(), "shoddy-net", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(ws, "machines"));
        // net.shoddy lives beside the program so its Include resolves by source.
        foreach (string f in Directory.GetFiles(
                     Path.Combine(RepoRoot.Dir, "machines"), "*.shoddy"))
            File.Copy(f, Path.Combine(ws, "machines", Path.GetFileName(f)));
        string prog = Path.Combine(ws, "machines", "prog.shoddy");
        File.WriteAllText(prog, src);

        var machines = new MachineSet();
        var lines = Lexer.ReadProgram(prog, machines.TryResolve);
        var program = new ShoddyProgram();
        machines.SeedInto(program);
        Parser.Parse(lines, program);

        var output = new StringWriter();
        string? prior = Environment.GetEnvironmentVariable("SHODDY_ALLOW_NET");
        // Both switches are read at Engine construction, so they are set
        // before Run and restored in finally. Same discipline, same reason.
        string? priorTls = Environment.GetEnvironmentVariable("SHODDY_TLS_INSECURE");
        Environment.SetEnvironmentVariable("SHODDY_ALLOW_NET", allowNet ? "1" : null);
        Environment.SetEnvironmentVariable("SHODDY_TLS_INSECURE", tlsInsecure ? "1" : null);
        try
        {
            exit = Weaver.Execute(program, machines.Machines, output,
                                  TextReader.Null, new[] { port.ToString() });
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHODDY_ALLOW_NET", prior);
            Environment.SetEnvironmentVariable("SHODDY_TLS_INSECURE", priorTls);
        }
        return output.ToString();
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
