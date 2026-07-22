# Shoddy Neural — Implementation Prompts

> **Status: not started.** Three prompts, run in order. Prompt 1 is runtime
> (C#) work; Prompt 2 is machine (Shoddy) work depending on Prompt 1;
> Prompt 3 is the demographics mill depending on Prompt 2. The reference
> program and its spec are at the bottom.

The goal: a reusable feed-forward neural network machine
(`machines/neural.shoddy`) and a mill (`mills/demographics/`) that ports
the C# demo in `mills/demographics/cs/NeuralRegressionProgram.cs` —
predict income from sex, age, state, and political leaning with an
8-100-1 network, tanh hidden layer, identity output, mini-batch SGD on
MSE loss.

Out of scope for all three: multiple hidden layers, other activations
(sigmoid, ReLU), classification outputs (softmax, cross-entropy),
momentum/Adam. The `Net` record fixes one hidden layer; nothing below
forecloses generalizing later, and nothing below attempts it now.

---

## Prompt 1 — Runtime: the TANH builtin

Add one builtin to the core runtime (`Engine.Builtin()` **and**
`Engine.BuiltinWords`, per the usual pattern):

| Builtin | What it is | Why it must be native |
|---------|-----------|----------------------|
| `TANH` | hyperbolic tangent `( x -- tanh x )` | the hidden-layer activation, executed in the innermost training loop tens of millions of times per run; `Math.Tanh` is one BCL call, exact, and saturates correctly at ±1 — the derived form `(Exp(2x)-1)/(Exp(2x)+1)` costs an `Exp` plus stack traffic per call and overflows for x > ~354 without a hand clamp |

This is math.shoddy's own principle (the runtime supplies the primitives
that need native precision; everything derivable stays in Shoddy) and
the established project precedent: when a mill needs something the
language lacks, extend the runtime rather than degrade the mill — the
`InKey` and `ERF`/`GAMMAP`/`BETAI` cases. Just `TANH`: no `SINH`/`COSH`
until something needs them.

Also in this prompt:

- Name check done in advance: no `TANH` exists anywhere in `src/`, no
  machine `Def`s a `Tanh`. Re-verify against Def, Type, and record field
  names at implementation time (a builtin outranks a field accessor).
- C# known-value tests alongside the ERF/GAMMAP/BETAI tests in
  `SpecialTests.cs`: `TANH 0` = 0, `TANH 1` ≈ 0.76159415595576,
  odd symmetry, `TANH 500` = 1 exactly (saturation, no overflow).
- Add the word everywhere the builtin word list is duplicated: QUICKREF,
  SPEC, the vscode-shoddy syntax highlighter, the "authoritative" list
  in `curriculum/agent-context.md` §16, **and the primitive list in
  math.shoddy's header comment** (it enumerates the runtime's math
  primitives and would otherwise go stale).

---

## Prompt 2 — Machine: neural.shoddy (and matrix additions)

Create `machines/neural.shoddy`, pure Shoddy except where noted,
`Include`-ing matrix (which brings seq transitively), random (for
`Shuffle`), and file (for save/load). Compile with
`mill machine machines/neural.shoddy`. Standard `Rem` copyright header;
document in the header, the way stats.shoddy does, that this is
classroom-scale — hundreds of rows and a few thousand epochs, not bulk
training.

### First, small additions to matrix.shoddy

These are generic linear algebra, so they belong in matrix, not neural:

```basic
Def VAdd(xs As Array Of Number, ys As Array Of Number) As Array Of Number
    Rem elementwise +, the sibling of the existing VSub

Def VMul(xs As Array Of Number, ys As Array Of Number) As Array Of Number
    Rem elementwise * (Hadamard)

Def Outer(u As Array Of Number, v As Array Of Number) As Matrix
    Rem outer product: Length(u) x Length(v), cell (i,j) = u_i * v_j

Def MatSub(a As Matrix, b As Matrix) As Matrix
    Rem the sibling of MatAdd, dimension-checked the same way
```

