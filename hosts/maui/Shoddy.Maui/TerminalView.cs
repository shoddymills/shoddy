// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace Shoddy.Maui;

/// <summary>
/// The one text surface (the terminal decision): a Skia-drawn glyph
/// grid over a <see cref="TerminalGrid"/>, wearing the brand's paper
/// and ink. Drawn rather than composed from labels because the vt100
/// games repaint continuously and hundreds of text elements re-laying
/// out per frame would not keep up — the same reasoning as D10, on the
/// same dependency we already carry.
///
/// The view follows the tail while new output arrives; a pan (drag /
/// touch) scrolls into the scrollback, and any scroll position other
/// than the tail holds still under new output. Cell attributes render
/// mono-classic: bold weighs the glyph, dim softens it, reverse swaps
/// the cell, underline underlines, invisible skips, blink shows
/// steady (a v1 the games can live with).
///
/// Input is deliberately absent here: the calculator types into its
/// native Entry, and the games' key row arrives with the shelf. The
/// view draws; the pipes carry.
/// </summary>
public sealed class TerminalView : SKCanvasView
{
    static readonly SKColor Paper = SKColor.Parse("#efece4");
    static readonly SKColor Ink = SKColor.Parse("#2E2A26");
    static readonly SKColor InkSoft = SKColor.Parse("#5b544a");

    public TerminalGrid Grid { get; }

    /// <summary>True (the calculator's shape): the grid re-columns to
    /// the view. False (a game's shape): the grid keeps its fixed size
    /// and the view letterboxes.</summary>
    public bool AutoResize { get; set; } = true;

    public float FontSize { get; set; } = 15f;

    readonly SKTypeface regular;
    readonly SKTypeface bold;
    int invalidatePending;
    int linesBack;                  // 0 = at the tail
    double panAccum;

    public TerminalView() : this(new TerminalGrid()) { }

    public TerminalView(TerminalGrid grid)
    {
        Grid = grid;
        grid.Changed += OnGridChanged;

        regular = Mono(SKFontStyle.Normal);
        bold = Mono(SKFontStyle.Bold);

        var pan = new PanGestureRecognizer();
        pan.PanUpdated += OnPan;
        GestureRecognizers.Add(pan);

        PaintSurface += Paint;
    }

    static SKTypeface Mono(SKFontStyle style) =>
        SKTypeface.FromFamilyName("Consolas", style)
        ?? SKTypeface.FromFamilyName("Courier New", style)
        ?? SKTypeface.CreateDefault();

    void OnGridChanged()
    {
        if (Interlocked.Exchange(ref invalidatePending, 1) == 0)
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Interlocked.Exchange(ref invalidatePending, 0);
                InvalidateSurface();
            });
    }

    void OnPan(object? sender, PanUpdatedEventArgs e)
    {
        if (e.StatusType == GestureStatus.Started) panAccum = 0;
        if (e.StatusType != GestureStatus.Running) return;
        double lineHeight = Math.Max(1, FontSize * 1.25);
        int lines = (int)((e.TotalY - panAccum) / lineHeight);
        if (lines == 0) return;
        panAccum += lines * lineHeight;
        ScrollBy(lines);
    }

    /// <summary>Positive scrolls back toward history, negative toward
    /// the tail — wheel and pan both land here.</summary>
    public void ScrollBy(int lines)
    {
        lock (Grid.Sync)
            linesBack = Math.Clamp(linesBack + lines, 0, Grid.ScrollbackCount);
        InvalidateSurface();
    }

#if WINDOWS
    /// <summary>Pan covers touch; the desktop scrolls with a wheel,
    /// which MAUI's gestures do not carry — hooked at the WinUI
    /// element when the handler arrives.</summary>
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Handler?.PlatformView is Microsoft.UI.Xaml.UIElement el)
            el.PointerWheelChanged += (_, e) =>
            {
                int delta = e.GetCurrentPoint(el).Properties.MouseWheelDelta;
                ScrollBy(delta / 120 * 3);
                e.Handled = true;
            };
    }
