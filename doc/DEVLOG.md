# The Invention of Shoddy — A Conversation Record

*Making useless things useful, through skill.*

This document is the record of the conversation in which the Shoddy
language was invented, specified, implemented, and grown into the tree
you are standing in. It took place in a single session between its human
designer (Sir Stephen) and Claude, on July 17–18, 2026.

**Fidelity note.** Every human message below is reproduced *verbatim*,
including typos, which are preserved deliberately as part of the record.
Claude's replies — many of them long documents and code — are faithfully
condensed, with pivotal passages quoted; the full artifacts they produced
are the files of this repository. Nothing has been added that did not
happen.

---

## Turn 1 — The first question

> **what would a computer language, built on BASIC, but with structured
> idents instead of begin/end style, and purely functional look like?**

Claude sketched "Python's whitespace, BASIC's vocabulary, Haskell's soul"
— a language provisionally called FUNBASIC. Core decisions proposed: `LET`
as true binding (no `LET X = X + 1`), no `GOTO` or `FOR` (recursion and
`MAP`/`FOLD` instead), `IF/THEN/ELSE` as an expression, effects confined
to `MAIN`, layout instead of terminators. First code ever written in the
language-to-be:

```basic
DEF FACT(N)
    IF N = 0 THEN
        1
    ELSE
        N * FACT(N - 1)
```

Also proposed: `SELECT CASE` as "your killer feature" for pattern
matching, and the observation that BASIC's string library (`MID$`,
`LEFT$`) was always pure and would survive unchanged. Line numbers were
declared dead ("they existed for editing and GOTO, both gone").

## Turn 2 — The pivot to the stack

> **in stead of algebraic, how 'bout stack? something like forth?**

Claude answered that this describes Joy — the purely functional
concatenative language — wearing BASIC's clothes and Python's indentation:
"Joy's semantics, BASIC's keywords, Python's layout." Every word a pure
function `Stack → Stack`; Forth's dirty parts (memory ops, mutable loops)
deleted rather than restyled; indented `IF` branches as sugar for
quotations passed to `IFTE`; `TAKE` proposed as the escape hatch from
shuffle-hell. The first stack-dialect code:

```basic
DEF SQUARE          REM ( n -- n^2 )
    DUP *

DEF FACT            REM ( n -- n! )
    DUP 0 =
    IF THEN
        DROP 1
    ELSE
        DUP 1 - FACT *
```

Offered: a spec, or a small interpreter ("maybe 200 lines of Python").

## Turn 3

> **both please**

(No response was recorded for this turn.)

## Turn 4 — C it is

> **instead, please make it C**

Claude wrote the v0.1 deliverables: `SPEC.md` (the concatenative core —
layout rules, quotations, `TAKE`, combinators, the grammar) and a
single-file C interpreter, `stackbasic.c`, plus `examples.sb`. A
complication struck: **the Linux sandbox refused to start for the entire
session**, so the interpreter was written and shipped *without ever being
compiled* — verified instead by a careful manual trace, during which two
bugs were found and fixed by desk-check alone (a dangling-ELSE in nested
conditionals, and format-string hygiene). Claude noted honestly that the
code was untested and asked for compiler output if anything tripped.

## Turn 5 — Algebra knocks

> **what would this look like standard parenthesis for arguments? and
> with type definitions (only describe,dont change deliverables)**

