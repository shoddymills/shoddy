// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Maui;
using Xunit;

namespace Shoddy.Maui.Tests;

/// <summary>
/// The terminal state machine against its contract: the escape subset
/// machines/vt100.shoddy emits — every sequence here is the byte
/// string one of that machine's words builds, so a drift between the
/// two shows up as a failing name, not a mystery. Headless: cells in,
/// cells asserted, no view anywhere.
/// </summary>
public class TerminalGridTests
{
    const string ESC = "\x1b";
    const string CSI = "\x1b[";

    static TerminalGrid Grid(int cols = 20, int rows = 5) => new(cols, rows);

    [Fact]
    public void PlainTextLandsAtTheCursor()
    {
        TerminalGrid g = Grid();
        g.Feed("HELLO");
        Assert.Equal("HELLO", g.RowText(0));
        Assert.Equal(5, g.CursorCol);
    }

    [Fact]
    public void CrLfStartsTheNextLine()
    {
        TerminalGrid g = Grid();
        g.Feed("ONE\r\nTWO");
        Assert.Equal("ONE", g.RowText(0));
        Assert.Equal("TWO", g.RowText(1));
    }

    [Fact]
    public void ABareLinefeedAlsoReturnsTheCarriage()
    {
        // LNM starts ON — a Mac-built host writes bare LF and the
        // transcript must not staircase. CSI 20l turns it off.
        TerminalGrid g = Grid();
        g.Feed("ONE\nTWO");
        Assert.Equal("TWO", g.RowText(1));

        TerminalGrid pure = Grid();
        pure.Feed(CSI + "20l" + "ONE\nTWO");
        Assert.Equal("ONE", pure.RowText(0));
        Assert.Equal("   TWO", pure.RowText(1).TrimEnd());
    }

    [Fact]
    public void AutoWrapIsDeferredAtTheLastColumn()
    {
        TerminalGrid g = Grid(cols: 5, rows: 3);
        g.Feed("ABCDE");
        // The classic vt100 subtlety: writing the last column does NOT
        // wrap yet — the cursor hangs until the next printable.
        Assert.Equal(0, g.CursorRow);
        g.Feed("F");
        Assert.Equal("F", g.RowText(1));
    }

    [Fact]
    public void FullScreenScrollRetiresIntoScrollback()
    {
        TerminalGrid g = Grid(cols: 10, rows: 3);
        g.Feed("A\r\nB\r\nC\r\nD");
        Assert.Equal(1, g.ScrollbackCount);
        Assert.Equal('A', g.ScrollbackCell(0, 0).Ch);
        Assert.Equal("B", g.RowText(0));
        Assert.Equal("D", g.RowText(2));
    }

    [Fact]
    public void CursorPosIsOneBasedAndClamped()
    {
        TerminalGrid g = Grid();
        g.Feed(CSI + "2;3H" + "X");
        Assert.Equal('X', g.Cell(1, 2).Ch);
        g.Feed(CSI + "99;99H");
        Assert.Equal(4, g.CursorRow);
        Assert.Equal(19, g.CursorCol);
        // The hvpos alias (final byte f) is the same movement.
        g.Feed(CSI + "1;1f" + "Y");
        Assert.Equal('Y', g.Cell(0, 0).Ch);
    }

    [Fact]
    public void RelativeMovesClampAtTheEdges()
    {
        TerminalGrid g = Grid();
        g.Feed(CSI + "3;3H");
        g.Feed(CSI + "1A" + CSI + "1D");
        Assert.Equal(1, g.CursorRow);
        Assert.Equal(1, g.CursorCol);
        g.Feed(CSI + "99A" + CSI + "99D");
        Assert.Equal(0, g.CursorRow);
        Assert.Equal(0, g.CursorCol);
        g.Feed(CSI + "99B" + CSI + "99C");
        Assert.Equal(4, g.CursorRow);
        Assert.Equal(19, g.CursorCol);
    }

