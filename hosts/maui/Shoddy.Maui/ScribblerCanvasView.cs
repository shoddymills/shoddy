// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Runtime.InteropServices;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace Shoddy.Maui;

/// <summary>
/// The scribbler's picture inside a MAUI layout (B1.4b): a view that
/// presents the seam's RGBA buffer and does nothing else — a picture
/// inside the app's UI, never the app's UI. The blit is SkiaSharp
/// (D10): InstallPixels wraps the buffer zero-copy for the draw, which
/// is the decision the spike measures and the falsifier would revert.
///
/// The buffer belongs to the runtime and mutates on the Shoddy thread
/// while this view reads it on the UI thread. That is the mill's own
/// presentation model — the desktop window blits from the same live
/// buffer — and at worst a frame shows a half-drawn update that the
/// next invalidate replaces.
/// </summary>
public sealed class ScribblerCanvasView : SKCanvasView
{
    byte[]? pixels;
    int width, height;
    int invalidatePending;

    public ScribblerCanvasView()
    {
        PaintSurface += Paint;
    }

    /// <summary>Called on the Shoddy thread by the handle's OnBlit:
    /// note the buffer and coalesce a redraw onto the main thread —
    /// many blits between frames cost one invalidate.</summary>
    public void Present(byte[] rgba, int w, int h)
    {
        pixels = rgba;
        width = w;
        height = h;
        if (Interlocked.Exchange(ref invalidatePending, 1) == 0)
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Interlocked.Exchange(ref invalidatePending, 0);
                InvalidateSurface();
            });
    }

    void Paint(object? sender, SKPaintSurfaceEventArgs e)
    {
        SKCanvas canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Black);
        byte[]? px = pixels;
        int w = width, h = height;
        if (px is null || w < 1 || h < 1 || px.Length < w * h * 4) return;

        // Zero-copy: pin the runtime's buffer, wrap it as an SKBitmap
        // for exactly the duration of the draw, scale to fit.
        GCHandle pin = GCHandle.Alloc(px, GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using var bmp = new SKBitmap();
            if (!bmp.InstallPixels(info, pin.AddrOfPinnedObject(), info.RowBytes)) return;

            SKRect dest = FitRect(e.Info.Width, e.Info.Height, w, h);
            using var paint = new SKPaint();
            canvas.DrawBitmap(bmp, dest, paint);
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>Aspect-fit: the drawing keeps its shape whatever shape
    /// MAUI gives the view.</summary>
    static SKRect FitRect(float viewW, float viewH, int w, int h)
    {
        float scale = Math.Min(viewW / w, viewH / h);
        float dw = w * scale, dh = h * scale;
        float x = (viewW - dw) / 2, y = (viewH - dh) / 2;
        return new SKRect(x, y, x + dw, y + dh);
    }
}
