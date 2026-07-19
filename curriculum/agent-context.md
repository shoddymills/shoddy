<!--
  Copyright (c) 2026 Stephen Vincent Foster. All rights reserved.

  This file is part of the Shoddy Language project.
  Licensed under the Shoddy Language License 1.0.0 (PolyForm Noncommercial
  License 1.0.0 with Additional Use Grant). See the LICENSE file in the
  project root for full terms.
-->

# Shoddy — Agent Context Reference

Single reference file for any Craftsman lesson prompt. Ground-truthed against
the C# reference implementation (`src/Shoddy.Devil` lexer/parser,
`src/Shoddy.Runtime` engine) as of 2026-07-19, not just the prose docs — where
`doc/GUIDE.html`, `doc/QUICKREF.html`, `doc/SPEC.html` were imprecise or
silent, this file follows the implementation and flags the gap in §18.
No heritage/backstory content; no marketing prose.

## 0. How to use this

- This is background reference, not a per-lesson authority. A lesson's own
  `<shoddy_facts>` block (see `lesson-01.md`) still defines what is *in scope*
  to teach at that point — cite this file to stay accurate, not to introduce
  concepts early.
- §17's scope map ties every Phase‑1 lesson to the sections it needs.
  Sections marked **[unscheduled]** (records, sum types, the stack dialect,
  the `machines/` library) are real, correct, and used in Phase 1's own
  worked examples for internal consistency — but no lesson has been written
  for them yet (see `roadmap.html` §6 open items).

## 1. Toolchain

```
mill run FILE.shoddy [args...]   compile in memory + run (default command)
mill weave FILE.shoddy           compile to FILE.dll (run: dotnet FILE.dll)
mill machine LIB.shoddy          compile a machines/*.shoddy to a machine DLL
mill gen FILE.shoddy             print the generated C#
mill lex FILE.shoddy             dump token lines (debug aid)
mill dap                         DAP server (editor-launched, not by hand)
```

VS Code extension (`vscode-shoddy/`): **Ctrl+Shift+B** or the ▶ button runs
the current file (`Cmd` on macOS); **F5** starts the native debugger (the
*perch*) — breakpoints, step over/into/out (F10/F11/Shift+F11), call stack,
Locals/Globals/Value-Stack panes. `Input` reads EOF under the perch; run
interactive programs with Ctrl+Shift+B instead. All errors print to the
terminal as `ERROR (line N): message`, exit code 1; line 0 (no line) omits
the parenthetical.

`Include "FILE.shoddy"` resolves relative to the including file first, then
the `SHODDYLIB` environment variable's directory; compiled runs prefer a
sibling `Shoddy.Machines.<Name>.dll` over splicing source. Include-once.

## 2. Lexical rules

- **Case-insensitive.** All matching is on an ASCII uppercase fold; original
  spelling is kept only for display (records print in declared case).
  Convention: PascalCase for keywords/types/functions, camelCase for
  variables/params.
- **Self-delimiting characters** (split tokens on sight, no whitespace
  needed): `( ) { } [ ] ,`. Everything else is whitespace-delimited — so
  `<=` must be written with no space, and operators like `>=` are ordinary
  space-separated words, not glued to neighbors (`x>=y` fails to lex as
  intended; write `x >= y`).
- **Numbers**: sign, digits, optional `.digits`, optional exponent — no hex,
  no `inf`/`nan` literals.
- **Strings**: double-quoted, single line only. Escapes: `\" \\ \n \t \r`.
  Unterminated string = parse error.
- **Comments**: `Rem` (whole token, case-folded) or `'` to end of line.
- **Reserved, illegal anywhere in a word**: `! % # $ ?`. Parse error if used.
- **Unary minus tokenizing quirk**: a token like `-n` that is *not* itself a
  numeric literal (`-3` is) is split into a `-` token and `n` — this is how
  `-n^2` lexes as `NEGATE n ^ 2`-ish rather than one weird token. `--` (stack
  signature arrow) is exempted from this split.

## 3. Program structure

Four top-level forms, all at column 0:

