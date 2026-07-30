// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Shoddy.Compiler;
using Shoddy.Devil;
using Shoddy.Runtime;

namespace Shoddy.Tests;

/// <summary>
/// The special-function builtins (ERF, GAMMAP, BETAI, TANH) and SORT.
/// The special functions back the stats machine's CDFs and the neural
/// machine's activation and are exactly the kind of numerics where a
/// sign error hides quietly, so they are pinned against known values
/// and closed-form identities. SORT is checked on
/// the inputs a naive quicksort gets wrong: already-sorted, reversed,
/// duplicates, empty — plus kind preservation, List in, List out.
/// (In "golden" because RunSrcError redirects Console.Error, which
/// must not interleave with the other classes that do the same.)
/// </summary>
[Collection("golden")]
public class SpecialTests
{
    // ---- ERF -------------------------------------------------------------

    [Fact]
    public void ErfKnownValues()
    {
        // Reference values from scipy.special.erf.
        string output = RunSrc(string.Join('\n',
            "Def Main()",
            "    Print(Erf(0))",
            "    Print(Abs(Erf(0.5) - 0.5204998778130465) < 0.000000000001)",
            "    Print(Abs(Erf(1) - 0.8427007929497149) < 0.000000000001)",
            "    Print(Abs(Erf(2) - 0.9953222650189527) < 0.000000000001)",
            "    Print(Abs(Erf(3) - 0.9999779095030014) < 0.000000000001)",
            "    Print(Erf(-1) + Erf(1))",     // odd function, exactly
            ""));
        Assert.Equal("0\nTrue\nTrue\nTrue\nTrue\n0\n", output);
    }

    // ---- GAMMAP ----------------------------------------------------------

    [Fact]
    public void GammaPIdentities()
    {
        // P(1, x) = 1 - e^-x; P(1/2, x) = erf(sqrt x); P(a, 0) = 0; and the
        // chi-square anchor P(1/2, 3.8414588.../2) = 0.95 (the 95th
        // percentile of chi-square with 1 df).
        string output = RunSrc(string.Join('\n',
            "Def Main()",
            "    Print(Abs(GammaP(1, 2) - 0.8646647167633873) < 0.000000000001)",
            "    Print(Abs(GammaP(0.5, 4) - Erf(2)) < 0.000000000001)",
            "    Print(GammaP(3, 0))",
            "    Print(Abs(GammaP(0.5, 3.841458820694124 / 2) - 0.95) < 0.000000001)",
            ""));
        Assert.Equal("True\nTrue\n0\nTrue\n", output);
    }

    // ---- BETAI -----------------------------------------------------------

    [Fact]
    public void BetaIClosedForms()
    {
        // I_x(1,1) = x; I_x(2,2) = x^2(3-2x); I_x(1,b) = 1-(1-x)^b;
        // I_x(a,1) = x^a; I_.5(a,a) = 1/2; and the t-distribution anchor:
        // two-sided p at the 0.975 quantile of t(10) is 0.05.
        string output = RunSrc(string.Join('\n',
            "Def Main()",
            "    Print(Abs(BetaI(1, 1, 0.3) - 0.3) < 0.000000000001)",
            "    Print(Abs(BetaI(2, 2, 0.3) - 0.216) < 0.000000000001)",
            "    Print(Abs(BetaI(1, 3, 0.2) - 0.488) < 0.000000000001)",
            "    Print(Abs(BetaI(2, 1, 0.7) - 0.49) < 0.000000000001)",
            "    Print(Abs(BetaI(5, 5, 0.5) - 0.5) < 0.000000000001)",
            "    Let t = 2.228138851986273",
            "    Print(Abs(BetaI(5, 0.5, 10 / (10 + t * t)) - 0.05) < 0.0000001)",
            ""));
        Assert.Equal("True\nTrue\nTrue\nTrue\nTrue\nTrue\n", output);
    }

