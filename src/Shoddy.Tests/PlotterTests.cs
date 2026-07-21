// Copyright (c) 2026 Stephen Vincent Foster. All rights reserved.
//
// This file is part of the Shoddy Language project.
// Licensed under the Shoddy Language License 1.0.0 (PolyForm Noncommercial
// License 1.0.0 with Additional Use Grant). See the LICENSE file in the
// project root for full terms.

using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// machines/plotter.shoddy, entirely headless: each chart drawn into a
/// fake scribbler buffer, verified by reading pixels back — a bar
/// interior holds its palette colour, a gap stays white, an axis pixel
/// is black, a pie wedge flood-filled correctly. Expected coordinates
/// are computed from the machine's published geometry (margins 46/12/
/// 14/28, ScaleTo rounding). Joins "golden" for the same reasons as
/// ScribblerTests: process-global registry state and stderr capture.
/// </summary>
[Collection("golden")]
public class PlotterTests
{
    static readonly string Root = RepoRoot.Dir;

    // Palette colours the assertions look for.
    const string Blue = "Array(31, 119, 180)";
    const string Orange = "Array(255, 127, 14)";
    const string Red = "Array(214, 39, 40)";
    const string BoxFill = "Array(174, 199, 232)";
    const string White = "Array(255, 255, 255)";
    const string Black = "Array(0, 0, 0)";

    [Fact]
    public void HistogramBarsAxesAndBackground()
    {
        // 200x150, data {0,0,0,5} in 2 bins → counts {3,1}. Plot area
        // x 46..187, y 14..121. Bar 1 (full height) covers cols 46..116;
        // bar 2 tops out at y=85. The x-axis line at y=121 survives the
        // bars (they fill down to y=120), and the far corner stays white.
        string output = RunPlotter(
            "    Let h = Histogram(NewPlot(200, 150), { 0, 0, 0, 5 }, 2)",
            "    Print(ScribblerGetPixel(h, 60, 50))",     // bar 1 interior
            "    Print(ScribblerGetPixel(h, 150, 100))",   // bar 2 interior
            "    Print(ScribblerGetPixel(h, 150, 50))",    // above bar 2
            "    Print(ScribblerGetPixel(h, 100, 121))",   // x-axis
            "    Print(ScribblerGetPixel(h, 5, 5))");      // margin corner
        Assert.Equal(string.Join('\n', Blue, Blue, White, Black, White, ""), output);
    }

    [Fact]
    public void BarChartBarsGapAndPalette()
    {
        // 200x150, values {2,1}: slot 71, pad 11. Bar 1 cols 57..105 at
        // full height; bar 2 cols 128..176 from y=68 down, in the second
        // palette colour; the gap between them stays white.
        string output = RunPlotter(
            "    Let h = BarChart(NewPlot(200, 150), { \"A\", \"B\" }, { 2, 1 })",
            "    Print(ScribblerGetPixel(h, 80, 60))",     // bar 1 interior
            "    Print(ScribblerGetPixel(h, 150, 100))",   // bar 2 interior
            "    Print(ScribblerGetPixel(h, 150, 50))",    // above bar 2
            "    Print(ScribblerGetPixel(h, 115, 100))");  // the gap
        Assert.Equal(string.Join('\n', Blue, Orange, White, White, ""), output);
    }

    [Fact]
    public void PieChartWedgesAndLegend()
    {
        // 200x200, two equal slices: centre (76,100), radius 64, boundary
        // diameter vertical. Slice 1 (clockwise from 12 o'clock) is the
        // right half, seeded at (108,100); slice 2 the left, at (44,100).
        // Legend swatches sit at x=144, rows 14.. and 28.. .
        string output = RunPlotter(
            "    Let h = PieChart(NewPlot(200, 200), { \"L\", \"R\" }, { 1, 1 })",
            "    Print(ScribblerGetPixel(h, 108, 100))",   // right wedge
            "    Print(ScribblerGetPixel(h, 44, 100))",    // left wedge
            "    Print(ScribblerGetPixel(h, 146, 16))",    // swatch 1
            "    Print(ScribblerGetPixel(h, 146, 30))",    // swatch 2
            "    Print(ScribblerGetPixel(h, 5, 5))");      // outside
        Assert.Equal(string.Join('\n', Blue, Orange, Blue, Orange, White, ""), output);
    }

    [Fact]
    public void BoxPlotBoxMedianWhiskerOutlier()
    {
        // 200x150, data {1,2,3,4,100}: axis spans 1..100, so the box
        // (Q1=2 → x47, Q3=4 → x50) is narrow, median at x49, and 100 is
        // an outlier dotted red at x187 on the midline y=68. The left
        // whisker reaches the minimum at x46.
        string output = RunPlotter(
            "    Let h = BoxPlot(NewPlot(200, 150), { 1, 2, 3, 4, 100 })",
            "    Print(ScribblerGetPixel(h, 48, 60))",     // box interior
            "    Print(ScribblerGetPixel(h, 49, 60))",     // median line
            "    Print(ScribblerGetPixel(h, 187, 68))",    // outlier dot
            "    Print(ScribblerGetPixel(h, 46, 68))",     // whisker cap
            "    Print(ScribblerGetPixel(h, 120, 68))");   // fence gap
        Assert.Equal(string.Join('\n', BoxFill, Black, Red, Black, White, ""), output);
    }

