# A Shoddy console consumer

Scaffolded by `dotnet new shoddy-console --mill <path> [--grant "a;b"]`.
The mill builds as part of `dotnet build` (ShoddyWeave: verify → gate →
weave → reference), and its words are ordinary calls through
`Shoddy.Hosting`.

- **Grants are this project's capability statement.** Default-deny: a
  required capability the mill declares and this project has not
  granted fails the build, and the error prints the exact
  `Grant="…"` line to add. `--grant` pre-fills the list; the manifest
  (`mill.manifest` in the mill folder) is the truth it must match.
- **F5 debugs both sides**: the compound launch weaves the
  instrumented core, starts the host under the .NET debugger with the
  perch service armed (`SHODDY_PERCH`), and attaches the Shoddy
  debugger to it. Set `ShoddyPerchProject` in the csproj first.
  Release builds carry neither the service nor instrumentation.

Three constraints every host inherits (C6.4), stated in this project's
terms:

1. **One debuggee per process.** The debug sink seam is
   process-global: a process hosting two mills can debug one of them
   at a time.
2. **A paused core blocks its calling thread.** Call mill words off
   any thread you need live — a request thread, a UI thread, a timer
   callback — or a breakpoint will freeze it.
3. **Breakpoints key on source paths.** Debug the sources the build
   wove; attaching across a process or machine boundary needs the
   paths to agree (path mapping).