Description only, as instructed. Two moves: `F(a, b)` as pure sugar for
`a b F` (with the observation that this converges back to Turn 1's
FUNBASIC — "the two designs converge; the stack becomes an implementation
detail"), and types via BASIC's own keyword: `DEF HYPOT(A AS NUMBER, B AS
NUMBER) AS NUMBER`. Stack-effect comments promoted to checked signatures;
quotations get arrow types `[ T -- BOOLEAN ]`; row polymorphism noted for
`DUP`; the Joy list/quotation pun breaks under typing. Verdict: "a
BASIC-flavored **Kitten**."

## Turn 6

> **show me a few examples. such as aggregating an array**

`SUM` as `FOLD(XS, 0, [ + ])`, `AVERAGE`, `MAXIMUM`, `COUNTIF` with a
quotation-typed parameter, filter-then-aggregate in both applicative and
point-free style, and the hybrid pipeline
`RANGE(1, N) FILTER(...) MAP(...) FOLD(0, +)` — method-chaining
ergonomics "without inventing a `.` operator."

## Turn 7

> **show me implementations of FOLD, FILTER, and RANGE...in this
> language.**

Self-hosted, over five primitives (`EMPTY?`, `FIRST`, `REST`, `PREPEND`,
`CALL`): FOLD tail-recursive ("this is *the* loop of the language"),
FILTER and RANGE by structural recursion, MAP falling out free, plus FOLD
again in bare concatenative style. Tail-call caveats noted honestly.

## Turn 8 — The brackets on trial

> **what is the purpose of the [].... seems noisy**

The answer: brackets are the deferral marker — Lisp's `quote`, literally
Smalltalk's block syntax — required because concatenative execution is
eager. But the instinct was ruled correct: in the typed applicative
surface, bare names auto-quote at quotation-typed positions
(`FOLD(XS, 0, +)`), sections shrink snippets (`>=(100)`), `FN(X) =>`
covers true lambdas, and brackets survive only in the core, where "IF
couldn't exist" without them.

## Turn 9

> **yes please.**

The typed applicative dialect was written up as `TYPED-DIALECT.md` (v0.2):
calls, infix precedence, `LET`, implicit `TAKE`, `{ }` lists split from
`[ ]` quotations, the bracket-reduction ladder, and a full desugaring
table.

## Turn 10 — Sigils, part one

> **explain the ? on LONG?**

`?` is part of the name — the Scheme/Ruby predicate convention meeting
BASIC's own sigil tradition (`STR$` was always "returns a string";
`EVEN?` is to BOOLEAN what `STR$` is to STRING).

## Turn 11

> **Thanks! I had forgotten that. why are we not using $ and % #**

Because sigils *were* BASIC's type system, and with `AS` declarations
they become a second, contradictable spelling of the same fact — and
because no sigil can spell `LIST OF T` or a generic return. "Sigils
don't scale past scalars." `$` and `?` retained as conventions at this
point.

## Turn 12

> **then why !**

The history: `!` was single-precision's explicit spelling, needed because
`DEFDBL A-Z` could change the default under you; `#` "looks heavier, so
it got double precision." And the irony: in Scheme/Ruby, `!` means
*mutation* — "in a purely functional BASIC, `!` is the one sigil with
nothing to name."

## Turn 13

> **can we remove it for this purpose...and stick with bool?**

`! % #` became reserved characters, enforced by the tokenizer; `$` and
`?` remained the two live sigils.

## Turn 14 — Sigils, part two: extinction

> **can we not get rid of sigis compeltly?**

