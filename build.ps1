<#
.SYNOPSIS
    Baut CurlGrabber und packt ZIP-Dateien fuer ein GitHub-Release.

.DESCRIPTION
    Es entstehen zwei Varianten:

      win-x64             klein (~0,2 MB), benoetigt die .NET-10-Desktop-Runtime
      win-x64-standalone  eine einzelne EXE (~100 MB), laeuft ohne Installation

    Hinweis: PublishSingleFile wird bewusst nur fuer die Standalone-Variante
    verwendet. Kombiniert man es mit SelfContained=false, bettet das SDK die
    Runtime trotzdem ein und die Datei waechst auf ueber 100 MB - der
    framework-abhaengige Build bleibt deshalb mehrdateiig.

.EXAMPLE
    .\build.ps1
    Baut beide Varianten nach dist\.

.EXAMPLE
    .\build.ps1 -Release
    Baut beide Varianten und laedt sie als GitHub-Release hoch (benoetigt die GitHub CLI).
#>
[CmdletBinding()]
param(
    [switch]$Release,
    [string]$Version,
    [string]$Notes
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\CurlGrabber\CurlGrabber.csproj'
$outDir = Join-Path $root 'dist'

if (-not $Version) {
    $csproj = [xml](Get-Content $project)
    $Version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
}

Write-Host "CurlGrabber $Version" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Build-Variant {
    param([string]$Suffix, [bool]$Standalone)

    $publishDir = Join-Path $outDir "publish-$Suffix"
    $zipPath = Join-Path $outDir "CurlGrabber-v$Version-$Suffix.zip"

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    $publishArgs = @(
        'publish', $project,
        '-c', 'Release',
        '-r', 'win-x64',
        '-o', $publishDir,
        '--nologo',
        '-p:DebugType=none',
        "-p:Version=$Version"
    )

    if ($Standalone) {
        $publishArgs += @('--self-contained', 'true', '-p:PublishSingleFile=true')
    } else {
        $publishArgs += @('--self-contained', 'false')
    }

    dotnet @publishArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($Suffix) ist fehlgeschlagen." }

    Get-ChildItem $publishDir -Filter *.pdb -Recurse | Remove-Item -Force

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath

    $size = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
    Write-Host ("  {0,-20} {1,8} MB" -f $Suffix, $size) -ForegroundColor Green
    return $zipPath
}

$assets = @(
    (Build-Variant -Suffix 'win-x64' -Standalone $false)
    (Build-Variant -Suffix 'win-x64-standalone' -Standalone $true)
)

if (-not $Release) {
    Write-Host "Pakete liegen in $outDir" -ForegroundColor Cyan
    return
}

$tag = "v$Version"
Write-Host "Release $tag wird veroeffentlicht..." -ForegroundColor Cyan

# Bewusst 'release list' statt 'release view': view schreibt bei fehlendem Release auf
# stderr, was PowerShell 5.1 in einen abbrechenden Fehler verwandelt.
$existingTags = @(gh release list --json tagName --jq '.[].tagName')
if ($LASTEXITCODE -ne 0) { throw "Releases konnten nicht abgefragt werden." }

if ($existingTags -contains $tag) {
    gh release upload $tag @assets --clobber
} else {
    $createArgs = @('release', 'create', $tag) + $assets + @('--title', "CurlGrabber $tag")
    if ($Notes) { $createArgs += @('--notes', $Notes) } else { $createArgs += '--generate-notes' }
    gh @createArgs
}
if ($LASTEXITCODE -ne 0) { throw "Das GitHub-Release ist fehlgeschlagen." }

Write-Host "Release $tag ist online." -ForegroundColor Green
