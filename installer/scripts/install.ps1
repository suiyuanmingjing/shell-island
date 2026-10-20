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

$source = Join-Path $PSScriptRoot 'app'
$dest = Join-Path $env:LOCALAPPDATA 'ShellIsland'
Get-Process ShellIsland -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item (Join-Path $source '*') $dest -Recurse -Force

# Defender 需要放行注入器和 TAP DLL，否则会被当成恶意软件删除
try {
    Add-MpPreference -ExclusionPath $dest -ErrorAction Stop
} catch {
    Write-Warning "无法添加 Defender 排除：$($_.Exception.Message)"
}

$shell = New-Object -ComObject WScript.Shell
$startup = [Environment]::GetFolderPath('Startup')
$shortcut = $shell.CreateShortcut((Join-Path $startup 'ShellIsland.lnk'))
$shortcut.TargetPath = Join-Path $dest 'ShellIsland.exe'
$shortcut.WorkingDirectory = $dest
$shortcut.Save()

$programs = [Environment]::GetFolderPath('Programs')
$menu = Join-Path $programs 'ShellIsland.lnk'
$menuShortcut = $shell.CreateShortcut($menu)
$menuShortcut.TargetPath = Join-Path $dest 'ShellIsland.exe'
$menuShortcut.WorkingDirectory = $dest
$menuShortcut.Save()

Start-Process (Join-Path $dest 'ShellIsland.exe')
Write-Host "ShellIsland 已安装到 $dest"
