# Shoddy Sound â€” Implementation Prompt

## The name

A West Riding mill town ran on sound. The working day was called and dismissed
by the mill **buzzer** â€” the steam hooter on the engine house roof, heard
across the whole valley â€” and inside the shed the din of the looms was so
total that weavers learned to lip-read over it. The buzzer was the one sound
the mill made *on purpose*.

The **buzzer** is Shoddy's sound device: the machine layer and the mill's
audio code are named for it (`machines/buzzer.shoddy`, `Buzzer.cs`). The
builtin words themselves keep their BASIC lineage â€” `SOUND` and `PLAY` are
what GW-BASIC called them, and Shoddy honours its ancestors â€” just as the
scribbler kept `Input` and `Print`.

---

## Context

Shoddy is a purely functional BASIC compiled onto a concatenative (Forth/Joy)
stack core, running on .NET 10. Every path is compiled: the front end lexes
and parses to a `ShoddyProgram`, the weaver generates C#, and Roslyn compiles
it â€” `mill run` just keeps the resulting assembly in memory.

```
src/
  Shoddy.Runtime/     â€” Engine.cs (stack VM + builtins), Model.cs (VType/Value/Node),
                        Scribbler.cs (window seam, Ticker), Font8x8.cs, Format.cs,
                        Machines.cs, ShoddyError.cs
  Shoddy.Devil/       â€” Lexer.cs, Parser.cs
  Shoddy.Compiler/    â€” Weaver.cs, CodeGen.cs, Machines.cs
  Shoddy.Mill/        â€” Program.cs (CLI), Dap.cs (DAP server),
                        ScribblerWindows.cs, ScribblerWindow.cs,
                        MainThreadDispatcher.cs
  Shoddy.Tests/
machines/             â€” stdlib: seq, str, matrix, dict, stats, file, isam, simplex,
                        mps, vt100, random, recio, money, clock, scribbler, turtle,
                        plotter, keys
```

The scribbler (graphics) is already in place and is the template for
everything here. Facts an implementer needs and should not re-derive:

- New builtins are `case "WORD":` arms in `Engine.Builtin()` **and** entries
  in the static `Engine.BuiltinWords` `HashSet<string>`. `CodeGen` consults
  `BuiltinWords` at weave time; a word missing from the set is an unknown word.
- Shoddy folds every name to uppercase. `CodeGen.EmitWord` resolves
  **local â†’ global â†’ def â†’ external def â†’ type â†’ builtin â†’ record field**.
  A `Def` silently shadows a builtin; a builtin silently shadows a record
  field accessor. Check every new name both ways.
- **Known collision, found in advance:** `machines/stats.shoddy` exports
  `Def Freq(xs)`. The buzzer machine must not define `Freq` â€” use `MidiFreq`
  (see below). Record fields must likewise avoid `Freq` folding.
- `ScribblerRegistry` (`Scribbler.cs`) is the precedent for a runtime/mill
  seam: static delegates the mill installs at startup, null when headless.
  `BuzzerRegistry` below follows it.
- `Ticker` (`Scribbler.cs`) is the monotonic clock behind `TICKS` and event
  timestamps. Reuse it; never introduce a second clock.
- Scribbler events deliberately pass GLFW key auto-repeat through as further
  `KeyDown`s (`ScribblerWindow.WireInput`). A synth program must filter
  repeats itself â€” see the demo.
- Development happens on both Windows and macOS (`build.cmd`, `build.sh`).

Naming conventions follow the codebase: C# types in PascalCase
(`BuzzerRegistry`), Shoddy surface words in PascalCase (`NoteOn`, `Play`),
folded builtin names in uppercase (`NOTEON`) wherever the engine's switch and
word set are meant.

---

## Task: give the mill a voice

The mill gains an audio device (Silk.NET.OpenAL, backed by OpenAL Soft) and
six builtins: five that cover the three ways a program can own a note's
lifetime, plus a per-channel volume.
No new value type, no new thread of our own, no window required â€” a console
program may beep.

### Design principles

- **No Silk.NET in `Shoddy.Runtime`.** The runtime validates arguments and
  calls through `BuzzerRegistry` delegates. The mill owns OpenAL entirely.
