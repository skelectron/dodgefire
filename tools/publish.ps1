# Builds a self-contained Windows x64 release of CROSSFIRE and zips it for itch.io (or anywhere).
# Nothing is uploaded. Usage (from the repo root):  powershell -ExecutionPolicy Bypass -File tools\publish.ps1
param(
    [string]$Runtime = "win-x64",
    [string]$OutDir = "artifacts"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$hash = (git rev-parse --short HEAD).Trim()
$dirty = if (git status --porcelain) { "-dirty" } else { "" }
$name = "crossfire-$Runtime-$hash$dirty"
$publishDir = Join-Path $root "$OutDir\$name"
$zip = Join-Path $root "$OutDir\$name.zip"

if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
if (Test-Path $zip) { Remove-Item -Force $zip }

Write-Host "Publishing $name ..."
dotnet publish src\Crossfire.App\Crossfire.App.csproj -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -p:DebugType=none -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# BRUTAL License v1.0 and asset checks: refuse to package a build that is missing them.
$required = @(
    "Crossfire.App.exe",
    "NOTICE.txt",
    "LICENSES\BRUTAL-LICENSE.txt",
    "LICENSES\BRUTAL-GLFW-THIRD-PARTY-NOTICES.txt",
    "LICENSES\BRUTAL-SDL-MIXER-THIRD-PARTY-NOTICES.txt",
    "LICENSES\BRUTAL-RAKNET-THIRD-PARTY-NOTICES.txt",
    "LICENSES\DOTNET-LICENSE.txt",
    "RakNetDLL.dll",
    "assets\brutal\LOGO_BRUTAL_WHT.png",
    "assets\kenney\space-shooter\license.txt",
    "assets\kenney\audio\music_menu.ogg"
)
foreach ($f in $required) {
    if (-not (Test-Path (Join-Path $publishDir $f))) { throw "Build is missing $f" }
}
$licenses = (Get-ChildItem (Join-Path $publishDir "LICENSES") -Filter *.txt).Count

# Rename the exe so players see the game's name.
Rename-Item (Join-Path $publishDir "Crossfire.App.exe") "CROSSFIRE.exe"

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zip
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ""
Write-Host "Done: $zip ($size MB, $licenses licence files)"
Write-Host "Run:  $publishDir\CROSSFIRE.exe"
