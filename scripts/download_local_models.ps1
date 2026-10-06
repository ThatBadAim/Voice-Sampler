# Downloads official ONNX embedding model weights into the models directory
# and verifies their SHA-256 integrity against models/manifest.json.

param(
    [string]$TargetDir = ""
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir

if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    $TargetDir = Join-Path $RootDir "models"
}

if (-not (Test-Path $TargetDir)) {
    New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
}

$ManifestPath = Join-Path $RootDir "models\manifest.json"
if (-not (Test-Path $ManifestPath)) {
    throw "Manifest file not found at $ManifestPath"
}

$Manifest = Get-Content -Raw -Path $ManifestPath | ConvertFrom-Json

Write-Host "==> Verifying and downloading VoiceScan embedding models to: $TargetDir"

foreach ($model in $Manifest.models) {
    $fileName = $model.filename
    $expectedHash = $model.sha256
    $sourceUrl = $model.source_url
    $destPath = Join-Path $TargetDir $fileName

    $needsDownload = $true
    if (Test-Path $destPath) {
        $actualHash = (Get-FileHash -Path $destPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -eq $expectedHash.ToLowerInvariant()) {
            Write-Host "  [OK] $fileName is already present and verified ($actualHash)"
            $needsDownload = $false
        } else {
            Write-Host "  [WARN] $fileName hash mismatch (expected $expectedHash, got $actualHash). Re-downloading..."
        }
    }

    if ($needsDownload) {
        Write-Host "  [DOWNLOADING] $fileName from $sourceUrl..."
        $tempPath = "$destPath.tmp"
        if (Test-Path $tempPath) { Remove-Item -Force $tempPath }

        # Use curl.exe if available (fast and supports large streaming downloads)
        $curlCmd = Get-Command "curl.exe" -ErrorAction SilentlyContinue
        if ($curlCmd) {
            & curl.exe -L -f --retry 3 -o $tempPath $sourceUrl
        } else {
            Invoke-WebRequest -Uri $sourceUrl -OutFile $tempPath -UseBasicParsing
        }

        $downloadedHash = (Get-FileHash -Path $tempPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($downloadedHash -ne $expectedHash.ToLowerInvariant()) {
            Remove-Item -Force $tempPath -ErrorAction SilentlyContinue
            throw "Downloaded file $fileName failed SHA-256 verification! Expected $expectedHash, got $downloadedHash"
        }

        Move-Item -Force -Path $tempPath -Destination $destPath
        Write-Host "  [VERIFIED] $fileName downloaded and verified successfully."
    }
}

Write-Host "==> All models ready in $TargetDir"
