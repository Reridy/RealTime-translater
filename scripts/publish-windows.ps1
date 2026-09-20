param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$repo = Split-Path $PSScriptRoot -Parent
$appProject = Join-Path $repo "src\RealTimeTranslater.App\RealTimeTranslater.App.csproj"
$tessdata = Join-Path $repo "tessdata"
$distRoot = Join-Path $repo "dist"
$publishDir = Join-Path $distRoot "RealTimeTranslater-$Runtime"
$zipPath = "$publishDir.zip"

$requiredOcr = @(
    (Join-Path $tessdata "eng.traineddata"),
    (Join-Path $tessdata "jpn.traineddata")
)

$missingOcr = $requiredOcr | Where-Object { -not (Test-Path $_) }
if ($missingOcr.Count -gt 0) {
    Write-Host "OCR data is missing; downloading tessdata_fast..."
    & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "download-tessdata.ps1")
    if ($LASTEXITCODE -ne 0) {
        throw "Could not download OCR language data."
    }
}

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $distRoot | Out-Null

Write-Host "Publishing self-contained $Runtime build..."
& dotnet publish $appProject -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=true -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

$publishedTessdata = Join-Path $publishDir "tessdata"
New-Item -ItemType Directory -Force -Path $publishedTessdata | Out-Null

Copy-Item (Join-Path $tessdata "eng.traineddata") $publishedTessdata -Force
Copy-Item (Join-Path $tessdata "jpn.traineddata") $publishedTessdata -Force

$readme = @"
RealTime Translater - Windows x64
================================

1. Run RealTimeTranslater.App.exe.
2. For local AI translation, install Ollama and pull translategemma:4b.
3. For supported Unity games, install the optional BepInEx adapter described in docs/UNITY_ADAPTER.md.
4. User settings and the persistent translation cache are stored under:
   %LOCALAPPDATA%\RealTimeTranslater

This package includes English and Japanese Tesseract OCR data.
"@

Set-Content -Path (Join-Path $publishDir "README_FIRST.txt") -Value $readme -Encoding UTF8

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host ""
Write-Host "Release package created:"
Write-Host "  $zipPath"