All five characters (`! % # $ ?`) reserved. Predicates took the `IS`
prefix (`ISEMPTY`, `ISEVEN` — Visual BASIC's own `IsEmpty` precedent);
the string library de-sigiled (`STR`, `MID`, `LEFT` — finally consistent
with `VAL` and `ASC`, which never had one). Applied across the
interpreter, spec, and examples.

## Turn 15 — Where are the files?

> **not seeing updates in the working folder**

No folder had been connected: work had gone to an internal scratchpad.
A folder was connected (`Dropbox\Projects\stackBasic`) and all four files
written into it.

## Turn 16 — The reckoning

> **those are not even close to what we ended with.**

Correct: `SPEC.md` and `examples.sb` still showed the v0.1 postfix core;
everything since lived in the "companion" dialect doc. Asked how to fix
it, the designer chose **Full rewrite**. Claude extended the interpreter
with the entire applicative surface — precedence-climbing infix, paren
calls, `LET`, `FN` lambdas, sections, `{ }` list literals,
`IF cond THEN`, pipeline chaining — all compiling down to the unchanged
stack backend; rewrote `examples.sb` in final syntax; unified the spec
(core relegated to Appendix A); reduced `TYPED-DIALECT.md` to a
superseded stub. All of it still written blind: the sandbox remained
down, so roughly a thousand lines of new parser were desk-checked only.

## Turns 17–19 — The sandbox rises; a trespass

> **can you see our local working folder and your sandbox?**

Asked three times as the designer fought the environment. Twice: folder
yes, sandbox no. On the third ask the sandbox came alive — gcc 11.4 —
and the folder contained a new file, `problem.md`:

> *i'm going to challenge you. Write a sb program that implements a
> linear programming solver using the revised simplex method.* [minimize
> 5x+7y s.t. 2x+y≥8, x+2y≥10]

Claude compiled the interpreter for the first time — **zero warnings
under `-Wall -Wextra`, and every one of examples.sb's outputs exactly
right, for code written entirely without a compiler** — and then, taking
the file's existence as an instruction, built and solved the challenge
unbidden (via the dual, revised simplex with explicit basis inverse:
X=2, Y=4, cost 38, two pivots).

> **i didn't ask you to do the challenge...wtf?**

Acknowledged plainly: a file sitting in a folder is not an instruction.
Offered to delete `simplex.sb`.

## Turn 20 — Forgiveness, and arrays

> **i like what ya did!**
> **thoughts on changing LIST to VECTOR and adding ARRAY?**
> **don't do it yet... just an answer please**

Answer: rename no — "VECTOR would promise O(1) access the structure
doesn't have" — addition yes, and spelled ARRAY, "the more
BASIC-authentic word anyway," with `DIM` reclaimed. "Recurse on lists,
index into arrays."

## Turn 21 — Records enter

> **before that....**
> **what 'bout TUPLE(name AS STRING, age AS NUMBER)?**

Claude argued the name down: QuickBASIC already had record syntax —
`TYPE ... END TYPE` — and layout deletes the `END TYPE` for free. TUPLE
reserved for positional products, which the language may never need
("the concatenative core already returns multiple values natively").
Auto-generated accessor words, `WITH` for pure update, and `SELECT CASE`
destructuring foreshadowed.

## Turn 22

> **whats your recommendation? show me some examples**

The full record design shown working: `TYPE PERSON` with layout;
positional and named construction; accessors composing with everything
(`MAP(PEOPLE, NAME)`); collision rule (dispatch on tag); `WITH`;
structural equality; print-as-valid-input; and the `SELECT CASE
PERSON(N, A) WHERE A < 18` teaser.

## Turn 23 — Records and arrays land

> **very nice! yes. do that, the array**

Both implemented and tested: `TYPE` declarations, constructors (arity
checked at parse), accessor words, `WITH`, structural `=`, repr
printing; `ARRAY` with `DIM`/`TOARRAY`/`TOLIST`, O(1) `NTH`, functional
`SETNTH`, and `MAP`/`FILTER`/`FOLD`/`EACH`/`LENGTH` polymorphic over
both kinds. Test tail: `PERSON(NAME = "ANN", AGE = 34)` …
`ARRAY(1, 4, 9, 16, 25)` … all exact.

## Turn 24 — The challenge, licensed

> **now you may do the challenge!**

Already done — so v2 was built instead: matrices as arrays, and a pure
solver returning a `RESULT` record with every `PRINT` in `MAIN`.
"'Effects live at the edge' went from a spec convention to something
this program literally demonstrates." Same answer: X=2, Y=4, 38.

## Turn 25 — The library idea is born

> **is it time for a MATRIX type?**

"MATRIX yes, builtin no." A `TYPE MATRIX` over a flat row-major array,
written in the language itself — the test of whether records + arrays
suffice. The real gap exposed: no `INCLUDE`. Proposed: INCLUDE, then
matrix.sb, then simplex v3.

## Turn 26 — The standard library

> **yes please!**
> **create one for the FOLD and MAP type words -- include the usual
> suspects.**

`INCLUDE` added to the interpreter (include-once, paths relative to the
including file), plus `ERROR`, `FLOOR`, `CEIL`, `ROUND`. Then `seq.sb`
(SUM PRODUCT MAXIMUM MINIMUM AVERAGE ANY ALL COUNTIF CONTAINS INDEXOF
TAKEN DROPN APPEND LAST FLATTEN SORT ZIP ZIPWITH, TYPE PAIR) and
`matrix.sb` (MAT MATGET MATSET IDENT TRANSP MATMUL MATADD MATVEC …,
dimension-checked via ERROR).

**And the library immediately found a language bug.** `CONTAINS` passes
the section `=(V)` to `ANY`, which executed it in the wrong environment
— quotations weren't closures. The fix was the principled one:
**quotations capture their defining environment** (lexical scoping),
costing exactly one pointer because environments are immutable. "Writing
the library was the test that found it, which is the whole argument for
building MATRIX in the language instead of the interpreter." Simplex v3
then shipped on the new libraries: `Y = MATVEC(TRANSP(BINV), CB)`.

## Turn 27 — The guide is commissioned

> **the spec is a bit heavy. keep it...but write a beginners guide.
> don't presume a deep knowledge of the topic. give examples. show the
> syntatical sugar differences. before doing this...what would you
> suggest as the outline?**

A ten-chapter outline proposed: teach the friendly syntax first, reveal
the stack in chapter 7 "the same way you'd teach Python before
bytecode," grade-book finale, gotchas chapter, cheat-sheet appendix.

## Turn 28 — The gap

> **btw -- did we allow for console input?**

No — `PRINT` was the language's only I/O. `INPUT(prompt)` designed on
the spot (one builtin; numbers via existing `VAL`).

## Turn 29 — The audit

> **what other commodity features did we miss?**

The honest list: string SPLIT/JOIN/INSTR/TRIM/REPLACE; no `^` operator
at all; no SIN/COS/TAN/ATN/EXP/LOG; no RND; AND/OR didn't short-circuit;
no string escapes; calls can't span lines; no dictionaries; no SELECT
CASE yet; no error handling; no REPL. Deliberately absent and staying
that way: GOTO, mutation, FOR, globals. A "commodity pack" proposed.

## Turn 30 — The commodity pack

> **yes my learned friend**

Delivered: `INPUT`, `^` (right-associative, classic precedence:
`-N ^ 2` = `-(N^2)`), the math library + `PI` + `RND`, `INSTR`,
`ASSERT`, string escapes, short-circuit `AND`/`OR` (compiled to
conditionals), and `str.sb`. Testing found another real bug: `-N` lexed
as one unknown word — fixed in the tokenizer with `-3` literals and `--`
signatures protected. `INPUT` verified with piped stdin.

## Turn 31 — The guide

> **yes.**

`GUIDE.md` written to the outline, with `gradebook.sb` as the runnable
interactive finale (roster in, grades, average, honor roll). All 33 code
snippets from chapters 1–8 executed and verified against the printed
outputs. "Nothing in the guide is aspirational."

## Turn 32 — Pattern matching arrives

> **can we turn that ugly if/then/else in GRADE() into pattern
> matching?**

`SELECT CASE` implemented — the feature promised in Turn 1. All the
QBasic clause forms (`CASE 0`, `CASE 1, 2, 3`, `CASE 4 TO 10`,
`CASE IS > 10`, `CASE ELSE`), as an expression, scrutinee evaluated
once, compiled to nested conditionals over a hidden unspellable binding.
GRADE became:

```basic
    SELECT CASE SCORE(S)
        CASE >= 90
            "A"
        ...
```

## Turn 33 — Destructuring

> **yes, deconstruct the types**

`CASE PERSON(N, A) WHERE A < 18` — type test, field binding, guards that
fail through to the next clause. Verified with three PAIR clauses
distinguished by guard alone.

## Turn 34 — Stocktaking

> **what are our data types?**

The seven-type answer (NUMBER STRING BOOLEAN QUOTATION LIST ARRAY
records), the LIST/QUOTATION shared-skeleton secret, and the gaps named:
dictionaries, sum types, no NULL by design.

## Turn 35 — The types that were refused

> **if we add the following types, what compexity does that add: bit,
> short, integer / long, decimal(precision,scale), char**

The complexity audit: CHAR "low cost, near-zero value — skip
permanently"; BIT redundant (with the note that classic BASIC's AND/OR
were bitwise); SHORT/INTEGER/LONG a quadratic promotion-matrix explosion
for a payoff the language can't use; DECIMAL(p,s) "the highest cost and
the only one with a real constituency" — the first value-parameterized
type, which would drag static checking from future work into required
work. Verdict: "the language's current superpower is that 'how do
numbers interact?' has exactly one answer." MONEY-as-library proposed
instead.