| Form | Meaning |
|---|---|
| `Def Name(p As t, ...) As r` | Applicative def: expression-syntax body, implicit `Take` of params, last statement's value is the result. |
| `Def Name ( a b -- c )` | Concatenative def: postfix stack body; the signature is documentation only (not checked). |
| `Def Name` (bare, no parens) | Concatenative def, undocumented effect. |
| `Def Main()` | Entry point. |
| `Type Name` + indented `field As Type` lines | Record declaration. |
| `Type Name = V1(f, ...) \| V2 \| ...` | Sum type: each variant is its own full record type (ctor + accessors + `With` + patterns); a bare variant has zero fields. |
| `Include "FILE.SHODDY"` | Splice a file, include-once, left margin only. |
| `Let Name = expr` (left margin, outside any `Def`) | Program-wide constant. All top-level `Let`s run once, in source order, before `Main`; each sees earlier ones and any `Def`. Outermost lexical scope — visible everywhere, closed over by quotations. |

**Include resolution mechanics** (exact rule, from `Lexer.ResolveInclude`):
for `Include "NAME"`, the *including file's own directory* is tried first —
this is the primary path, not a fallback, so a program can `Include` a
sibling file in its own working folder with zero extra setup
(`Include "helpers.shoddy"` next to `main.shoddy`), and subfolders/`..`
work the same way it's used inside this repo (`Include "../machines/matrix.shoddy"`
from `tst/libtest.shoddy`). Only if that lookup fails does it check
`SHODDYLIB`. Two consequences worth knowing: (1) there is **no
multi-directory search path** — `SHODDYLIB` is exactly one fallback
directory, so a second personal "global" library location isn't possible
the same way; relative paths are the only option for reusable code shared
across folders. (2) A locally-named file **silently shadows** a same-named
`machines/` file for that program (plain `File.Exists`, first match wins,
no warning) — e.g. an apprentice's own `str.shoddy` would quietly replace
the real string-helpers library. Give apprentice-authored library files
distinct names once a lesson introduces multi-file programs.

**Def header dispatch** (exact rule): if token 3 isn't `(`, it's concatenative
bare. If it is `(`, scan for a `--` at bracket-depth 0 before the matching
`)` — found it → concatenative-with-signature; not found → applicative
(expression body, `As type` annotations parsed and *ignored* at parse time,
checked only at runtime by the ops that touch the value).

`Type` and `Def`/`Let` names share one namespace — redeclaring any name in
any of these forms is `duplicate definition of X`. `Type` declarations must
precede their first use; `Def`s may call later `Def`s freely (defs are
resolved lazily).

## 4. Types

| Type | Notes |
|---|---|
| `Number` | IEEE double, only numeric type; exact integers to 2^53. |
| `String` | Immutable, single-line. |
| `Boolean` | `True`/`False`; **never interchangeable with Number.** |
| `Quotation` `[ t ... -- u ... ]` | Function value; lexical closure. |
| `List Of t` | Cons list, literal `{ 1, 2, 3 }`. Cheap `First`/`Rest`/`Prepend`; built for recursion. |
| `Array Of t` | Fixed length, O(1) `Nth`/`SetNth` (functional — returns a copy). Built for indexing. |
| User records (`Type`) | Nominal; constructor + accessors + `With` + `Case` patterns; structural `=`. |

Type variables: single lowercase letters (`t`, `u`), implicitly generalized.
Annotations (`As Number`, `As List Of t`, `As [ t -- Boolean ]`) are parsed
but **not statically checked** — a mismatch aborts at runtime with a line
number when the offending op runs, not at parse time.

## 5. Operators (loose → tight, verified against `Parser.OpPrec`)

