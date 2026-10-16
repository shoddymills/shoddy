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
#                            and is the version every binary reports
#   ./build.ps1 install [V]  sparky on THIS machine: build, prove, install
#                            to one fixed place, register with Claude Code
#
# install is the one command between a change and an agent using it.
# It publishes the single file for this platform, drives it over stdio
# and refuses if it does not answer (so a broken build never replaces a
# working one), copies it to $env:SPARKY_HOME (default
# %LOCALAPPDATA%\Shoddy\sparky on Windows, ~/.local/share/shoddy/sparky
# elsewhere), and registers that path with Claude Code as "sparky",
# replacing any earlier registration. The path never changes, so
# re-running it is the whole upgrade; a new Claude Code session picks it
# up. By default it registers at project scope, in the .mcp.json of the
# folder install was run from, with --root ".": sparky and fettle both
# root in that repo. An absolute $env:SPARKY_ROOT registers at user scope
# with that root instead - see Register-Sparky. Without the claude CLI it
# installs and prints the command to register by hand.
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
# Where install was run from, before the Set-Location: a project
# registration is written there.
$Invoked = (Get-Location).Path
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

# A node gate, judged on its exit code alone, as Run judges dotnet.
function Gate([string[]]$NodeArgs) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & node @NodeArgs } finally { $ErrorActionPreference = $prev }
    if ($LASTEXITCODE -ne 0) {
        Write-Host "STOPPED: node $($NodeArgs -join ' ') failed (exit $LASTEXITCODE)." -ForegroundColor Red
        exit 1
    }
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

# The binary at $Exe answers initialize and computes over its woven core,
# or the install stops before anything is replaced. The JSON goes in on
# stdin, so no argument quoting is involved.
function Test-Sparky([string]$Exe) {
    $scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('sparky-smoke-' + [System.IO.Path]::GetRandomFileName())
    New-Item -ItemType Directory -Path $scratch | Out-Null
    $lines = @(
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}',
        '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"eval","arguments":{"lines":["{ 12500 13100 11900 } MEAN"]}}}'
    )
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $answer = ($lines | & $Exe --root $scratch 2>$null) | Out-String }
    finally { $ErrorActionPreference = $prev; Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue }
    if ($answer -notmatch '"serverInfo"' -or $answer -notmatch 'x: 12500') {
        Write-Host 'STOPPED: the new sparky did not answer over stdio - nothing was replaced.' -ForegroundColor Red
        Write-Host $answer
        exit 1
    }
    Write-Host 'ok  the new sparky answers over stdio' -ForegroundColor Green
}

# Point Claude Code at $Exe, replacing an earlier "sparky". An absolute
# SPARKY_ROOT registers at user scope with that root. The arguments go
# as an array, so '--' reaches claude unchanged.
#
# OTHERWISE IT REGISTERS AT PROJECT SCOPE, in the .mcp.json of the
# folder install was run from, with --root SPARKY_ROOT (default "."). sparky resolves --root against the
# folder Claude Code launches it in; fettle resolves it against the
# folder holding the registration. Only a project registration makes
# those one folder, so only there does "." mean the repo to both. The
# user-scope "sparky" is removed either way: fettle reads ~/.claude.json
# first, and would pair with that one instead.
function Register-Sparky([string]$Exe) {
    $root = if ($env:SPARKY_ROOT) { $env:SPARKY_ROOT } else { '.' }
    $scope = 'project'
    if ([System.IO.Path]::IsPathRooted($root)) {
        $scope = 'user'
        New-Item -ItemType Directory -Force -Path $root | Out-Null
    } else {
        New-Item -ItemType Directory -Force -Path (Join-Path $Invoked $root) | Out-Null
    }
    $cmd = @($Exe, '--root', $root)
    if (Get-Command claude -ErrorAction SilentlyContinue) {
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $remove = @('mcp', 'remove', '--scope', 'user', 'sparky')
            & claude @remove 2>&1 | Out-Null
            $add = @('mcp', 'add', '--scope', $scope, 'sparky', '--') + $cmd
            if ($scope -eq 'project') {
                Push-Location $Invoked
                try {
                    $remove = @('mcp', 'remove', '--scope', 'project', 'sparky')
                    & claude @remove 2>&1 | Out-Null
                    & claude @add
                } finally { Pop-Location }
            } else {
                & claude @add
            }
        } finally { $ErrorActionPreference = $prev }
        if ($LASTEXITCODE -ne 0) { Write-Host 'STOPPED: claude mcp add failed.' -ForegroundColor Red; exit 1 }
        $where = if ($scope -eq 'project') { "project scope, $(Join-Path $Invoked '.mcp.json')" } else { 'user scope' }
        Write-Host "registered with Claude Code ($where) as sparky - start a new session to use it" -ForegroundColor Green
    } else {
        Write-Host 'the claude CLI is not on PATH, so nothing was registered. Give your MCP client:'
        Write-Host ('  ' + ($cmd -join ' '))
    }
}

