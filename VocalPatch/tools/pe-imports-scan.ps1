# pe-imports-scan.ps1 - collect the functions every PE under a folder set imports from
# one system DLL, and print the union. Used to decide exactly which entry points a
# drop-in proxy has to re-export.
#
#   powershell -ExecutionPolicy Bypass -File .\tools\pe-imports-scan.ps1 `
#       -Roots 'C:\Program Files\ACE Studio','D:\NetEase Cloud Music XStudio' -Dll WINMM.dll -Union
#
# Header walk copied from pe-imports.ps1 so both stay in sync. Files larger than
# -MaxBytes are skipped (a 250 MB voice blob is not an import source worth 250 MB of RAM).

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Roots,
    [string] $Dll = 'WINMM.dll',
    [int] $MaxBytes = 125829120,
    [switch] $Union,
    [switch] $PerFile
)

$ErrorActionPreference = 'Stop'

# -Roots is one ';'-separated string so it survives a `powershell -File` hand-off.
$rootList = @($Roots -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })

function Get-PeImports([string] $path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $e = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($e -le 0 -or $e + 24 -ge $bytes.Length) { return @() }
    if ([Text.Encoding]::ASCII.GetString($bytes, $e, 4) -ne "PE`0`0") { return @() }

    $coff = $e + 4
    $numSections = [BitConverter]::ToUInt16($bytes, $coff + 2)
    $sizeOpt = [BitConverter]::ToUInt16($bytes, $coff + 16)
    $opt = $coff + 20
    $magic = [BitConverter]::ToUInt16($bytes, $opt)
    $is64 = ($magic -eq 0x20B)
    $ddOff = if ($is64) { $opt + 112 } else { $opt + 96 }
    $sectionsOff = $opt + $sizeOpt
    $thunkSize = if ($is64) { 8 } else { 4 }

    $sections = @()
    for ($i = 0; $i -lt $numSections; $i++) {
        $s = $sectionsOff + $i * 40
        if ($s + 40 -gt $bytes.Length) { break }
        $sections += [pscustomobject]@{
            VSize   = [BitConverter]::ToUInt32($bytes, $s + 8)
            VA      = [BitConverter]::ToUInt32($bytes, $s + 12)
            RawSize = [BitConverter]::ToUInt32($bytes, $s + 16)
            RawPtr  = [BitConverter]::ToUInt32($bytes, $s + 20)
        }
    }

    function RvaToOff([uint32] $rva) {
        foreach ($sec in $sections) {
            $span = [Math]::Max($sec.VSize, $sec.RawSize)
            if ($rva -ge $sec.VA -and $rva -lt ($sec.VA + $span)) {
                $off = [int]($sec.RawPtr + ($rva - $sec.VA))
                if ($off -lt $bytes.Length) { return $off }
                return -1
            }
        }
        return -1
    }
    function ReadAscii([int] $off) {
        if ($off -lt 0 -or $off -ge $bytes.Length) { return '' }
        $end = $off
        while ($end -lt $bytes.Length -and $bytes[$end] -ne 0) { $end++ }
        return [Text.Encoding]::ASCII.GetString($bytes, $off, $end - $off)
    }
    function ReadThunks([uint32] $thunkRva) {
        $names = @()
        if ($thunkRva -eq 0) { return $names }
        $off = RvaToOff $thunkRva
        if ($off -lt 0) { return $names }
        for ($i = 0; $i -lt 4096; $i++) {
            $at = $off + $i * $thunkSize
            if ($at + $thunkSize -gt $bytes.Length) { break }
            $v = if ($is64) { [BitConverter]::ToUInt64($bytes, $at) } else { [uint64][BitConverter]::ToUInt32($bytes, $at) }
            if ($v -eq 0) { break }
            $ordFlag = if ($is64) { 0x8000000000000000 } else { 0x80000000 }
            if (($v -band $ordFlag) -ne 0) { $names += '#' + ($v -band 0xFFFF) }
            else {
                $no = RvaToOff ([uint32]($v -band 0x7FFFFFFF))
                if ($no -ge 0) { $names += (ReadAscii ($no + 2)) }
            }
        }
        return $names
    }
    function ReadImports([int] $index, [int] $stride, [int] $nameOffset, [int] $thunkOffset, [int] $iatOffset) {
        $rva = [BitConverter]::ToUInt32($bytes, $ddOff + $index * 8)
        if ($rva -eq 0) { return @() }
        $off = RvaToOff $rva
        if ($off -lt 0) { return @() }
        $list = @()
        $cursor = $off
        while ($cursor + $stride -le $bytes.Length) {
            $zero = $true
            for ($d = 0; $d -lt $stride; $d += 4) {
                if ([BitConverter]::ToUInt32($bytes, $cursor + $d) -ne 0) { $zero = $false; break }
            }
            if ($zero) { break }
            $nameRva = [BitConverter]::ToUInt32($bytes, $cursor + $nameOffset)
            $dllName = ''
            if ($nameRva -ne 0) {
                $no = RvaToOff $nameRva
                if ($no -ge 0) { $dllName = ReadAscii $no }
            }
            if ($dllName) {
                $thunkRva = [BitConverter]::ToUInt32($bytes, $cursor + $thunkOffset)
                if ($thunkRva -eq 0) { $thunkRva = [BitConverter]::ToUInt32($bytes, $cursor + $iatOffset) }
                if ($dllName -ieq $Dll) { $list += ReadThunks $thunkRva }
            }
            $cursor += $stride
        }
        return $list
    }

    $all = @()
    $all += ReadImports 1 20 12 0 16     # IMAGE_IMPORT_DESCRIPTOR
    $all += ReadImports 13 32 4 16 12    # IMAGE_DELAYLOAD_DESCRIPTOR
    return $all
}

$allNames = New-Object System.Collections.Generic.HashSet[string]
$files = 0
$scanned = 0

foreach ($root in $rootList) {
    if (-not (Test-Path -LiteralPath $root)) { Write-Warning "missing root: $root"; continue }
    $items = if ((Get-Item -LiteralPath $root).PSIsContainer) {
        Get-ChildItem -LiteralPath $root -Recurse -File -Include *.dll,*.exe -ErrorAction SilentlyContinue
    } else { Get-Item -LiteralPath $root }

    foreach ($f in $items) {
        $files++
        if ($f.Length -gt $MaxBytes) { continue }
        $hit = @()
        try { $hit = @(Get-PeImports $f.FullName) } catch { continue }
        $hit = @($hit | Where-Object { $_ -and $_ -notmatch '^#' })
        if ($hit.Count -eq 0) { continue }
        $scanned++
        if ($PerFile) { "{0}`t{1}`t{2}" -f $f.FullName, $hit.Count, (($hit | Sort-Object -Unique) -join ' ') }
        foreach ($n in $hit) { [void]$allNames.Add($n) }
    }
}

Write-Host ("scanned {0} of {1} file(s) under {2}" -f $scanned, $files, ($rootList -join '; '))
if ($Union) {
    Write-Host ("{0} distinct function(s) imported from {1}:" -f $allNames.Count, $Dll)
    ($allNames | Sort-Object) | ForEach-Object { $_ }
}
