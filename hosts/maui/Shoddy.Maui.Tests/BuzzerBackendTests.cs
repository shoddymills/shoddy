// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace Shoddy.Maui.Tests;

/// <summary>
/// B3.7 on the Windows lane: all seven BuzzerRegistry delegates, driven
/// through the shared channel engine into a real AudioGraph device —
/// no staging, no nulls. What this proves mechanically: install wires
/// all seven; each one runs against the real sink without fault; a
/// held note survives until NoteOff; drain-shutdown completes. What it
/// cannot prove is AUDIBILITY — that is the listen item the desktop
/// harness owns (B7.3), and CI's runners have no audio device at all,
/// which is what SHODDY_HEADLESS_CI declares.
/// </summary>
public class BuzzerBackendTests
{
    readonly ITestOutputHelper output;

    public BuzzerBackendTests(ITestOutputHelper output) => this.output = output;

    static bool Headless => Environment.GetEnvironmentVariable("SHODDY_HEADLESS_CI") == "1";

    [Fact]
    public void AllSevenDelegatesDriveTheRealSink()
    {
        if (Headless)
        {
            output.WriteLine("skipped: SHODDY_HEADLESS_CI — no audio device on this runner; the desktop harness runs this for real");
            return;
        }

        Maui.MauiAudioBackend.Install();
        try
        {
            // Install wires all seven — a null delegate in a shipped
            // Reckoner is a defect, not a degradation (B3.7).
            Assert.NotNull(BuzzerRegistry.Sound);
            Assert.NotNull(BuzzerRegistry.NoteOn);
            Assert.NotNull(BuzzerRegistry.NoteOff);
            Assert.NotNull(BuzzerRegistry.Queue);
            Assert.NotNull(BuzzerRegistry.Stop);
            Assert.NotNull(BuzzerRegistry.Gain);
            Assert.NotNull(BuzzerRegistry.Wave);

            // The devils-dust shape that the staged v1 silenced: sticky
            // wave and gain, a queue, a stop — then the two only a test
            // mill exercises, NoteOn/NoteOff (B8).
            BuzzerRegistry.Wave!(1, 2);
            BuzzerRegistry.Gain!(1, 0.2);
            BuzzerRegistry.Queue!(1, 440, 120);
            BuzzerRegistry.Queue!(1, 660, 120);
            BuzzerRegistry.Sound!(880, 80);
            BuzzerRegistry.NoteOn!(2, 220);
            Thread.Sleep(150);
            BuzzerRegistry.NoteOff!(2);
            BuzzerRegistry.Stop!(1);
            BuzzerRegistry.Queue!(1, 550, 100);
        }
        finally
        {
            // Drain returns only after the real device played the tail
            // out — a sink that never rendered would hang here and the
            // test host's timeout would say so.
            Maui.MauiAudioBackend.Shutdown();
        }
    }
}
