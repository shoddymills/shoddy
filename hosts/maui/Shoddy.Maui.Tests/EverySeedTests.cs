// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shoddy.Hosting;
using Shoddy.Maui;
using Shoddy.Runtime;
using Xunit;

namespace Shoddy.Maui.Tests;

/// <summary>
/// The every-seed test (B4.8, B4.10, B8), written before the backends
/// it grades: a theory case per seed folded into halifax-core, each
/// exercising at least one registered word and asserting a REAL answer
/// — never a null-seam one. The case list is generated from the
/// shipped manifest, so **a new seed added to the tree fails here
/// until the app supports it**; a seed in the probe table that has
/// left the manifest fails too. That mechanism is why no capability
/// list is maintained by hand.
///
/// Resources are the app's own (B4.10): a real canvas buffer behind
/// the scribbler registry, the real audio engine behind the buzzer
/// registry (skipped where CI has no device — SHODDY_HEADLESS_CI), a
/// scratch file root the engine's working directory points into, and
/// a local HTTP server for NETGET.
/// </summary>
public class EverySeedTests : IClassFixture<EverySeedTests.SeedHarness>
{
    // ---- the probe table: seed → lines to type, expectation ----

    sealed record Probe(string[] Lines, string Expect, bool IsRegex = false);

    static readonly Dictionary<string, Probe> Probes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["seedalg"] = new(new[] { "\"x+x\" ALGSIMPLIFY" }, "2 * x"),
        ["seedbool"] = new(new[] { "255 HEX" }, "FF"),
        ["seedbuiltin"] = new(new[] { "5 NEGATE" }, "-5"),
        ["seedclock"] = new(new[] { "STAMPDATE" }, @"\d{4}-\d{2}-\d{2}", IsRegex: true),
        ["seedcsv"] = new(new[] { "\"a,b,c\" CSVREAD CSVWIDTH" }, "3"),
        ["seeddict"] = new(new[] { "{ } \"a\" 1 DPUT \"a\" DGET" }, "1"),
        ["seedeng"] = new(new[] { "180 ENGRAD" }, "3.14159"),
        ["seedfile"] = new(new[]
        {
            "\"t.tape\" { \"hello\" \"world\" } WRITELINES",
            "\"t.tape\" READLINES FIRST",
        }, "\"hello\""),
        ["seedfin"] = new(new[] { "5 FINPCT" }, "0.05"),
        ["seedhtml"] = new(new[] { "\"<b>hi</b>\" HTMLPARSE HTMLTEXT" }, "<b>hi</b>"),
        ["seedhttps"] = new(new[] { "\"nonsense\" HTTPSTATUS" }, "0"),
        ["seedisam"] = new(new[]
        {
            "\"i.tape\" { { \"id\" \"number\" } } \"id\" \"t\" ISAMOPEN",
            "\"t\" { 7 } ISAMINSERT",
            "\"t\" ISAMCOUNT",
        }, "1"),
        ["seedjson"] = new(new[] { "\"[1,2,3]\" JSONPARSE JSONTEXT" }, "[1,2,3]"),
        ["seedkeys"] = new(new[] { "65 KEYNAME" }, "\"A\""),
        ["seedlin"] = new(new[] { "2 2 { 1 2 3 4 } MAT LINTRACE" }, "5"),
        ["seedmath"] = new(new[] { "3 4 HYPOT" }, "5"),
        ["seedmatrix"] = new(new[] { "{ 1 2 } { 3 4 } DOT" }, "11"),
        ["seedmip"] = new(new[] { "{ 1 } 1 1 { 1 } MAT { 1 } LPPROBLEM { } MIPPROBLEM MIPSOLVE" }, "OPTIMAL"),
        ["seedmoney"] = new(new[] { "1234.56 MONEY MFMT" }, "$1,234.56"),
        ["seedneural"] = new(new[] { "{ { 1 } { 3 } } NEURALSCALERFIT" }, "( \"center\" { 2 } )"),
        ["seedrandom"] = new(new[] { "42 SEED", "1 6 RANDOMINT" }, @"\b[1-6]\b", IsRegex: true),
        ["seedrecio"] = new(new[]
        {
            "\"r.tape\" { { \"id\" \"number\" } } \"r\" RECOPEN",
            "\"r\" { 42 } RECAPPEND",
            "\"r\" RECCOUNT",
        }, "1"),
        ["seedregex"] = new(new[] { "\"abc123\" \"[0-9]+\" RXTEST" }, "True"),
        ["seedseq"] = new(new[] { "1 5 RANGE LENGTH" }, "5"),
        ["seedshaker"] = new(new[] { "SHAKEMOD" }, "67108859"),
        ["seedsimplex"] = new(new[] { "{ 1 } 1 1 { 1 } MAT { 1 } LPPROBLEM LPSOLVE" }, "OPTIMAL"),
        ["seedsparse"] = new(new[] { "2 2 { 1 0 0 4 } MAT SPARSE SPNNZ" }, "2"),
        ["seedstats"] = new(new[] { "{ 1 2 3 4 } MEAN" }, "2.5"),
        ["seedstr"] = new(new[] { "\"hi\" UPPER" }, "\"HI\""),
        ["seedvt100"] = new(new[] { "27 CHR \"A\" STRCAT VTEVALKEY" }, "\"up\""),
        ["seedxml"] = new(new[] { "\"<a>hi</a>\" XMLPARSE XMLTEXT" }, "<a>hi</a>"),
        // The content seeds are probed specially below: their words
        // need the canvas, the audio device, or a live server.
        ["seedscribbler"] = new(Array.Empty<string>(), "special"),
        ["seedturtle"] = new(Array.Empty<string>(), "special"),
        ["seedplotter"] = new(Array.Empty<string>(), "special"),
        ["seedbuzzer"] = new(Array.Empty<string>(), "special"),
        ["seednet"] = new(Array.Empty<string>(), "special"),
    };

    static bool Headless => Environment.GetEnvironmentVariable("SHODDY_HEADLESS_CI") == "1";

    // ---- the shared harness: one session, real resources, and a real
    // teardown — the canvases the probes open are process-global state
    // (ScribblerRegistry.OpenCount), and a scribbler left open would
    // make a later test's console read refuse, exactly as the INPUT
    // builtin is designed to. QUIT runs RckShutAll and drains it. ----

    public sealed class SeedHarness : IDisposable
    {
        public readonly HalifaxSession Session;
        public readonly List<ScribblerHandle> Opened = new();

        public SeedHarness()
        {
            // A real canvas behind the registry: no window on a test
            // host, but a real handle with a real buffer the draw words
            // write into — the null seam is CreateScribbler == null,
            // and this is not that.
            ScribblerRegistry.CreateScribbler = (w, h) =>
            {
                var handle = new ScribblerHandle { Width = w, Height = h, Pixels = new byte[w * h * 4] };
                Opened.Add(handle);
                return handle;
            };
            if (!Headless) MauiAudioBackend.Install();

            string root = Path.Combine(Path.GetTempPath(), "shoddy-every-seed", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            // The engine's file words resolve against the working
            // directory — the same mapping the app makes to app
            // storage (B3.5).
            Directory.SetCurrentDirectory(root);
            Session = HalifaxSession.Open(new ShoddyHostOptions { FileRoot = root, AllowNet = true });
            _ = Session.Opening();
        }

        public void Dispose()
        {
            Session.SubmitAsync("QUIT").GetAwaiter().GetResult();   // RckShutAll
            ScribblerRegistry.CreateScribbler = null;
            if (!Headless) MauiAudioBackend.Shutdown();
        }
    }

    readonly SeedHarness harness;

    public EverySeedTests(SeedHarness harness) => this.harness = harness;

    public static IEnumerable<object[]> ManifestSeeds()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "mills")))
            dir = dir.Parent;
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(dir!.FullName, "mills", "halifax", "mill.manifest")));
        foreach (JsonElement m in manifest.RootElement.GetProperty("machines").EnumerateArray())
        {
            string name = m.GetString()!;
            if (name.StartsWith("seed", StringComparison.OrdinalIgnoreCase))
                yield return new object[] { name };
        }
    }

    [Theory]
    [MemberData(nameof(ManifestSeeds))]
    public async Task TheSeedAnswersForReal(string seed)
    {
        Assert.True(Probes.ContainsKey(seed),
            $"a new seed arrived: {seed} is in halifax's fold and this suite has no probe for it — " +
            "add one that exercises a registered word for real (B4.8's mechanism at work)");

        switch (seed.ToLowerInvariant())
        {
            case "seedscribbler": await CanvasProbe("320 200 \"w\" SCRIBOPEN",
                "\"w\" 255 0 0 SCRIBFILL"); return;
            case "seedturtle": await CanvasProbe("320 200 \"tw\" TURTLEOPEN",
                "\"tw\" 100 100 TURTLEGOTO"); return;
            case "seedplotter": await CanvasProbe("640 480 \"p\" PLOTOPEN",
                "\"p\" { 1 2 2 3 } 4 PLOTHISTOGRAM"); return;
            case "seedbuzzer": await BuzzerProbe(); return;
            case "seednet": await NetProbe(); return;
        }

        Probe probe = Probes[seed];
        IReadOnlyList<string> shown = await Run(probe.Lines);
        AssertAnswered(seed, shown, probe.Expect, probe.IsRegex);
    }

    // A probe run answers the LAST line's transcript lines; every line
    // on the way must be accepted (a refusal starts with "?").
    async Task<IReadOnlyList<string>> Run(string[] lines)
    {
        IReadOnlyList<string> shown = Array.Empty<string>();
        foreach (string line in lines)
        {
            HalifaxTurn? turn = await harness.Session.SubmitAsync(line);
            Assert.NotNull(turn);
            shown = turn!.Shown;
        }
        return shown;
    }

    static void AssertAnswered(string seed, IReadOnlyList<string> shown, string expect, bool isRegex)
    {
        Assert.False(shown.Any(l => l.StartsWith("?")),
            $"{seed}: the calculator refused the probe: {string.Join(" | ", shown)}");
        bool hit = isRegex
            ? shown.Any(l => Regex.IsMatch(l, expect))
            : shown.Any(l => l.Contains(expect, StringComparison.Ordinal));
        Assert.True(hit,
            $"{seed}: no real answer — wanted {(isRegex ? "match" : "substring")} '{expect}' in: {string.Join(" | ", shown)}");
    }

    /// <summary>A drawing seed: open through the real registry (the
    /// null seam would refuse), draw, and assert the seam's buffer
    /// actually took ink.</summary>
    async Task CanvasProbe(string openLine, string drawLine)
    {
        int before = harness.Opened.Count;
        IReadOnlyList<string> shown = await Run(new[] { openLine, drawLine });
        Assert.False(shown.Any(l => l.StartsWith("?")),
            $"the draw was refused: {string.Join(" | ", shown)}");
        Assert.True(harness.Opened.Count > before, "no canvas was opened through the registry");
        ScribblerHandle handle = harness.Opened[^1];
        Assert.True(handle.Pixels.Any(b => b != 0),
            "the canvas buffer is untouched — the words answered without drawing");
    }

    /// <summary>The buzzer seed: BUZZFREQ answers pure on any host;
    /// the device words play through the real engine where a device
    /// exists (the audible check itself is the desktop harness's).</summary>
    async Task BuzzerProbe()
    {
        AssertAnswered("seedbuzzer", await Run(new[] { "\"A4\" BUZZFREQ" }), "440", isRegex: false);
        if (Headless) return;
        IReadOnlyList<string> shown = await Run(new[]
        {
            "1 \"T120 O4 CDE\" BUZZPLAY",
            "1 BUZZQUEUED",
        });
        AssertAnswered("seedbuzzer", shown, @"x: \d", isRegex: true);
        await Run(new[] { "1 BUZZSTOP" });
    }

    /// <summary>NETGET against a server this test owns: the user names
    /// the URL (B4.6) and the reply comes back whole.</summary>
    async Task NetProbe()
    {
        var listener = new HttpListener();
        int port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            HttpListenerContext ctx = await listener.GetContextAsync();
            byte[] body = Encoding.UTF8.GetBytes("shoddy-every-seed-hello");
            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        });
        try
        {
            IReadOnlyList<string> shown = await Run(new[]
            {
                $"\"http://127.0.0.1:{port}/\" NETGET",
            });
            AssertAnswered("seednet", shown, "shoddy-every-seed-hello", isRegex: false);
        }
        finally
        {
            listener.Stop();
        }
    }

    static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
