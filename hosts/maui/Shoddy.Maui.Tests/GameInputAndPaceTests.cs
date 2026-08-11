// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Reflection;
using Shoddy.Hosting;
using Shoddy.Maui;
using Shoddy.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace Shoddy.Maui.Tests;

/// <summary>
/// The two failures live play found, reproduced headless. First: a line
/// game must take input fed mid-run, and must keep taking it while a
/// scribbler is open — the engine's console-vs-window refusal is about
/// OS focus, which a piped host does not have, and the app runs a
/// terminal beside a canvas by design. Second: a canvas game driven by
/// a host-side interval timer must actually reach its frame rate; the
/// blit count is the frame count. In the "scribbler seam" collection
/// because these tests own the process-global registry while they run.
/// </summary>
[Collection("scribbler seam")]
public class GameInputAndPaceTests
{
    readonly ITestOutputHelper output;

    public GameInputAndPaceTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task OregonTakesALineMidRun()
    {
        var grid = new TerminalGrid(80, 24);
        var input = new TerminalInputQueue();
        _ = ShoddyHost.RunWovenAsync(Assembly.Load("oregon"),
            new TerminalWriter(grid), input, Array.Empty<string>());
        await WaitFor(grid, "DO YOU NEED INSTRUCTIONS");
        input.Enqueue("NO\r\n");
        await WaitFor(grid, "HOW GOOD A SHOT");    // the marksmanship question
        input.Enqueue("3\r\n");
        await WaitFor(grid, "OXEN TEAM");          // the first outfitting prompt
    }

    [Fact]
    public async Task PipedLineInputIgnoresAnOpenScribbler()
    {
        // The app's real shape: a canvas game running on one page while
        // a line game reads its prompt on another. The count is the
        // whole signal the refusal keys on.
        Interlocked.Increment(ref ScribblerRegistry.OpenCount);
        try
        {
            var grid = new TerminalGrid(80, 24);
            var input = new TerminalInputQueue();
            _ = ShoddyHost.RunWovenAsync(Assembly.Load("oregon"),
                new TerminalWriter(grid), input, Array.Empty<string>());
            await WaitFor(grid, "DO YOU NEED INSTRUCTIONS");
            input.Enqueue("NO\r\n");
            await WaitFor(grid, "HOW GOOD A SHOT");
        }
        finally
        {
            Interlocked.Decrement(ref ScribblerRegistry.OpenCount);
        }
    }

    [Fact]
    public async Task DevilsDustReachesItsFrameRateHeadless()
    {
        int blits = 0;
        Timer? tick = null;
        ScribblerHandle? made = null;
        Func<int, int, ScribblerHandle>? prev = ScribblerRegistry.CreateScribbler;
        ScribblerRegistry.CreateScribbler = (w, h) =>
        {
            var handle = new ScribblerHandle { Width = w, Height = h, Pixels = new byte[w * h * 4] };
            handle.OnBlit = (_, _, _) => Interlocked.Increment(ref blits);
            ScribblerEvent? lastTick = null;
            handle.OnSetInterval = ms =>
            {
                tick?.Dispose();
                tick = null;
                if (ms > 0)
                    tick = new Timer(_ =>
                    {
                        // One pending tick at most, as the app's timer
                        // keeps it — a slow frame drops ticks rather
                        // than queueing a backlog ahead of the quit key.
                        if (lastTick != null && !lastTick.Taken) return;
                        var t = new ScribblerEvent
                        {
                            Type = ScribblerEvent.Kind.Tick,
                            At = Ticker.Now,
                        };
                        lastTick = t;
                        handle.Events.Enqueue(t);
                        handle.Signal.Release();
                    }, null, ms, ms);
            };
            made = handle;
            return handle;
        };
        MauiAudioBackend.Install();     // the app's shape: sound armed while frames run
        try
        {
            Task<int> run = ShoddyHost.RunWovenAsync(Assembly.Load("devils-dust"),
                new StringWriter(), new TerminalInputQueue(), Array.Empty<string>());

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (made is null && DateTime.UtcNow < deadline) await Task.Delay(25);
            Assert.NotNull(made);

            await Task.Delay(500);      // settle: sink init, first frames
            Interlocked.Exchange(ref blits, 0);
            await Task.Delay(3000);
            double fps = Interlocked.CompareExchange(ref blits, 0, 0) / 3.0;
            output.WriteLine($"devils-dust, sound armed: {fps:F1} frames/second (asked for 30)");

            // Second phase, sound disarmed: if the rate recovers, the
            // drag is the audio path, not the simulation.
            MauiAudioBackend.Shutdown();
            await Task.Delay(250);
            Interlocked.Exchange(ref blits, 0);
            await Task.Delay(3000);
            double silentFps = Interlocked.CompareExchange(ref blits, 0, 0) / 3.0;
            output.WriteLine($"devils-dust, sound off: {silentFps:F1} frames/second");

            made!.Events.Enqueue(new ScribblerEvent
            {
                Type = ScribblerEvent.Kind.KeyDown, Key = 'Q', At = Ticker.Now,
            });
            made.Signal.Release();
            int exit = await run.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, exit);

            // The floor is a Release claim, and a modest one: it exists
            // to catch a dead tick path, not to grade the flock. The
            // Debug runtime interprets several times slower (measured
            // ~5 fps against Release's ~25) — reported, not a defect;
            // games ship Release. The armed-vs-off gap is the audio
            // path's per-frame cost, on the record above for when the
            // drone's cache behaviour gets its own look.
#if DEBUG
            Assert.True(fps > 0, "no frames at all — the tick path is dead");
#else
            Assert.True(silentFps >= 15,
                $"expected at least 15 fps in Release with sound off, measured {silentFps:F1}");
#endif
        }
        finally
        {
            tick?.Dispose();
            ScribblerRegistry.CreateScribbler = prev;
        }
    }

    static async Task WaitFor(TerminalGrid grid, string text)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            lock (grid.Sync)
                if (grid.ScreenText().Contains(text)) return;
            await Task.Delay(50);
        }
        string screen;
        lock (grid.Sync) screen = grid.ScreenText();
        Assert.Fail($"'{text}' never appeared; the screen shows:\n{screen}");
    }
}
