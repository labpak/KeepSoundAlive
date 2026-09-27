# KeepSoundAlive installer for Windows (no admin rights needed).
#   irm https://raw.githubusercontent.com/labpak/KeepSoundAlive/main/install.ps1 | iex
# Downloads the latest release into %LOCALAPPDATA%\Programs\KeepSoundAlive, adds a Start menu shortcut and starts it.
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$url = "https://github.com/labpak/KeepSoundAlive/releases/latest/download/KeepSoundAlive-win-$arch.exe"
$dir = Join-Path $env:LOCALAPPDATA 'Programs\KeepSoundAlive'
$exe = Join-Path $dir 'KeepSoundAlive.exe'

Get-Process KeepSoundAlive -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force $dir | Out-Null
Write-Host "Downloading $url ..."
$ProgressPreference = 'SilentlyContinue'
Invoke-WebRequest -Uri $url -OutFile $exe -UseBasicParsing

$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'KeepSoundAlive.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $dir
$link.Description = 'Keeps speakers awake with an inaudible signal'
$link.Save()

Start-Process $exe
Write-Host "KeepSoundAlive installed to $exe and started."
