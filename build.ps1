#!/usr/bin/env pwsh
# Shoddy build wrapper (Windows). Unix users: use build.sh (same commands).
#
#   ./build.ps1 build                 build the mill into bin/
#   ./build.ps1 test                  run the golden suite + libtest assertions
#   ./build.ps1 run FILE.shoddy       compile in memory and run a program
#   ./build.ps1 weave FILE.shoddy     compile a program to an assembly
#   ./build.ps1 machines              compile every machine to a machine DLL
#   ./build.ps1 vsix [bump]           package the VS Code extension (.vsix)
#   ./build.ps1 clean                 remove build output
#   ./build.ps1 help                  show this help
#
# vsix [bump]: optional patch|minor|major or an exact X.Y.Z to bump the
# extension version before packaging (e.g. ./build.ps1 vsix patch).
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = 'help',
    [Parameter(Position = 1)][string]$File
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$Mill = Join-Path 'bin' 'mill.exe'

function Invoke-Build {
    dotnet publish src/Shoddy.Mill -c Release -o bin
}

function Assert-Mill {
    if (-not (Test-Path $Mill)) {
        Write-Host 'mill not built; running build first...'
        Invoke-Build
    }
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
    'machines' {
        Assert-Mill
        Get-ChildItem machines/*.shoddy | ForEach-Object {
            Write-Host "==> $($_.Name)"
            & $Mill machine $_.FullName
        }
    }
    'vsix' {
        if (-not (Get-Command vsce -ErrorAction SilentlyContinue)) {
            Write-Host 'installing @vscode/vsce (npm -g)...'
            npm install -g @vscode/vsce
        }
        Push-Location vscode-shoddy
        try {
            if ($File) { vsce package $File --no-git-tag-version }
            else { vsce package }
        }
        finally { Pop-Location }
    }
    'clean' {
        if (Test-Path bin) { Remove-Item -Recurse -Force bin }
        Get-ChildItem src -Recurse -Directory -Include bin, obj |
            ForEach-Object { Remove-Item -Recurse -Force $_.FullName }
        Write-Host 'cleaned.'
    }
    { $_ -in 'help', '-h', '--help' } {
        Get-Content $PSCommandPath | Select-Object -Skip 1 -First 13 |
            ForEach-Object { $_ -replace '^#\s?', '' }
    }
    default {
        Write-Error "unknown command: $Command`nrun './build.ps1 help' for usage."
        exit 2
    }
}
