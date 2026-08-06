#!/usr/bin/env pwsh
# Build / run the halifax mill (Windows).
# Unix users: use build.sh (same commands).
#
#   ./build.ps1 run              the calculator, at a prompt
#   ./build.ps1 test             the headless suite: no terminal needed
#   ./build.ps1 demo             the golden session, fed from files/demo.halifax
#   ./build.ps1 build            weave the program into bin/
#   ./build.ps1 clean            remove bin/
#
# run and demo both execute from the REPO ROOT, so a path you type at the
# prompt - SAVE "mine.halifax" - lands there, and so does the halifaxrc
# the shell looks for on the way up. That is the same rule tally uses for
# its spec paths: relative to where you ran from, not to where the mill
# happens to live.
#
# demo feeds files/demo.halifax to the prompt: the R5.2 sequence, in
# order, ending on a traced cascade. It is there to show the shell works
# end to end, not to look like a session - redirected input is not echoed
# back, so each answer starts on the prompt line that asked for it. In a
# terminal your own keystrokes fill that gap, which is what the docs
# page's transcript shows and what the demo GIF is recorded from.
#
# The test target needs NOTHING - no terminal, no display, no network.
# Everything between a typed line and the lines it produces is pure,
# which is the whole reason halifax-core.shoddy and halifax.shoddy are
# separate files.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = 'run'
)

$ErrorActionPreference = 'Stop'

# Push rather than Set. Set-Location changes the SESSION's directory, not a
# scope's, so a mill script run from the repo root - which is how
# ../../build.ps1 runs all twelve - would leave the caller's shell parked in
# the mill's folder after it finished. The finally runs on `exit` too, so
# every path out of the switch below restores where you were.
Push-Location $PSScriptRoot
try {

    $Repo = '../..'
    $Mill = Join-Path $Repo 'bin/mill.exe'

    function Assert-Mill {
        if (-not (Test-Path $Mill)) {
            Write-Host "mill toolchain not built; building it into $Repo/bin ..."
            dotnet publish (Join-Path $Repo 'src/Shoddy.Mill') -c Release -o (Join-Path $Repo 'bin')
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
    }

    switch ($Command) {
        'run' {
            Assert-Mill
            Push-Location $Repo
            try { & bin/mill.exe run mills/halifax/halifax.shoddy }
            finally { Pop-Location }
            exit $LASTEXITCODE
        }
        'test' {
            Assert-Mill
            # $null on the pipeline is this shell's `< /dev/null`: it closes
            # stdin, so a suite that ever read a line would hit EOF rather
            # than wait for a keystroke that is not coming.
            Push-Location $Repo
            try { $null | & bin/mill.exe run mills/halifax/test.shoddy }
            finally { Pop-Location }
            exit $LASTEXITCODE
        }
        'demo' {
            Assert-Mill
            Push-Location $Repo
            try { Get-Content mills/halifax/files/demo.halifax | & bin/mill.exe run mills/halifax/halifax.shoddy }
            finally { Pop-Location }
            exit $LASTEXITCODE
        }
        'build' {
            Assert-Mill
            # weave writes BESIDE the source and has no -o flag; this moves the
            # result into bin/, the same shuffle the other weaving mills do.
            & $Mill weave halifax.shoddy
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            New-Item -ItemType Directory -Path bin -Force | Out-Null
            Move-Item -Force halifax.dll, halifax.runtimeconfig.json bin/
            $extra = @(Get-ChildItem Shoddy.*.dll -ErrorAction SilentlyContinue)
            if ($extra.Count -gt 0) { Move-Item -Force $extra bin/ }
            Write-Host 'woven into bin/'
        }
        'clean' {
            if (Test-Path bin) { Remove-Item -Recurse -Force bin }
        }
        default {
            [Console]::Error.WriteLine('usage: ./build.ps1 [run|test|demo|build|clean]')
            exit 2
        }
    }

    # Branches that run no native command leave $LASTEXITCODE at whatever
    # the last one set, so a target like clean could report a failure it
    # had nothing to do with. The .sh twin's case arm returns 0 here.
    exit 0
}
finally { Pop-Location }
