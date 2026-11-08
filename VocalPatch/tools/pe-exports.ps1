# pe-exports.ps1 - list a PE's exports; flag forwarders ("name -> target").
#
#   .\tools\pe-exports.ps1 -Path C:\Windows\System32\winmm.dll -Summary
#   .\tools\pe-exports.ps1 -Path .\proxy.dll -ForwardersOnly

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Path,
    [switch] $NamesOnly,
    [switch] $ForwardersOnly,
    [switch] $Summary
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
$ddOff = if ($magic -eq 0x20B) { $opt + 112 } else { $opt + 96 }
$sectionsOff = $opt + $sizeOpt

$sections = @()
for ($i = 0; $i -lt $numSections; $i++) {
    $s = $sectionsOff + $i * 40
    $sections += [pscustomobject]@{
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

$expRva = [BitConverter]::ToUInt32($bytes, $ddOff + 0 * 8)
if ($expRva -eq 0) { Write-Host "$Path : no export directory"; return }

$off = RvaToOff $expRva
$expDirSize = [BitConverter]::ToUInt32($bytes, $off + 16)   # AddressOfFunctions size == export range size
$nameCount = [BitConverter]::ToUInt32($bytes, $off + 24)
$funcCount = [BitConverter]::ToUInt32($bytes, $off + 20)
$namesRva = [BitConverter]::ToUInt32($bytes, $off + 32)
$ordRva = [BitConverter]::ToUInt32($bytes, $off + 36)
$funcRva = [BitConverter]::ToUInt32($bytes, $off + 28)
$dllNameOff = RvaToOff ([BitConverter]::ToUInt32($bytes, $off + 12))

$funcOff = RvaToOff $funcRva
$namesOff = RvaToOff $namesRva
$ordOff = RvaToOff $ordRva

$byIndex = @{}
for ($k = 0; $k -lt $funcCount; $k++) {
    $byIndex[[int][BitConverter]::ToUInt32($bytes, $funcOff + $k * 4)] = $k
}

$results = @()
for ($n = 0; $n -lt $nameCount; $n++) {
    $sr = [BitConverter]::ToUInt32($bytes, $namesOff + $n * 4)
    $so = RvaToOff $sr
    $sym = ReadAscii $so
    $ordinal = [BitConverter]::ToUInt16($bytes, $ordOff + $n * 2)
    $rva = $byIndex[$ordinal]
    $fnRva = [BitConverter]::ToUInt32($bytes, $funcOff + $rva * 4)
    $isForward = ($fnRva -ge $expRva -and $fnRva -lt ($expRva + $expDirSize))
    $target = ''
    if ($isForward) {
        $fo = RvaToOff $fnRva
        $target = ReadAscii $fo
    }
    $results += [pscustomobject]@{ Name = $sym; Ordinal = $ordinal; Forward = $isForward; Target = $target }
}

$file = Get-Item $Path
Write-Host ("{0}  ({1:N0} bytes)" -f $file.FullName, $file.Length)
if ($dllNameOff -ge 0) { Write-Host ("  internal name: {0}" -f (ReadAscii $dllNameOff)) }
Write-Host ("  exports: {0} named, {1} function slots, {2} forwarders" -f $nameCount, $funcCount, ($results | Where-Object Forward).Count)

if ($Summary) { return }
if ($ForwardersOnly) {
    $results | Where-Object Forward | ForEach-Object { "  {0} -> {1}" -f $_.Name, $_.Target }
    return
}
if ($NamesOnly) {
    $results | ForEach-Object { $_.Name }
    return
}
$results | ForEach-Object { "  [{0,4}] {1}{2}" -f $_.Ordinal, $_.Name, $(if ($_.Forward) { " -> $($_.Target)" } else { '' }) }