| Level | Operators | Assoc | Notes |
|---|---|---|---|
| 1 | `Or` | — | Short-circuit in expression bodies (compiles to a conditional); the postfix core word `OR` is strict. |
| 2 | `And` | — | Same short-circuit treatment. |
| 3 | `= <> < > <= >=` | — | Numbers or strings ordered; `=`/`<>` also work on booleans, records (structural, recursive), arrays. |
| 4 | `+ - &` | left | `&` is string concatenation only (not numeric add-then-stringify). |
| 5 | `* / Mod` | left | `/` and `Mod` by zero both abort. `Mod` is C `fmod` semantics (sign follows dividend). |
| 6 | `^` | **right** | `2^3^2` = `2^(3^2)` = 512. `-n^2` binds as `-(n^2)` (classic BASIC) — unary `-` parses its operand at precedence 6, so it only reaches across a `^` chain, never across `+`/`*`/etc. |
| prefix | `Not` | — | Parses its operand at precedence 3 (binds tighter than `And`/`Or`, not tighter than comparisons). |
| prefix | unary `-` | — | Compiles to `NEGATE`; parses its operand at precedence 6. |

## 6. Control flow

**If/Else** — expression; each branch's last value is the If's value.
```
If cond Then
    result
Else
    other
```
Inline form: `Ifte(cond, thenVal, elseVal)` (both arms are quotations under
the hood, so only the taken branch runs).

**Select Case** — QBasic-style value matching, compiled to nested `Ifte`
over a one-time hidden binding of the scrutinee (it evaluates **exactly
once**, even across many clauses).
```
Select Case expr
    Case 0                              ' equality
    Case 1, 2, 3                        ' any-of (OR of equalities)
    Case 4 To 10                        ' inclusive range (>= 4 And <= 10)
    Case Is > 10                        ' any comparison op; "Is" optional
    Case Person(n, a) Where a < 18      ' destructure + guard; binds n, a
    Case Order(Person(n, _), it, t)     ' patterns nest to any depth; _ = ordinary binder, "ignored" by convention
    Case = Person("ANN", 34)            ' equality against a constructed record (NOT a pattern — the leading = forces it)
    Case None                           ' bare zero-field variant, equality
    Case Else                           ' default; must be last
```
No `Case Else` and nothing matches → runtime error
`"SELECT CASE: no matching CASE"`. `Case Type(...)` (no leading `=`) is
**always** a destructuring pattern, never an equality test, even if the
constructor happens to match a value structurally — use `Case = Type(...)`
for equality. Pattern binder names are in scope for that clause's `Where`
guard and body only.

## 7. Bindings & scope

`Let Name = expr` inside a body binds an **immutable** local for the rest of
that body — one binding per name, ever (`Let x = 5` then `Let x = x + 1` is
`duplicate binding`, not a rebind). At column 0 outside any `Def`, `Let`
instead declares a program-wide constant (§3). There is no separate `Const`
keyword — an always-immutable `Let` already is one. Function parameters are
an implicit `Take` (same binding discipline) at body entry.

## 8. Functions & function values

- **Calls** desugar left-to-right: `f(a, b)` → `a b f`.
- **Bare name in argument position auto-quotes** (is passed, not called):
  `Map(xs, Square)`, `Fold(xs, 0, +)`, `Each(xs, Print)`. A bare *local*
  binding instead evaluates normally (locals never auto-quote). Parenthesize
  to force a name to evaluate instead of quote: `f((g))`.
- **Sections** pre-load an operator's trailing args into a quotation:
  `>=(100)` ≡ `[ 100 >= ]` ("≥ with 100 pre-loaded"); works for any operator
  token, e.g. `*(2)` doubles.
