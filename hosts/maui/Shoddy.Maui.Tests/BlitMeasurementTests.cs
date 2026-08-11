// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace Shoddy.Maui.Tests;

/// <summary>
/// The D10 measurement, taken where CI can take it: the per-frame cost
/// of presenting the seam's raw RGBA buffer through SkiaSharp's
/// InstallPixels — the zero-copy path the decision rests on. A churned
/// 512×512 buffer is wrapped, scaled and drawn into an offscreen
/// surface for 300 frames; the assertion bounds the mean per-frame cost
/// well inside a 60fps budget, so a regression to an encode/decode per
/// frame (the falsifier's failure mode) fails loudly rather than
/// shipping as jank. The on-screen half of the measurement — present
/// latency under a real compositor — belongs to the desktop harness
/// run, not to a hosted runner (B7.3, R4).
/// </summary>
public class BlitMeasurementTests
{
    readonly ITestOutputHelper output;

    public BlitMeasurementTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void ZeroCopyBlitStaysInsideTheFrameBudget()
    {
        const int w = 512, h = 512, frames = 300;
        // Pre-churned frames, cycled: the content changes every frame
        // (the load-bearing case) but generating it is not blit cost
        // and stays outside the stopwatch.
        var rng = new Random(1);
        byte[][] churned = new byte[8][];
        for (int i = 0; i < churned.Length; i++)
        {
            churned[i] = new byte[w * h * 4];
            rng.NextBytes(churned[i]);
        }
        byte[] rgba = churned[0];

        using var surface = SKSurface.Create(new SKImageInfo(1024, 1024));
        SKCanvas canvas = surface.Canvas;
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var dest = new SKRect(0, 0, 1024, 1024);

        // Warm-up: Skia's raster pipeline and the JIT both pay
        // first-frame costs that are not the per-frame cost this test
        // bounds — a cold first run once pushed the mean over the gate.
        for (int frame = 0; frame < 20; frame++)
        {
            GCHandle warm = GCHandle.Alloc(rgba, GCHandleType.Pinned);
            try
            {
                using var bmp = new SKBitmap();
                bmp.InstallPixels(info, warm.AddrOfPinnedObject(), info.RowBytes);
                canvas.DrawBitmap(bmp, dest);
                canvas.Flush();
            }
            finally { warm.Free(); }
        }

        var sw = Stopwatch.StartNew();
        for (int frame = 0; frame < frames; frame++)
        {
            rgba = churned[frame % churned.Length];
            GCHandle pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
            try
            {
                using var bmp = new SKBitmap();
                Assert.True(bmp.InstallPixels(info, pin.AddrOfPinnedObject(), info.RowBytes),
                    "InstallPixels refused the buffer — the zero-copy path is gone");
                canvas.DrawBitmap(bmp, dest);
                canvas.Flush();
            }
            finally
            {
                pin.Free();
            }
        }
        sw.Stop();

        double perFrame = sw.Elapsed.TotalMilliseconds / frames;
        output.WriteLine($"blit: {perFrame:F3} ms/frame over {frames} frames of {w}x{h} into 1024x1024");
        Assert.True(perFrame < 8.0,
            $"the blit took {perFrame:F2} ms/frame — half a 60fps budget gone before the compositor; " +
            "if InstallPixels stopped being zero-copy, D10's falsifier fires");
    }
}
