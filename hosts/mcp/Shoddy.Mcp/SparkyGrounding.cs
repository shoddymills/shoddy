// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Shoddy.Mcp;

/// <summary>
/// Grounding, from two sources that are each authoritative about a
/// different thing — and NOTHING IS COPIED BETWEEN THEM.
///
/// WORD FACTS COME FROM THE RUNNING DICTIONARY. RckHelp answers a word's
/// name, stack effect, description and touches-the-world from the state
/// Sparky is actually running, and no word table is authored, generated
/// or cached anywhere in this project. That is not a convenience: a
/// copied detail is a detail that goes stale, and a grounding server
/// that lies about a word is worse than no grounding server.
///
/// SUBJECT PROSE COMES FROM docs/machines/*.html, embedded at build time
/// so the server carries its own teaching material. What is taken from a
/// page is its INTRODUCTION and its USER'S GUIDE — never its Word
/// Reference table, which is hand-maintained and is the one part of a
/// page the dictionary can contradict.
///
/// THE LANGUAGE DOCUMENTS BELOW ARE AUTHORED HERE, and that is the right
/// place for them: they describe the SHAPE of Shoddy and the shape of
/// this server, neither of which is a fact about a word. A model that
/// has read a thousand Forths will otherwise assume things that are not
/// true here, fluently, and never think to check.
/// </summary>
public static class SparkyGrounding
{
    public const string ShapeUri = "sparky://grounding/shape";
    public const string ReckonerUri = "sparky://grounding/reckoner";
    public const string SurfaceUri = "sparky://grounding/surface";
    public const string DictionaryUri = "sparky://dictionary";
    public const string SubjectPrefix = "sparky://machines/";

    public const string Shape = """
# The shape of Shoddy — what does not change

Shoddy is a small, purely functional BASIC that compiles to .NET. It
LOOKS like BASIC and does not behave like it. Do not pattern-match to
VB.NET, VBA, QBasic, Python or F#.

- **Nothing mutates.** `Let` binds a name once. There is no assignment,
  no `x = x + 1`, no `goto`. Updating a record returns a new one.
- **There is no `For`, `While`, `Do`, `Next` or `Wend`.** Iteration is
  recursion, or `Map` / `Filter` / `Fold` / `Each` / `Times`. `Fold` is
  the workhorse: it threads a value through a sequence.
- **Indentation is the structure.** No `End If`, no `End Function`, no
  braces, no semicolons.
- **A Def's last expression is its result.** There is no `Return`.
- **Names are case-insensitive.** `total`, `Total` and `TOTAL` are one
  word.
- **Booleans are not numbers.** `If n Then` is an error; write
  `If n <> 0 Then`.
- **`Number` is an IEEE double and the only numeric type.** There are no
  integers and NO BITWISE OPERATORS — bit work is a machine's words.
- **Sequences are 1-based** and ranges include both ends.
- **There are no exceptions.** `Error(msg)` aborts. Anything a caller
  should handle comes back as a VALUE: `Option` (`Some`/`None`) and
  `Result` (`Ok`/`Err`).
- **Records compare structurally with `=`. Lists compare by identity**,
  so two separately built lists of the same items are not equal.
- **Self-tail-recursion compiles to a loop**; mutual recursion does not.
- **Arrays are fixed-length with O(1) `Nth`; lists are cons cells.**
  Index into arrays, recurse over lists.
- **Operators are infix**: `a Mod b`, never `Mod(a, b)`.
- **A bare name in argument position is passed as a function**, not
  called: `Map(xs, Double)`.

You are not writing Shoddy here — you are typing at a reckoner prompt,
which is RPN. But the dictionary is built out of this language, and the
answers you get back have its shape: no mutation, values not exceptions,
1-based everything.
""";

