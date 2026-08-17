#!/usr/bin/env pwsh
# Build the Fettler lane: restore, build and test fettle.
#
#   ./build.ps1              restore + build (Debug)
#   ./build.ps1 test         build + run Fettler.Tests
#   ./build.ps1 release      Release build - what a client should launch
#   ./build.ps1 publish [V]  self-contained single-file binaries, one
#                            archive per OS; V names them (fettle-V-RID)
#
# Unlike every other lane here this one needs NOTHING built first. R1.2
# forbids Fettler to reference any other project in this repository, so
# there is no bin/mill.exe to publish and no mill to weave - the check
# other lanes open with would have nothing to check.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = 'build',
    [Parameter(Position = 1)][string]$Version = ''
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Run([string[]]$DotnetArgs) {
    Write-Host ("> dotnet " + ($DotnetArgs -join ' ')) -ForegroundColor Cyan
    # dotnet writes restore progress to stderr under some hosts, and
    # Windows PowerShell 5.1 turns a native program's redirected stderr
    # into NativeCommandError records. Judge the call on its exit code
    # alone - the same fault R3.7 exists to keep out of Fettler itself.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { dotnet @DotnetArgs } finally { $ErrorActionPreference = $prev }
    if ($LASTEXITCODE -ne 0) {
        Write-Host "STOPPED: dotnet $($DotnetArgs -join ' ') failed (exit $LASTEXITCODE)." -ForegroundColor Red
        exit 1
    }
}

switch ($Command) {
    'build'   { Run @('build', 'Fettler.slnx') }
    'test'    { Run @('test', 'Fettler.Tests') }
    'release' { Run @('build', 'Fettler.slnx', '-c', 'Release') }
    'publish' {
        # R2.3: a self-contained single-file fettle per OS, so the target
        # machine needs no .NET install and no repository checkout.
        #
        # win-x64 ships as .zip; the unix RIDs as .tar.gz, because the
        # executable bit only survives a tar. An archive cut on Windows
        # cannot record that bit at all, so unix archives made here are
        # for inspection - the ones a release attaches are a unix
        # runner's. Fettler of all things should say so out loud: R6.9
        # is the clause about exactly this bit.
        $suffix = ''
        if ($Version) { $suffix = "-$Version" }
        $pub = Join-Path $PSScriptRoot '..\artifacts\publish'
        foreach ($rid in @('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')) {
            $dir = Join-Path $pub "fettle\$rid"
            Run @('publish', 'fettle', '-c', 'Release', '-r', $rid, '--self-contained',
                  '-p:PublishSingleFile=true', '-o', $dir)

            # Two licence obligations, and both are met IN THE ARCHIVE -
            # not merely in the repository, which a person downloading a
            # release never sees. Apache-2.0 asks the licence and any
            # NOTICE to travel with a distributed binary, and fettle
            # bundles PdfPig into the one file it ships. MIT asks its own
            # copyright notice to be in every copy, and this archive is a
            # copy. An obligation nobody can read has not been met.
            Copy-Item (Join-Path $PSScriptRoot '..\NOTICE') $dir -Force
            Copy-Item (Join-Path $PSScriptRoot '..\LICENSE') $dir -Force

            if ($rid -eq 'win-x64') {
                Compress-Archive -Path @((Join-Path $dir 'fettle.exe'),
                                         (Join-Path $dir 'NOTICE'),
                                         (Join-Path $dir 'LICENSE')) `
                    -DestinationPath (Join-Path $pub "fettle$suffix-$rid.zip") -Force
            } else {
                tar -czf (Join-Path $pub "fettle$suffix-$rid.tar.gz") -C $dir fettle NOTICE LICENSE
                if ($LASTEXITCODE -ne 0) {
                    Write-Host "STOPPED: tar failed for $rid (exit $LASTEXITCODE)." -ForegroundColor Red
                    exit 1
                }
            }
        }
        Get-ChildItem $pub -File -Filter 'fettle*' | ForEach-Object { Write-Host ("  -> " + $_.Name) }
    }
    default   { Write-Host "unknown command: $Command (build | test | release | publish)" -ForegroundColor Red; exit 1 }
}
