#!/usr/bin/env pwsh
# Build the MCP lane: restore, build and test Sparky. The lane's
# projects build the toolchain they weave with and reference the machine
# library, so a plain dotnet build is the whole build; the repo root's
# build runs first only so the mill's own suite has a mill.
#
#   ./build.ps1              restore + build (Debug)
#   ./build.ps1 test         build + run Shoddy.Mcp.Tests
#   ./build.ps1 run          build + start the server on stdin/stdout
#   ./build.ps1 release      Release build - what a client should launch
#   ./build.ps1 publish [V]  self-contained single-file binaries, one
#                            archive per OS; V names them (sparky-V-RID)
#
# run is here for a person holding a terminal, and it will look like it
# has hung: the server is waiting for a JSON-RPC message on stdin, which
# is exactly what it should do. README.md has two lines that prove it is
# alive.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = 'build',
    [Parameter(Position = 1)][string]$Version = ''
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# The root's build: the full mill, for the mill's own suite. MSBuild's
# up-to-date check makes this free when nothing changed. Push/Pop,
# because the root script sets its own location.
Push-Location (Join-Path $PSScriptRoot '../..')
try { & ./build.ps1 build } finally { Pop-Location }
if ($LASTEXITCODE -ne 0) {
    Write-Host "STOPPED: the repo root's build failed (exit $LASTEXITCODE)." -ForegroundColor Red
    exit 1
}

function Run([string[]]$DotnetArgs) {
    Write-Host ("> dotnet " + ($DotnetArgs -join ' ')) -ForegroundColor Cyan
    # dotnet writes restore progress to stderr under some hosts; judge
    # the call on its exit code alone - the only honest signal a native
    # program gives.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { dotnet @DotnetArgs } finally { $ErrorActionPreference = $prev }
    if ($LASTEXITCODE -ne 0) {
        Write-Host "STOPPED: dotnet $($DotnetArgs -join ' ') failed (exit $LASTEXITCODE)." -ForegroundColor Red
        exit 1
    }
}

switch ($Command) {
    'build'   { Run @('build', 'Sparky.slnx') }
    'test'    { Run @('test', 'Shoddy.Mcp.Tests') }
    'run'     { Run @('build', 'sparky'); Run @('run', '--project', 'sparky', '--no-build') }
    'release' { Run @('build', 'Sparky.slnx', '-c', 'Release') }
    'publish' {
        # What a release attaches: a self-contained single-file sparky
        # per OS, so the far end needs no repo and no .NET install.
        # Cross-targeting every RID from one machine is safe because the
        # lane is pure managed code - mcp.yml asserts that from outside.
        # The Windows RIDs ship as .zip; the unix RIDs as .tar.gz, because
        # the executable bit only survives a tar. win-arm64 is its own
        # archive so an ARM64 Windows machine runs sparky natively rather
        # than under x64 emulation. An archive cut on Windows
        # cannot record that bit at all, so unix archives made here are
        # for inspection - the ones a release attaches are the Linux
        # runner's.
        $suffix = ''
        if ($Version) { $suffix = "-$Version" }
        $pub = Join-Path $PSScriptRoot '..\..\artifacts\publish'
        # ShoddyRidPublish: these restores keep their lock files under
        # artifacts/, unlocked - see Directory.Build.props for why.
        # Single-file is set in sparky/Sparky.csproj, not here.
        foreach ($rid in @('win-x64', 'win-arm64', 'linux-x64', 'osx-x64', 'osx-arm64')) {
            $dir = Join-Path $pub "sparky\$rid"
            Run @('publish', 'sparky', '-c', 'Release', '-r', $rid, '--self-contained', '-p:ShoddyRidPublish=true', '-o', $dir)
            if ($rid -like 'win-*') {
                Compress-Archive -Path (Join-Path $dir 'sparky.exe') `
                    -DestinationPath (Join-Path $pub "sparky$suffix-$rid.zip") -Force
            } else {
                tar -czf (Join-Path $pub "sparky$suffix-$rid.tar.gz") -C $dir sparky
                if ($LASTEXITCODE -ne 0) {
                    Write-Host "STOPPED: tar failed for $rid (exit $LASTEXITCODE)." -ForegroundColor Red
                    exit 1
                }
            }
        }
        Get-ChildItem $pub -File | ForEach-Object { Write-Host ("  -> " + $_.Name) }
    }
    default   { Write-Host "unknown command: $Command (build | test | run | release | publish)" -ForegroundColor Red; exit 1 }
}
