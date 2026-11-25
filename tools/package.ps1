# Builds a portable Soundboard zip that runs without .NET installed: one self-contained
# Soundboard.exe plus the README. Output: artifacts\Soundboard-<version>-win-x64.zip
#   powershell -ExecutionPolicy Bypass -File tools\package.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Soundboard\Soundboard.csproj'
$runtime = 'win-x64'

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $project." }

$artifacts = Join-Path $root 'artifacts'
$staging = Join-Path $artifacts "Soundboard-$version-$runtime"
$zip = "$staging.zip"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

Write-Host "Publishing Soundboard $version ($runtime, self-contained) ..."
dotnet publish $project -c Release -r $runtime --self-contained true -o $staging --nologo -v q `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Copy-Item (Join-Path $root 'README.md') $staging
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
Remove-Item $staging -Recurse -Force

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Done: $zip ($size MB)"