switch ($Command) {
    'build'   { Run @('build', 'Sparky.slnx') }
    'test'    { Run @('test', 'Shoddy.Mcp.Tests') }
    'run'     { Run @('build', 'sparky'); Run @('run', '--project', 'sparky', '--no-build') }
    # release grades its own closure: no window, audio, toolchain or
    # third-party protocol package rides along (scripts/verify-closure.js).
    'release' { Run @('build', 'Sparky.slnx', '-c', 'Release'); Gate @('../../scripts/verify-closure.js', 'sparky') }
    'publish' {
        # What a release attaches: a self-contained single-file sparky
        # per OS, so the far end needs no repo and no .NET install.
        # Cross-targeting every RID from one machine is safe because the
        # lane is pure managed code - verify-closure.js asserts that on
        # the release output.
        # The Windows RIDs ship as .zip; the unix RIDs as .tar.gz, because
        # the executable bit only survives a tar. win-arm64 is its own
        # archive so an ARM64 Windows machine runs sparky natively rather
        # than under x64 emulation. An archive cut on Windows
        # cannot record that bit at all, so unix archives made here are
        # for inspection - the ones a release attaches are the Linux
        # runner's.
        $suffix = ''
        if ($Version) { $suffix = "-$Version" }
        # Forward slashes: correct on Windows, and the only separator pwsh
        # passes through unchanged on macOS and Linux.
        $pub = Join-Path $PSScriptRoot '../../artifacts/publish'
        # ShoddyRidPublish: these restores keep their lock files under
        # artifacts/, unlocked - see Directory.Build.props for why.
        # Single-file is set in sparky/Sparky.csproj, not here.
        foreach ($rid in @('win-x64', 'win-arm64', 'linux-x64', 'osx-x64', 'osx-arm64')) {
            $dir = Join-Path $pub "sparky/$rid"
            $publishArgs = @('publish', 'sparky', '-c', 'Release', '-r', $rid, '--self-contained', '-p:ShoddyRidPublish=true', '-o', $dir)
            if ($Version) { $publishArgs += "-p:Version=$Version" }
            Run $publishArgs
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
        # Then drive the archive for THIS platform, unpacked outside the
        # repo: the exact file a person downloads, not the build output.
        Gate @('../../scripts/verify-shipped.js', 'sparky', $Version)
    }
    'install' {
        $onWindows = ($env:OS -eq 'Windows_NT')
        $arm = ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq
                [System.Runtime.InteropServices.Architecture]::Arm64)
        if ($onWindows) { $rid = if ($arm) { 'win-arm64' } else { 'win-x64' }; $name = 'sparky.exe' }
        elseif ($IsMacOS) { $rid = if ($arm) { 'osx-arm64' } else { 'osx-x64' }; $name = 'sparky' }
        else { $rid = if ($arm) { 'linux-arm64' } else { 'linux-x64' }; $name = 'sparky' }
        $out = Join-Path $PSScriptRoot "../../artifacts/publish/sparky-install/$rid"
        $publishArgs = @('publish', 'sparky', '-c', 'Release', '-r', $rid, '--self-contained', '-p:ShoddyRidPublish=true', '-o', $out)
        if ($Version) { $publishArgs += "-p:Version=$Version" }
        Run $publishArgs
        $built = Join-Path $out $name
        Test-Sparky $built
        $dest = if ($env:SPARKY_HOME) { $env:SPARKY_HOME }
                elseif ($onWindows) { Join-Path $env:LOCALAPPDATA 'Shoddy/sparky' }
                else { Join-Path $HOME '.local/share/shoddy/sparky' }
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        $target = Join-Path $dest $name
        # Windows refuses to overwrite a running .exe but lets it be
        # renamed, so the old one steps aside first; a client still
        # running it keeps its process. The leftover is cleared next time.
        $old = Join-Path $dest "$name.old"
        if (Test-Path $old) { Remove-Item -Force $old -ErrorAction SilentlyContinue }
        if (Test-Path $target) { Move-Item -Force $target $old }
        Copy-Item $built $target
        if (-not $onWindows) { & chmod +x $target }
        Write-Host "installed $target"
        Register-Sparky $target
    }
    default   { Write-Host "unknown command: $Command (build | test | run | release | publish | install)" -ForegroundColor Red; exit 1 }
}
