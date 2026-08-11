// Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
// Licensed under the MIT License. See the LICENSE file in the project root.

using System.Diagnostics;

namespace Shoddy.Tests;

/// <summary>
/// Run a child process and capture both its streams WITHOUT DEADLOCKING.
///
/// The obvious spelling is wrong, and it was written six times across these
/// tests before anyone noticed:
///
///     string o = p.StandardOutput.ReadToEnd();   // blocks until stdout closes
///     string e = p.StandardError.ReadToEnd();    // never reached
///     p.WaitForExit();
///
/// A redirected pipe holds only a few kilobytes. While the parent drains
/// stdout to the end, the child keeps writing to stderr; once that buffer
/// fills, the child blocks on its write and can never close stdout, so the
/// parent blocks on its read. Both sides wait for the other, forever. It is
/// invisible on a quiet child and certain on a chatty one, which is why it
/// survived: `dotnet build` is quiet until the day it emits warnings.
///
/// The symptom was not an error. It was a test host that stopped dead
/// partway through the suite with no output and no CPU, and because vstest
/// prints nothing per test it read as a hang rather than a deadlock. The
/// repo already carried a note blaming a nested `dotnet build` inside
/// `dotnet test` for stalling a release runner for half an hour (v2.0.0) —
/// same cause, diagnosed as far as the symptom.
///
/// The fix is to have BOTH reads already in flight before blocking on
/// either, so neither pipe can fill while the other is being drained.
/// </summary>
internal static class ProcessRun
{
    internal static (int Exit, string Out, string Err) Capture(ProcessStartInfo psi)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using Process p = Process.Start(psi)!;

        // Started, not awaited: both pipes are draining concurrently from here.
        Task<string> outText = p.StandardOutput.ReadToEndAsync();
        Task<string> errText = p.StandardError.ReadToEndAsync();
        p.WaitForExit();

        return (p.ExitCode, outText.GetAwaiter().GetResult(), errText.GetAwaiter().GetResult());
    }

    /// <summary>Both streams joined, for a caller that only wants to show
    /// whatever the child said when it failed.</summary>
    internal static (int Exit, string Text) CaptureAll(ProcessStartInfo psi)
    {
        var (exit, o, e) = Capture(psi);
        return (exit, o + e);
    }
}
