// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Text;

namespace Shoddy.Maui;

[Flags]
public enum TerminalAttr : byte
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Underline = 4,
    Blink = 8,
    Reverse = 16,
    Invisible = 32,
}

public struct TerminalCell
{
    public char Ch;
    public TerminalAttr Attr;
    /// <summary>ANSI colours, stored 1..8 (black..white); 0 is the
    /// theme's default ink/paper. The grid records what the program
    /// said; mapping onto a palette is the view's business.</summary>
    public byte Fg;
    public byte Bg;
}

/// <summary>
/// The terminal's state machine, pure and view-free: a cell grid, a
/// cursor, and an interpreter for exactly the escape subset
/// machines/vt100.shoddy can emit — the ANSI/VT100 sequences its words
/// build, the VT52 half behind SetVt52Mode, the DEC special-graphics
/// charset, plus the bare controls (CR LF BS TAB BEL SO SI) any piped
/// program produces. That machine is the contract's other end: we own
/// both sides of the wire, so "VT100 compatibility" here means "what
/// vt100.shoddy says", not xterm. An unrecognized sequence is recorded
/// on <see cref="Unhandled"/> — visible, never silently swallowed.
///
/// One deliberate departure, documented because it is load-bearing:
/// newline mode (LNM) starts ON, so a bare LF also returns the
/// carriage. Shoddy programs print lines; on Windows the pipe carries
/// CRLF and the choice is invisible, but a Mac-built host writes bare
/// LF and without this every transcript would staircase. A program
/// that wants pure linefeeds sends SetLinefeedMode (CSI 20l), which is
/// honored.
///
/// Rows scrolled off the top of a FULL-SCREEN scroll go to the
/// scrollback (capped); a partial scroll region — a game's status line
/// arrangement — discards, as a real terminal does.
///
/// Thread-safe by one lock: the mill's worker feeds characters while
/// the view paints. Take <see cref="Sync"/> around any read of the
/// public state.
/// </summary>
public sealed class TerminalGrid
{
    public const int DefaultCols = 80;
    public const int DefaultRows = 24;
    const int ScrollbackCap = 4000;

    public readonly object Sync = new();

    List<TerminalCell[]> screen = new();
    readonly List<TerminalCell[]> scrollback = new();

    int cols, rows;
    int curRow, curCol;             // 0-based
    bool wrapPending;               // classic deferred wrap at the last column
    TerminalAttr attr;
    byte fg, bg;                    // current ANSI colours, 0 = default
    int scrollTop, scrollBottom;    // 0-based, inclusive
    bool autoWrap = true;
    bool originMode;
    bool newlineMode = true;        // LNM — see the header for why ON
    bool vt52;
    bool reverseScreen;
    int savedRow, savedCol;
    TerminalAttr savedAttr;
    byte savedFg, savedBg;
    bool[] tabStops = Array.Empty<bool>();

    // Character sets: G0/G1 designations ('B' US, 'A' UK, '0' special
    // graphics, '1'/'2' alt ROM — rendered as US) and the shift state.
    char g0 = 'B', g1 = 'B';
    bool shiftedG1;                 // SO selects G1, SI back to G0
    int singleShift;                // 2 or 3 after SS2/SS3, else 0

    enum PState { Ground, Esc, Csi, EscHash, DesignateG0, DesignateG1, Vt52Row, Vt52Col }
    PState state = PState.Ground;
    readonly StringBuilder csi = new();
    int vt52Row;

    /// <summary>Raised (inside the lock) after a Feed changes anything;
    /// the view coalesces these into invalidates.</summary>
    public event Action? Changed;

    /// <summary>Terminal-to-host answers (cursor position reports,
    /// status, ident): the host wires this into the input pipe.</summary>
    public Action<string>? Respond;

    /// <summary>Sequences outside the contract, kept for diagnosis —
    /// a game emitting something unknown should be one look away from
    /// an answer, never a silent swallow.</summary>
    public readonly List<string> Unhandled = new();

    public TerminalGrid(int cols = DefaultCols, int rows = DefaultRows)
    {
        Resize(cols, rows);
    }

