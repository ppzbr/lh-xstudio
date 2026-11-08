# package.ps1 - assemble everything a user needs into dist\.
#
#   powershell -ExecutionPolicy Bypass -File .\package.ps1
#   powershell -ExecutionPolicy Bypass -File .\package.ps1 -SkipBuild
#
# Layout produced:
#   dist\QUICKSTART.zh.md            how to use it (Chinese)
#   dist\loader\*.dll                the injectable native loaders (x64 + x86)
#   dist\injector\vpatch-inject.exe  injects the loader and drives the actions
#   dist\cli\vpatch.exe              format conversion and jobs on files
#   dist\payload\                    the managed runtime + loader, injected together
#   dist\openutau\VocalPatch <name>\ ready-to-copy OpenUtau plugin folders
#   dist\dropin\*.dll                drop-in proxy shims: copy one next to an editor exe

[CmdletBinding()]
param(
    [switch] $SkipBuild,
    [string] $OutDir
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $OutDir) { $OutDir = Join-Path $root 'dist' }

function Assert-Path([string] $path, [string] $what) {
    if (-not (Test-Path $path)) { throw "$what not found: $path" }
    return $path
}

if (-not $SkipBuild) {
    Write-Host 'building the solution (Release)'
    & dotnet build (Join-Path $root 'VocalPatch.slnx') -c Release -v m
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }
}

$cliOut = Assert-Path (Join-Path $root 'src\VocalPatch.Cli\bin\Release\net8.0') 'CLI output'
$injOut = Assert-Path (Join-Path $root 'src\VocalPatch.Injector\bin\Release\net8.0') 'injector output'
$utauOut = Assert-Path (Join-Path $root 'src\VocalPatch.OpenUtau\bin\Release\net8.0') 'OpenUtau plugin output'
$payload = Assert-Path (Join-Path $root 'src\VocalPatch.Editor\bin\Release\net472') 'injection payload'

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }

# An editor that already has the runtime injected keeps the payload DLLs memory-mapped,
# and Windows refuses to overwrite a mapped image. It does allow renaming one, so a
# locked file is parked as '<name>.locked-<stamp>' and the fresh copy takes its place.
# The running editor keeps its old image; the next injection loads the new one. That
# means dist\ can be refreshed without closing the editor.
$movedAside = New-Object System.Collections.Generic.List[string]

# Leftovers from a previous refresh. Files still held by a running editor cannot be
# deleted until it exits, so this is best-effort.
foreach ($stale in @(Get-ChildItem -Path $OutDir -Recurse -File -ErrorAction SilentlyContinue |
                     Where-Object { $_.Name -like '*.locked-*' -or $_.Name -like '*.vpatch-new' })) {
    Remove-Item $stale.FullName -Force -ErrorAction SilentlyContinue
}

function Copy-File([string] $from, [string] $to) {
    $staging = "$to.vpatch-new"
    Copy-Item $from -Destination $staging -Force
    try {
        Move-Item $staging $to -Force -ErrorAction Stop
        return
    }
    catch {
        # Expected when the target is locked by a running editor.
    }
    # Park it under .parked\ rather than beside the payload, so an injection folder that
    # is about to be handed to someone does not fill up with '<name>.locked-<stamp>'.
    $parked = Join-Path $OutDir '.parked'
    if (-not (Test-Path $parked)) { New-Item -ItemType Directory -Force -Path $parked | Out-Null }
    $aside = Join-Path $parked "$(Split-Path $to -Leaf).locked-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    try {
        Move-Item $to $aside -Force -ErrorAction Stop
        Move-Item $staging $to -Force -ErrorAction Stop
    }
    catch {
        Remove-Item $staging -Force -ErrorAction SilentlyContinue
        throw "cannot refresh $to (locked and cannot be renamed) - close whatever loaded it and re-run"
    }
    $movedAside.Add($to)
}

function Copy-Tree([string] $from, [string] $to, [string[]] $exclude = @()) {
    New-Item -ItemType Directory -Force -Path $to | Out-Null
    Get-ChildItem $from -Force | Where-Object { $exclude -notcontains $_.Name } |
        ForEach-Object {
            if ($_.PSIsContainer) { Copy-Tree $_.FullName (Join-Path $to $_.Name) }
            else { Copy-File $_.FullName (Join-Path $to $_.Name) }
        }
}

Write-Host 'copying CLI'
Copy-Tree $cliOut (Join-Path $OutDir 'cli')

Write-Host 'copying injector'
Copy-Tree $injOut (Join-Path $OutDir 'injector')

Write-Host 'copying the managed injection payload'
Copy-Tree $payload (Join-Path $OutDir 'payload')

Write-Host 'copying the native loaders'
$loaderDir = Join-Path $OutDir 'loader'
New-Item -ItemType Directory -Force -Path $loaderDir | Out-Null
$nativeOut = Join-Path $root 'native\out'
$loaders = @()
foreach ($arch in @('x64', 'x86')) {
    $from = Join-Path $nativeOut "vocalpatch_loader_$arch.dll"
    if (Test-Path $from) {
        Copy-File $from (Join-Path $loaderDir "vocalpatch_loader_$arch.dll")
        $loaders += $arch
    }
    else {
        Write-Warning "native\out\vocalpatch_loader_$arch.dll missing - run native\build_loader.ps1 first"
    }
}