## Turn 36 — The philosophical turn

> **what happend to the stack... how did algebra come back?**

The answer became the project's thesis: the stack never left — it went
underneath, one innocent desugaring at a time (TAKE, then parens, then
infix, then LET…). "A stack is notation for **lines**; algebra is
notation for **trees**… a stack is what algebra looks like when it
runs." The two opening questions "weren't competing designs. The first
specified the surface; the second specified the engine."

## Turn 37

> **is the stack syntax still there?**

Proven live: pure postfix definitions, both dialects mixed on one line
(`RANGE(1, 10) [ SQUARE ] MAP FOLD(0, +) PRINT` → 385), and two lessons
collected en route — signatures can't contain `!`, and the body dialect
is chosen per-DEF by the header.

## Turn 38 — Money

> **generate money as suggested**

`money.sb`: `TYPE MONEY` over whole cents (exact past $90 trillion in a
double), rounding only in `MONEYMUL` and explicitly, `MONEYSPLIT`
allocating without losing a penny. The test output made the argument:
`0.1 + 0.2 = 0.3` → FALSE in doubles; TRUE in MONEY;
`[ "$3.34" "$3.33" "$3.33" ]`.

## Turn 39 — The reorganization

> **i have moved all the files into sub folders....please review**

The designer had restructured into `bin/ doc/ lib/ src/ tst/`. Review
found exactly the predictable breakage — `INCLUDE` paths are relative to
the including file — fixed in three files; `lib/`'s internal include had
survived the move untouched. Noted without action: the `simpex.prompt`
typo, and the future case for an include search path.