    [Fact]
    public void EraseLineVariants()
    {
        TerminalGrid g = Grid(cols: 10, rows: 2);
        g.Feed("ABCDEFGHIJ" + CSI + "1;5H" + CSI + "0K");
        Assert.Equal("ABCD", g.RowText(0));

        g = Grid(cols: 10, rows: 2);
        g.Feed("ABCDEFGHIJ" + CSI + "1;5H" + CSI + "1K");
        Assert.Equal("     FGHIJ", g.RowText(0));

        g = Grid(cols: 10, rows: 2);
        g.Feed("ABCDEFGHIJ" + CSI + "2K");
        Assert.Equal("", g.RowText(0));
    }

    [Fact]
    public void EraseScreenVariantsAndClearDoesNotHome()
    {
        TerminalGrid g = Grid(cols: 5, rows: 3);
        g.Feed("AA\r\nBB\r\nCC" + CSI + "2;1H" + CSI + "0J");
        Assert.Equal("AA", g.RowText(0));
        Assert.Equal("", g.RowText(1));
        Assert.Equal("", g.RowText(2));

        g = Grid(cols: 5, rows: 3);
        g.Feed("AA\r\nBB\r\nCC" + CSI + "2;3H" + CSI + "1J");
        Assert.Equal("", g.RowText(0));
        Assert.Equal("", g.RowText(1));
        Assert.Equal("CC", g.RowText(2));

        g = Grid(cols: 5, rows: 3);
        g.Feed("AA\r\nBB" + CSI + "2J");
        Assert.Equal("", g.ScreenText().Trim());
        // 2J erases; it does not home. Programs send CursorHome too.
        Assert.Equal(1, g.CursorRow);
    }

    [Fact]
    public void ScrollRegionScrollsAloneAndFeedsNoScrollback()
    {
        TerminalGrid g = Grid(cols: 10, rows: 4);
        g.Feed("TOP" + CSI + "2;3r");
        g.Feed(CSI + "2;1H" + "one\r\ntwo\r\nthree");
        // Row 0 is outside the region and untouched; the region
        // scrolled once; nothing entered scrollback.
        Assert.Equal("TOP", g.RowText(0));
        Assert.Equal("two", g.RowText(1));
        Assert.Equal("three", g.RowText(2));
        Assert.Equal(0, g.ScrollbackCount);
    }

    [Fact]
    public void ReverseIndexScrollsDownAtTheTop()
    {
        TerminalGrid g = Grid(cols: 5, rows: 3);
        g.Feed("A\r\nB\r\nC" + CSI + "1;1H" + ESC + "M" + "Z");
        Assert.Equal("Z", g.RowText(0));
        Assert.Equal("A", g.RowText(1));
    }

    [Fact]
    public void SaveAndRestoreCursorCarriesAttributes()
    {
        TerminalGrid g = Grid();
        g.Feed(CSI + "2;2H" + CSI + "1m" + ESC + "7");
        g.Feed(CSI + "4;4H" + CSI + "0m" + ESC + "8" + "X");
        Assert.Equal('X', g.Cell(1, 1).Ch);
        Assert.True((g.Cell(1, 1).Attr & TerminalAttr.Bold) != 0);
    }

    [Fact]
    public void SgrAttributesStickAndClear()
    {
        TerminalGrid g = Grid();
        g.Feed(CSI + "1m" + "B" + CSI + "4m" + "U" + CSI + "0m" + "N");
        Assert.Equal(TerminalAttr.Bold, g.Cell(0, 0).Attr);
        Assert.Equal(TerminalAttr.Bold | TerminalAttr.Underline, g.Cell(0, 1).Attr);
        Assert.Equal(TerminalAttr.None, g.Cell(0, 2).Attr);
    }