- **`Fn(x, ...) => expr`** — inline lambda; sugar for `[ Take x, ... expr ]`.
- **`[ ... ]`** — raw quotation, legal anywhere, *required* inside
  concatenative bodies (otherwise `If`'s branches would run immediately).
- **`Call(f, a, b)`** applies a function value ≡ `a b f Call`. In
  `Call(F, args...)`, `F` is evaluated/emitted *last* (after its args), so a
  bare `F` there is not auto-quoted the way a normal argument would be.
- **Quotations are closures** — capture their creation environment
  (immutable bindings, so capture is one pointer, not a copy). A section
  like `>=(p)` still sees `p` whenever the receiving combinator later runs
  it.

## 9. Lists & arrays

| Word | Kind | Meaning |
|---|---|---|
| `{ a, b, c }` | ctor | List literal; elements are expressions, evaluated at construction. |
| `First(xs)` / `IsEmpty(xs)` | primitive, poly | Also work on arrays. |
| `Rest(xs)` / `Prepend(x, xs)` | primitive, **list-only** | On an array these would hide an O(n) copy — by design, not offered. |
| `Nth(xs, k)` | poly, 1-based | O(1) on arrays. Out-of-range: `NTH: index K out of range 1..N`. |
| `SetNth(xs, k, v)` | poly, functional | New sequence, position k = v. Arrays clone their element buffer; lists share the unaffected `QItem`s (both are safe because both are immutable). |
| `Length` / `Reverse` / `Concat` | poly (Concat: both operands must be the same kind) | |
| `ToArray` / `ToList` | convert | |
| `Dim(n, v)` | array ctor | n copies of v. Negative n aborts. |
| `Map Filter Fold Each` | poly, library-speed builtins | Preserve the input's kind (`Map` over an array yields an array). |
| `Range(a, b)` | → List | Inclusive, `a` through `b` stepping by 1 (if `a > b`, empty). |
| `Times(n, f)` | — | Run `f` n times, no accumulation. |

`FIRST`/`REST` of an empty sequence both abort (`FIRST of empty sequence` /
`REST of empty list`); check `IsEmpty` first — this is exactly why `And`/`Or`
short-circuit: `Not IsEmpty(xs) And First(xs) > 0` is safe.

## 10. Records & pattern matching

```
Type Person
    Name As String
    Age  As Number
```
Declares, at once: a **constructor** — positional `Person("ANN", 34)` (arity
checked at parse time) or named `Person(Name = "ANN", Age = 34)` (not
mixable); **accessor** words per field — `Name(p)`, `Age(p)` — ordinary
functions, so they compose (`Map(people, Name)`); two Types may share a field
name, the accessor dispatches on the record's runtime tag (don't name a
field after a builtin — the builtin wins); and **`With(p, Age = 35, ...)`**
for functional update.

Records are immutable, compare structurally and recursively with `=`, nest
freely, and print as valid input syntax: `Person(Name = "ANN", Age = 34)`.

**Sum types**: `Type Shape = Circle(r) | Rect(w, h)` — each variant is a full
record type; `Select Case` dispatches on the variant. A bare variant (`Type
Option = Some(v) | None`) has zero fields; construct as `None()` or bare
`None` *outside* argument position, match with `Case None` (structural
equality) or `Case None()`.

Patterns nest to any depth (`Case Order(Person(n, _), items, total)`); `_` is
an ordinary binder name, conventionally meaning "ignored."

## 11. The concatenative core (stack dialect)

Every program compiles to this; it is also directly writable.

| Word | Effect |
|---|---|
| `Dup` | `( a -- a a )` |
| `Drop` | `( a -- )` |
| `Swap` | `( a b -- b a )` |
| `Over` | `( a b -- a b a )` |
| `Rot` | `( a b c -- b c a )` |
| `Nip` | `( a b -- b )` |
| `Tuck` | `( a b -- b a b )` |
| `Depth` | `( -- n )` current stack size |
| `Take a, b` | Pop into immutable names, topmost value → rightmost name. |
| `[ code ]` | Quote without running. |
| `cond If Then` / `Else` | Postfix conditional; ends its line; indented branches follow (same block rules as expression-body `If`). |

Bare `Def Name` bodies are pure postfix; `Def Name(...)` (applicative)
bodies never contain raw stack code; mixed pipelines like
`Range(1, n) Filter(IsEven) Map(Square) Fold(0, +)` are still applicative —
"pipeline chaining" is adjacency of *expression* calls, not stack words.

**Desugaring (surface → core)**, the complete set:

| Surface | Core |
|---|---|
| `f(A, b)` | `A b f` |
| `A + b * c` | `A b c * +` |
| `xs Filter(p)` | `xs p Filter` |
| `Let x = e` | `e Take x` |
| `Map(xs, f)` | `xs [ f ] Map` |
| `>=(100)` | `[ 100 >= ]` |
| `Fn(x) => body` | `[ Take x body ]` |
| `If c Then a Else b` | `c [ a ] [ b ] Ifte` |
| `Call(f, a, b)` | `a b f Call` |
| `Age(p)` | `p Age` |
| `Person(Name = n, Age = a)` | `n a Person` (declaration order, not written order) |
| `{ 1, 2, 3 }` | list construction (each element is a quotation yielding one value) |