    public const string Reckoner = """
# The reckoner — how a line works

Lines are RPN. Numbers and strings go on the stack; words work on what
is there. `3 4 +` leaves 7. The stack is shown after every line, top
value labelled `x`, then `y`, `z`, `t` beneath it.

- **A list is `{ 1 2 3 }`.** A program (a quotation) is `[ DUP * ]`.
  `{ 1 2 3 } [ 2 * ] MAP` answers `{ 2 4 6 }`.
- **A string is double-quoted**, and prints with its quotes on the stack
  and without them under `PRINT`.
- **You define a word with `: NAME ... ;`** — for example
  `: VAT DUP 0.2 * + ;`. It may span several lines; the definition is
  not run until it closes.
- **THE LINE IS THE TRANSACTION.** A line either takes effect whole or
  is refused whole, and a refusal leaves the stack exactly as it was.
  Refusals start with `?:`.
- **NOTHING KEYABLE ABORTS.** There is no line you can type that ends
  the session. If something is wrong you get a refusal and another
  chance, so trying a thing is cheap — try it rather than asking.
- **`UNDO` restores a stack, not the world.** A file already written is
  still written; a line already drawn is still drawn; a `PRINT` already
  printed cannot be unprinted.

## Two facts that surprise everything that has seen a Forth

- **A USER WORD TAKES EXACTLY ONE CELL.** Not "as many as it pops" — one.
  If a word needs a second argument, bank it in a register first with
  `STO` and read it inside with `RCL`. Writing a two-argument definition
  is the single most common mistake here and it will be refused.
- **RECURSION IS IMPOSSIBLE, ON PURPOSE.** A definition is validated
  against the dictionary as it stands, so its body may name only words
  that ALREADY EXIST — never the name being defined. Loop with `TIMES`,
  `MAP`, `FILTER` or `FOLD` instead.

## Reading a stack effect

`HELP` answers one. `STO  ( x name -- )` means the word takes two cells
and leaves none, and the ORDER is the order you push them: the value
first, then the name. `RCL  ( name -- x )` takes one and leaves one.
`PLOTHISTOGRAM  ( plot xs bins -- )` takes three, in that order.

Argument order is the mistake you will make most often, and reading the
effect line is the whole cure.

## Never invent a word

`WORDS` lists everything, grouped by the seed it came from. `HELP NAME`
gives a word's exact stack effect and description. `VIEW NAME` shows a
definition you made. Use the `help` and `words` tools rather than
guessing a word exists — every word carries its own effect and
description, so there is never a reason to invent one.

## When a line is refused, the refusal tells you the fix

This is why trying is cheap and why this briefing can be short: the
engine knows far more about its own words than any prompt can carry, and
it says so. Real refusals, verbatim:

    12500 13100 11900 MEAN     ?: MEAN needs a LIST, got NUMBER
    { 1 2 3 4 } 2 2 MAT        ?: MAT needs a NUMBER, got LIST
    640 480 PLOTOPEN           ?: PLOTOPEN needs a width, a height and a name
    { 1 2 2 3 } PLOTHISTOGRAM  ?: PLOTHISTOGRAM needs 3, the stack holds 2
    "3" 4 +                    ?: + is not defined for STRING and NUMBER

Every one of those names what was wrong. Send the corrected line; the
stack is exactly as it was. Do NOT apologise to the user for a refused
line and do not narrate it as a failure — it is how you find the right
line, and it costs nothing.

## Where the calculator CANNOT catch you

Everything above is caught. These are not: the line is accepted, an
answer appears, and it is the wrong answer. There are four of them, and
they are the reason to read this section rather than skim it.

**Trig is in RADIANS unless you say otherwise.**

    90 SIN            x: 0.8939966636      radians, and almost certainly not the question
    DEG   90 SIN      x: 1                 degrees

Set `DEG` before any trigonometry a person asked for in degrees, and say
in your answer which mode you used. `RAD` puts it back.

**Sample and population statistics are different words.**

    { 2 4 4 4 5 5 7 9 } STDDEV     x: 2.138089935    sample, divides by n-1
    { 2 4 4 4 5 5 7 9 } STDDEVP    x: 2              population, divides by n

Same for `VAR` and `VARP`. Both are right answers to different
questions; nothing will tell you that you chose the wrong one. Decide
which the student meant, and say which you used.

**`FIX` changes what is SHOWN, not what is held.**

    2 FIX   3.14159265      x: 3.14

The stack still holds every digit; `STD` shows them again. Never read a
`FIX`ed display back as the value.

**Numbers are IEEE doubles, so money is its own kind.**

    0.1 0.2 +               x: 0.3
    0.1 0.2 + 0.3 =         x: False

The display rounds; the comparison does not. For currency use `MONEY`
and its words, which are exact and which refuse a bare number so you
cannot mix the two by accident.

## A worked line or two

    { 12500 13100 11900 } MEAN
    x: 12500

A word needing two arguments, banking one first:

    0.2 "rate" STO
    : WITHVAT DUP "rate" RCL * + ;
    250 WITHVAT
    x: 300

A matrix, built rows-and-columns first:

    2 2 { 1 2 3 4 } MAT LINDET
    x: -2

A chart, which arrives as a picture with the third line:

    640 480 "p" PLOTOPEN
    "p" { 1 2 2 3 3 3 } 4 PLOTHISTOGRAM
    "p" PLOTBLIT
""";