    [Fact]
    public void TabsDefaultEveryEightAndCanBeSetAndCleared()
    {
        TerminalGrid g = Grid(cols: 30, rows: 2);
        g.Feed("\tX");
        Assert.Equal('X', g.Cell(0, 8).Ch);
        // Set a custom stop at column 4 (0-based col 3), clear all
        // defaults first: CSI 3g then ESC H at the target.
        g = Grid(cols: 30, rows: 2);
        g.Feed(CSI + "3g" + CSI + "1;4H" + ESC + "H" + "\r\tY");
        Assert.Equal('Y', g.Cell(0, 3).Ch);
    }

    [Fact]
    public void AlignmentTestFillsWithEs()
    {
        TerminalGrid g = Grid(cols: 4, rows: 2);
        g.Feed(ESC + "#8");
        Assert.Equal("EEEE\nEEEE", g.ScreenText());
    }

    [Fact]
    public void SpecialGraphicsDrawTheBox()
    {
        TerminalGrid g = Grid();
        g.Feed(ESC + "(0" + "lqk" + ESC + "(B" + "A");
        Assert.Equal('┌', g.Cell(0, 0).Ch);
        Assert.Equal('─', g.Cell(0, 1).Ch);
        Assert.Equal('┐', g.Cell(0, 2).Ch);
        Assert.Equal('A', g.Cell(0, 3).Ch);
    }

    [Fact]
    public void ShiftOutSelectsG1()
    {
        TerminalGrid g = Grid();
        g.Feed(ESC + ")0" + "\x0e" + "q" + "\x0f" + "q");
        Assert.Equal('─', g.Cell(0, 0).Ch);
        Assert.Equal('q', g.Cell(0, 1).Ch);
    }

    [Fact]
    public void Vt52ModeMovesAndAddressesAndReturns()
    {
        TerminalGrid g = Grid();
        g.Feed(CSI + "?2l");                       // SetVt52Mode
        g.Feed(ESC + "Y" + (char)(2 + 32) + (char)(5 + 32) + "V");   // row 2, col 5 (0-based)
        Assert.Equal('V', g.Cell(2, 5).Ch);
        g.Feed(ESC + "H" + "H");                   // vt52 home
        Assert.Equal('H', g.Cell(0, 0).Ch);
        g.Feed(ESC + "<");                         // SetAnsiMode
        g.Feed(CSI + "5;1H" + "ANSI");
        Assert.Equal("ANSI", g.RowText(4));
    }

    [Fact]
    public void ReportsAnswerOnTheRespondSeam()
    {
        TerminalGrid g = Grid();
        var answers = new List<string>();
        g.Respond = answers.Add;
        g.Feed(CSI + "3;7H" + CSI + "6n" + CSI + "5n");
        Assert.Equal(new[] { "\x1b[3;7R", "\x1b[0n" }, answers);
    }

    [Fact]
    public void UnknownSequencesAreRecordedNotSwallowed()
    {
        TerminalGrid g = Grid();
        g.Feed(CSI + "99Z");
        Assert.Contains(g.Unhandled, u => u.Contains("99Z"));
    }

    [Fact]
    public void ResetClearsEverything()
    {
        // ESC c is an ANSI sequence: a program in VT52 mode must come
        // back through ESC < first (in VT52 mode, RIS is not defined —
        // the same both-protocols-reuse-short-sequences property the
        // machine's own comments call out).
        TerminalGrid g = Grid();
        g.Feed(CSI + "1m" + "X" + ESC + "c" + "Y");
        Assert.Equal('Y', g.Cell(0, 0).Ch);
        Assert.Equal(TerminalAttr.None, g.Cell(0, 0).Attr);
    }

    [Fact]
    public void ResizeKeepsContentAndRestops()
    {
        TerminalGrid g = Grid(cols: 10, rows: 3);
        g.Feed("KEEP");
        g.Resize(20, 5);
        Assert.Equal("KEEP", g.RowText(0));
        g.Feed("\r\n\tX");
        Assert.Equal('X', g.Cell(1, 8).Ch);
    }
}