Tail recursion (self-calls only) compiles to a loop — constant stack space;
mutual recursion still grows the call stack. The generated C# for a
self-tail-recursive def literally contains `continue; // self tail call —
the loop is the TCO` (visible via `mill gen`).

## 12. Standard library (`machines/`) — **[unscheduled]** for lessons, verified against source

Everything below is written *in* Shoddy — self-hosted, no special status vs.
user code. `Include "seq.shoddy"` etc. Words are pure unless noted.

**seq.shoddy** (no dependencies)
| Word | Signature | Notes |
|---|---|---|
| `Sum(xs)` `Product(xs)` | `List Of Number → Number` | `Fold(xs,0,+)` / `Fold(xs,1,*)` |
| `Maximum(xs)` `Minimum(xs)` | `List Of Number → Number` | `Fold(xs, First(xs), Max/Min)` — **aborts on empty** (`First` does) |
| `Average(xs)` | → `Number` | `Sum(xs)/Length(xs)` — aborts (div by zero) on empty |
| `Any(xs,p)` `All(xs,p)` | → `Boolean` | Fold over `Or`/`And` — actually visits every item (no short-circuit exit) |
| `CountIf(xs,p)` | → `Number` | `Length(Filter(xs,p))` |
| `Contains(xs,v)` | → `Boolean` | `Any(xs, =(v))` |
| `IndexOf(xs,v)` | → `Number`, 1-based, 0 = absent | Linear scan via recursion |
| `Taken(xs,n)` / `DropN(xs,n)` | → `List Of t` | First n / all-but-first-n; `n<=0` → resp. `{ }` / `xs` |
| `Append(xs,x)` | → `List Of t` | `Concat(xs, { x })` |
| `Last(xs)` | → `t` | `Nth(xs, Length(xs))` — aborts on empty |
| `Flatten(xss)` | → `List Of t` | `Fold(xss, { }, Concat)` |
| `Sort(xs)` | → `List Of Number`, ascending | Quicksort via `Filter` (not in-place; not stable-documented) |
| `Type Pair: Fst, Snd` | — | Generic 2-field record |
| `Zip(xs,ys)` `ZipWith(xs,ys,f)` | → `List Of Pair` / `List Of v` | Truncates to the shorter input |

**str.shoddy** (no dependencies; built on `Instr`/`Left`/`Right`/`Mid`)
| Word | Notes |
|---|---|
| `Split(s, sep)` | → `List Of String`. **Aborts** (`SPLIT: EMPTY SEPARATOR`) if `sep` is `""`. |
| `Join(xs, sep)` | List → String. `""` on empty list. |
| `Trim(s)` | Strips leading/trailing **space characters only** (not tab). |
| `Replace(s, old, new)` | **Aborts** on empty `old`. Recursive, replaces every occurrence, not just first. |
| `StartsWith` / `EndsWith(s, p)` | → `Boolean` |
| `StrRep(s, n)` | Repeat `s` n times; `n<=0` → `""` |

**dict.shoddy** (includes seq.shoddy) — association list over `Pair`, keys via `=`
| Word | Notes |
|---|---|
| `DictPut(d,k,v)` | New dict, k set (replaces existing — implemented as delete-then-prepend, so **last write wins and the entry moves to the front**). Empty: `{ }`. |
| `DictGet(d,k)` | **Aborts** (`DICTGET: KEY NOT FOUND`) if missing. |
| `DictGetOr(d,k,dflt)` | Safe form. |
| `DictHas(d,k)` → `Boolean` | |
| `DictDel(d,k)` | New dict without k (no-op if absent). |
| `DictKeys(d)` / `DictVals(d)` | Order = current internal list order (most-recently-put first), not insertion order. |

