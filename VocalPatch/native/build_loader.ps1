# build_loader.ps1 - build VocalPatch's native CLR-hosting loader.
#
# Uses whichever C compiler is present, in this order:
#   Zig  (cross-builds both bitnesses in one go)
#   MSVC (native-builds one bitness)
#   gcc / mingw-w64 (native-builds one bitness)
#   TinyCC (one bitness per tcc.exe - the win32 and win64 packages are separate)
#
#   .\native\build_loader.ps1
#       -> native\out\vocalpatch_loader_x64.dll and/or _x86.dll
#   .\native\build_loader.ps1 -Payload "$pwd\src\VocalPatch.Editor\bin\Release\net472"
#       -> also drops the 64-bit loader beside VocalPatch.Editor.dll
#   .\native\build_loader.ps1 -TestHost
#       -> also builds native\out\load_host_*.exe, throwaway hosts that
#          LoadLibrary() the loader so you can prove it works with no editor
#
# Bitness must match the host editor: a 64-bit loader needs a 64-bit injector,
# a 32-bit loader needs a 32-bit injector.

[CmdletBinding()]
param(
    [string] $Source,
    [string] $OutDir,
    [string] $Payload,
    [switch] $TestHost
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is empty inside a param default on Windows PowerShell 5.1, so the
# paths are resolved here instead.
$scriptRoot = $PSScriptRoot
if (-not $scriptRoot) { $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Source) { $Source = Join-Path $scriptRoot 'vocalpatch_loader.c' }
if (-not $OutDir) { $OutDir = Join-Path $scriptRoot 'out' }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Find-Tool([string] $name) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

# TinyCC ships as two separate packages (win32 and win64). Look in PATH and in
# the usual scratch locations, and identify each by the bitness it can produce.
function Find-TinyCC {
    $found = @{}
    foreach ($name in @('tcc', 'tcc32', 'tcc64')) {
        $path = Find-Tool $name
        if ($path) { $found[$path] = $true }
    }
    foreach ($guess in @(
        (Join-Path $env:TEMP 'tcc\tcc\tcc.exe'),
        (Join-Path $env:TEMP 'tcc32\tcc\tcc.exe'),
        (Join-Path $env:TEMP 'tcc64\tcc\tcc.exe'),
        'C:\tcc\tcc.exe'
    )) {
        if (Test-Path $guess) { $found[$guess] = $true }
    }

    # tcc.exe -v prints the target it was built for; "64" means it emits 64-bit.
    $result = @()
    foreach ($path in $found.Keys) {
        $arch = 'x86'
        try {
            $version = (& $path -v 2>&1 | Out-String)
            if ($version -match 'x86_64|win64' -or $version -match '^tcc version.*64' -or
                $version -match 'for Windows x64') { $arch = 'x64' }
        } catch { continue }
        $result += [pscustomobject]@{ Path = $path; Arch = $arch }
    }
    return $result
}

function Get-PeMachine([string] $path) {
    $stream = [IO.File]::OpenRead($path)
    try {
        $reader = New-Object IO.BinaryReader($stream)
        $stream.Position = 0x3C
        $pe = $reader.ReadInt32()
        $stream.Position = $pe + 4
        return $reader.ReadUInt16()
    } finally { $stream.Dispose() }
}

$built = @()

$zig = Find-Tool 'zig'
$cl = Find-Tool 'cl'
$gcc = Find-Tool 'gcc'
$tiny = Find-TinyCC

if ($zig) {
    Write-Host "compiler: zig ($zig)"
    foreach ($arch in @('x64', 'x86')) {
        $target = if ($arch -eq 'x64') { 'x86_64-windows-gnu' } else { 'x86-windows-gnu' }
        $out = Join-Path $OutDir "vocalpatch_loader_$arch.dll"
        & $zig cc -target $target -shared -O2 -o $out $Source
        if ($LASTEXITCODE -ne 0) { throw "zig cc failed for $arch" }
        $built += $out
    }
}
elseif ($cl -or $gcc -or $tiny.Count -gt 0) {
    if ($cl) {
        Write-Host "compiler: MSVC ($cl) - one bitness (this prompt's architecture)"
        $arch = if ([Environment]::Is64BitProcess) { 'x64' } else { 'x86' }
        Push-Location $OutDir
        try {
            & $cl /nologo /LD /O2 "/Fe:vocalpatch_loader_$arch.dll" $Source
            if ($LASTEXITCODE -ne 0) { throw 'cl failed' }
            $built += (Join-Path $OutDir "vocalpatch_loader_$arch.dll")
        } finally { Pop-Location }
    }

    if ($gcc) {
        Write-Host "compiler: gcc ($gcc) - one bitness (this prompt's architecture)"
        $arch = if ([Environment]::Is64BitProcess) { 'x64' } else { 'x86' }
        $out = Join-Path $OutDir "vocalpatch_loader_$arch.dll"
        & $gcc -shared -O2 -o $out $Source -lkernel32
        if ($LASTEXITCODE -ne 0) { throw 'gcc failed' }
        $built += $out
    }

    if ($tiny.Count -gt 0) {
        foreach ($tcc in $tiny) {
            $out = Join-Path $OutDir "vocalpatch_loader_$($tcc.Arch).dll"
            Write-Host "compiler: TinyCC $($tcc.Arch) ($($tcc.Path))"
            & $tcc.Path -shared -O2 -o $out $Source -lkernel32
            if ($LASTEXITCODE -ne 0) { throw "tcc failed for $($tcc.Arch)" }
            $built += $out
        }
    }
}
else {
    throw @"
No C compiler found. Any one of these is enough:
  Zig      portable zip from ziglang.org (cross-builds both bitnesses)
  MSVC     Visual Studio Build Tools (C++ workload)
  mingw    gcc from mingw-w64
  TinyCC   download.savannah.gnu.org/releases/tinycc/ (tcc-0.9.27-win64-bin.zip
           and tcc-0.9.27-win32-bin.zip; extract to %TEMP%\tcc and %TEMP%\tcc32)
"@
}

$built = $built | Sort-Object -Unique
foreach ($dll in $built) {
    $item = Get-Item $dll
    $machine = Get-PeMachine $dll
    $kind = switch ($machine) { 0x8664 { 'x64' } 0x14C { 'x86' } default { "0x{0:X4}" -f $machine } }
    Write-Host ("built {0} ({1:N0} bytes, {2})" -f $item.Name, $item.Length, $kind)
}

if ($TestHost) {
    Write-Host ''
    Write-Host 'building throwaway test hosts (load_host_*.exe)'
    if ($zig) {
        foreach ($arch in @('x64', 'x86')) {
            $target = if ($arch -eq 'x64') { 'x86_64-windows-gnu' } else { 'x86-windows-gnu' }
            & $zig cc -target $target -O2 -o (Join-Path $OutDir "load_host_$arch.exe") (Join-Path $scriptRoot 'load_host.c')
        }
    }
    if ($cl) {
        $arch = if ([Environment]::Is64BitProcess) { 'x64' } else { 'x86' }
        Push-Location $OutDir
        try { & $cl /nologo /O2 "/Fe:load_host_$arch.exe" (Join-Path $scriptRoot 'load_host.c') }
        finally { Pop-Location }
    }
    if ($gcc) {
        $arch = if ([Environment]::Is64BitProcess) { 'x64' } else { 'x86' }
        & $gcc -O2 -o (Join-Path $OutDir "load_host_$arch.exe") (Join-Path $scriptRoot 'load_host.c')
    }
    foreach ($tcc in $tiny) {
        & $tcc.Path -O2 -o (Join-Path $OutDir "load_host_$($tcc.Arch).exe") (Join-Path $scriptRoot 'load_host.c')
    }

    $payloadDir = $Payload
    if (-not $payloadDir) { $payloadDir = Join-Path $scriptRoot '..\src\VocalPatch.Editor\bin\Release\net472' }
    if (Test-Path (Join-Path $payloadDir 'VocalPatch.Editor.dll')) {
        Write-Host ''
        Write-Host 'self-test: loading the loader into a throwaway host process'
        $previous = $env:VOCALPATCH_EDITOR_DIR
        $env:VOCALPATCH_EDITOR_DIR = (Resolve-Path $payloadDir).Path
        # load_host.exe is not one of the editor process names, so the loader's
        # host allow-list would skip it; the self-test forces the host.
        $previousForce = $env:VOCALPATCH_FORCE_HOST
        $env:VOCALPATCH_FORCE_HOST = '1'
        $log = Join-Path $env:TEMP 'vocalpatch-loader.log'
        $editorLog = Join-Path $env:TEMP 'vocalpatch-editor.log'
        Remove-Item $log, $editorLog -ErrorAction SilentlyContinue
        foreach ($arch in @('x64', 'x86')) {
            $loader = Join-Path $OutDir "vocalpatch_loader_$arch.dll"
            $hostExe = Join-Path $OutDir "load_host_$arch.exe"
            if (-not (Test-Path $loader) -or -not (Test-Path $hostExe)) { continue }
            & $hostExe (Resolve-Path $loader).Path | Out-Null
            $verdict = if ((Get-Content $log -ErrorAction SilentlyContinue) -match 'loader ok') { 'OK' } else { 'FAILED' }
            Write-Host ("  {0}: {1}" -f $arch, $verdict)
        }
        $env:VOCALPATCH_EDITOR_DIR = $previous
        $env:VOCALPATCH_FORCE_HOST = $previousForce
    }
    else {
        Write-Warning "skipping self-test: no VocalPatch.Editor.dll in '$payloadDir'"
    }
}

if ($Payload) {
    if (-not (Test-Path (Join-Path $Payload 'VocalPatch.Editor.dll'))) {
        Write-Warning "No VocalPatch.Editor.dll in '$Payload' - build VocalPatch.Editor -c Release first."
    }
    $native = $built | Where-Object { $_ -match '_x64\.dll$' } | Select-Object -First 1
    if (-not $native) { $native = $built | Select-Object -First 1 }
    $dest = Join-Path $Payload 'vocalpatch_loader.dll'
    Copy-Item $native $dest -Force
    Write-Host "payload ready: $dest"
    Write-Host "inject with: vpatch-inject inject <process> `"$dest`""
}