#endif

    /// <summary>Jump back to the live tail (the calculator does this on
    /// submit, so an answer is never delivered off-screen).</summary>
    public void FollowTail()
    {
        linesBack = 0;
        InvalidateSurface();
    }

    void Paint(object? sender, SKPaintSurfaceEventArgs e)
    {
        SKCanvas canvas = e.Surface.Canvas;

        lock (Grid.Sync)
        {
            bool reversed = Grid.ReverseScreen;
            canvas.Clear(reversed ? Ink : Paper);

            float scale = Width > 0 ? (float)(e.Info.Width / Width) : 1f;
            using var font = new SKFont(regular, FontSize * scale);
            using var boldFont = new SKFont(bold, FontSize * scale);
            float cellW = font.MeasureText("M");
            float cellH = font.Spacing;
            if (cellW <= 0 || cellH <= 0) return;

            if (AutoResize)
            {
                int wantCols = Math.Max(20, (int)(e.Info.Width / cellW));
                int wantRows = Math.Max(4, (int)(e.Info.Height / cellH));
                if (wantCols != Grid.Cols || wantRows != Grid.Rows)
                    Grid.Resize(wantCols, wantRows);
            }

            using var fg = new SKPaint { IsAntialias = true };
            using var bg = new SKPaint { IsAntialias = false };

            int visible = Math.Max(1, (int)(e.Info.Height / cellH));
            // The window: `visible` lines ending `linesBack` above the
            // tail of scrollback + screen.
            int total = Grid.ScrollbackCount + Grid.Rows;
            int end = total - linesBack;
            int start = Math.Max(0, end - visible);

            for (int line = start; line < end; line++)
            {
                float y = (line - start) * cellH - font.Metrics.Ascent;
                bool onScreen = line >= Grid.ScrollbackCount;
                int screenRow = line - Grid.ScrollbackCount;
                for (int c = 0; c < Grid.Cols; c++)
                {
                    TerminalCell cell = onScreen
                        ? Grid.Cell(screenRow, c)
                        : Grid.ScrollbackCell(line, c);
                    bool cursorHere = onScreen && linesBack == 0
                        && screenRow == Grid.CursorRow && c == Grid.CursorCol;

                    bool rev = (cell.Attr & TerminalAttr.Reverse) != 0 ^ reversed ^ cursorHere;
                    SKColor inkHere = (cell.Attr & TerminalAttr.Dim) != 0 ? InkSoft : Ink;
                    SKColor cellFg = rev ? Paper : inkHere;
                    SKColor cellBg = rev ? inkHere : (reversed ? Ink : Paper);

                    float x = c * cellW;
                    if (rev)
                    {
                        bg.Color = cellBg;
                        canvas.DrawRect(x, (line - start) * cellH, cellW, cellH, bg);
                    }

                    char ch = cell.Ch;
                    if (ch == '\0' || ch == ' ') continue;
                    if ((cell.Attr & TerminalAttr.Invisible) != 0) continue;

                    fg.Color = cellFg;
                    SKFont face = (cell.Attr & TerminalAttr.Bold) != 0 ? boldFont : font;
                    canvas.DrawText(ch.ToString(), x, y, face, fg);
                    if ((cell.Attr & TerminalAttr.Underline) != 0)
                    {
                        fg.StrokeWidth = Math.Max(1f, scale);
                        canvas.DrawLine(x, y + 2 * scale, x + cellW, y + 2 * scale, fg);
                    }
                }
            }

            // Scrolled back: a thumb on the right edge says where, and
            // that the tail is elsewhere. At the tail it draws nothing.
            if (linesBack > 0 && total > visible)
            {
                using var thumb = new SKPaint { Color = InkSoft.WithAlpha(140), IsAntialias = true };
                float track = e.Info.Height;
                float size = Math.Max(24 * scale, track * visible / total);
                float topAt = (total - visible) > 0
                    ? (float)(total - visible - linesBack) / (total - visible)
                    : 0f;
                float yThumb = topAt * (track - size);
                canvas.DrawRoundRect(e.Info.Width - 6 * scale, yThumb, 4 * scale, size, 2 * scale, 2 * scale, thumb);
            }
        }
    }
}