**money.shoddy** — `Type Money: Cents` (whole cents, exact to 2^53 ≈ $90T)
| Word | Notes |
|---|---|
| `Dollars(d)` | `Money(Round(d*100))` |
| `MoneyVal(s)` | `Dollars(Val(s))` |
| `MoneyAdd/MoneySub` | Exact, no rounding needed |
| `MoneyMul(m, f)` | **Only rounding site besides `Dollars`** — `Round(Cents*f)` |
| `MoneySum(xs)` | `Fold(xs, Money(0), MoneyAdd)` |
| `MoneySplit(m, n)` | n parts, penny-preserving: first `Cents mod n` parts get one extra cent. |
| `MoneyFmt(m)` | `"$12.50"`; negative → `"-$"` prefix via recursive negation. |

**file.shoddy** (includes str.shoddy) — line-oriented, over the whole-file builtins
| Word | Notes |
|---|---|
| `ReadLines(path)` | Normalizes `\r\n`→`\n` (strips `\r` globally, not just at EOL), splits on `\n`, drops one trailing empty line if the file ended in `\n`. Empty file → `{ }`. |
| `WriteLines(path, xs)` | `Join(xs,"\n") & "\n"` — always newline-terminated. |
| `AppendLine(path, s)` | `AppendFile(path, s & "\n")` |

**recio.shoddy** — offset arithmetic over the binary builtins; records numbered from 1
`RecPos(k,size)` = `1+(k-1)*size` · `RecSeek` · `RecCount(f,size)` =
`Floor(BSize(f)/size)` · `GetRec`/`PutRec(f,k,size,reader/writer,[v])` seek
then delegate · `AppendRec` writes after `RecCount+1` · `AllRecs` maps
`GetRec` over `Range(1, RecCount)`.

**matrix.shoddy** (includes seq.shoddy) — `Type Matrix: Rows, Cols, Cells (flat row-major Array)`
`Mat(r,c,vals)` checks `Length(vals)=r*c`, else `MAT: WRONG NUMBER OF CELLS`
· `MatFill(r,c,v)` · `Ident(n)` · `MatFromRows` · `MatGet/MatSet` O(1),
1-based (`MatSet` is pure — `SetNth` under `With`) · `MatRow`/`MatCol` →
`Array` · `Dot`/`VSub`/`VScale` flat-vector helpers · `MatMul` checks
`Cols(a)=Rows(b)` → `MATMUL: DIMENSION MISMATCH` · `MatAdd` checks equal
dims → `MATADD: DIMENSION MISMATCH` · `MatScale` · `Transp` · `MatVec`
checks `Cols(m)=Length(v)` → `MATVEC: DIMENSION MISMATCH` · `MatShow`
(effectful — prints each row).

**isam.shoddy** (includes seq.shoddy) — indexed-sequential file, `Type Isam:
Fh, RecSize, Rd, Wr, KeyOf, Index (sorted List Of Pair), Free (List Of
Number)`. Disk layout: each slot is `1 + RecSize` bytes (1 live-flag byte +
payload). **Every mutating word returns a new handle — the file write is a
side effect, but the handle's Index/Free are ordinary immutable values; a
stale handle after a mutation has an out-of-date Index.** `IsamOpen` scans
every slot once to rebuild `Index`/`Free`. `IsamInsert` reuses a freed slot
if any, else appends. `IsamDelete` tombstones the flag byte + frees the
slot (does not shrink the file). Keys must support `<` (Number or String).
One live handle at a time; no crash safety, no concurrency.

## 13. I/O, errors, program args

