$ErrorActionPreference = "Stop"

if (-not (Get-Command ollama -ErrorAction SilentlyContinue)) {
    throw "Ollama is not installed or is not available on PATH."
}

Write-Host "Pulling the recommended dedicated translation model..."
& ollama pull translategemma:4b

if ($LASTEXITCODE -ne 0) {
    throw "Could not pull translategemma:4b."
}

Write-Host ""
Write-Host "Ready. In RealTime Translater use:"
Write-Host "  Translation provider: Ollama"
Write-Host "  Ollama model: translategemma:4b"