- **No new `VType`.** Sound needs no handle: effects are anonymous and
  channels are numbers, chip-style. (WAV samples would need a handle â€” that
  is deliberately left for later, and nothing here forecloses it.)
- **Sound never blocks and never fails the program.** Every sound word is
  fire-and-forget from the program's point of view: a beep must not stall a
  game loop, and a missing sound card must not kill a program whose logic is
  fine. Argument errors (bad channel, negative duration) still raise â€” those
  are program bugs.
- **Silence is a no-op, not an error.** Headless (woven output run via
  `dotnet`, tests, a machine with no audio device) the delegates are null and
  every sound word does nothing. This is the `SCRIBBLERBLIT`/`OnBlit`
  precedent â€” presentation degrades quietly â€” and deliberately *not* the
  `SCRIBBLEROPEN` precedent, because nothing ever blocks waiting on sound, so
  there is no hang to fail loudly about.
- **OpenAL does the mixing.** Sources play concurrently and the library mixes
  them in its own output thread. We never write a mixer, a callback, or an
  audio thread.
- **Keep the non-sound path free.** The device opens lazily on the first
  sound call. A program that never beeps never touches OpenAL.

---

## Three models of note lifetime

Everything in the API falls out of who owns a note's duration:

1. **Fire-and-forget** â€” `SOUND freq ms`. The *runtime* owns the lifetime.
   An anonymous pool of sources; when the pool is exhausted the oldest
   playing voice is stolen (classic sound-chip behaviour: never block, never
   error). Game effects. A chord is three consecutive calls â€” they start
   within microseconds and OpenAL mixes them.

2. **Key-state** â€” `NOTEON ch freq` / `NOTEOFF ch`. The *program* owns the
   lifetime: a note has no duration at start time, it sounds until released.
   Voice stealing would be wrong here, so these use explicitly addressed
   channels. Playing on a busy channel is deterministic replace, the
   program's own choice â€” no stealing heuristics.

3. **Sequenced** â€” `SOUNDQUEUE ch freq ms`. The *score* owns the timing.
   Notes append to a channel's buffer queue and play back-to-back
   **sample-accurately** via OpenAL buffer queueing. Do not drive melodies
   from `SCRIBBLERSETINTERVAL` ticks: tick timing is millisecond-jittery and
   drops missed ticks by design â€” fine for animation, audibly lumpy for
   rhythm. Queueing is why a player-piano program can hand over a whole tune
   and return to its event loop.

Channels serve models 2 and 3; the anonymous pool serves model 1; both
coexist. **Eight channels, numbered 1â€“8** (Shoddy is 1-based everywhere).

---

## The runtime/mill seam

```csharp
// Shoddy.Runtime â€” Buzzer.cs. No Silk.NET, nothing but BCL.
// Follows the ScribblerRegistry precedent for a static seam.
public static class BuzzerRegistry
{
    // Installed once by the mill at startup; null when no audio backend is
    // running (woven output via dotnet, headless tests). All six are
    // called from the Shoddy thread â€” OpenAL has no thread affinity, so
    // unlike the scribbler there is no main-thread dispatch and no
    // blocking marshal. The mill implements them directly.
    public static Action<double, double>? Sound;        // (freqHz, ms)  pool
    public static Action<int, double>?    NoteOn;       // (ch, freqHz)  hold
    public static Action<int>?            NoteOff;      // (ch)
    public static Action<int, double, double>? Queue;   // (ch, freqHz, ms)
    public static Action<int>?            Stop;         // (ch) silence + flush
    public static Action<int, double>?    Gain;         // (ch, vol 0..1)
}
```

The runtime side validates and dispatches; every delegate call is
`?.Invoke` â€” null means silence. There is no handle, no queue, no semaphore:
sound produces no events and nothing ever waits on it.

---

## Sound builtins

Six words, added to both `Engine.Builtin()` and `Engine.BuiltinWords`.
All six are **zero-result, like `PRINT` and `SLEEP`** â€” there is no handle
to return and no arity trap to avoid.