    // ---- what the view reads (under Sync) ----

    public int Cols => cols;
    public int Rows => rows;
    public int CursorRow => curRow;
    public int CursorCol => curCol;
    public bool ReverseScreen => reverseScreen;
    public int ScrollbackCount => scrollback.Count;
    public TerminalCell Cell(int row, int col) => screen[row][col];
    public TerminalCell ScrollbackCell(int line, int col)
    {
        TerminalCell[] r = scrollback[line];
        return col < r.Length ? r[col] : default;
    }

    /// <summary>A row's text, trailing blanks trimmed — the test
    /// suite's window into the grid.</summary>
    public string RowText(int row)
    {
        var sb = new StringBuilder(cols);
        for (int c = 0; c < cols; c++) sb.Append(screen[row][c].Ch == '\0' ? ' ' : screen[row][c].Ch);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Every screen row joined by newlines, trimmed — for
    /// "does the terminal show X" assertions.</summary>
    public string ScreenText()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < rows; r++)
        {
            if (r > 0) sb.Append('\n');
            sb.Append(RowText(r));
        }
        return sb.ToString();
    }

    // ---- feeding ----

    public void Feed(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        lock (Sync)
        {
            foreach (char ch in s) FeedChar(ch);
            Changed?.Invoke();
        }
    }

    public void Resize(int newCols, int newRows)
    {
        lock (Sync)
        {
            newCols = Math.Max(2, newCols);
            newRows = Math.Max(2, newRows);
            var fresh = new List<TerminalCell[]>(newRows);
            for (int r = 0; r < newRows; r++)
            {
                var row = new TerminalCell[newCols];
                Blank(row);
                if (r < screen.Count)
                    Array.Copy(screen[r], row, Math.Min(newCols, screen[r].Length));
                fresh.Add(row);
            }
            screen = fresh;
            cols = newCols;
            rows = newRows;
            scrollTop = 0;
            scrollBottom = rows - 1;
            tabStops = new bool[cols];
            for (int c = 8; c < cols; c += 8) tabStops[c] = true;
            curRow = Math.Min(curRow, rows - 1);
            curCol = Math.Min(curCol, cols - 1);
            wrapPending = false;
            Changed?.Invoke();
        }
    }

    public void Reset()
    {
        lock (Sync)
        {
            foreach (TerminalCell[] row in screen) Blank(row);
            curRow = curCol = 0;
            wrapPending = false;
            attr = TerminalAttr.None;
            fg = bg = 0;
            scrollTop = 0;
            scrollBottom = rows - 1;
            autoWrap = true;
            originMode = false;
            newlineMode = true;
            vt52 = false;
            reverseScreen = false;
            g0 = g1 = 'B';
            shiftedG1 = false;
            singleShift = 0;
            state = PState.Ground;
            tabStops = new bool[cols];
            for (int c = 8; c < cols; c += 8) tabStops[c] = true;
            Changed?.Invoke();
        }
    }

    // ---- the interpreter ----

    void FeedChar(char ch)
    {
        switch (state)
        {
            case PState.Ground: Ground(ch); break;
            case PState.Esc: Escaped(ch); break;
            case PState.Csi: CsiByte(ch); break;
            case PState.EscHash: Hash(ch); break;
            case PState.DesignateG0: g0 = ch; state = PState.Ground; break;
            case PState.DesignateG1: g1 = ch; state = PState.Ground; break;
            case PState.Vt52Row: vt52Row = ch - 32; state = PState.Vt52Col; break;
            case PState.Vt52Col:
                MoveTo(vt52Row, ch - 32);
                state = PState.Ground;
                break;
        }
    }

    void Ground(char ch)
    {
        switch (ch)
        {
            case (char)27: state = PState.Esc; return;
            case '\r': curCol = 0; wrapPending = false; return;
            case '\n': LineFeed(); if (newlineMode) curCol = 0; return;
            case '\b': if (curCol > 0) curCol--; wrapPending = false; return;
            case '\t': NextTab(); return;
            case (char)7: return;                       // BEL — silence
            case (char)14: shiftedG1 = true; return;    // SO → G1
            case (char)15: shiftedG1 = false; return;   // SI → G0
            case (char)0: return;
        }
        if (ch < 32) return;
        Put(Translate(ch));
    }

    void Escaped(char ch)
    {
        state = PState.Ground;
        if (vt52) { Vt52Escaped(ch); return; }
        switch (ch)
        {
            case '[': csi.Clear(); state = PState.Csi; break;
            case 'D': LineFeed(); break;                            // index
            case 'M': ReverseLineFeed(); break;                     // reverse index
            case 'E': LineFeed(); curCol = 0; break;                // next line
            case '7': savedRow = curRow; savedCol = curCol; savedAttr = attr; savedFg = fg; savedBg = bg; break;
            case '8': curRow = savedRow; curCol = savedCol; attr = savedAttr; fg = savedFg; bg = savedBg; wrapPending = false; break;
            case 'H': if (curCol < cols) tabStops[curCol] = true; break;
            case 'c': Reset2(); break;
            case '#': state = PState.EscHash; break;
            case '(': state = PState.DesignateG0; break;
            case ')': state = PState.DesignateG1; break;
            case 'N': singleShift = 2; break;                       // SS2
            case 'O': singleShift = 3; break;                       // SS3
            case '=': case '>': break;                              // keypad modes
            case '<': break;                                        // already ANSI
            default: Unknown("ESC " + ch); break;
        }
    }

    void Vt52Escaped(char ch)
    {
        switch (ch)
        {
            case 'A': if (curRow > 0) curRow--; break;
            case 'B': if (curRow < rows - 1) curRow++; break;
            case 'C': if (curCol < cols - 1) curCol++; break;
            case 'D': if (curCol > 0) curCol--; break;
            case 'H': curRow = 0; curCol = 0; break;
            case 'I': ReverseLineFeed(); break;
            case 'J': EraseScreen(0); break;
            case 'K': EraseLine(0); break;
            case 'Y': state = PState.Vt52Row; return;
            case 'F': g0 = '0'; break;                              // graphics on
            case 'G': g0 = 'B'; break;                              // graphics off
            case 'Z': Respond?.Invoke("\x1b/Z"); break;
            case '<': vt52 = false; break;                          // back to ANSI
            case '=': case '>': break;
            default: Unknown("ESC " + ch + " (vt52)"); break;
        }
        wrapPending = false;
    }

    void Hash(char ch)
    {
        state = PState.Ground;
        if (ch == '8')
        {
            // Screen alignment test: every cell an E.
            foreach (TerminalCell[] row in screen)
                for (int c = 0; c < cols; c++)
                    row[c] = new TerminalCell { Ch = 'E', Attr = TerminalAttr.None };
            return;
        }
        // Double width/height (#3 #4 #5 #6): line rendering modes this
        // grid does not model — recorded, not swallowed.
        if (ch is not ('3' or '4' or '5' or '6')) Unknown("ESC #" + ch);
    }

    void CsiByte(char ch)
    {
        if (ch is >= '0' and <= '9' or ';' or '?')
        {
            csi.Append(ch);
            return;
        }
        state = PState.Ground;
        string p = csi.ToString();
        bool priv = p.StartsWith('?');
        int[] n = Params(priv ? p[1..] : p);
        switch (ch)
        {
            case 'A': curRow = Math.Max(RegionTop(), curRow - Math.Max(1, n[0])); wrapPending = false; break;
            case 'B': curRow = Math.Min(RegionBottom(), curRow + Math.Max(1, n[0])); wrapPending = false; break;
            case 'C': curCol = Math.Min(cols - 1, curCol + Math.Max(1, n[0])); wrapPending = false; break;
            case 'D': curCol = Math.Max(0, curCol - Math.Max(1, n[0])); wrapPending = false; break;
            case 'H': case 'f': MoveTo(n[0] - 1, (n.Length > 1 ? n[1] : 1) - 1); break;
            case 'J': EraseScreen(n[0]); break;
            case 'K': EraseLine(n[0]); break;
            case 'r':
                scrollTop = Math.Max(0, n[0] - 1);
                scrollBottom = n.Length > 1 && n[1] > 0 ? Math.Min(rows - 1, n[1] - 1) : rows - 1;
                if (scrollBottom <= scrollTop) { scrollTop = 0; scrollBottom = rows - 1; }
                MoveTo(0, 0);
                break;
            case 'm': Sgr(n); break;
            case 'g':
                if (n[0] == 0) { if (curCol < cols) tabStops[curCol] = false; }
                else if (n[0] == 3) Array.Clear(tabStops);
                break;
            case 'h': Mode(priv, n, set: true); break;
            case 'l': Mode(priv, n, set: false); break;
            case 'n':
                if (n[0] == 5) Respond?.Invoke("\x1b[0n");
                else if (n[0] == 6) Respond?.Invoke($"\x1b[{curRow + 1};{curCol + 1}R");
                break;
            case 'c': Respond?.Invoke("\x1b[?1;0c"); break;
            case 'q': case 'y': break;                  // LEDs, confidence tests
            default: Unknown("CSI " + p + ch); break;
        }
    }

    void Mode(bool priv, int[] n, bool set)
    {
        foreach (int m in n)
        {
            if (priv)
                switch (m)
                {
                    case 2: if (!set) vt52 = true; break;
                    case 5: reverseScreen = set; break;
                    case 6: originMode = set; MoveTo(0, 0); break;
                    case 7: autoWrap = set; break;
                    case 1: case 3: case 4: case 8: case 9: break;  // cursor-key app, 132col, smooth, autorepeat, interlace
                    default: Unknown($"CSI ?{m}{(set ? 'h' : 'l')}"); break;
                }
            else
                switch (m)
                {
                    case 20: newlineMode = set; break;
                    default: Unknown($"CSI {m}{(set ? 'h' : 'l')}"); break;
                }
        }
    }

    void Sgr(int[] n)
    {
        foreach (int m in n)
            switch (m)
            {
                case 0: attr = TerminalAttr.None; fg = bg = 0; break;
                case 1: attr |= TerminalAttr.Bold; break;
                case 2: attr |= TerminalAttr.Dim; break;
                case 4: attr |= TerminalAttr.Underline; break;
                case 5: attr |= TerminalAttr.Blink; break;
                case 7: attr |= TerminalAttr.Reverse; break;
                case 8: attr |= TerminalAttr.Invisible; break;
                // The eight ANSI colours (a game's paint): stored 1..8,
                // 0 the theme default, 39/49 the explicit way back.
                case >= 30 and <= 37: fg = (byte)(m - 29); break;
                case 39: fg = 0; break;
                case >= 40 and <= 47: bg = (byte)(m - 39); break;
                case 49: bg = 0; break;
                default: Unknown("SGR " + m); break;
            }
    }

    // ---- geometry ----

    int RegionTop() => originMode ? scrollTop : 0;
    int RegionBottom() => originMode ? scrollBottom : rows - 1;

    void MoveTo(int row, int col)
    {
        if (originMode) row += scrollTop;
        curRow = Math.Clamp(row, RegionTop(), RegionBottom());
        curCol = Math.Clamp(col, 0, cols - 1);
        wrapPending = false;
    }

    void LineFeed()
    {
        wrapPending = false;
        if (curRow == scrollBottom) ScrollUp();
        else if (curRow < rows - 1) curRow++;
    }

    void ReverseLineFeed()
    {
        wrapPending = false;
        if (curRow == scrollTop) ScrollDown();
        else if (curRow > 0) curRow--;
    }

    void ScrollUp()
    {
        TerminalCell[] top = screen[scrollTop];
        if (scrollTop == 0 && scrollBottom == rows - 1)
        {
            // A full-screen scroll retires the row into scrollback; a
            // partial region (a game's fixed status line) discards it.
            scrollback.Add(top);
            if (scrollback.Count > ScrollbackCap) scrollback.RemoveAt(0);
            top = new TerminalCell[cols];
        }
        screen.RemoveAt(scrollTop);
        Blank(top);
        screen.Insert(scrollBottom, top);
    }

    void ScrollDown()
    {
        TerminalCell[] bottom = screen[scrollBottom];
        screen.RemoveAt(scrollBottom);
        Blank(bottom);
        screen.Insert(scrollTop, bottom);
    }

    void NextTab()
    {
        wrapPending = false;
        for (int c = curCol + 1; c < cols; c++)
            if (tabStops[c]) { curCol = c; return; }
        curCol = cols - 1;
    }

    void Put(char ch)
    {
        if (wrapPending && autoWrap)
        {
            curCol = 0;
            LineFeed();
        }
        screen[curRow][curCol] = new TerminalCell { Ch = ch, Attr = attr, Fg = fg, Bg = bg };
        if (curCol < cols - 1) curCol++;
        else wrapPending = autoWrap;
    }

    void EraseLine(int mode)
    {
        TerminalCell[] row = screen[curRow];
        (int from, int to) = mode switch
        {
            1 => (0, curCol),
            2 => (0, cols - 1),
            _ => (curCol, cols - 1),
        };
        for (int c = from; c <= to; c++) row[c] = default;
        wrapPending = false;
    }

    void EraseScreen(int mode)
    {
        switch (mode)
        {
            case 1:
                for (int r = 0; r < curRow; r++) Blank(screen[r]);
                EraseLine(1);
                break;
            case 2:
                foreach (TerminalCell[] row in screen) Blank(row);
                break;
            default:
                EraseLine(0);
                for (int r = curRow + 1; r < rows; r++) Blank(screen[r]);
                break;
        }
        wrapPending = false;
    }

    void Reset2()
    {
        // ESC c inside the lock: Reset() takes the lock, so inline the
        // same work without re-entering.
        foreach (TerminalCell[] row in screen) Blank(row);
        curRow = curCol = 0;
        wrapPending = false;
        attr = TerminalAttr.None;
        fg = bg = 0;
        scrollTop = 0;
        scrollBottom = rows - 1;
        autoWrap = true;
        originMode = false;
        newlineMode = true;
        vt52 = false;
        reverseScreen = false;
        g0 = g1 = 'B';
        shiftedG1 = false;
        singleShift = 0;
        tabStops = new bool[cols];
        for (int c = 8; c < cols; c += 8) tabStops[c] = true;
    }

    static void Blank(TerminalCell[] row) => Array.Clear(row);

    char Translate(char ch)
    {
        char set = singleShift switch
        {
            2 => g0,        // SS2/SS3 name G2/G3 on a real VT100; this
            3 => g1,        //   terminal folds them onto G0/G1
            _ => shiftedG1 ? g1 : g0,
        };
        singleShift = 0;
        return set == '0' ? SpecialGraphics(ch) : ch;
    }

    /// <summary>The DEC special graphics set — the line-drawing half a
    /// vt100 game builds its walls from — mapped to the Unicode
    /// box-drawing points.</summary>
    static char SpecialGraphics(char ch) => ch switch
    {
        '`' => '◆',   // diamond
        'a' => '▒',   // checkerboard
        'f' => '°',   // degree
        'g' => '±',   // plus/minus
        'j' => '┘',   // ┘
        'k' => '┐',   // ┐
        'l' => '┌',   // ┌
        'm' => '└',   // └
        'n' => '┼',   // ┼
        'o' => '⎺',   // scan 1
        'p' => '⎻',   // scan 3
        'q' => '─',   // ─
        'r' => '⎼',   // scan 7
        's' => '⎽',   // scan 9
        't' => '├',   // ├
        'u' => '┤',   // ┤
        'v' => '┴',   // ┴
        'w' => '┬',   // ┬
        'x' => '│',   // │
        'y' => '≤',   // ≤
        'z' => '≥',   // ≥
        '{' => 'π',   // π
        '|' => '≠',   // ≠
        '}' => '£',   // £
        '~' => '·',   // ·
        _ => ch,
    };

    static int[] Params(string p)
    {
        if (p.Length == 0) return new[] { 0 };
        string[] parts = p.Split(';');
        var n = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            n[i] = int.TryParse(parts[i], out int v) ? v : 0;
        return n;
    }

    void Unknown(string what)
    {
        if (Unhandled.Count < 64) Unhandled.Add(what);
    }
}
