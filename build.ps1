#!/usr/bin/env pwsh
# Shoddy build wrapper (Windows). Unix users: use build.sh (same commands).
#
#   ./build.ps1 build                 build the mill into bin/
#   ./build.ps1 test                  run the golden suite + libtest assertions
#   ./build.ps1 run FILE.shoddy       compile in memory and run a program
#   ./build.ps1 weave FILE.shoddy     compile a program to an assembly
#   ./build.ps1 machines              compile every machine to a machine DLL
#   ./build.ps1 stage                 stage the mill + machines into the extension
#   ./build.ps1 vsix [bump]           package the VS Code extension (.vsix)
#   ./build.ps1 clean                 remove build output
#   ./build.ps1 help                  show this help
#
# vsix [bump]: optional patch|minor|major or an exact X.Y.Z to bump the
# extension version before packaging (e.g. ./build.ps1 vsix patch).
# vsix stages first, so the package carries its own mill and machines.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = 'help',
    [Parameter(Position = 1)][string]$File
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$Mill = Join-Path 'bin' 'mill.exe'
# Where `stage` drops the extension's own copy of the toolchain. Both are
# build output: gitignored, and rebuilt from scratch on every stage.
$StageMill = Join-Path 'vscode-shoddy' 'mill'
$StageLib = Join-Path 'vscode-shoddy' 'machines'

function Invoke-Build {
    dotnet publish src/Shoddy.Mill -c Release -o bin
}

function Assert-Mill {
    if (-not (Test-Path $Mill)) {
        Write-Host 'mill not built; running build first...'
        Invoke-Build
    }
}

function Invoke-Machines {
    Assert-Mill
    # Dependency order: an Include "x.shoddy" resolves to the machine DLL
    # only if Shoddy.Machines.X.dll is already built — otherwise the
    # source is spliced in and its defs re-exported, which collides with
    # the real machine downstream (duplicate definition of ANY, etc.).
    $files = @(Get-ChildItem machines/*.shoddy | Sort-Object Name)
    $deps = @{}
    foreach ($f in $files) {
        $deps[$f.BaseName] = @(
            Select-String -Path $f.FullName -Pattern '^\s*Include\s+"(.+)\.shoddy"' |
                ForEach-Object { $_.Matches[0].Groups[1].Value.ToLowerInvariant() })
    }
    $built = New-Object 'System.Collections.Generic.HashSet[string]'
    $pending = [System.Collections.ArrayList]$files
    while ($pending.Count -gt 0) {
        $ready = @($pending | Where-Object {
            $unmet = @($deps[$_.BaseName] |
                Where-Object { $deps.ContainsKey($_) -and -not $built.Contains($_) })
            $unmet.Count -eq 0
        })
        if ($ready.Count -eq 0) {
            Write-Error ('include cycle among machines: ' +
                (($pending | ForEach-Object BaseName) -join ', '))
            exit 1
        }
        foreach ($f in $ready) {
            Write-Host "==> $($f.Name)"
            & $Mill machine $f.FullName
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            [void]$built.Add($f.BaseName.ToLowerInvariant())
            $pending.Remove($f)
        }
    }
}

# Give the extension its own toolchain, so installing the .vsix is the
# whole install: a framework-dependent mill (every RID's natives ride
# along in runtimes/, so one package runs everywhere the .NET runtime
# is) and the machines it needs. The extension points SHODDYLIB at the
# staged machines/, where Include finds the source and the resolver
# finds the DLL beside it in bin/.
function Invoke-Stage {
    Invoke-Machines
    foreach ($d in $StageMill, $StageLib) {
        if (Test-Path $d) { Remove-Item -Recurse -Force $d }
    }
    # Satellite resource assemblies are Roslyn's localized diagnostics —
    # ~5 MB of the package for messages the mill never surfaces.
    dotnet publish src/Shoddy.Mill -c Release -o $StageMill `
        -p:SatelliteResourceLanguages=en -p:DebugType=none
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    New-Item -ItemType Directory -Path (Join-Path $StageLib 'bin') -Force | Out-Null
    Copy-Item machines/*.shoddy $StageLib
    Copy-Item machines/bin/*.dll (Join-Path $StageLib 'bin')
    # Roslyn, Silk.NET, GLFW and OpenAL Soft are redistributed in the
    # package, so their notices have to travel with it — LGPL-2.1 for
    # OpenAL Soft, attribution for the rest.
    Copy-Item THIRD-PARTY-NOTICES.md vscode-shoddy
    $size = (Get-ChildItem $StageMill, $StageLib -Recurse -File |
        Measure-Object Length -Sum).Sum
    Write-Host ('staged mill + machines into vscode-shoddy/ ({0:N1} MB)' -f ($size / 1MB))
}

switch ($Command) {
    'build' { Invoke-Build }
    'test' {
        dotnet test src/Shoddy.Tests
        Assert-Mill
        & $Mill run tst/libtest.shoddy
    }
    'run' {
        if (-not $File) { Write-Error 'usage: ./build.ps1 run FILE.shoddy'; exit 2 }
        Assert-Mill
        & $Mill run $File
    }
    'weave' {
        if (-not $File) { Write-Error 'usage: ./build.ps1 weave FILE.shoddy'; exit 2 }
        Assert-Mill
        & $Mill weave $File
    }
    'machines' { Invoke-Machines }
    'stage' { Invoke-Stage }
    'vsix' {
        if (-not (Get-Command vsce -ErrorAction SilentlyContinue)) {
            Write-Host 'installing @vscode/vsce (npm -g)...'
            npm install -g @vscode/vsce
        }
        Invoke-Stage
        Push-Location vscode-shoddy
        try {
            if ($File) { vsce package $File --no-git-tag-version }
            else { vsce package }
        }
        finally { Pop-Location }
    }
    'clean' {
        if (Test-Path bin) { Remove-Item -Recurse -Force bin }
        if (Test-Path artifacts) { Remove-Item -Recurse -Force artifacts }
        if (Test-Path machines/bin) { Remove-Item -Recurse -Force machines/bin }
        foreach ($d in $StageMill, $StageLib) {
            if (Test-Path $d) { Remove-Item -Recurse -Force $d }
        }
        Get-ChildItem src -Recurse -Directory -Include bin, obj |
            ForEach-Object { Remove-Item -Recurse -Force $_.FullName }
        Write-Host 'cleaned.'
    }
    { $_ -in 'help', '-h', '--help' } {
        Get-Content $PSCommandPath | Select-Object -Skip 1 -First 15 |
            ForEach-Object { $_ -replace '^#\s?', '' }
    }
    default {
        Write-Error "unknown command: $Command`nrun './build.ps1 help' for usage."
        exit 2
    }
}