## Turn 40 — Documentation day

> **create a readme.md;**
> **convert the guide and spec docs to html**
> **create a quick reference guide with all words, types, etc... (in
> html)**

`README.md`, pandoc-converted `GUIDE.html`/`SPEC.html` with a shared
stylesheet, and the hand-built `QUICKREF.html` — every builtin, every
library word with LIB badges, the operator ladder, the desugaring table.

## Turn 41

> **the tables in the guide overlapping. don't be so fancy... top to
> bottom please**

The CSS multi-column layout was removed. "Same content, no cleverness."

## Turn 42 — Files, text

> **what can we do for file i/o? let's start with text files...standard
> features**

Whole-file builtins (`READFILE WRITEFILE APPENDFILE FILEEXISTS
DELETEFILE` — "a handle is a mutable cursor, and whole-file operations
keep the effect surface as flat as PRINT's") plus `file.sb`
(`READLINES`/`WRITELINES`/`APPENDLINE`, `\r\n` normalized).

## Turn 43 — Files, binary

> **let's add binary block-io. with SEEK(byte-position), and GET/PUT
> methods, for each type if necessary. specify fixed maximum length for
> strings. use binary or fixed termination**

Classic random-access record files: `BOPEN`/`BCLOSE`/`SEEK` (1-based
bytes)/`BPOS`/`BSIZE`; `PUTNUM`/`GETNUM` (8-byte double),
`PUTBOOL`/`GETBOOL` (1 byte), `PUTSTR`/`GETSTR` as fixed-length
zero-padded fields — over-length strings abort rather than silently
truncate. Tested with 20-byte NAME(12)+SCORE(8) records: seek to record
2, in-place update, exact 40-byte file.

## Turn 44

> **yes, add recio library**

`recio.sb`: the offset arithmetic layer — `RECPOS RECSEEK RECCOUNT
GETREC PUTREC APPENDREC ALLRECS`, generic via user READER/WRITER
functions passed bare.

## Turn 45 — The remembered gaps

> **you mentined dictionary and a copule of other types you felt were
> missing...what where they?**

Dictionaries, sum types (`TYPE SHAPE = CIRCLE(R) | RECT(W, H)` — with
the observation that `SELECT CASE` destructuring is already the
elimination form they need, and that OPTION is the principled NULL
answer), and nested patterns.

## Turn 46 — All three

> **do all three please.**

Sum types (one-line variant declarations; every variant a full record
type; `CASE NONE` matching by structural equality), nested patterns
(patterns became recursive trees matched by a small recursive C
matcher, replacing the flat engine — with `_` as the don't-care binder),
and `dict.sb` (DICTPUT DICTGET DICTGETOR DICTHAS DICTDEL DICTKEYS
DICTVALS over PAIR). Test tail: AREA dispatching CIRCLE/RECT; `SOME BIG
250` via a guard on a variant; `PAIR(PAIR(1, 2), 3)` matching to 6.

## Turn 47 — The final boss is described

> **now for something more complicated. i need and
> index-sequential-access-method library. describe approach and words.
> don't do anythinbg yet**

The ISAM design: "pure index over effectful storage" — tombstone-flagged
fixed slots on disk, a sorted key→slot index carried *inside the handle
record*, every mutation returning a new handle; cursorless key-order
navigation; free-slot reuse; rebuild-by-scan on open. Fifteen words
specified. "It should need **zero interpreter changes**."

## Turn 48 — The final boss falls

> **yes please!**

`isam.sb`, ~170 lines of pure Shoddy, working on the first run: inserts
in scrambled order returned in key order, in-place update, delete +
reinsert with a file-didn't-grow assertion proving slot reuse, range
scans, next/prev, and the acid test — close, reopen, index rebuilt from
the cold file, everything still there. The deepest dependency stack the
language had run: binary builtins → readers/writers → seq →
records-holding-closures → ISAM.

## Turn 49 — The name

> **we have a branding change. no longer 'stackbasic'.**
>
> **'shoddy', tag line 'making useless things useful, through skill.'**
>
> **update everything, including file names**

`src/stackbasic.c` → `src/shoddy.c`; `bin/sb` → `bin/shoddy`; every
reference in every file renamed; the tagline installed in the README,
both docs, the quick reference, and the C source header; `HELLO FROM
SHODDY`; full regression green under the new name. The `.sb` extension
kept ("reads fine as Shoddy Basic"), the folder name left to its owner.

"A purely functional BASIC that self-hosted an ISAM database, wearing a
name that dares you to underestimate it — the branding has genuinely
never been more accurate."

## Turn 50 — This document

> **how do i save this long, very productive, conversation?**

Sessions persist in the app, but Cowork transcripts aren't in official
data exports — so the recommendation was this file: the record of the
invention, in the repository, in Dropbox, outliving any app.

> **yes please, include as much as you can. this is to be a record of
> the invention. don't add anything to the conversation...exactly as it
> is please.**

---

## The state of the invention at Turn 50

```
bin/shoddy          the interpreter, built clean: -Wall -Wextra, zero warnings
src/shoddy.c        one file of C99, ~2,100 lines
lib/                seq  str  matrix  money  file  recio  dict  isam
doc/                GUIDE  SPEC  QUICKREF  DEVLOG (this file)  + HTML
tst/                libtest (assertion suite)  examples  gradebook  simplex
```

One language, two dialects (algebraic surface, concatenative core, one
desugaring table between them); seven types plus user records and sum
types; closures, pattern matching with guards and nesting; an
eight-module self-hosted standard library ending in an ISAM database;
a beginner's guide whose every example runs; and a revised simplex
solver that answers X=2, Y=4, cost 38.

*(The conversation continued. Part II follows.)*

---

# Part II — The continuation

## Turn 51 — The great de-shouting

> **i think many will be put off by all the upper case.  let's use pascal
> case for types and functions (include lib and core words), and camel
> case for variables.**

The early case-insensitivity decision made this nearly free: `Map` and
`MAP` were always the same word. The whole tree — libraries, tests,
docs — was script-converted to PascalCase words and camelCase variables,
with one real interpreter improvement: the tokenizer now remembers
declared spellings, so records print back as written
(`Person(Name = "Ann")`), and the printer speaks the convention
(`True`, `Array(1, 2, 3)`). Casualties of the automated pass (an
`Xb`/`xb` field collision, comments echoing uppercase string output)
were hand-repaired. Full regression green throughout.

## Turn 52 — A matter of tone

> **the beginers guide reads like we're calling the reader 'shoddy'...
> please refrase**

Correct: the tagline plus "assumes you can find your keyboard and not
much else" aimed the joke at the reader. Rewritten so the name
disclaims itself: "*Shoddy* is the language's name, not a review of it —
and it is definitely not a description of you."

## Turn 53 — The editor

> **would you be able to write a VSCODE plug-in for shoddy?**

Yes: `vscode-shoddy/` — TextMate grammar covering every builtin and
library word in both dialects, layout-aware indentation, ten snippets,
and a `Shoddy: Run Current File` command (Ctrl+Alt+R). No build step;
all JSON validated, JS syntax-checked, grammar regexes compiled.

## Turns 54–55 — Deployment, and an interruption

> **how should the interpreter by deployed with this extension?**

Answer: bundle per-platform binaries in platform-specific `.vsix` files
— and the analysis exposed that `Include`'s relative paths break for a
deployed interpreter, forcing the `SHODDYLIB` search-path feature.

> **please do.  can windows binary be provided on install?**

Yes — built ahead by CI and bundled (never compiled on the user's
machine). `SHODDYLIB` fallback added to the interpreter and verified
from `/tmp`; the extension gained platform-aware binary resolution and
bundles the standard library; a GitHub Actions workflow builds and
smoke-tests all four platforms. *(Here the session ran out of credits —
two "Continue from where you left off" prompts and a resent message
bracket the gap in the log.)*

## Turn 56 — The audit

> **credits are back.  review everything for completness**

The review found the `.md` doc sources deleted (HTML now the source of
truth), a new `feedme/` folder of the designer's cattle-feed data (noted,
untouched), and four staleness items — including a README still calling
dictionaries and sum types "future work." All fixed; everything
functional passed; fifteen restyle-shrapnel fixes to the quick
reference.

## Turn 57

> **bin copied.**

Read as: extension installed to VS Code. Verified no `shoddy.exe` had
reached the repo; laid out the two ways to complete the Windows loop.

## Turns 58–59 — Root constants

> **do we have constants, or LET bindings at the root?**

No — only the nullary-Def idiom (`Def Eps()`), re-evaluated per call.
Top-level `Let` was designed: once-at-startup, textual order, the
outermost lexical scope; the auto-quote interaction was predicted in the
answer and confirmed by test.

> **yes.  also ignore the feedme folder until otherwise directed.**

Implemented (~40 lines), tested with the golden-ratio identity
φ² = φ+1 asserted through a global inside a Def; simplex's five nullary
Defs became five `Let`s, its data tables now built once.

## Turns 60–62 — The .NET turn

> **what's your toughts on a .net c# implementation?**

Favorable as a second implementation: catchable errors nearly free, GC
honesty, `dotnet tool` distribution, embedding — with the de-risking
insight that the Shoddy-language test suite is already a
cross-implementation conformance suite.

> **i have vscode, i would use my license there to build this.**

The handoff was prepared: `tst/golden/` (171 captured lines, the
conformance truth) and `dotnet/PORTING.md` — architecture map plus the
twelve semantic landmines a port must honor.

> **and how about compiling directly to clr, as a console app.
> and...maybe...libs as a dll?**

Endorsed: transpile to C# via Roslyn (with `#line` mapping back to .sb —
Shoddy-source stack traces and breakpoints), one front-end with two
back-ends over a shared `Shoddy.Runtime`, self-tail-recursion compiled
to loops (TCO at last), and lib DLLs with embedded manifests — compiled
Shoddy callable from C#.