Name-check each against every machine's Defs and fields before landing.

### The Net record

```basic
Type Net
    W1 As Matrix              Rem nh x ni — MatVec(W1, x) is the hidden sum
    B1 As Array Of Number     Rem nh
    W2 As Matrix              Rem no x nh
    B2 As Array Of Number     Rem no (1 for regression)
```

The design's one big win: **a gradient has the same shape as the
network**, so the same record represents both, and accumulate / average
/ apply-update all fall out of two words —

```basic
Def NetAdd(a As Net, b As Net) As Net       Rem MatAdd / VAdd fieldwise
Def NetScale(n As Net, s As Number) As Net  Rem MatScale / VScale fieldwise
```

— no zeroing loops, no mutable scratch arrays. The C# original's ten
hand-rolled loop blocks collapse into the matrix expressions below.

### Words

```basic
Def NetNew(ni As Number, nh As Number, no As Number) As Net
    Rem weights and biases uniform in [-0.01, 0.01] via Rnd. IMPURE —
    Rem the one Rnd edge besides NetTrain's shuffle; a caller wanting
    Rem reproducibility calls the Seed builtin first, once, at the top
    Rem of Main. Document this the way random.shoddy documents its edge.

Type NetEval
    Hidden As Array Of Number   Rem post-tanh activations — backprop needs them
    Output As Array Of Number

Def NetEvalAt(n As Net, x As Array Of Number) As NetEval
    Rem h = Map(VAdd(MatVec(W1, x), B1), Tanh);  o = VAdd(MatVec(W2, h), B2)

Def NetOut(n As Net, x As Array Of Number) As Number
    Rem convenience: First element of Output — the regression value

Def NetGrad(n As Net, x As Array Of Number, y As Number) As Net
    Rem one example's gradient (spec'd exactly below)

Def NetStep(n As Net, xs As List Of Array Of Number, ys As List Of Number, lr As Number) As Net
    Rem one mini-batch: average NetGrad over the batch (Fold NetAdd,
    Rem NetScale by 1/batchsize), then NetAdd(n, NetScale(grad, -lr))

Def NetTrain(n As Net, xs, ys, lr As Number, batSize As Number, epochs As Number, report As [ Number Net -- ]) As Net
    Rem Fold over epochs. Each epoch: Shuffle(Range(1, n)) from
    Rem random.shoddy, break into Floor(n / batSize) consecutive chunks
    Rem (remainder dropped, matching the C# int division), Fold NetStep
    Rem over the chunks. Call report with (epoch, net) after EVERY
    Rem epoch — the caller decides when to print and pay for metrics,
    Rem keeping both the effect and the cost at the mill's edge.

Def NetMse(n As Net, xs, ys) As Number
Def NetAccuracy(n As Net, xs, ys, pctClose As Number) As Number
    Rem fraction with |pred - actual| < pctClose * |actual| — the C#
    Rem calls this "winner-takes-all" in a comment; it is not, don't
    Rem copy the comment

Def NetSave(path As String, n As Net)
Def NetLoad(path As String, ni As Number, nh As Number, no As Number) As Net
    Rem file.shoddy WriteLines/ReadLines, one number per line, in the
    Rem C# demo's serialization order so weight files interchange with
    Rem it: ih weights by input-major (for i in inputs, for j in
    Rem hidden), then hidden biases, then ho weights hidden-major, then
    Rem output biases. Note W1 here is nh x ni — the TRANSPOSE of the
    Rem C# ihWeights layout — so the save loop iterates MatGet(W1, j, i)
    Rem with i outer. Say so in a comment; this is where a silent
    Rem transpose bug would live.
```

### The gradient, exactly

So the implementer doesn't re-derive it. Loss is squared error without
the ½ factor, identity output — both matching the C#. For one example
`(x, y)` with `e = NetEvalAt(n, x)`, `h = Hidden(e)`, `o = Output(e)`:

```
oSig = o - y                      (identity derivative = 1; length no)
gW2  = Outer(oSig, h)             gB2 = oSig
hSig = VMul(MatVec(Transp(W2), oSig), oneMinusHSquared)
       where oneMinusHSquared = Map(h, Fn(v) => (1 - v) * (1 + v))
gW1  = Outer(hSig, x)             gB1 = hSig
```

**Do not port the C# batch bug.** `TrainBatch` reads `indices[ii]` with
`ii` in `0..batSize` for *every* batch of the epoch — never offset by
`batIdx * batSize` — so 95% of each epoch's shuffled data is skipped and
the first 10 rows are trained 20 times. The chunking spec above is the
intended behavior. Expect the fixed version's accuracy numbers to differ
from any published run of the C# demo; that is correct, not a
regression.

### Name collisions, found in advance

- **`Forward` is taken** — `turtle.shoddy` exports `Def Forward(t, d)`.
  The eval word is `NetEvalAt`, never `Forward`. In general every word
  here carries the `Net` prefix for the same reason the buzzer words
  avoided `Freq`.
- `Hidden` and `Output` as NetEval fields, and `W1`/`B1`/`W2`/`B2` as
  Net fields, collide with nothing today — re-verify at implementation
  time against every machine's Defs, Types, fields, and the builtin set
  (a Def outranks a builtin silently; a builtin outranks a field).

### Tests

Training output is floating-point and Rnd-driven, so these are contract
checks in `tst/neuraltest.shoddy` — the `randomtest.shoddy` pattern,
wired to run the same way — not golden bytes:

- **The gradient against central finite differences**: a tiny net (2-3-1,
  `Seed` first), perturb each weight and bias by ±1e-5, assert every
  `NetGrad` component matches `(loss(w+ε) - loss(w-ε)) / 2ε` within
  1e-4. This one test catches nearly every possible backprop mistake,
  including the transpose bug called out above.
- `NetAdd`/`NetScale` shape and value checks.
- **Learns a line**: y = 2x + 1 on a dozen points, a 1-4-1 net, a few
  hundred epochs, assert final MSE below a loose bound. Seeded, small,
  fast.
- `NetSave` → `NetLoad` round-trip: identical predictions, and the file
  line count is `ni*nh + nh + nh*no + no`.
- `machines/neural.shoddy` compiles with `mill machine`.
- New matrix words (`VAdd`, `VMul`, `Outer`, `MatSub`) get asserts in
  libtest's matrix section — those are pure and deterministic, so they
  belong in the golden-graded suite; **regenerate `tst/golden/libtest.out`**.

