#!/usr/bin/env pwsh
# MAINTAINER TOOL - the suites CI cannot run.
#
#   scripts/shoddy-display.ps1           run them
#   scripts/shoddy-display.ps1 -List     name them and stop
#
# THREE SUITES ARE EXCLUDED FROM CI ON PURPOSE. seedscribblertest,
# seedturtletest and seedplottertest open a real window. --no-window hides
# it, but GLFW still needs a platform to create one on, and a hosted runner
# has none - not even under Xvfb, which v1.10.1 shipped a gate for and which
# failed in exactly the same way.
#
# Before this script, the only trace of that hole was a comment in build.ps1
# telling you to type `bin/mill --no-window run tst/seedscribblertest.shoddy`
# by hand. A deliberate gap in the proof should be a thing you can RUN,
# named in WORKFLOW.md, not a sentence in a comment.
#
# THE LIST IS NOT WRITTEN HERE. It is read out of TST_EXCLUSIONS in
# scripts/suites.mjs - the same list verify-suites.js grades every
# tst/*.shoddy against - so a fourth windowed suite cannot be added there and
# quietly forgotten here. build.ps1 reads its rosters the same way and for
# the same reason.
[CmdletBinding()]
param(
    [switch]$List
)
$ErrorActionPreference = 'Stop'

Push-Location (Join-Path $PSScriptRoot '..')
try {
    $mill = Join-Path 'bin' 'mill.exe'

    $reason = 'needs a real display'
    $read = 'const m = await import("./scripts/suites.mjs");' +
            'console.log(Object.entries(m.TST_EXCLUSIONS)' +
            ".filter(([, why]) => why.startsWith(`"$reason`"))" +
            '.map(([name]) => name).join(" "))'

    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $names = (& node --input-type=module -e $read) } finally { $ErrorActionPreference = $prev }
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'STOPPED: could not read TST_EXCLUSIONS from scripts/suites.mjs.' -ForegroundColor Red
        exit 1
    }

    $suites = @(($names | Out-String).Trim() -split '\s+' | Where-Object { $_ })
    if ($suites.Count -lt 1) {
        Write-Host 'STOPPED: no display suites are listed - has the reason wording changed?' -ForegroundColor Red
        exit 1
    }

    if ($List) {
        foreach ($s in $suites) { Write-Host "tst/$s.shoddy" }
        exit 0
    }

    # SAID OUT LOUD RATHER THAN FAILING OBSCURELY. Without a desktop these
    # suites do not fail on an assertion, they fail somewhere inside GLFW,
    # and the message that comes back explains nothing. Refusing up front is
    # also what should stop anyone wiring this into a workflow: it CANNOT
    # pass on a hosted runner, and a red step somebody later disables is
    # worse than a gap that is written down.
    if (-not [Environment]::UserInteractive) {
        Write-Host 'STOPPED: this session has no desktop, and every suite here opens a window.' -ForegroundColor Red
        Write-Host '         Run it on a machine with a real display. It cannot pass in CI,'
        Write-Host '         which is why CI excludes these three by name.'
        exit 1
    }

    if (-not (Test-Path $mill)) {
        Write-Host "STOPPED: $mill is not built. Run: ./build.ps1 build" -ForegroundColor Red
        exit 1
    }

    $failed = @()
    foreach ($suite in $suites) {
        Write-Host "==> tst/$suite.shoddy" -ForegroundColor Cyan

        # Judged on the exit code alone, like everything else in this
        # toolchain: a native program's stderr is not a failure, and
        # treating it as one is what used to kill runs that had passed.
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try { & $mill --no-window run "tst/$suite.shoddy" } finally { $ErrorActionPreference = $prev }
        if ($LASTEXITCODE -ne 0) { $failed += $suite }
    }

    Write-Host ''
    if ($failed.Count -gt 0) {
        Write-Host ("FAILED: " + ($failed -join ', ')) -ForegroundColor Red
        exit 1
    }
    Write-Host "OK - $($suites.Count) display suite(s) passed." -ForegroundColor Green
    exit 0
}
finally { Pop-Location }
