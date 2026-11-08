# install-appinit.ps1 — register VocalPatch as an AppInit_DLL, so Windows loads it
# into every editor automatically. Register once; nothing to do after that.
#
#   Right-click  ->  Run with PowerShell (as administrator)
#   or:  powershell -ExecutionPolicy Bypass -File .\install-appinit.ps1
#
# What it does
#   1. copies the managed payload to a space-free fixed folder (AppInit_DLLs is a
#      space-delimited list, so a path with spaces cannot be registered)
#   2. sets, under HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows
#         AppInit_DLLs            = <payload>\vocalpatch_loader.dll   (appended)
#         LoadAppInit_DLLs        = 1
#         RequireSignedAppInit_DLLs = 0
#   3. reports whether Secure Boot is off (AppInit is disabled entirely when it is on)
#
# The loader carries a host allow-list: it only starts the runtime in the five
# editors (process-name prefix: cevio / synthv / voisona / openutau / piapro /
# vocaloid). Every other process it is loaded into is left untouched, silently.
#
# Windows loads AppInit DLLs into new processes only, so close and reopen the
# editor once after installing. From then on it is automatic.

[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:ProgramData 'VocalPatch'),
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'

$key = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows'

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $pr = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '需要管理员权限。请右键“以管理员身份运行 PowerShell”，或运行 install-appinit.cmd。'
    }
}

function Show-SecureBoot {
    try {
        $sb = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State' -ErrorAction Stop
        if ($sb.UEFISecureBootEnabled -eq 1) {
            Write-Warning 'Secure Boot 已开启：Windows 在 Secure Boot 下会完全禁用 AppInit_DLLs，本方案不会生效。'
            return $false
        }
    } catch { }
    Write-Host 'Secure Boot: 关闭（AppInit 可用）'
    return $true
}

$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }

# Where the payload lives in this package: dist\payload (script sits in dist\).
$sourcePayload = Join-Path $scriptDir 'payload'
if (-not (Test-Path (Join-Path $sourcePayload 'VocalPatch.Editor.dll'))) {
    # Repo layout fallback: <root>\src\VocalPatch.Editor\bin\Release\net472
    $candidate = Join-Path $scriptDir 'src\VocalPatch.Editor\bin\Release\net472'
    if (Test-Path (Join-Path $candidate 'VocalPatch.Editor.dll')) { $sourcePayload = $candidate }
    else { throw "找不到 payload（VocalPatch.Editor.dll）。请在 dist\ 目录下运行本脚本。" }
}

$targetPayload = Join-Path $InstallDir 'payload'
$loaderPath = Join-Path $targetPayload 'vocalpatch_loader.dll'
$loaderX64 = Join-Path $targetPayload 'vocalpatch_loader_x64.dll'

Write-Host "payload 源 : $sourcePayload"
Write-Host "安装到     : $targetPayload"

if ($DryRun) {
    Write-Host ("[DryRun] 会设置 {0}\AppInit_DLLs += {1}" -f $key, $loaderPath)
    Write-Host "[DryRun] 会设置 LoadAppInit_DLLs=1, RequireSignedAppInit_DLLs=0"
    return
}

Assert-Admin
Show-SecureBoot | Out-Null

# 1) deploy the payload to a space-free folder
New-Item -ItemType Directory -Force -Path $targetPayload | Out-Null
Copy-Item (Join-Path $sourcePayload '*') $targetPayload -Recurse -Force

if (-not (Test-Path $loaderPath)) {
    if (Test-Path $loaderX64) { Copy-Item $loaderX64 $loaderPath -Force }
    else { throw "payload 里没有 vocalpatch_loader.dll（或 _x64 版）。" }
}

if ($loaderPath -match '\s') {
    throw "安装路径含空格（$loaderPath），AppInit_DLLs 无法使用。请用 -InstallDir 指定一个无空格目录。"
}

# 2) register (append to any existing list, never clobber another product's entry)
$current = (Get-ItemProperty -Path $key -Name AppInit_DLLs -ErrorAction SilentlyContinue).AppInit_DLLs
if (-not $current) { $current = '' }
$entries = @($current -split '[ ,]+' | Where-Object { $_ -ne '' })
if ($entries -notcontains $loaderPath) { $entries += $loaderPath }
$newValue = ($entries -join ' ')

New-ItemProperty -Path $key -Name AppInit_DLLs             -PropertyType String -Value $newValue -Force | Out-Null
New-ItemProperty -Path $key -Name LoadAppInit_DLLs         -PropertyType DWord  -Value 1        -Force | Out-Null
New-ItemProperty -Path $key -Name RequireSignedAppInit_DLLs -PropertyType DWord -Value 0        -Force | Out-Null

Write-Host ''
Write-Host '已注册：'
(Get-ItemProperty $key) | Select-Object AppInit_DLLs, LoadAppInit_DLLs, RequireSignedAppInit_DLLs | Format-List

Write-Host ''
Write-Host '下一步：关闭并重新打开编辑器即可（AppInit 只对“新启动”的进程生效）。'
Write-Host '验证：打开编辑器后看 %TEMP%\vocalpatch-editor.log，应有该 pid 的 profile/watching/hotkey 行。'
Write-Host '卸载：运行 uninstall-appinit.ps1（同样需要管理员）。'
