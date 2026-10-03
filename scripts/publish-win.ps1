$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir
$OutputDir = Join-Path $RootDir "dist\win-x64"

Write-Host "==> Publishing VoiceScan for win-x64..."
if (Test-Path $OutputDir) {
    Remove-Item -Recurse -Force $OutputDir
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$Project = Join-Path $RootDir "app\VoiceScan.Avalonia\VoiceScan.Avalonia.csproj"
dotnet publish $Project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -o $OutputDir

# Bundle the models; refuse to ship a package that cannot scan.
Write-Host "==> Bundling models..."
$DestModels = Join-Path $OutputDir "models"
New-Item -ItemType Directory -Force -Path $DestModels | Out-Null
foreach ($Model in @("silero_vad.onnx", "wespeaker_en_voxceleb_resnet34.onnx")) {
    $Source = Join-Path $RootDir "models\$Model"
    if (-not (Test-Path $Source)) {
        throw "models\$Model is missing. See models\manifest.json for the download link and checksum."
    }
    Copy-Item $Source $DestModels
}
Copy-Item (Join-Path $RootDir "models\manifest.json") $DestModels

$PackagesDir = Join-Path $RootDir "dist\packages"
New-Item -ItemType Directory -Force -Path $PackagesDir | Out-Null
$ZipPath = Join-Path $PackagesDir "VoiceScan-win-x64.zip"

Write-Host "==> Creating zip archive..."
if (Test-Path $ZipPath) {
    Remove-Item -Force $ZipPath
}
Compress-Archive -Path "$OutputDir\*" -DestinationPath $ZipPath

Write-Host "==> Successfully packaged VoiceScan to $ZipPath"
