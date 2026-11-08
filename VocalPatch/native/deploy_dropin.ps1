# deploy_dropin.ps1 - copy the VocalPatch drop-in shim into each editor's folder.
#
# Two shapes of drop-in:
#
#   winmm.dll  - SynthV Studio 2, SynthV Studio (v1), VoiSona, Piapro Studio NT,
#                ACE Studio, NetEase Cloud Music XStudio and CeVIO AI.
#                One file copied in; the editor (or a DLL it loads) imports winmm
#                at startup, so it is loaded automatically. Delete the file to undo.
#
#                CeVIO is the odd one: its exe is a pure managed assembly with no
#                import table of its own, so it never reaches winmm directly. Its
#                audio stack does - MediaSuite.dll, CeVIO.Audio.dll and
#                CeVIO.Talk.External2_x64.dll all import it - and Windows resolves
#                their imports from the application directory first, exactly like
#                a native host. So the same one-file drop-in works.
#
#   hostfxr.dll - the self-contained .NET apps: OpenUtau, SynthUTAU and UtauV.
#                The apphost loads hostfxr.dll from its own folder by absolute
#                path, so the real one is parked as hostfxr.orig.dll and the shim
#                takes its place. The shim forwards to the parked copy, keeping
#                the host the app shipped with. Undo = restore the parked copy.
#
#   .\native\deploy_dropin.ps1                     # report only (the default)
#   .\native\deploy_dropin.ps1 -Apply              # install
#   .\native\deploy_dropin.ps1 -Apply -Remove      # uninstall

[CmdletBinding()]
param(
    [switch] $Apply,
    [switch] $Remove
)

$ErrorActionPreference = 'Stop'

# Works from the source tree (native\) and from a packaged dist\dropin\ alike.
$outDir = if (Test-Path (Join-Path $PSScriptRoot 'winmm.dll')) { $PSScriptRoot }
          else { Join-Path $PSScriptRoot 'out\dropin' }

$importsScript = $null
foreach ($candidate in @(
        (Join-Path $PSScriptRoot 'pe-imports.ps1'),
        (Join-Path $PSScriptRoot '..\tools\pe-imports.ps1'),
        (Join-Path $PSScriptRoot '..\..\tools\pe-imports.ps1'))) {
    if (Test-Path $candidate) { $importsScript = $candidate; break }
}

$marker = 'VocalPatchDropInShim'
$markerBytes = [Text.Encoding]::ASCII.GetBytes($marker)

function Test-IsShim([string] $path) {
    if (-not (Test-Path $path)) { return $false }
    try {
        $bytes = [IO.File]::ReadAllBytes($path)
        if ($bytes.Length -lt $markerBytes.Length) { return $false }
        for ($i = 0; $i -le $bytes.Length - $markerBytes.Length; $i++) {
            if ($bytes[$i] -ne $markerBytes[0]) { continue }
            $match = $true
            for ($j = 1; $j -lt $markerBytes.Length; $j++) {
                if ($bytes[$i + $j] -ne $markerBytes[$j]) { $match = $false; break }
            }
            if ($match) { return $true }
        }
    } catch { }
    return $false
}