    public const string Surface = """
# What this server is, and what it is not

Sparky hands you the Halifax dictionary — the whole of the reckoner's
standard library at an RPN prompt — as callable tools, plus the
grounding to use it correctly. Statistics, matrices, linear algebra,
linear and integer programming, finance, symbolic algebra, neural nets,
regular expressions, sparse matrices, CSV/JSON/XML/HTML, indexed files,
number bases and bit work, charts and turtle graphics.

**Compute the answer. Do not estimate it.** That is the whole point of
this server: you have a calculator that shows its working, and a student
is better served by `{ 12500 13100 11900 } MEAN` than by your arithmetic.

## What is in the dictionary

Seven hundred words, grouped by the seed each came from. This is the
index; `words` gives the full list and `help` gives any one word exactly.

    core          arithmetic, comparison, stack shuffling, registers, UNDO
                  and REDO, TRACE, the tape, angle and display modes, and
                  the combinators MAP FILTER FOLD TIMES IFT IFTE
    builtin       string slicing and character codes, number parsing,
                  arrays, whole-file read and write, the clock
    seed-math     logs to a base, hypotenuse, distance, clamp, lerp, remap
    seed-stats    mean, median, spread, quantiles, correlation, normal, t
    seed-seq      ranges, length, reverse, concat, first and rest, sorting
    seed-str      case, trim, split, join, fixed-decimal text
    seed-money    exact decimal money, splitting a sum without losing a penny
    seed-matrix   building matrices, identity, transpose, multiply, dot
    seed-dict     string-keyed dictionaries
    seed-file     line-oriented files, and whether one exists
    seed-clock    timestamps, monotonic ticks, elapsed time, durations
    seed-random   seeded generation, ranges, shuffling, sampling
    seed-csv      reading and writing CSV, columns, headers, filtering
    seed-bool     number bases, bit operations, masks, fields, logic gates
    seed-shaker   reversible obfuscation of a list, with a tamper checksum
    seed-json     parse, render, pretty-print, load and save JSON
    seed-xml      the same for XML
    seed-html     the same for HTML
    seed-simplex  linear programs, and MPS files
    seed-lin      linear algebra: determinants, inverses, decompositions,
                  eigenvalues, solving
    seed-eng      angles, complex numbers, statistics, number theory,
                  polynomials, calculus, units and physical constants
    seed-fin      interest, annuities, loans, NPV and IRR, depreciation,
                  day counts, bonds
    seed-alg      symbolic algebra: simplify, expand, factor, solve, and
                  first-order differential equations
    seed-neural   small neural networks: build, train, predict, score, save
    seed-recio    fixed-layout binary record files
    seed-isam     indexed files: keyed lookup, ranges, insert and update
    seed-scribbler a pixel canvas to draw on
    seed-net      outbound HTTP fetches
    seed-turtle   turtle graphics
    seed-plotter  charts: histogram, scatter, box, bar, pie
    seed-https    HTTPS fetches, and reading a response apart
    seed-regex    regular expressions: test, find, groups, replace, split
    seed-sparse   sparse matrices, stored by column
    seed-mip      mixed-integer programs
    seed-terminal PRINT
    shell         SAVE, LOAD, TAPESAVE and RESET, called as tools

Ask `subject` for any of those and you get a teaching card: what the
machine is for, worked examples that run at this prompt, and its live
word list.

## Resources are opened under a NAME, not held on the stack

A canvas, a plotter, a turtle, a record file and an indexed file are all
opened under a name you choose, and every later word takes that name:

    640 480 "p" PLOTOPEN
    "p" { 1 2 2 3 } 4 PLOTHISTOGRAM
    "p" PLOTBLIT

Nothing is pushed by the open. `BOUND` lists what is open, `CLOSE` shuts
one by name, `CLOSEALL` shuts them all. This is why `UNDO` cannot leave a
resource stranded: what is open is not on the stack in the first place.

## What is deliberately absent

| Not here | Why |
|---|---|
| sound | the server does not control the host's audio and cannot know if anything is listening |
| key handling | there is no keystroke source, so the words that classify keys classify nothing |
| terminal escape sequences | there is no screen; a client receiving them in JSON is worse off than one told the word is absent |
| any way to read input | no seed registers `INPUT`, `INPUTLINE` or `INKEY` |
| any listening socket | `NETGET` and `NETREQUEST` are the whole of the network surface — there is no listen, accept or blocking-wait word |

These are absent from the DICTIONARY, not merely withheld: ask `HELP`
about one of their words and you are told it is not a word here.

## Four things that catch a text-only caller harder than a person

- **`UNDO` does not un-draw.** After `TURTLEFORWARD`, `UNDO` restores
  the stack and leaves the line on the canvas.
- **`UNDO` does not un-write, either.** A word that has already written
  a file has still written it.
- **`abandon` costs a turtle more than a chart.** A turtle's position,
  heading, pen and colour live in the session; a plotter has no state of
  its own at all, so a chart is one word to redraw and a turtle drawing
  is not.
- **`CLOSE` ends the drawing.** After it the dictionary cannot reach the
  surface — though this server keeps the last captured frame, so you can
  still be shown what was made.

## Pictures

Charts and turtle drawings go into a pixel buffer with no window. A line
ending in `PLOTBLIT`, `SCRIBBLIT` or `TURTLEBLIT` carries its picture
back in the same answer. A drawing you forgot to blit is not lost: ask
the `canvas` tool, which reads the buffer as it stands.

## Persistence

`SAVE`, `LOAD`, `TAPESAVE` and `RESET` are the server's words, not the
dictionary's — call the tools of those names. Typing them in a line gets
you a refusal saying so.

Files live under one root the server owns. A plain name like
`"mine.sparky"` lands in it, and so does `"saved/mine.sparky"`. A path
that tries to climb out of it — `"../elsewhere"`, or an absolute path
somewhere else — is refused before anything is touched, so use plain
names.
""";