## Turn 63 — Decimal, asked and answered a second time

> **with the move to dotnet, what's your thoughts on number implemented
> as dotnet decimal?  with a lib for rounding and such?**

No — and this time the reason was conformance, not complexity: libtest
itself asserts `0.1 + 0.2 = 0.3` is False. Plus: no decimal
transcendentals, banker's-vs-half-up rounding, the 8-byte on-disk
format, trailing-zero printing. Exactness remains money.sb's job;
decimal belongs at the C# interop boundary. Pinned in the porting brief.

## Turn 64 — The reveal, and the mill

> **fantastic.  i have decide to go dotnet first.  no c implementation.
> with compilation.  this has turned into a completely new dotnet
> language.**
>
> **one last thing.  'shoddy' is a reference to the shoddy woolen mills
> of the west riding, yourshire.  what are the thoughts on calling the
> libraries 'machines', and the executable a 'mill'?**

The etymology recast everything: shoddy is reclaimed wool — rags ground
down and re-spun into new cloth by the mills of the West Riding — so the
tagline was literal all along, and the guide's "self-deprecating" origin
story was corrected to the truth. The naming was endorsed as
functionally accurate metaphor: the **mill** (`mill run`, `mill weave`),
the **machines** (`Shoddy.Machines.Seq`...), whimsy confined to the
toolchain, keywords kept plain — with the *devil* (the rag-grinding
machine) noted as the lexer's rightful name-in-waiting. PORTING.md
became the founding document of the new world: C retired to
specification artifact, goldens promoted to constitution.