# Byte-identical to the shim we just built? The marker check only proves "a shim is
# here", which hides a fleet that is half refreshed: an editor left running keeps
# the previous build on disk and the report still says "installed".
function Test-IsCurrent([string] $path, [string] $source) {
    if (-not (Test-Path $path) -or -not (Test-Path $source)) { return $false }
    try {
        if ((Get-Item -LiteralPath $path).Length -ne (Get-Item -LiteralPath $source).Length) { return $false }
        return ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq
                (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash)
    } catch { return $false }
}

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# shim = the DLL placed into the app folder; host = 'winmm' (simple copy) or
# 'hostfxr' (park the original first). Probe = a second file to check for the
# winmm import when the exe itself does not have one (it is CeVIO's audio stack
# and only CeVIO's audio stack that pulls winmm into that process).
$targets = @(
    [pscustomobject]@{ Name = 'Synthesizer V Studio 2 Pro'; Exe = 'synthv-studio.exe';    Dir = 'D:\Synthesizer V Studio 2 Pro';                  Shim = 'winmm';   Probe = $null }
    [pscustomobject]@{ Name = 'Synthesizer V Studio Pro';   Exe = 'synthv-studio.exe';    Dir = 'D:\Synthesizer V Studio Pro';                    Shim = 'winmm';   Probe = $null }
    [pscustomobject]@{ Name = 'VoiSona (D:)';               Exe = 'VoiSona.exe';          Dir = 'D:\VoiSona118';                                  Shim = 'winmm';   Probe = $null }
    [pscustomobject]@{ Name = 'VoiSona (Program Files)';    Exe = 'VoiSona.exe';          Dir = 'C:\Program Files\Techno-Speech\VoiSona';          Shim = 'winmm';   Probe = $null }
    [pscustomobject]@{ Name = 'Piapro Studio NT';           Exe = 'Piapro Studio NT.exe'; Dir = 'C:\Program Files\Crypton\Piapro Studio NT\Standalone'; Shim = 'winmm'; Probe = $null }
    [pscustomobject]@{ Name = 'ACE Studio';                 Exe = 'ACE Studio.exe';       Dir = 'C:\Program Files\ACE Studio';                     Shim = 'winmm';   Probe = $null }
    [pscustomobject]@{ Name = 'NetEase XStudio';            Exe = 'NetEase Cloud Music XStudio.exe'; Dir = 'D:\NetEase Cloud Music XStudio';       Shim = 'winmm';   Probe = $null }
    [pscustomobject]@{ Name = 'CeVIO AI';                   Exe = 'CeVIO AI.exe';         Dir = 'C:\Program Files\CeVIO\CeVIO AI';                 Shim = 'winmm';   Probe = 'MediaSuite.dll' }
    [pscustomobject]@{ Name = 'OpenUtau';                   Exe = 'OpenUtau.exe';         Dir = 'D:\OpenUtau';                                    Shim = 'hostfxr'; Probe = $null }
    [pscustomobject]@{ Name = 'SynthUTAU';                  Exe = 'SynthUTAU.exe';        Dir = 'D:\SynthUTAU-Release-win-x64-20261005\SynthUTAU'; Shim = 'hostfxr'; Probe = $null }
    [pscustomobject]@{ Name = 'UtauV';                      Exe = 'UtauV.exe';            Dir = 'D:\UtauV\bin';                                   Shim = 'hostfxr'; Probe = $null }
)

$isAdmin = Test-Admin
Write-Host ("mode    : {0}   admin: {1}" -f $(if ($Remove) { 'REMOVE' } elseif ($Apply) { 'APPLY' } else { 'REPORT ONLY' -replace 'REPORT ONLY','report only' }), $isAdmin)
Write-Host ("shims   : {0}" -f $outDir)
Write-Host ''

$installed = 0; $skipped = 0; $failed = 0; $stale = 0

