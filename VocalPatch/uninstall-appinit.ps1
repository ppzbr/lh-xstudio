# uninstall-appinit.ps1 — undo install-appinit.ps1.
#
#   Right-click -> Run with PowerShell (as administrator)
#   or:  powershell -ExecutionPolicy Bypass -File .\uninstall-appinit.ps1
#
# Removes VocalPatch's entry from AppInit_DLLs and switches the mechanism back off
# when no other product is using it. Pass -RemoveFiles to also delete the deployed
# payload folder.

[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:ProgramData 'VocalPatch'),
    [switch] $RemoveFiles
)

$ErrorActionPreference = 'Stop'

$key = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows'

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$pr = New-Object Security.Principal.WindowsPrincipal($id)
if (-not $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '需要管理员权限。请运行 uninstall-appinit.cmd（会弹 UAC）。'
}

$current = (Get-ItemProperty -Path $key -Name AppInit_DLLs -ErrorAction SilentlyContinue).AppInit_DLLs
if (-not $current) { $current = '' }
$entries = @($current -split '[ ,]+' | Where-Object { $_ -ne '' })
$kept = @($entries | Where-Object { $_ -notmatch 'vocalpatch' -and $_ -notlike "*$InstallDir*" })

New-ItemProperty -Path $key -Name AppInit_DLLs -PropertyType String -Value ($kept -join ' ') -Force | Out-Null
if ($kept.Count -eq 0) {
    New-ItemProperty -Path $key -Name LoadAppInit_DLLs -PropertyType DWord -Value 0 -Force | Out-Null
    Write-Host 'AppInit_DLLs 已清空，LoadAppInit_DLLs=0（机制关闭）。'
} else {
    Write-Host ('仍有其它 AppInit_DLLs 条目，保留：' + ($kept -join ' '))
}

if ($RemoveFiles -and (Test-Path $InstallDir)) {
    Start-Sleep -Milliseconds 300   # let any editor release the mapped DLLs
    Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "已删除 $InstallDir（若被运行中的编辑器占用，关闭后手动删）。"
}

Write-Host ''
(Get-ItemProperty $key) | Select-Object AppInit_DLLs, LoadAppInit_DLLs | Format-List
Write-Host '完成后重启编辑器，注入即不再发生。'