```
SOUND       ( freq ms -- )
    Fire-and-forget square-wave blip from the anonymous pool. freq must be
    > 0, ms must be >= 0 (0 is a legal no-op). Pool exhausted: steal the
    oldest playing effect, silently.

NOTEON      ( ch freq -- )
    Channel ch starts sounding freq and holds until NOTEOFF. On a channel
    already sounding a held note: retune in place (no re-attack) â€” this is
    what a keyboard glide or pitch-bend wants. On a channel with a queue:
    the queue is flushed first (deterministic replace).

NOTEOFF     ( ch -- )
    Release channel ch's held note. A channel with nothing held: no-op.

SOUNDQUEUE  ( ch freq ms -- )
    Append a note to channel ch's queue; notes play strictly back-to-back,
    sample-accurate, with no further involvement from the program. freq 0
    is a REST of ms milliseconds â€” a score needs silence it can count on.
    Queueing onto a channel holding a NOTEON note releases the note first.

SOUNDSTOP   ( ch -- )
    Silence channel ch now: release any held note, flush any queue. The
    anonymous SOUND pool is not addressable and cannot be stopped.

SOUNDGAIN   ( ch vol -- )
    Set channel ch's volume, 0..1, default 1. Sticky: applies to the held
    note or queue playing now and everything after, until set again â€”
    SOUNDSTOP flushes notes, not gain. Channel volume only; per-note MML
    volume (Vn) is deliberately left for later, with the waveforms. The
    anonymous SOUND pool always plays at full gain.
```

Validation (raises `ShoddyError` via `Die`, these are program bugs):

- `ch` outside 1â€“8: `"NoteOn: channel must be 1..8, got 12"`.
- `freq <= 0` where a pitch is required (`SOUND`, `NOTEON`; `SOUNDQUEUE`
  allows exactly 0 as a rest). Clamp nothing â€” octave math that drives a
  frequency to zero or below is easier to find when it raises than when a
  clamped substitute plays.
- `ms < 0`.
- `vol` outside 0â€“1 (`SOUNDGAIN`). No boost above 1 â€” the 0.2 synthesis
  amplitude is the clipping headroom and gain must not spend it.
- A per-channel queue cap, tracked in the runtime so it raises headless too.
  The runtime gets no drain feedback through the seam, so it cannot count
  pending notes â€” instead each channel carries the `Ticker` time at which
  its queue drains (`SOUNDQUEUE` extends it by ms; `SOUNDSTOP` and `NOTEON`
  reset it). More than five minutes queued ahead raises with a message
  naming the fix: feed long scores incrementally from Tick events. Blocking
  would violate "sound never blocks"; dropping notes would corrupt the
  music; loud is right.

Frequencies are Hz and fractional (`Number` all the way down) â€” equal
temperament comes out irrational and must not be rounded.

---

## Mill audio layer (`Buzzer.cs` in `Shoddy.Mill`)

Add to `Shoddy.Mill.csproj`: `Silk.NET.OpenAL` (`2.*`) and the OpenAL Soft
native package (`Silk.NET.OpenAL.Soft.Native.*` for the target platforms â€”
pin whatever the Silk.NET 2.x line currently ships, the way GLFW natives ride
in via `Silk.NET.Windowing`). Nothing is added to `Shoddy.Runtime`.

### Device lifecycle

- **Lazy init, first sound call, Shoddy thread.** Open the device and one
  context, `alcMakeContextCurrent` once; OpenAL calls are thread-safe after
  that. A few milliseconds on the first beep is acceptable; a startup cost
  on every `mill lex` is not.
- **Init failure â†’ permanent silence.** No audio device (CI, a server, a
  container) must not crash or error a program. Failed init sets a flag and
  every delegate becomes a no-op â€” identical behaviour to headless, which is
  the point.
- Delegates are installed at mill startup on both the `mill run` and
  `mill dap` paths (init is lazy, so this costs nothing). Sound works under
  the debugger; the scribbler's dap restriction is about the window loop and
  does not apply here.