| Word | Signature | Notes |
|---|---|---|
| `Print(v)` | any value | Newline-terminated. Top-level: strings print **raw** (unquoted); everything else prints its `Repr` (§14). Effectful — convention keeps it inside `Main`. |
| `Input(prompt)` | → `String` | Prints prompt (no newline), reads one line. `""` at EOF, never aborts. |
| `Error(msg)` | `( s -- )` | Aborts with msg + current line. |
| `Assert(cond, msg)` | `( bool s -- )` | Aborts (`ASSERTION FAILED: msg`) unless cond. |
| `ReadFile(path)` → `String` | whole file | Missing file: `READFILE: cannot open 'path'`. |
| `WriteFile` / `AppendFile(path, s)` | — | Create/overwrite or append; open failure aborts. |
| `FileExists(path)` → `Boolean` | | |
| `DeleteFile(path)` | — | Aborts if the file doesn't exist. |
| `Args()` | → `List Of String` | **Program's command-line arguments — not documented in GUIDE/QUICKREF/SPEC**, but a real, exported builtin (`ARGS` in `Engine.BuiltinWords`). |

**Binary random-access files** — 1-based byte positions; `Get*`/`Put*`
advance the position; up to 16 files open at once.
`BOpen(path)`→handle (creates if absent) · `BClose(h)` · `Seek(h,pos)` ·
`BPos(h)`/`BSize(h)` · `PutNum`/`GetNum` (8-byte IEEE double) ·
`PutBool`/`GetBool` (1 byte) · `PutStr(h,s,len)` (fixed field, zero-padded;
`sb.Length > len` aborts — **never silently truncates**) · `GetStr(h,len)`
(reads `len` bytes, strips at first NUL). Reading past EOF always aborts
(`... : read past end of file`).

## 14. Value formatting (for "predict before you run")

- **Numbers**: integral value with `|d| < 1e15` → printed with no decimal
  point (`Fact(10)` → `3628800`, not `3628800.0`). Otherwise C's `%.10g`:
  10 significant digits, trailing zeros trimmed, scientific notation
  (`1.234567890e+12`) once the decimal exponent is `< -4` or `>= 10`.
- **`Print` vs `Repr`**: `Print` of a bare `String` writes it **unquoted**.
  Every other case (including a string *inside* a list/record/array) uses
  `Repr`, which quotes strings.
- **Booleans**: `True` / `False` (capitalized, never `1`/`0`).
- **Records**: `TypeName(Field1 = v1, Field2 = v2)` — declared spelling and
  field order, valid as input syntax.
- **Arrays**: `Array(v1, v2, v3)`.
- **Lists / quotations**: `[ v1 v2 v3 ]` — space-separated, no commas.

## 15. Runtime & parse error messages (exact strings, for coaching accuracy)

| Situation | Message |
|---|---|
| Empty value stack popped | `stack underflow` |
| Wrong value kind popped | `X expects a NUMBER/STRING/BOOLEAN/QUOTATION/RECORD, got Y` (Boolean case adds `(booleans are not numbers in Shoddy)`) |
| `/` or `Mod` by 0 | `division by zero` / `MOD by zero` |
| `Sqr`/`Log` of an invalid domain | `SQR of negative number` / `LOG of non-positive number` |
| `^` producing non-finite | `invalid exponentiation` |
| Unresolved name | `unknown word: X` |
| `Assert` failure | `ASSERTION FAILED: msg` |
| Comparison type mismatch | `X expects two NUMBERs or two STRINGs` |
| `Val` on non-numeric text | `VAL: 's' is not a number` |
| `Asc("")` | `ASC of empty string` |
| Sequence op on empty | `FIRST of empty sequence`, `REST of empty list` |
| Index out of range | `NTH: index K out of range 1..N` (same shape for `SETNTH`) |
| Missing/bad file | `READFILE: cannot open 'path'`, similarly for write/append/delete/bopen |
| Field on wrong type | `TypeName has no field FIELD`, `X expects a RECORD, got Y` |
| `!  %  #  $  ?` in a word | `'tok': the characters ! % # $ ? are reserved and may not appear in words` |
| Redeclaration | `duplicate definition of X`, `duplicate binding`, `duplicate field X` |
| Bad `SELECT CASE` | no match with no `Case Else` → `SELECT CASE: no matching CASE` |
| Layout errors | `unexpected indent`, `ELSE without matching IF`, `missing ] (quotations may not span lines)`, `expected indented block after IF/ELSE` |

All of the above are wrapped by the top-level handler as
`ERROR (line N): message` (§1).

## 16. Full builtin word list (authoritative — `Engine.BuiltinWords`)