`MachineTests.cs`: libtest itself does not include neural, so
`BuildOrder` and the machine-count assert change only if the matrix
additions ripple (they don't — matrix is already in the order). If
neural is added to `BuildOrder` anyway for compile coverage, place it
after matrix and bump the count assert.

### Docs

`doc/machines/neural.html` (new), its row in `doc/machines/index.html`
and the quickref machine map, the README machines list, and whatever
curriculum tables enumerate the machines. Document the scale caveat and
the impure edges (NetNew, NetTrain's shuffle) prominently.

---

## Prompt 3 — Mill: demographics

Create `mills/demographics/demographics.shoddy` plus `build.cmd` /
`build.sh` copied from the `simplex-from-mps` mill and adjusted (weave
to `bin/demographics.dll`; `run` takes no arguments — the data paths are
fixed). Run from the mill directory so the relative `dat/` paths
resolve, and say so in the header, simplex-mps style.

### Data

Already in place, `#` for comments:

- `dat/training-people.dat` — 200 rows
- `dat/test-people.dat` — 40 rows

Nine comma-separated columns (1-based): 1 sex (0 male / 1 female),
2 age/100, 3-5 state one-hot (Michigan, Nebraska, Oklahoma), **6
income/100 000 — the target**, 7-9 politics one-hot (conservative,
moderate, liberal). X is columns 1-5 and 7-9, in that order.

### The loader

Mill-local, not a machine: `ReadLines` + drop `#` lines + `Split(",")` +
`Val` per token, then select columns into an `Array Of Number` x and a
`Number` y per row. Column-selecting numeric CSV loading is *borderline*
reusable, but the rule is the same as for builtins — promote it when a
second mill wants it, not before.

### The demo flow

Mirror the C# `Main`, including its console narrative:

1. Banner; load both files; print the first three x rows and y targets.
2. `Seed(0)` — one call, top of Main, so runs reproduce.
3. `NetNew(8, 100, 1)`.
4. `NetTrain` with lr 0.01, batSize 10, maxEpochs bound in one `Let` at
   the top. The report quotation prints epoch, `NetMse`, and
   `NetAccuracy(…, 0.10)` when `epoch Mod freq = 0`, freq =
   maxEpochs / 10 — cost only when printing.
5. Train and test accuracy at 0.10 closeness.
6. Predict male, 34, Oklahoma, moderate:
   `{ 0, 0.34, 0, 0, 1, 0, 1, 0 }` — print the scaled prediction and
   the de-scaled dollar figure (× 100 000).
7. `NetSave` to `dat/people-wts.txt`, `NetLoad` into a fresh net,
   re-predict, `Assert` the two predictions agree — this exercises the
   save/load path the C# demo left commented out (and broken: its
   `LoadWeights` uses `List<double>` with no
   `using System.Collections.Generic`).

No `Console.ReadLine()`-style hold-open; mills exit when done.

### Performance: measure before committing to 2000 epochs

The C# demo's 2000 epochs × 20 batches is on the order of 10⁹ arithmetic
ops through immutable arrays and closures. Shoddy is compiled, but the
functional overhead is unmeasured at this scale — so **time one epoch
first** (the clock machine) and size maxEpochs from that. Acceptable
outcomes, in order of preference: 2000 epochs in reasonable wall time;
fewer epochs with the tradeoff documented in the header; or, if training
is fundamentally too slow to be honest, runtime work in a follow-up
prompt (the TANH/InKey precedent again) — never a degraded mill that
pretends to train.

Target quality: with the batch bug fixed, expect train and test accuracy
around 0.90 at 10% closeness. If test accuracy lands below ~0.80,
suspect the port (gradient transpose, column selection off by one)
before suspecting the hyperparameters.

### The reference program

`mills/demographics/cs/NeuralRegressionProgram.cs` stays where it is as
the reference. Its known defects, so nobody ports them: the batch
indexing bug (Prompt 2), the missing `using` (step 7 above), and the
`Accuracy` comment mislabeling pct-close as winner-takes-all. Note them
in the mill header comment.

---

## Checklist

- [ ] `TANH` in `Engine.Builtin()` **and** `Engine.BuiltinWords`;
      known-value C# tests; QUICKREF, SPEC, highlighter, agent-context
      §16, and math.shoddy's header list all updated
- [ ] `VAdd`, `VMul`, `Outer`, `MatSub` in matrix.shoddy with libtest
      asserts; `tst/golden/libtest.out` regenerated
- [ ] `machines/neural.shoddy`: `Net`, `NetEval`, `NetNew`, `NetEvalAt`,
      `NetOut`, `NetAdd`, `NetScale`, `NetGrad`, `NetStep`, `NetTrain`,
      `NetMse`, `NetAccuracy`, `NetSave`, `NetLoad`; compiles with
      `mill machine`; no `Forward`, no shadowed builtins or fields
- [ ] Gradient verified against central finite differences in
      `tst/neuraltest.shoddy`; learns-a-line smoke test; save/load
      round-trip; C# serialization order interchange documented
- [ ] Batch chunking tiles the whole shuffled epoch — the C# bug is
      fixed, not ported
- [ ] `mills/demographics/`: `demographics.shoddy`, build scripts on the
      simplex-from-mps pattern, loader, full demo flow with seeded
      reproducible run and save/load exercised
- [ ] One-epoch timing measured before maxEpochs chosen; the choice and
      its reason in the mill header
- [ ] `doc/machines/neural.html`, index/quickref/README machine lists,
      curriculum tables; scale caveat and impure edges documented
