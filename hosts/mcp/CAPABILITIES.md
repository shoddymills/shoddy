# What sits behind each capability

Sparky grants every capability `mills/sparky`'s manifest declares and
supplies a real resource behind every one. That is the rule this file
records: **no capability is granted that the server cannot honestly
supply**, because a granted capability with a null seam behind it means a
word that is registered, listed by `WORDS`, explained by `HELP`, and
answering nonsense.

The grant is read from `sparky/Sparky.csproj`. It is one line, and it is
this server's capability statement.

| Capability | Resource the server supplies | Costs |
|---|---|---|
| `file` | one root directory per process — `--root PATH`, or `LocalApplicationData/Shoddy/Sparky` | nothing; a plain directory |
| `terminal` | each session's own output writer, drained per turn into the turn's `printed` field | nothing; `PRINT` never reaches the console |
| `clock` | acknowledgement only — the words are the runtime's | nothing |
| `random` | acknowledgement only | nothing |
| `scribbler` | a real RGBA buffer with no window, and a capture table that turns a blit into a PNG | nothing; no display, no GPU, no main thread |
| `net` | outbound sockets, armed per-engine through `ShoddyHostOptions.AllowNet` | outbound network access, user-named URLs only |

Five of the six cost nothing anywhere. Only `net` is a real permission,
and only in the outbound direction.

## The file root is a boundary, not a base

Every file word resolves its path against the root and is refused if the
result would land outside it — a path climbing out with `..`, an absolute
path elsewhere, and a symbolic link anywhere along the way are all turned
away before anything is touched. The aborting words stop with
`{WORD}: '{path}' is outside the file root`; the guarded twins report it
in their own shape; `FileExists` answers `False`, so nothing outside can
be probed for.

That is `Shoddy.Runtime.FileRoot`'s doing, not this host's, so every host
gets it. What it is **not** is a defence against a hostile process
sharing the root: a component swapped for a link between the check and
the open that follows it needs the operating system to prevent, not a
path comparison. Sparky is a local server whose trust boundary is the
local user, who could run anything anyway.

The server also sets its process working directory to the root. That is
not what enforces the boundary and is not needed for it — it is so that
anything *outside* the engine which resolves a relative path lands in the
same place as everything else.

## `net` is outbound-only by construction, not by policy

`seednet` registers `NETGET` and `NETREQUEST`. Its server half — listen,
accept, serve — is not bridged, and neither are the raw socket builtins,
so **there is no listening, accepting or blocking-wait word reachable
from Sparky's dictionary**. This is a fact about which words exist, not a
rule applied on top of them, and the mill's own suite asserts it.

`ShoddyHost.Load` arms the ambient switch inside a construction gate and
restores it, so the grant applies to Sparky's Mode N engines and nothing
else in the process. `ShoddyHost.ArmNetForProcess` is a Mode T concern
and is never called.

## The three seeds that are not granted, and not folded

`buzzer`, `keys` and `vt100` are neither granted nor seeded, so their
absence is a fact about the dictionary rather than a warning in a build
log.

| Not folded | Why |
|---|---|
| `seedbuzzer` | Sound. The server does not control the host's audio lifetime, cannot know whether anything is listening, and has no use for it. |
| `seedkeys` | Pure, and pointless. It classifies keystrokes, and there is no keystroke source. |
| `seedvt100` | Pure, and pointless. It builds escape sequences for a screen that does not exist; a client receiving them in JSON is worse off than one told the word is absent. |

**A capability grant does not gate a dictionary, which is why these are
excluded by the fold instead.** Withholding `scribbler` from halifax
would leave `SCRIBOPEN` registered, in `WORDS`, in `HELP`, and returning
null-seam nonsense. The seed fold is the only mechanism that decides
which words exist — and a fold is a mill's own code, which is why Sparky
is a mill of its own rather than a narrower grant over halifax's.

Ask `HELP BUZZPLAY` and you are told it is not a word in this dictionary.
That is the honest answer, and the one a model can act on.

## What needed no excluding

`INPUT`, `INPUTLINE` and `INKEY` are absent from every reckoner
dictionary already: `seedbuiltin` bridges no reading builtin and
`seedterminal` registers `PRINT` and only `PRINT`. This section exists so
a later reader does not go looking for a suppression that was never
needed.