    [Fact]
    public void ScatterPlotPointsAndAxes()
    {
        // 200x150, points (0,0) and (10,10): the data corners land on the
        // plot-area corners (46,121) and (187,14) as radius-2 dots.
        string output = RunPlotter(
            "    Let h = ScatterPlot(NewPlot(200, 150), { 0, 10 }, { 0, 10 })",
            "    Print(ScribblerGetPixel(h, 46, 121))",    // point (0,0)
            "    Print(ScribblerGetPixel(h, 187, 14))",    // point (10,10)
            "    Print(ScribblerGetPixel(h, 100, 70))",    // empty middle
            "    Print(ScribblerGetPixel(h, 100, 121))");  // x-axis
        Assert.Equal(string.Join('\n', Blue, Blue, White, Black, ""), output);
    }

    [Fact]
    public void ChartGuardsAbort()
    {
        Assert.Contains("PIECHART: VALUES MUST BE POSITIVE",
            RunPlotterError("    Let h = PieChart(NewPlot(100, 100), { \"A\", \"B\" }, { 1, 0 })"));
        Assert.Contains("BARCHART: VALUES MUST BE NON-NEGATIVE",
            RunPlotterError("    Let h = BarChart(NewPlot(100, 100), { \"A\" }, { -1 })"));
        Assert.Contains("HISTOGRAM: NEED AT LEAST ONE BIN",
            RunPlotterError("    Let h = Histogram(NewPlot(100, 100), { 1, 2 }, 0)"));
    }

    [Fact]
    public void PlotterMachineCompilesToDll()
    {
        // Weave plotter with its includes (scribbler → seq, clock;
        // stats → seq, dict) spliced as source — same single-load-context
        // reasoning as TurtleMachineCompilesToDll.
        string ws = MachineWorkspace();
        string sb = Path.Combine(ws, "machines", "plotter.shoddy");
        var machines = new MachineSet();
        var lines = Lexer.ReadProgram(sb, machines.TryResolve);
        var prog = new ShoddyProgram();
        machines.SeedInto(prog);
        Weaver.WeaveMachine(Parser.Parse(lines, prog), sb, machines.Machines);
        Assert.True(File.Exists(Path.Combine(ws, "machines", "Shoddy.Machines.Plotter.dll")));
    }

    // ---- helpers --------------------------------------------------------

    /// <summary>Weave and run a Main body that Includes plotter.shoddy
    /// against the fake window backend (bare buffers, no GL).</summary>
    static string RunPlotter(params string[] body)
    {
        var (output, exit, err) = Execute(body);
        Assert.True(exit == 0, $"expected exit 0, got {exit}: {err}");
        return output;
    }

    /// <summary>Same, but the program must abort; returns stderr.</summary>
    static string RunPlotterError(params string[] body)
    {
        var (_, exit, err) = Execute(body);
        Assert.Equal(1, exit);
        return err;
    }

    static (string Output, int Exit, string Err) Execute(string[] body)
    {
        string ws = MachineWorkspace();
        string sb = Path.Combine(ws, "prog.shoddy");
        File.WriteAllText(sb, "Include \"machines/plotter.shoddy\"\n\nDef Main()\n"
                              + string.Join('\n', body) + "\n");

        ScribblerRegistry.CreateScribbler = (w, h) => new ScribblerHandle { Width = w, Height = h, Pixels = new byte[w * h * 4] };
        ScribblerRegistry.OpenCount = 0;
        var err = new StringWriter();
        TextWriter oldErr = Console.Error;
        Console.SetError(err);
        try
        {
            var machines = new MachineSet();
            var lines = Lexer.ReadProgram(sb, machines.TryResolve);
            var prog = new ShoddyProgram();
            machines.SeedInto(prog);
            Parser.Parse(lines, prog);
            var output = new StringWriter();
            int exit = Weaver.Execute(prog, machines.Machines, output, TextReader.Null, Array.Empty<string>());
            return (output.ToString(), exit, err.ToString());
        }
        finally
        {
            Console.SetError(oldErr);
            ScribblerRegistry.CreateScribbler = null;
            ScribblerRegistry.OpenCount = 0;
        }
    }

    static string MachineWorkspace()
    {
        string ws = Path.Combine(Path.GetTempPath(), "shoddy-plotter", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(ws, "machines"));
        foreach (string f in Directory.GetFiles(Path.Combine(Root, "machines"), "*.shoddy"))
            File.Copy(f, Path.Combine(ws, "machines", Path.GetFileName(f)));
        return ws;
    }
}
