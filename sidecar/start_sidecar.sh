#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${SCRIPT_DIR}"

# Activate virtual environment if present
if [[ -f "${SCRIPT_DIR}/.venv/bin/activate" ]]; then
    source "${SCRIPT_DIR}/.venv/bin/activate"
elif [[ -f "${SCRIPT_DIR}/venv/bin/activate" ]]; then
    source "${SCRIPT_DIR}/venv/bin/activate"
elif [[ -f "${SCRIPT_DIR}/../.venv/bin/activate" ]]; then
    source "${SCRIPT_DIR}/../.venv/bin/activate"
fi

echo "Starting VoiceScan Inference Sidecar on http://127.0.0.1:54321..."
exec python3 -m uvicorn inference_server:app --host 127.0.0.1 --port 54321 "$@"