- **Teardown drains, then stops.** At mill exit (the same place
  `ScribblerWindows` tears down): held `NOTEON` notes stop immediately â€”
  they would never end â€” but queued notes are allowed to finish, polled at
  ~10 ms, capped at the total queued duration plus a second. "Queue a tune,
  return from Main" is a complete program, the audio sibling of "draw a
  picture, return".

### Synthesis

- 44100 Hz, 16-bit, mono, square wave. Amplitude around 0.2 of full scale â€”
  eight channels plus the effect pool must sum without clipping, and OpenAL
  clamps harshly.
- **Every fixed-duration buffer bakes in a ~5 ms linear attack and release.**
  Without the ramps every note edge clicks; with them, queued melodies
  articulate naturally.
- **Held notes loop a buffer of whole periods.** Loop an integer number of
  complete cycles (enough to exceed ~50 ms) so the loop seam is
  phase-continuous; a fractional period at the seam buzzes. The buffer is
  synthesized at the requested frequency; a retune (`NOTEON` on a sounding
  channel) sets `AL_PITCH = newFreq / bufferFreq` and touches nothing else.
- **Cache synthesized PCM keyed by `(freq, ms)`**, bounded (a few hundred
  entries, evict least-recent). The same jump-blip every frame must cost
  one synthesis, not sixty per second. AL buffers are never shared: each
  play gens its own buffer from the cached PCM (upload is a memcpy â€” the
  synthesis was the cost), owned by exactly one source slot and deleted
  when that slot moves on. Nothing can ever delete a buffer still in use.

### Sources

- **Effect pool:** ~16 sources. Allocation sweeps for `Stopped` sources to
  recycle; none free â†’ steal the source with the oldest start (track a
  running sequence number). Never block, never error.
- **Channels:** 8 dedicated streaming sources. `SOUNDQUEUE` first unqueues
  any processed buffers, then queues the new one. **The classic streaming bug:** a source
  that ran its queue dry goes to `Stopped`; after queueing, check the state
  and call `Play` again or the rest of the melody never sounds.
- `SOUNDGAIN` is one `AL_GAIN` set on the channel's dedicated source â€” the
  `AL_PITCH` retune precedent: no new buffer, no cache involvement. Track
  the set value per channel; it is what ramps restore.
- `NOTEOFF`'s release ramp is a short loop on the calling thread stepping
  the source gain down over ~5 ms, then stop, then restore the channel's
  set gain. Five milliseconds on the Shoddy thread is imperceptible and
  buys click-free releases without an envelope engine.

---

## Headless paths

`BuzzerRegistry` delegates are null in exactly the situations
`ScribblerRegistry.CreateScribbler` is, plus one more (device init failure,
above). In every case each sound word validates its arguments â€” bad
arguments raise even in silence, so headless tests exercise real code â€” and
then does nothing. Document in the machine header: sound is audible under
`mill run` and `mill dap`; woven output run via bare `dotnet` is silent.
(Lifting that limitation is the same future factoring the scribbler doc
describes â€” do not foreclose it, do not do it now.)

This is the one deliberate divergence from the scribbler, which fails loudly
headless. The scribbler must, because a headless `SCRIBBLERWAIT` would hang
forever. Sound has no wait, so a silent no-op is safe â€” and it means a
program's tests never fail because the build agent has no sound card.

---

## `machines/buzzer.shoddy`

Standard `Rem` copyright header; `Include "seq.shoddy"`. Compile with
`mill machine machines/buzzer.shoddy`.

**Do not define `Freq`** â€” `stats.shoddy` already exports it and both fold
to `FREQ`. The frequency word is `MidiFreq`. For the same reason the note
record's fields are `FreqHz` and `Ms`, never `Freq` (a Def outranks a field
accessor, so a program including stats would silently break a `Freq` field).

