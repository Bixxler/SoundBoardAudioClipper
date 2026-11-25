# Builds Soundboard in Release mode, installs it to %LOCALAPPDATA%\Programs\Soundboard,
# and creates a desktop shortcut. Run it again after code changes to update the install.
#   powershell -ExecutionPolicy Bypass -File install.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Soundboard'
$exe = Join-Path $installDir 'Soundboard.exe'

$running = Get-Process Soundboard -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($running) { throw "Soundboard is running from the install folder. Close it, then run this again." }

Write-Host "Publishing to $installDir ..."
dotnet publish "$root\src\Soundboard\Soundboard.csproj" -c Release -o $installDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$desktop = [Environment]::GetFolderPath('Desktop')   # Follows OneDrive desktop redirection
$shortcutPath = Join-Path $desktop 'Soundboard.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $installDir
$shortcut.IconLocation = "$exe,0"
$shortcut.Description = 'Soundboard: play sounds into Discord and clip Discord audio'
$shortcut.Save()

Write-Host "Installed. Desktop shortcut: $shortcutPath"