    // ---- subject cards ----

    /// <summary>The embedded page for a machine or seed, as prose a
    /// lesson can be built from: the introduction and the User's Guide,
    /// never the Word Reference. The live word list is added by the
    /// caller, which has a dictionary and this does not.</summary>
    public static string? Card(string name)
    {
        string? html = Page(name);
        return html is null ? null : Prose(html);
    }

    /// <summary>Names that have a page, so `machines` can list what
    /// `subject` will answer for.</summary>
    public static IEnumerable<string> Pages() =>
        Asm.GetManifestResourceNames()
           .Where(n => n.StartsWith("subject.", StringComparison.Ordinal))
           .Select(n => n["subject.".Length..^".html".Length])
           .OrderBy(n => n, StringComparer.Ordinal);

    static readonly Assembly Asm = typeof(SparkyGrounding).Assembly;

    /// <summary>`alg`, `seedalg` and the group name `seed-alg` all reach
    /// the same page a caller means, because a caller reading a WORDS
    /// heading has the third spelling and nothing else.</summary>
    static string? Page(string name)
    {
        string plain = name.Trim().ToLowerInvariant().Replace("-", "");
        foreach (string candidate in new[] { plain, "seed" + plain })
        {
            using Stream? s = Asm.GetManifestResourceStream("subject." + candidate + ".html");
            if (s is null) continue;
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        return null;
    }

    /// <summary>The sections worth teaching from. `words`, `usedby` and
    /// `uses` are skipped on purpose: the first is a hand-maintained
    /// table the dictionary can contradict, and the other two are
    /// bookkeeping about the tree rather than about the subject.</summary>
    static readonly string[] Wanted = { "summary", "why", "guide", "cannot", "story" };

    static string Prose(string html)
    {
        var sb = new StringBuilder();
        Match h1 = Regex.Match(html, @"<h1[^>]*>(.*?)</h1>", RegexOptions.Singleline);
        if (h1.Success) sb.Append("# ").Append(Text(h1.Groups[1].Value)).Append("\n\n");
        Match kicker = Regex.Match(html, @"<p class=""kicker"">(.*?)</p>", RegexOptions.Singleline);
        if (kicker.Success) sb.Append(Text(kicker.Groups[1].Value)).Append("\n\n");

        // Every h2 and the body that follows it, up to the next h2.
        MatchCollection heads = Regex.Matches(html,
            @"<h2 id=""([a-z0-9-]+)""[^>]*>(.*?)</h2>", RegexOptions.Singleline);
        for (int i = 0; i < heads.Count; i++)
        {
            string id = heads[i].Groups[1].Value;
            if (!Wanted.Contains(id)) continue;
            int from = heads[i].Index + heads[i].Length;
            int to = i + 1 < heads.Count ? heads[i + 1].Index : html.Length;
            sb.Append("## ").Append(Text(heads[i].Groups[2].Value)).Append("\n\n");
            sb.Append(Body(html[from..to])).Append('\n');
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>HTML to something a model reads as prose: code blocks
    /// fenced, list items bulleted, table rows piped, everything else
    /// stripped. Deliberately small — the pages are hand-written and
    /// regular, and a full parser here would be a dependency bought to
    /// solve a problem nobody has.</summary>
    static string Body(string html)
    {
        string s = Regex.Replace(html, @"<pre[^>]*><code[^>]*>(.*?)</code></pre>",
            m => "\n```\n" + Text(m.Groups[1].Value) + "\n```\n", RegexOptions.Singleline);
        s = Regex.Replace(s, @"</t[dh]>\s*<t[dh][^>]*>", " | ");
        s = Regex.Replace(s, @"<tr[^>]*>", "\n| ");
        s = Regex.Replace(s, @"</tr>", " |");
        s = Regex.Replace(s, @"<li[^>]*>", "\n- ");
        s = Regex.Replace(s, @"</(p|h3|h4|ul|ol|table|div)>", "\n\n");
        s = Regex.Replace(s, @"<h3[^>]*>", "\n### ");
        return Regex.Replace(Text(s), @"\n{3,}", "\n\n").Trim();
    }

    /// <summary>Tags off, entities decoded. WebUtility handles the named
    /// entities the pages actually use (&amp;middot;, &amp;mdash;,
    /// &amp;larr;, &amp;nbsp;) as well as the four that matter for
    /// code.</summary>
    static string Text(string html) =>
        WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", ""))
                  .Replace(' ', ' ')   // &nbsp;, which reads as a broken word
                  .Trim();
}
