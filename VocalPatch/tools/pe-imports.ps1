# pe-imports.ps1 - list a PE image's static and delay-load imports.
#
#   .\tools\pe-imports.ps1 -Path "D:\Synthesizer V Studio 2 Pro\synthv-studio.exe" -NamesOnly
#   .\tools\pe-imports.ps1 -Path "...\synthv-studio.exe" -Detail
#   .\tools\pe-imports.ps1 -Path "...\synthv-studio.exe" -Detail -Only WINMM.dll
#
# Hand-rolled header walk (no dumpbin on this box). Used to find a DLL that a
# closed-source editor imports by name (so a proxy can be dropped beside it) and
# to learn exactly which entry points that proxy has to provide.

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Path,
    [switch] $NamesOnly,
    [switch] $FunctionsOnly,
    [switch] $Detail,
    [string] $Only
)

$ErrorActionPreference = 'Stop'
$bytes = [IO.File]::ReadAllBytes($Path)

$e_lfanew = [BitConverter]::ToInt32($bytes, 0x3C)
if ([Text.Encoding]::ASCII.GetString($bytes, $e_lfanew, 4) -ne "PE`0`0") { throw "not a PE image: $Path" }

$coff = $e_lfanew + 4
$machine = [BitConverter]::ToUInt16($bytes, $coff)
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
    $sections += [pscustomobject]@{
        Name    = ([Text.Encoding]::ASCII.GetString($bytes, $s, 8)).TrimEnd([char]0)
        VSize   = [BitConverter]::ToUInt32($bytes, $s + 8)
        VA      = [BitConverter]::ToUInt32($bytes, $s + 12)
        RawSize = [BitConverter]::ToUInt32($bytes, $s + 16)
        RawPtr  = [BitConverter]::ToUInt32($bytes, $s + 20)
    }
}

function RvaToOff([uint32] $rva) {
    foreach ($sec in $script:sections) {
        $span = [Math]::Max($sec.VSize, $sec.RawSize)
        if ($rva -ge $sec.VA -and $rva -lt ($sec.VA + $span)) {
            return [int]($sec.RawPtr + ($rva - $sec.VA))
        }
    }
    return -1
}
function ReadAscii([int] $off) {
    $end = $off
    while ($end -lt $script:bytes.Length -and $script:bytes[$end] -ne 0) { $end++ }
    return [Text.Encoding]::ASCII.GetString($script:bytes, $off, $end - $off)
}

function ReadThunks([uint32] $thunkRva) {
    $names = @()
    if ($thunkRva -eq 0) { return $names }
    $off = RvaToOff $thunkRva
    if ($off -lt 0) { return $names }
    for ($i = 0; $i -lt 4096; $i++) {
        $at = $off + $i * $script:thunkSize
        $v = if ($script:is64) { [BitConverter]::ToUInt64($script:bytes, $at) } else { [uint64][BitConverter]::ToUInt32($script:bytes, $at) }
        if ($v -eq 0) { break }
        $ordFlag = if ($script:is64) { 0x8000000000000000 } else { 0x80000000 }
        if (($v -band $ordFlag) -ne 0) {
            $names += "#" + ($v -band 0xFFFF)
        } else {
            $no = RvaToOff ([uint32]($v -band 0x7FFFFFFF))
            if ($no -ge 0) { $names += (ReadAscii ($no + 2)) }   # skip the 2-byte Hint
        }
    }
    return $names
}

# IMAGE_IMPORT_DESCRIPTOR is 20 bytes and the DLL-name RVA sits at +12;
# IMAGE_DELAYLOAD_DESCRIPTOR is 32 bytes with the name RVA at +4.
function ReadImports([int] $index, [int] $stride, [int] $nameOffset, [int] $thunkOffset, [int] $iatOffset) {
    $rva = [BitConverter]::ToUInt32($script:bytes, $script:ddOff + $index * 8)
    if ($rva -eq 0) { return @() }
    $off = RvaToOff $rva
    if ($off -lt 0) { return @() }
    $list = @()
    $cursor = $off
    while ($true) {
        $zero = $true
        for ($d = 0; $d -lt $stride; $d += 4) {
            if ([BitConverter]::ToUInt32($script:bytes, $cursor + $d) -ne 0) { $zero = $false; break }
        }
        if ($zero) { break }
        $nameRva = [BitConverter]::ToUInt32($script:bytes, $cursor + $nameOffset)
        $dll = ''
        if ($nameRva -ne 0) {
            $no = RvaToOff $nameRva
            if ($no -ge 0) { $dll = ReadAscii $no }
        }
        $thunkRva = [BitConverter]::ToUInt32($script:bytes, $cursor + $thunkOffset)
        if ($thunkRva -eq 0) { $thunkRva = [BitConverter]::ToUInt32($script:bytes, $cursor + $iatOffset) }
        if ($dll) { $list += [pscustomobject]@{ Dll = $dll; Functions = (ReadThunks $thunkRva) } }
        $cursor += $stride
    }
    return $list
}

$all = @()
$all += ReadImports 1 20 12 0 16
$all += ReadImports 13 32 4 16 12

$file = Get-Item $Path
Write-Host ("{0}" -f $file.FullName)
Write-Host ("  arch={1}  size={0:N0}" -f $file.Length, $(if ($is64) { 'x64' } else { 'x86' }))

if ($Only) { $all = $all | Where-Object { $_.Dll -ieq $Only } }

if ($FunctionsOnly) {
    # Pipeline output (not Write-Host), so a calling script can capture it.
    $all | ForEach-Object { $_.Functions } | Sort-Object -Unique
    return
}

if ($NamesOnly) {
    ($all | Select-Object -ExpandProperty Dll) | Sort-Object -Unique | ForEach-Object { $_ }
    return
}

if (-not $Detail) {
    foreach ($m in ($all | Sort-Object Dll)) {
        Write-Host ("  {0,-40} ({1} function(s))" -f $m.Dll, $m.Functions.Count)
    }
    return
}

foreach ($m in ($all | Sort-Object Dll)) {
    Write-Host ("  {0}  [{1} function(s)]" -f $m.Dll, $m.Functions.Count)
    $m.Functions | ForEach-Object { "      $_" }
}
