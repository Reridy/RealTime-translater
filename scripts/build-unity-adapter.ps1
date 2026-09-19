param(
    [Parameter(Mandatory = $true)]
    [string]$GameDir
)

$ErrorActionPreference = "Stop"

$game = (Resolve-Path $GameDir).Path
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo "adapters\UnityBepInEx\RealTimeTranslater.UnityBepInEx.csproj"

$required = @(
    "BepInEx\core\BepInEx.dll",
    "Kurea Struggle_Data\Managed\UnityEngine.dll",
    "Kurea Struggle_Data\Managed\UnityEngine.CoreModule.dll",
    "Kurea Struggle_Data\Managed\UnityEngine.UIModule.dll",
    "Kurea Struggle_Data\Managed\UnityEngine.TextRenderingModule.dll",
    "Kurea Struggle_Data\Managed\UnityEngine.UI.dll",
    "Kurea Struggle_Data\Managed\Unity.TextMeshPro.dll"
)

foreach ($relative in $required) {
    $path = Join-Path $game $relative
    if (-not (Test-Path $path)) {
        throw "Required game assembly not found: $path"
    }
}

Write-Host "Building Unity/BepInEx adapter..."
& dotnet build $project -c Release "/p:GameDir=$game"

if ($LASTEXITCODE -ne 0) {
    throw "Adapter build failed."
}

$output = Join-Path $repo "adapters\UnityBepInEx\bin\Release\netstandard2.1\RealTimeTranslater.UnityBepInEx.dll"
if (-not (Test-Path $output)) {
    throw "Built adapter DLL was not found: $output"
}

$destination = Join-Path $game "BepInEx\plugins\RealTimeTranslaterUnityAdapter"
New-Item -ItemType Directory -Force -Path $destination | Out-Null

$destinationFile = Join-Path $destination "RealTimeTranslater.UnityBepInEx.dll"

if (Test-Path $destinationFile) {
    try {
        $stream = [System.IO.File]::Open(
            $destinationFile,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None
        )
        $stream.Dispose()
    }
    catch [System.IO.IOException] {
        throw @"
The installed Unity adapter DLL is currently in use.

Close Kurea Struggle completely before reinstalling the adapter, then run this script again.

Locked file:
  $destinationFile
"@
    }
}

Copy-Item $output $destinationFile -Force

Write-Host ""
Write-Host "Installed:"
Write-Host "  $destinationFile"
Write-Host ""
Write-Host "Start RealTime Translater, choose 'Unity Adapter + OCR fallback', then start the game."
