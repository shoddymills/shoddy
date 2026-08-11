# A Shoddy class-library wrapper

Scaffolded by `dotnet new shoddy-lib --mill <path> [--grant "a;b"]`.
The mill builds as part of `dotnet build` (ShoddyWeave: verify → gate →
weave → reference), and this library exposes its words as ordinary .NET
methods through `Shoddy.Hosting` — consumable from C#, F# or VB with no
wrappers beyond this one.

- **Grants are this project's capability statement.** Default-deny: a
  required capability the mill declares and this project has not
  granted fails the build, and the error prints the exact
  `Grant="…"` line to add. The manifest (`mill.manifest` in the mill
  folder) is the truth it must match.
- **Debugging** happens through whatever host consumes this library —
  see the shoddy-console template for the compound F5 wiring.

Three constraints every host of this library inherits (C6.4):

1. **One debuggee per process** — the debug sink seam is process-global.
2. **A paused core blocks its calling thread** — call mill words off
   any thread the host needs live.
3. **Breakpoints key on source paths** — attach across a boundary needs
   path mapping.
