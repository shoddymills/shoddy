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
/// Prose input stays outside (the calculator types into its native
/// Entry), but game input lives here: set <see cref="Keys"/> and the
/// view captures raw keystrokes into that queue — characters as
/// themselves, the named specials through <see cref="TerminalKeys"/>
/// as their whole escape sequences. The view draws; the pipes carry.
///
/// The eight ANSI colours render as a palette tuned to the paper —
/// each hue darkened until it reads as ink on #efece4, because a
/// game's blue maze must be both blue and legible.
/// </summary>
public sealed class TerminalView : SKCanvasView
{
    static readonly SKColor Paper = SKColor.Parse("#efece4");
    static readonly SKColor Ink = SKColor.Parse("#2E2A26");
    static readonly SKColor InkSoft = SKColor.Parse("#5b544a");

    // Indexed 1..8 (black..white) to match TerminalCell.Fg/Bg; 0 is
    // never looked up. "White" maps to the soft ink: on paper, white
    // paint is the quiet voice, not an invisible one.
    static readonly SKColor[] Ansi =
    {
        SKColors.Transparent,
        SKColor.Parse("#2E2A26"),   // black — the ink itself
        SKColor.Parse("#9E3B33"),   // red
        SKColor.Parse("#41703F"),   // green
        SKColor.Parse("#8F6E1A"),   // yellow, darkened to read on paper
        SKColor.Parse("#3B5A96"),   // blue
        SKColor.Parse("#84437E"),   // magenta
        SKColor.Parse("#2E7376"),   // cyan
        SKColor.Parse("#5b544a"),   // white — the soft ink
    };

    public TerminalGrid Grid { get; }

    /// <summary>When set, the view captures keyboard input into this
    /// queue — the game half of the wire. Null (the default) leaves
    /// typing to whatever native control owns it.</summary>
    public TerminalInputQueue? Keys { get; set; }

    /// <summary>False hides the block cursor — the line games take
    /// their typing in a native entry beneath the transcript, and a
    /// block squatting mid-screen says "type here" exactly where you
    /// cannot.</summary>
    public bool ShowCursor { get; set; } = true;

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
    /// <summary>The hidden key sink, the terminal-emulator trick every
    /// web terminal uses: the drawn canvas is a Panel and panels do not
    /// reliably take keyboard focus, so a real focusable control — a
    /// 1x1 transparent TextBox parked offscreen inside the canvas —
    /// owns the keyboard instead. KeyDown relays the named specials
    /// (arrows, PF, Escape, Enter, Back, Tab) through
    /// <see cref="TerminalKeys"/>; TextChanged harvests the printable
    /// characters and clears the box. Focus follows the sink: on its
    /// own Loaded, on any click on the board, and on
    /// <see cref="FocusKeyboard"/>.</summary>
    Microsoft.UI.Xaml.Controls.TextBox? keySink;

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Handler?.PlatformView is not Microsoft.UI.Xaml.UIElement el) return;
        el.PointerWheelChanged += (_, e) =>
        {
            int delta = e.GetCurrentPoint(el).Properties.MouseWheelDelta;
            ScrollBy(delta / 120 * 3);
            e.Handled = true;
        };
        if (Keys is null || el is not Microsoft.UI.Xaml.Controls.Panel panel) return;

        var box = new Microsoft.UI.Xaml.Controls.TextBox
        {
            Opacity = 0,
            Width = 1,
            Height = 1,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
        };
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(box, -200);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(box, -200);
        panel.Children.Add(box);
        keySink = box;

        box.KeyDown += (_, e) =>
        {
            if (Keys is not TerminalInputQueue q) return;
            string? seq = TerminalKeys.Special(e.Key.ToString());
            if (seq is null) return;                 // characters come via TextChanged
            q.Enqueue(seq);
            e.Handled = true;
        };
        box.TextChanged += (_, _) =>
        {
            string typed = box.Text;
            if (typed.Length == 0) return;
            box.Text = "";
            Keys?.Enqueue(typed);
        };
        box.Loaded += (_, _) => box.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        el.PointerPressed += (_, _) => box.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    /// <summary>Give the drawn surface the keyboard — a game page calls
    /// this when it appears, and a click on the board does the same.</summary>
    public void FocusKeyboard()
        => keySink?.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
#else
    public void FocusKeyboard() { }
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
            float fontPx = FontSize * scale;
            if (!AutoResize)
            {
                // A fixed grid (a game's 80x26) fits the view instead:
                // the font takes whatever size shows every cell.
                using var probe = new SKFont(regular, 64f);
                float unitW = probe.MeasureText("M") / 64f;
                float unitH = probe.Spacing / 64f;
                if (unitW > 0 && unitH > 0)
                    fontPx = Math.Max(4f, Math.Min(
                        e.Info.Width / (Grid.Cols * unitW),
                        e.Info.Height / (Grid.Rows * unitH)));
            }
            using var font = new SKFont(regular, fontPx);
            using var boldFont = new SKFont(bold, fontPx);
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
                    bool cursorHere = ShowCursor && onScreen && linesBack == 0
                        && screenRow == Grid.CursorRow && c == Grid.CursorCol;

                    bool rev = (cell.Attr & TerminalAttr.Reverse) != 0 ^ reversed ^ cursorHere;
                    SKColor inkHere = cell.Fg != 0 ? Ansi[cell.Fg]
                        : (cell.Attr & TerminalAttr.Dim) != 0 ? InkSoft : Ink;
                    SKColor paperHere = cell.Bg != 0 ? Ansi[cell.Bg] : (reversed ? Ink : Paper);
                    SKColor cellFg = rev ? Paper : inkHere;
                    SKColor cellBg = rev ? inkHere : paperHere;

                    float x = c * cellW;
                    if (rev || cell.Bg != 0)
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
