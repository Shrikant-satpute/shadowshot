# Builds Shadowshot.exe and ShadowshotSetup.exe (Release) into dist\ plus a portable zip.
# Usage: .\build.ps1 [-Icon]    (-Icon regenerates src\Shadowshot\Assets\shadowshot.ico first)
param([switch]$Icon)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$ico = Join-Path $root 'src\Shadowshot\Assets\shadowshot.ico'

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE" }
}

if ($Icon -or -not (Test-Path -LiteralPath $ico)) {
    Write-Host 'Generating icon...'
    # '--' quoted: a bare -- would be swallowed by PowerShell before reaching dotnet.
    Invoke-Dotnet run --project (Join-Path $root 'tools\IconGen\IconGen.csproj') -c Release '--' $ico
}

Invoke-Dotnet build (Join-Path $root 'Shadowshot.sln') -c Release

New-Item -ItemType Directory -Force -Path $dist | Out-Null
$app = Join-Path $root 'src\Shadowshot\bin\Release\net48\Shadowshot.exe'
$setup = Join-Path $root 'src\ShadowshotSetup\bin\Release\net48\ShadowshotSetup.exe'
Copy-Item -LiteralPath $app, $setup -Destination $dist -Force

$zipItems = @((Join-Path $dist 'Shadowshot.exe'))
$readme = Join-Path $root 'README.md'
if (Test-Path -LiteralPath $readme) { $zipItems += $readme } else { Write-Warning 'README.md not found; the portable zip contains only Shadowshot.exe.' }
$zip = Join-Path $dist 'Shadowshot-portable.zip'
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
# Compress-Archive resolves the destination folder (and a -Force delete) as wildcard paths; escape [ ] for it.
Compress-Archive -LiteralPath $zipItems -DestinationPath ([WildcardPattern]::Escape($zip))

Write-Host ''
Get-ChildItem -LiteralPath $dist -File | ForEach-Object {
    Write-Host ('{0,-28} {1,8:N0} KB' -f $_.Name, [math]::Ceiling($_.Length / 1KB))
}