# The 64-bit loader also goes beside the assembly, which is where the loader looks
# first. That makes dist\payload a single self-contained thing to inject.
$payloadLoader = Join-Path $OutDir 'payload\vocalpatch_loader.dll'
if ($loaders -contains 'x64') {
    Copy-File (Join-Path $loaderDir 'vocalpatch_loader_x64.dll') $payloadLoader
}

Write-Host 'copying the drop-in proxy shims'
$dropinDir = Join-Path $OutDir 'dropin'
New-Item -ItemType Directory -Force -Path $dropinDir | Out-Null
$dropinNative = Join-Path $root 'native\out\dropin'
foreach ($name in @('winmm.dll', 'hostfxr.dll')) {
    $from = Join-Path $dropinNative $name
    if (Test-Path $from) { Copy-File $from (Join-Path $dropinDir $name) }
    else { Write-Warning "native\out\dropin\$name missing - run native\build_proxy.ps1 first" }
}
# The installer/remover plus its import checker, so dist\dropin is self-contained.
foreach ($extra in @('native\deploy_dropin.ps1', 'tools\pe-imports.ps1', 'tools\pe-exports.ps1')) {
    $from = Join-Path $root $extra
    if (Test-Path $from) { Copy-Item $from (Join-Path $dropinDir (Split-Path $from -Leaf)) -Force }
}
$dropinDoc = Join-Path $root 'dropin说明.txt'
if (Test-Path $dropinDoc) { Copy-Item $dropinDoc (Join-Path $dropinDir '说明.txt') -Force }

Write-Host 'writing a starter vpatch-editor.ini'
$ini = @(
    '# VocalPatch injected-runtime settings. Everything here is optional.'
    '#'
    '# profile=<cevio|synthv|voisona|openutau|piapro>   force a profile instead of'
    '#                                                  detecting the host process.'
    '#                                                  The process name works too:'
    '#                                                  profile=synthv-studio'
    '# autosave=true       snapshot project files while the editor runs'
    '# patchsaves=true     snapshot on every save the editor performs'
    '# snapshotinterval=30 seconds between autosave checks for one file'
    '# watch=<dir>         extra folder to watch (separate several with ;)'
    '# hotkey.<action>=Q   rebind one action (a letter, 0x51 or 81)'
    '# hotkeydebug=false   log every key the hook sees, to diagnose a hotkey'
    'autosave=true'
    'patchsaves=true'
    'snapshotinterval=30'
    'hotkeydebug=false'
) -join "`r`n"
Set-Content -Path (Join-Path $OutDir 'payload\vpatch-editor.ini') -Value $ini -Encoding ASCII

Write-Host 'building the OpenUtau plugin folders'
$utauDir = Join-Path $OutDir 'openutau'
New-Item -ItemType Directory -Force -Path $utauDir | Out-Null
$utauExe = Join-Path $utauOut 'vpatch-utau.exe'
$recipes = @(
    @{ Name = 'Quantize';        Args = @('--job', 'quantize') },
    @{ Name = 'Staccato';        Args = @('--job', 'staccato') },
    @{ Name = 'Swing';           Args = @('--job', 'swing') },
    @{ Name = 'Transpose +12';   Args = @('--job', 'transpose', '--set', 'semitones', '12') },
    @{ Name = 'Normalize';       Args = @('--job', 'normalize') },
    @{ Name = 'Harmony Third Up'; Args = @('--job', 'harmony', '--set', 'interval', 'thirdUp') },
    @{ Name = 'Pinyin';          Args = @('--pinyin') },
    @{ Name = 'Quantize + Pinyin'; Args = @('--job', 'quantize', '--pinyin') }
)
foreach ($recipe in $recipes) {
    $folder = Join-Path $utauDir ("VocalPatch " + $recipe.Name)
    $arguments = @('install', $folder, '--name', $recipe.Name) + $recipe.Args
    & $utauExe @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "installing OpenUtau plugin '$($recipe.Name)' failed" }
}

Write-Host 'copying the quickstart'
$quickstart = Join-Path $root 'QUICKSTART.zh.md'
if (Test-Path $quickstart) { Copy-Item $quickstart (Join-Path $OutDir 'QUICKSTART.zh.md') -Force }

Write-Host 'copying the install guide'
$installGuide = Join-Path $root '安装说明.txt'
if (Test-Path $installGuide) { Copy-Item $installGuide (Join-Path $OutDir '安装说明.txt') -Force }

Write-Host 'copying the AppInit auto-inject installer'
foreach ($name in @('install-appinit.ps1', 'uninstall-appinit.ps1', 'install-appinit.cmd', 'uninstall-appinit.cmd')) {
    $from = Join-Path $root $name
    if (Test-Path $from) { Copy-Item $from (Join-Path $OutDir $name) -Force }
}

Write-Host ''
Write-Host "dist ready: $OutDir"
Get-ChildItem $OutDir | ForEach-Object {
    Write-Host ("  {0}" -f $_.Name)
}

if ($movedAside.Count -gt 0) {
    Write-Host ''
    Write-Host 'refreshed while in use (the old copies were kept):'
    foreach ($path in $movedAside) { Write-Host ("  {0}" -f $path) }
    Write-Host 'an editor that was already running still holds the previous payload - close it'
    Write-Host 'and inject again to pick these up.'
}
Write-Host ''
Write-Host 'next:'
Write-Host '  dist\injector\vpatch-inject.exe targets'
Write-Host '  dist\injector\vpatch-inject.exe inject "<process name or pid>" "dist\payload\vocalpatch_loader.dll"'
