param()
$ErrorActionPreference = 'Stop'

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList @(
        '-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`""
    )
    exit
}

Get-Process ShellIsland -ErrorAction SilentlyContinue | Stop-Process -Force
$dest = Join-Path $env:LOCALAPPDATA 'ShellIsland'
$startup = [Environment]::GetFolderPath('Startup')
$programs = [Environment]::GetFolderPath('Programs')
Remove-Item (Join-Path $startup 'ShellIsland.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $programs 'ShellIsland.lnk') -Force -ErrorAction SilentlyContinue
try { Remove-MpPreference -ExclusionPath $dest -ErrorAction SilentlyContinue } catch {}
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "已卸载。重启 Explorer 可清掉已注入的 TAP。"
