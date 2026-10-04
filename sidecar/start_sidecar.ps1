# VoiceScan Standalone Inference Sidecar Launcher (PowerShell)
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

# Activate virtual environment if present
if (Test-Path "$scriptDir\.venv\Scripts\Activate.ps1") {
    & "$scriptDir\.venv\Scripts\Activate.ps1"
} elseif (Test-Path "$scriptDir\venv\Scripts\Activate.ps1") {
    & "$scriptDir\venv\Scripts\Activate.ps1"
} elseif (Test-Path "$scriptDir\..\.venv\Scripts\Activate.ps1") {
    & "$scriptDir\..\.venv\Scripts\Activate.ps1"
}

Write-Host "Starting VoiceScan Inference Sidecar on http://127.0.0.1:54321..." -ForegroundColor Cyan
python -m uvicorn inference_server:app --host 127.0.0.1 --port 54321 @args