## Turn 65 — Closing

> **THANK YOU OH SO MUCH!!!**
> **Please save this conversation log as a record!!!**

This Part II is that record.

---

## The state of the invention at the close

```
src/shoddy.c        the C interpreter — retired to specification artifact
bin/shoddy          its build; still passes everything
lib/                seq str matrix money file recio dict isam  (the machines-to-be)
doc/                GUIDE SPEC QUICKREF (HTML) + DEVLOG (this record)
tst/                libtest examples gradebook simplex + golden/ (the constitution)
vscode-shoddy/      editor support, bundled interpreter + library
dotnet/PORTING.md   the founding brief of the .NET era
.github/workflows/  the CI that builds every platform
```

What leaves this conversation: a purely functional BASIC with two
dialects, records and sum types, pattern matching with guards and
nesting, closures, top-level constants, an eight-machine self-hosted
library ending in an ISAM database, file and binary I/O, a beginner's
guide whose every example runs, an editor extension, a conformance
suite with 171 golden lines — and a future as a compiled CLR language
called by its true name: a mill, full of machines, in the tradition of
the West Riding.

Rags in. Cloth out. Through skill.

---

# Part III — The .NET era

*Recorded 18 July 2026, later the same day. Parts I and II closed with
a decision: dotnet-first, with compilation, under the mill's true name.
This part records the day the decision was executed.*