```basic
Type BuzzerNote
    FreqHz As Number     Rem 0 = rest
    Ms As Number

Def MidiFreq(midi As Number) As Number
    Rem 440 * 2 ^ ((midi - 69) / 12) â€” A4 = 69 = 440 Hz, equal temperament

Def NoteMidi(name As String) As Number
    Rem "C4" -> 60, "F#3" -> 54, "Bb5" -> 82. Scientific pitch: C4 is
    Rem middle C. Letters A-G, "#" sharp, "b" flat, octave digit required.

Def NoteFreq(name As String) As Number
    Rem MidiFreq(NoteMidi(name))

Def MmlNotes(mml As String) As List Of BuzzerNote
    Rem PURE â€” parse MML to a note list, no I/O. This split is what makes
    Rem the parser testable headless and in golden tests.

Def Play(ch As Number, mml As String)
    Rem SoundQueue each BuzzerNote from MmlNotes onto ch. Fire-and-forget:
    Rem returns as soon as the tune is queued, which is immediately.

Def PlayNotes(ch As Number, notes As List Of BuzzerNote)
    Rem the queue half of Play, for programmatically built scores
```

### MML (Music Macro Language), GW-BASIC subset

Case-insensitive, whitespace ignored. `PLAY "T120 O4 L8 C D E F G2"`:

- `A`â€“`G` â€” a note; optional `#`/`+` (sharp) or `-` (flat); optional length
  number (`4` = quarter note, `8` = eighthâ€¦); optional trailing `.`
  (dotted, Ã—1.5).
- `On` â€” octave, 0â€“8, default 4 (scientific: O4 contains middle C â€”
  document this, GW-BASIC's octaves sat one lower).
- `<` / `>` â€” octave down / up.
- `Ln` â€” default length, default 4.
- `Tn` â€” tempo in quarter-notes per minute, default 120.
- `Pn` / `Rn` â€” a rest of length n (dotted allowed) â†’ `FreqHz` 0.
- Whole note = `4 * 60000 / T` ms; length `n` divides it.

A malformed string raises `Error` naming the offending position. GW-BASIC's
`MN`/`ML`/`MS` articulation modes and `Nn` are deliberately left for later.

---

## Example program (`tst/buzzer-demo.shoddy`)

All three lifetime models in one small program, with no collection juggling:
a startup jingle (sequenced), a mouse glissando (key-state â€” the mouse
button's own up/down is the held state, so no key-repeat filtering is
needed), and a keyboard blip (fire-and-forget). The one loop-carried value
beyond the scribbler is a `held` flag.

```basic
Include "../machines/scribbler.shoddy"
Include "../machines/buzzer.shoddy"

Def Main()
    Let sc = ScribblerOpen(480, 120)
    Let sc = ScribblerTitle(sc, "Shoddy Buzzer Demo")
    Let sc = ScribblerFill(sc, 20, 20, 40)
    Let sc = ScribblerText(sc, 10, 10, 1, 255, 255, 255,
                           "Drag to slide, Space to blip, Q to quit")
    Play(1, "T160 O4 L8 C E G > C4")
    EventLoop(ScribblerBlit(sc), False)

Def PitchAt(x As Number) As Number
    Rem left edge A3 (midi 57), one semitone per 20 pixels
    MidiFreq(57 + Floor(x / 20))

Def EventLoop(sc As Scribbler, held As Boolean) As Scribbler
    Select Case NextEvent(sc)
        Case ScribblerMouseDown(x, y, btn, mods, at)
            NoteOn(2, PitchAt(x))
            EventLoop(sc, True)
        Case ScribblerMouseMove(x, y, mods, at)
            If held Then NoteOn(2, PitchAt(x))   Rem retune, no re-attack
            EventLoop(sc, held)
        Case ScribblerMouseUp(x, y, btn, mods, at)
            NoteOff(2)
            EventLoop(sc, False)
        Case ScribblerTyped(ch, mods, at)
            If ch = " " Then
                Sound(880, 60)
            If Lower(ch) = "q" Then
                ScribblerClose(sc)
            Else
                EventLoop(sc, held)
        Case ScribblerQuit
            SoundStop(1)
            SoundStop(2)
            ScribblerClose(sc)
        Case Else
            EventLoop(sc, held)
```

Run with `mill run tst/buzzer-demo.shoddy`. Confirm the recursion is a
genuine self tail call (one `continue` loop, not a growing CLR stack) and
that every `Select Case` branch yields the same arity â€” the quit branches
return the closed scribbler, as in the scribbler demo. Note the glissando
deliberately uses the mouse: a *keyboard* synth must additionally ignore
GLFW auto-repeat `KeyDown`s by carrying a held-key set in its loop state â€”
say so in the demo's header comment and leave that program for later.

---

## Tests

Sound hardware is untestable in CI; everything up to the delegate call is
testable headless, which is why the seam and the pure parser split exist:

- Install fake `BuzzerRegistry` delegates that record their arguments;
  assert `SOUND`/`NOTEON`/`SOUNDQUEUE`/`NOTEOFF`/`SOUNDSTOP`/`SOUNDGAIN`
  pass through exactly what the program said, in order.
- With all delegates null, every sound word is a silent no-op â€” no throw.
- Validation raises with delegates null *and* installed: channel 0, channel
  9, negative ms, freq 0 to `SOUND` and `NOTEON`, freq 0 accepted by
  `SOUNDQUEUE` as a rest, `SOUNDGAIN` volume below 0 or above 1,
  queued-ahead cap exceeded (and `SOUNDSTOP` resetting it).
- `MidiFreq(69)` = 440; `MidiFreq(81)` = 880; `NoteMidi` round-trips
  sharps, flats, and octaves; malformed note names raise.
- `MmlNotes` golden tests: a known string to a known `(FreqHz, Ms)` list â€”
  tempo, default length, dots, rests, `<`/`>`, and a malformed string
  raising with a position. Frequencies asserted to a tolerance, never
  exact-decimal.
- `machines/buzzer.shoddy` compiles with `mill machine`.
- A woven program that never touches sound produces byte-identical output
  to before the change.
- No golden test asserts on anything audible, ever.

Keep the mill layer thin enough that what remains untested is only "OpenAL
plays a buffer" â€” synthesis math (period rounding, envelope shape, cache
keys) can live in small pure static methods and get unit tests if it grows.

