# Shoddy Stats — Implementation Prompts

> **Status: implemented** (2026-07-20). Both prompts landed: the four
> builtins with C# tests (`SpecialTests.cs`), `machines/stats.shoddy`,
> `Shuffle`/`Sample` in random.shoddy, and all the test/doc fixups below.
> Decisions taken during implementation are noted inline.

Two prompts, run in order. Prompt 1 is runtime (C#) work; Prompt 2 is machine
(Shoddy) work and depends on Prompt 1 being done. The item tables at the bottom
are the shared spec both prompts refer to.

Out of scope for both: visualizations (histogram, bar, box, scatter). Those
become a separate `statplot.shoddy` machine after the turtle graphics machine
lands, layered on turtle/scribbler. The visualization table stays here so the
list is complete.

---

## Prompt 1 — Runtime: special functions and sort

Add four builtins to the core runtime (`Engine.Builtin()` and
`Engine.BuiltinWords`, per the usual pattern). Note that `System.Math` has no
special functions — erf, gamma, and incomplete beta/gamma are not in the BCL —
so these are small, well-known numerical implementations written in C#, which
is the right place for them: math.shoddy's own principle is that the runtime
supplies the primitives that need native precision, and everything derivable
stays in Shoddy.

| Builtin | What it is | Why it must be native |
|---------|-----------|----------------------|
| `ERF` | error function | normal probabilities; not derivable from EXP/LOG with acceptable accuracy |
| `GAMMAP` | regularized lower incomplete gamma P(a, x) | chi-square CDF |
| `BETAI` | regularized incomplete beta I_x(a, b) | t and F CDFs (t-tests, ANOVA) |
| `SORT` | ascending sort, polymorphic over List/Array like MAP | a good sort cannot be written in Shoddy: O(n) Nth, deep non-tail recursion, and the current quicksort's First-pivot makes sorted input its worst case |

Also in this prompt, because it must land with `SORT`:

- Delete the `Sort` Def from `machines/seq.shoddy` — a Def silently shadows a
  builtin of the same name, so leaving it would hide the new builtin.
- Check every new builtin name against existing Def, Type, and record field
  names (a builtin outranks a field accessor).
- C# tests for the three special functions against known values — these are
  exactly the functions where a sign error hides quietly — and for SORT
  (including already-sorted, reversed, duplicate, and empty inputs). Any test
  that redirects `Console.Error` must join the xUnit `[Collection("golden")]`
  that serializes the other stderr-redirecting classes, or parallel test
  classes race on the redirect.
- Add the new words everywhere the builtin word list is duplicated: QUICKREF,
  SPEC, the vscode-shoddy syntax highlighter, and the "authoritative" list in
  `curriculum/agent-context.md` §16. Those lists were already stale (missing
  the Scribbler words and several math builtins: Sgn, Fix, Atn2, Asin, Acos,
  Log10, Wrap, Seed, Ticks, Sleep, Clock, Args) — true them up while there.

---

## Prompt 2 — Machines: stats.shoddy and friends

Create `machines/stats.shoddy` implementing the stat-function tables below,
pure Shoddy, `Include`-ing seq and dict. (Not math: nothing in the spec needs
it — Exp/Log/^ are builtins — and every extra Include ripples into
MachineTests' machine count.) Structure and conventions:

- **Move aggregation out of seq.shoddy.** `Sum`, `Product`, `Maximum`,
  `Minimum`, `Average` leave seq (seq keeps structure only: predicates,
  searching, slicing, zipping) and land in stats as the descriptive
  foundation. Stats-facing name is `Mean`, with `Average` kept as an alias.
  Fix up the callers: `tst/gradebook.shoddy` gains an Include; libtest's
  aggregation asserts move to a stats test section.
- **Sample vs. population:** `Var`/`StdDev` = sample (n−1); `VarP`/`StdDevP`
  = population.
- **Percentiles:** linear interpolation (R type 7 / Excel PERCENTILE.INC),
  documented in the file.
- **Mode:** `Modes` returns a `List Of Number` (all modes), never silently
  picks one of a tie.
- **Variance is two-pass** (mean first, then squared deviations) — stable and
  natural in pure-functional style.
- **Compound results are records**, in the style of seq's `Pair`: a `Fit`
  (slope, intercept, r²) for regression, a `TestResult` (statistic, df,
  p-value) for the tests. P-values are two-sided; the two-sample t-test is
  the pooled/equal-variance textbook form (R's `var.equal = TRUE`).
- **CDFs derive from the Prompt 1 builtins**: normal from `ERF`, chi-square
  from `GAMMAP`, t and F from `BETAI`. Real p-values throughout — no
  critical-value tables.
- **Frequency tables and Modes** build on dict.shoddy's association lists.

Also in this prompt:

- `Shuffle` and `Sample` go in `machines/random.shoddy`, not stats — they are
  Rnd-based and impure, and random.shoddy owns that edge. Keep random.shoddy
  dependency-free (a local `DropAt` helper instead of seq). Their tests are
  contract checks in `tst/randomtest.shoddy` — nondeterministic output can't
  be a golden file. Note lists compare by identity, so multiset assertions go
  through `ToArray`.
- Known-answer Shoddy tests in the libtest pattern: small canonical datasets
  with values checked against an outside authority (R or scipy).
- **Update `MachineTests.cs`**: `BuildOrder` gains `"stats"` (after dict),
  the machine-count assert goes 8 → 9, and the `SUM` external-def assert now
  proves stats (not seq) arrived — add an `ANY` assert for seq itself.
- **Regenerate `tst/golden/libtest.out`** — moving the aggregation asserts
  and adding the stats section changes the golden bytes. `gradebook.out` is
  unchanged (gradebook only gains an Include).
- **Docs beyond the word lists**: `doc/machines/seq.html` loses its
  aggregation/sorting sections, `doc/machines/stats.html` is new,
  `index.html` gains its row, `random.html` gains Shuffle/Sample; in
  `curriculum/`, agent-context.md's §9/§12 tables move with the code and the
  lesson-guide smoke test must Include stats.shoddy (its `Sum` check breaks
  otherwise).
- Document the intended scale in the stats.shoddy header: lists are linked
  lists, so this is classroom-sized data, not bulk analytics.
- One naming trap from the spec tables: "Range" (max − min) must not be a Def
  named `Range` — that would shadow the `Range(a, b)` builtin. It landed as
  `RangeOf`.

---

## The spec: what to implement

### Stat functions — descriptive

| Item | Tier | Notes |
|------|------|-------|
| Mean | Core | Average |
| Weighted mean | Extended | |
| Geometric mean | Extended | Rates, ratios, growth |
| Harmonic mean | Extended | |
| Median | Core | |
| Mode | Core | Optional but common |
| Minimum and maximum | Core | |
| Range | Core | max − min |
| Variance | Core | Sample (n−1) and population (n) variants |
| Standard deviation | Core | Same sample/population variants |
| Standard error of the mean | Extended | |
| Median absolute deviation (MAD) | Extended | Robust spread |
| Skewness | Extended | Shape of the distribution |
| Kurtosis | Extended | Shape of the distribution |
| Percentiles and quartiles | Core | Especially 25th, 50th, 75th |
| Interquartile range (IQR) | Extended | Standard spread measure; drives box-plot outlier fences |
| Outlier detection | Extended | 1.5×IQR rule, z-score threshold |
| Frequency tables | Core | For categorical data |
| Relative frequencies and percentages | Core | |
| Cumulative frequencies / empirical CDF | Extended | |
| Covariance | Extended | Primitive underlying correlation |
| Correlation | Core | Usually Pearson |
| Spearman rank correlation | Extended | Robust/nonparametric complement to Pearson |
| Linear regression | Core | Simple: one x, one y |
| Z-scores | Core | |

### Stat functions — inferential

| Item | Tier | Notes |
|------|------|-------|
| Normal distribution basics | Core | CDF via ERF |
| One-sample z-test or t-test for a mean | Core | |
| Two-sample t-test | Core | Independent groups |
| Paired t-test | Core | Before/after |
| One-way ANOVA | Extended | Comparing 3+ groups |
| Chi-square test for independence | Core | In contingency tables |
| Chi-square goodness-of-fit test | Extended | |
| Two-proportion z-test | Extended | |
| Confidence intervals for a mean | Core | |
| Confidence intervals for a proportion | Core | |
| Effect sizes | Extended | Cohen's d, r² |

### Utilities

| Item | Tier | Notes |
|------|------|-------|
| Random sampling / shuffling | Extended | Impure; lives in random.shoddy |

### Visualizations — deferred to statplot.shoddy (after turtle)

| Item | Tier | Notes |
|------|------|-------|
| Histogram | Core | |
| Bar chart | Core | |
| Pie chart | Core | Often taught, even if not ideal |
| Box plot | Core | For spread and outliers |
| Scatter plot | Core | |