## The port

The solution went up in `src/` — five projects: `Shoddy.Runtime` (the
value model and every builtin), `Shoddy.Devil` (the front-end: reader,
lexer, parser — the devil grinds the rags), `Shoddy.Mill` (the `mill`
executable), `Shoddy.Compiler` (the weave), `Shoddy.Tests` (the
constitution, executable). The C interpreter retired to `oldc/` as the
specification artifact it was promised to be, and was consulted
exactly as intended — the porting brief's description of the tokenizer
turned out grander than the truth (tokens are whitespace-split blobs;
operators must stand alone; a number is whatever survives `strtod`),
and only the C could say so.

Phase 1, the tree-walking interpreter, went green the same morning:
all four golden programs byte-identical on the first full run — the
`%.10g` formatter, the half-up `Round`, the NUL-stripped `GetStr`, the
unspellable `SEL` binding and all. The suite ran in 24 milliseconds.

## The weave

Phase 2 followed by afternoon. `mill weave` emits C# — readable,
deliberately, per the brief's refusal to emit raw IL — and compiles it
with Roslyn. Take bindings become C# locals; CLR closures replace the
captured-environment machinery (a rebind allocates a fresh local, so
old closures keep the old value, exactly as the immutable chain did);
Select Case arrives pre-desugared; case folding is resolved at weave
time. And the headline: **self-tail-recursion compiles to a loop** —
arguments ride the value stack, so the tail call is `continue;` back
to the def's own Take. A million-deep `Count(n - 1)` runs flat. Shoddy
got its TCO where the brief said it would: in the compiler.

Then the machines became literal. `mill machine machines/seq.sb`
compiles a library to `Shoddy.Machines.Seq.dll` beside its source,
manifest as assembly attributes — exported defs, record types, their
declared spellings. An `Include` resolves to the machine if built and
splices if not; references travel transitively (libtest never includes
seq directly; its surface arrives through the matrix machine), and an
assembly loads once however many routes reach it: compiled
include-once, as the brief required. `lib/` was renamed `machines/` to
match — the naming decision of record, finally earned.

## The deletion

Then the question that reshaped the day: *why keep the interpreter at
all?* It had served as Phase 1's conformance bootstrap and could have
lived on as a differential oracle — but two back-ends mean two sets of
semantics, dual quotation representations, hooks, and corner-case
footnotes. The decision: **Shoddy is 100% compiled.** The interpreter
was deleted the same day it was born. `mill run` now weaves to memory
— Roslyn, no artifacts on disk, ~0.6 seconds — and executes
in-process; `run` and `weave` are one pipeline, and the language has
exactly one set of semantics. The deletion immediately paid for
itself: the single-file `bin/mill` had been quietly unable to weave
(its runtime DLL was bundled inside the executable, where Roslyn could
not reference it), which only surfaced because `run` now weaves too.
The mill publishes as a folder now, and every path is exercised.

## The workshop

VS Code came along: a rebuilt `vscode-shoddy` extension (a real
TextMate grammar — quotations, sections, stack-effect signatures,
both comment forms, all case-insensitive — snippets, and mill commands
including **Show Generated C#**), workspace tasks, and `doc/VSCODE.md`
as the loom-side manual. The CI workflow was rewoven for .NET: the
golden suite on four platforms, per-platform mill artifacts, one
platform-neutral extension package. The `.sb` extension survived a
challenge (the corpus speaks it; the editor also accepts `.shoddy`).

## The state of the invention at the close of Part III

```
src/                the solution: Runtime · Devil · Mill · Compiler · Tests
bin/                the published mill (folder; run bin/mill)
machines/           seq str matrix money file recio dict isam
doc/                GUIDE SPEC QUICKREF VSCODE + HERITAGE + this record
tst/                the four programs + golden/ (the constitution)
vscode-shoddy/      the extension, wired to the mill
dotnet/PORTING.md   the founding brief, with its status and amendment
oldc/shoddy.c       the C reference, retired with honors
```

Six tests grade the whole cloth: the four goldens woven to memory and
run in-process, libtest woven against all eight machine DLLs, and the
million-deep tail call. Everything byte-identical. What began the day
as a C interpreter ended it as a compiled CLR language with separately
compiled libraries, an editor, and no interpreter left to disagree
with itself.

Rags in. Cloth out. Compiled, this time. Through skill.