    [Fact]
    public void SpecialFunctionDomainErrors()
    {
        Assert.Contains("GAMMAP: A must be positive", RunSrcError("Def Main()\n    Print(GammaP(0, 1))\n"));
        Assert.Contains("GAMMAP: X must be non-negative", RunSrcError("Def Main()\n    Print(GammaP(1, -1))\n"));
        Assert.Contains("BETAI: A and B must be positive", RunSrcError("Def Main()\n    Print(BetaI(-1, 1, 0.5))\n"));
        Assert.Contains("BETAI: X outside [0, 1]", RunSrcError("Def Main()\n    Print(BetaI(1, 1, 2))\n"));
    }

    // ---- TANH ------------------------------------------------------------

    [Fact]
    public void TanhKnownValues()
    {
        // Reference value from Math.Tanh / scipy.special.tanh. TANH 500
        // must saturate to exactly 1 — the derived (e^2x-1)/(e^2x+1)
        // form overflows there, which is why this is a builtin.
        string output = RunSrc(string.Join('\n',
            "Def Main()",
            "    Print(Tanh(0))",
            "    Print(Abs(Tanh(1) - 0.76159415595576) < 0.000000000001)",
            "    Print(Tanh(-1) + Tanh(1))",   // odd function, exactly
            "    Print(Tanh(500))",
            "    Print(Tanh(-500))",
            ""));
        Assert.Equal("0\nTrue\n0\n1\n-1\n", output);
    }

    // ---- SORT ------------------------------------------------------------

    [Fact]
    public void SortNumbers()
    {
        string output = RunSrc(string.Join('\n',
            "Def Main()",
            "    Print(Sort({ 3, 1, 4, 1, 5, 9, 2, 6 }))",
            "    Print(Sort({ 1, 2, 3, 4, 5 }))",         // already sorted
            "    Print(Sort({ 5, 4, 3, 2, 1 }))",         // reversed
            "    Print(Sort({ 2, 2, 2 }))",               // duplicates
            "    Print(Sort({ }))",                       // empty
            "    Print(Sort({ 1.5, -2, 0 }))",
            ""));
        Assert.Equal(string.Join('\n',
            "[ 1 1 2 3 4 5 6 9 ]",
            "[ 1 2 3 4 5 ]",
            "[ 1 2 3 4 5 ]",
            "[ 2 2 2 ]",
            "[ ]",
            "[ -2 0 1.5 ]",
            ""), output);
    }

    [Fact]
    public void SortPolymorphicAndStrings()
    {
        // Like MAP, the result has the input's kind: Array in, Array out.
        string output = RunSrc(string.Join('\n',
            "Def Main()",
            "    Print(Sort(ToArray({ 3, 1, 2 })))",
            "    Print(Sort({ \"PEAR\", \"APPLE\", \"FIG\" }))",
            ""));
        Assert.Equal("Array(1, 2, 3)\n[ \"APPLE\" \"FIG\" \"PEAR\" ]\n", output);
    }

    [Fact]
    public void SortRejectsMixedTypes()
    {
        Assert.Contains("SORT expects all NUMBERs or all STRINGs",
                        RunSrcError("Def Main()\n    Print(Sort({ 1, \"A\" }))\n"));
    }

    // ---- helpers --------------------------------------------------------

    static string RunSrc(string src)
    {
        var prog = Parser.Parse(Lexer.ReadProgram(WriteTemp(src)));
        var output = new StringWriter();
        int exit = Weaver.Execute(prog, null, output, TextReader.Null, Array.Empty<string>());
        Assert.True(exit == 0, $"expected exit 0, got {exit}");
        return output.ToString();
    }

    static string RunSrcError(string src)
    {
        var err = new StringWriter();
        TextWriter oldErr = Console.Error;
        Console.SetError(err);
        try
        {
            var prog = Parser.Parse(Lexer.ReadProgram(WriteTemp(src)));
            int exit = Weaver.Execute(prog, null, new StringWriter(), TextReader.Null, Array.Empty<string>());
            Assert.Equal(1, exit);
            return err.ToString();
        }
        finally { Console.SetError(oldErr); }
    }

    static string WriteTemp(string src)
    {
        string dir = Path.Combine(Path.GetTempPath(), "shoddy-special", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string sb = Path.Combine(dir, "prog.shoddy");
        File.WriteAllText(sb, src);
        return sb;
    }
}