foreach ($t in $targets) {
    $exe = Join-Path $t.Dir $t.Exe
    if (-not (Test-Path $exe)) { Write-Host ("{0,-28} missing ({1})" -f $t.Name, $t.Dir); $skipped++; continue }

    if (-not $t.Shim) {
        Write-Host ("{0,-28} unsupported - pure .NET Framework, no native import to plant (use the injector)" -f $t.Name)
        $skipped++
        continue
    }

    $shimSrc = Join-Path $outDir "$($t.Shim).dll"
    $dest = Join-Path $t.Dir "$($t.Shim).dll"
    $parked = Join-Path $t.Dir "$($t.Shim).orig.dll"

    # Sanity: something the host loads must really import winmm.
    if ($t.Shim -eq 'winmm' -and $importsScript -and (Test-Path $importsScript)) {
        $probe = $exe
        $names = @(& $importsScript -Path $probe -NamesOnly | Where-Object { $_ -match '\.dll$' })
        if (-not (($names -contains 'WINMM.dll') -or ($names -contains 'winmm.dll'))) {
            if ($t.Probe) {
                $probe = Join-Path $t.Dir $t.Probe
                if (Test-Path $probe) {
                    $names = @(& $importsScript -Path $probe -NamesOnly | Where-Object { $_ -match '\.dll$' })
                } else { $names = @() }
            }
            if (-not (($names -contains 'WINMM.dll') -or ($names -contains 'winmm.dll'))) {
                Write-Host ("{0,-28} skipped - {1} does not import winmm.dll" -f $t.Name, (Split-Path $probe -Leaf))
                $skipped++
                continue
            }
            Write-Host ("{0,-28} (winmm comes in via {1})" -f $t.Name, (Split-Path $probe -Leaf))
        }
    }

    if (-not $Apply) {
        $state = 'not installed'
        if (Test-IsShim $dest) {
            if (Test-IsCurrent $dest $shimSrc) { $state = 'shim installed' }
            else { $state = 'shim installed, OLDER build - close the editor and re-run -Apply'; $stale++ }
        }
        $extra = if ($t.Shim -eq 'hostfxr' -and (Test-Path $parked)) { ' (original parked)' } else { '' }
        Write-Host ("{0,-28} ready - {1}{2}" -f $t.Name, $state, $extra)
        continue
    }

    if ($Remove) {
        if (-not (Test-Path $dest)) { Write-Host ("{0,-28} nothing to remove" -f $t.Name); continue }
        if (-not (Test-IsShim $dest)) {
            Write-Host ("{0,-28} REFUSED - {1} is not our shim" -f $t.Name, $dest)
            $failed++
            continue
        }
        try {
            if ($t.Shim -eq 'hostfxr') {
                Remove-Item $dest -Force
                if (Test-Path $parked) { Rename-Item $parked $dest -ErrorAction Stop }
            } else {
                Remove-Item $dest -Force
            }
            Write-Host ("{0,-28} removed" -f $t.Name)
            $installed++
        } catch {
            Write-Host ("{0,-28} FAILED to remove: {1}" -f $t.Name, $_.Exception.Message)
            $failed++
        }
        continue
    }

    if (-not (Test-Path $shimSrc)) { Write-Host ("{0,-28} missing shim {1} - run build_proxy.ps1" -f $t.Name, $shimSrc); $failed++; continue }

    # Nothing to do if the exact build is already in place. Without this a plain
    # (non-elevated) -Apply reports FAILED on every Program Files target that is in
    # fact correct, which reads like a broken install.
    if ((Test-IsShim $dest) -and (Test-IsCurrent $dest $shimSrc) -and
        ($t.Shim -ne 'hostfxr' -or (Test-Path $parked))) {
        Write-Host ("{0,-28} up to date" -f $t.Name)
        $installed++
        continue
    }

    try {
        if ($t.Shim -eq 'hostfxr') {
            if (Test-IsShim $dest) {
                Copy-Item $shimSrc $dest -Force          # refresh in place
            } else {
                if (-not (Test-Path $parked)) { Rename-Item $dest $parked -ErrorAction Stop }
                else { Remove-Item $dest -Force }         # replace whatever is there
                Copy-Item $shimSrc $dest -Force
            }
        } else {
            Copy-Item $shimSrc $dest -Force
        }
        Write-Host ("{0,-28} INSTALLED -> {1}" -f $t.Name, $dest)
        $installed++
    }
    catch [IO.IOException] {
        # A running editor holds the shim it already loaded, so the copy cannot land.
        if (Test-IsShim $dest) {
            if (Test-IsCurrent $dest $shimSrc) {
                Write-Host ("{0,-28} already installed (in use by a running editor)" -f $t.Name)
            } else {
                Write-Host ("{0,-28} holds an OLDER shim (in use) - close it and re-run -Apply" -f $t.Name)
                $stale++
            }
            $installed++
        } else {
            Write-Host ("{0,-28} FAILED: {1}" -f $t.Name, $_.Exception.Message)
            $failed++
        }
    }
    catch {
        Write-Host ("{0,-28} FAILED: {1}  (administrator rights needed for Program Files)" -f $t.Name, $_.Exception.Message)
        $failed++
    }
}

Write-Host ''
Write-Host ("installed/settled: {0}   skipped: {1}   failed: {2}   one build behind: {3}" -f $installed, $skipped, $failed, $stale)
Write-Host 'An editor picks the shim up on its next launch.'