```
DUP DROP SWAP OVER ROT NIP TUCK DEPTH
+ - * / MOD NEGATE ABS MIN MAX SQR FLOOR CEIL ROUND ^
SIN COS TAN ATN EXP LOG PI RND
ERROR ASSERT INSTR
= <> < > <= >=
AND OR NOT TRUE FALSE
& LEN STR VAL LEFT RIGHT MID CHR ASC UPPER LOWER
PRINT READFILE WRITEFILE APPENDFILE FILEEXISTS DELETEFILE
BOPEN BCLOSE SEEK BPOS BSIZE PUTNUM GETNUM PUTBOOL GETBOOL PUTSTR GETSTR
INPUT ARGS
CALL IFTE MAP FILTER FOLD EACH TIMES RANGE LENGTH REVERSE CONCAT
ISEMPTY FIRST NTH SETNTH DIM TOARRAY TOLIST REST PREPEND
```
Everything else callable (`Sum`, `Split`, `MatMul`, ...) is library Shoddy
from `machines/`, not a builtin.

## 17. Curriculum scope map (Phase 1)

| Lesson | New concept | Reference sections |
|---|---|---|
| 1 — Hello, Shoddy | `Print`, run cycle, `Rem` | §1, §2, §3 (Main), §14 (Print vs Repr), §15 (error format) |
| 2 — Values & Names | `Let`, `Number`/`String`/`Boolean`, `&` | §4, §6, §7, §14 (number formatting) |
| 3 — Decisions | `If/Then/Else`, `Select Case`, ranges/comparisons | §5, §6 |
| 4 — Doing Things Again | `Range`, `Each`, `Times` (no loops) | §9, §16 |
| 5 — Building Your Own Words | `Def` — user functions | §3, §8 |
| 6 — A Word That Calls Itself | recursion via `First`/`Rest`/`IsEmpty` | §9 |
| 7 — Words That Take Words | `Fn`, `Map`/`Filter`/`Fold` | §8, §9 |

**[unscheduled]** — correct and internally used (e.g. `tst/gradebook.shoddy`
uses records) but no lesson exists yet per `roadmap.html` §6/§7: records &
`Type` (§10), sum types & pattern matching (§6, §10), the concatenative
stack dialect (§11), the `machines/` standard library (§12), binary/ISAM
file I/O (§13). Flag to the curriculum author before citing these in a
lesson prompt — see recommendation in the accompanying chat response.

## 18. Gotchas (deduped from `GUIDE.html` §10, source-verified)

- A call must finish on the line it starts; only a *statement* (pipeline)
  may continue on deeper-indented lines.
- `Type` and `Include` must appear before first use; `Def`s may forward-call.
- `Maximum({ })`/`First({ })`/`Last({ })` all abort — no identity element.
- In argument position a bare name is passed, not called (§8) — no
  parentheses on `Square` in `Map(xs, Square)`.
- `Input` always returns `String`; convert with `Val`.
- `Print` takes exactly one value — build the line with `&` first.
- BASIC-veteran traps: no sigils (`a$`/`n%` reserved, illegal), no `goto`,
  no mutable `Dim x(10)` (this `Dim(n,v)` builds an immutable array), no
  `for`/`next`.

## 19. Known doc inconsistencies fixed here (vs. GUIDE/QUICKREF/SPEC prose)

- **`Round` is round-half-up** (`Floor(x + 0.5)`), not round-half-away-from-
  zero — `Round(-0.5)` is `0`, `Round(-1.5)` is `-1`. No doc states the
  direction; only the source does.
- **`Left`/`Right`/`Mid` clamp rather than error** on out-of-range lengths
  or start positions (e.g. `Mid("HI", 99, 3)` → `""`, not an error).
- **`Chr(0)` returns `""`**, not a one-character NUL string (mirrors C
  string semantics from the reference implementation).

Resolved directly in `doc/QUICKREF.html` (2026-07-19): `Pi` is now listed as
`Pi()` with a note that it's a zero-argument function, not a bare constant;
`Args()` is now documented in the I/O table.