---

## Checklist

- [x] `BuzzerRegistry` in `Shoddy.Runtime/Buzzer.cs` â€” six nullable
      delegates, no Silk.NET, no new `VType`, nothing waits on sound
- [x] 6 builtins (`SOUND`, `NOTEON`, `NOTEOFF`, `SOUNDQUEUE`, `SOUNDSTOP`,
      `SOUNDGAIN`) in `Engine.Builtin()` **and** `Engine.BuiltinWords`;
      all zero-result; gain sticky per channel, 0â€“1, `AL_GAIN` only
- [x] Channels 1â€“8; range, sign, and queue-cap validation raises even when
      delegates are null; freq 0 legal only as a `SOUNDQUEUE` rest
- [x] No folded-name collisions: the five builtin words checked against
      every machine's `Def`/`Type`/field names; **no `Freq` anywhere** â€”
      `stats.shoddy` owns it; fields are `FreqHz`/`Ms`
- [x] `Shoddy.Mill.csproj` references `Silk.NET.OpenAL` + OpenAL Soft
      natives; `Shoddy.Runtime.csproj` unchanged
- [x] Device opens lazily on first sound call, from the Shoddy thread; init
      failure degrades to permanent silent no-op, never an error
- [x] Effect pool (~16) recycles stopped sources and steals the oldest;
      never blocks, never errors
- [x] Held notes loop whole periods; retune is `AL_PITCH`, not a new
      buffer; releases ramp ~5 ms; fixed buffers bake attack/release ramps
- [x] Streaming channels unqueue processed buffers on each call and restart
      a source that ran dry; buffer cache keyed `(freq, ms)` and bounded
- [x] Mill exit drains queued notes (capped), stops held notes immediately;
      `mill dap` has working sound; `mill lex`/`weave`/`machine` and every
      non-sound program unaffected, including startup cost
- [x] `machines/buzzer.shoddy`: `MidiFreq`, `NoteMidi`, `NoteFreq`,
      `MmlNotes` (pure), `Play`, `PlayNotes`; MML subset as specified;
      compiles with `mill machine`
- [ ] `tst/buzzer-demo.shoddy` plays the jingle, glides under the mouse,
      blips on Space, quits on Q; genuine self tail call; header comment
      warns keyboard synths about auto-repeat `KeyDown`s
- [x] Headless tests added and passing; no test asserts on audio output
