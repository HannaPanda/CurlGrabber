<#
.SYNOPSIS
    Baut CurlGrabber und packt eine ZIP-Datei fuer ein GitHub-Release.

.EXAMPLE
    .\build.ps1
    Framework-abhaengig (kleine EXE, benoetigt die .NET-Desktop-Runtime).

.EXAMPLE
    .\build.ps1 -SelfContained
    Alles eingebettet - laeuft ohne installierte .NET-Runtime, aber ~150 MB gross.

.EXAMPLE
    .\build.ps1 -Release
    Baut und laedt das Ergebnis als GitHub-Release hoch (benoetigt die GitHub CLI).
#>
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$Release,
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\CurlGrabber\CurlGrabber.csproj'
$outDir = Join-Path $root 'dist'

if (-not $Version) {
    $csproj = [xml](Get-Content $project)
    $Version = $csproj.Project.PropertyGroup.Version | Select-Object -First 1
}

$suffix = if ($SelfContained) { 'win-x64-standalone' } else { 'win-x64' }
$publishDir = Join-Path $outDir "publish-$suffix"
$zipPath = Join-Path $outDir "CurlGrabber-v$Version-$suffix.zip"

Write-Host "CurlGrabber $Version -> $suffix" -ForegroundColor Cyan

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '-o', $publishDir,
    '--nologo',
    "-p:PublishSingleFile=true",
    "-p:DebugType=none",
    "-p:Version=$Version"
)
$publishArgs += if ($SelfContained) { '--self-contained', 'true' } else { '--self-contained', 'false' }

dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish ist fehlgeschlagen." }

Get-ChildItem $publishDir -Filter *.pdb | Remove-Item -Force

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath

$size = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "Fertig: $zipPath ($size MB)" -ForegroundColor Green

if (-not $Release) { return }

$tag = "v$Version"
Write-Host "Release $tag wird veroeffentlicht..." -ForegroundColor Cyan

$existing = gh release view $tag --json tagName 2>$null
if ($LASTEXITCODE -eq 0) {
    gh release upload $tag $zipPath --clobber
} else {
    gh release create $tag $zipPath --title "CurlGrabber $tag" --generate-notes
}
if ($LASTEXITCODE -ne 0) { throw "Das GitHub-Release ist fehlgeschlagen." }

Write-Host "Release $tag ist online." -ForegroundColor Green
