$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$destination = Join-Path $repoRoot "tessdata"

New-Item -ItemType Directory -Force -Path $destination | Out-Null

$baseUrl = "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main"
$languages = @("jpn", "eng")

foreach ($language in $languages) {
    $target = Join-Path $destination "$language.traineddata"

    Write-Host "Downloading $language.traineddata..."
    Invoke-WebRequest -Uri "$baseUrl/$language.traineddata" -OutFile $target -UseBasicParsing
}

Write-Host ""
Write-Host "Tesseract language data is ready in:"
Write-Host "  $destination"
