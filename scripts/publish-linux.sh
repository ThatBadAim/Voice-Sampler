#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
OUTPUT_DIR="${ROOT_DIR}/dist/linux-x64"

echo "==> Publishing VoiceScan for linux-x64..."
rm -rf "${OUTPUT_DIR}"
mkdir -p "${OUTPUT_DIR}"

dotnet publish "${ROOT_DIR}/app/VoiceScan.Avalonia/VoiceScan.Avalonia.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:PublishTrimmed=false \
    -o "${OUTPUT_DIR}"

# Bundle the models; refuse to ship a package that cannot scan.
echo "==> Bundling models..."
mkdir -p "${OUTPUT_DIR}/models"
for model in ecapa_tdnn.onnx titanet_small.onnx; do
    if [ ! -f "${ROOT_DIR}/models/${model}" ]; then
        echo "ERROR: models/${model} is missing. See models/manifest.json for the download link and checksum." >&2
        exit 1
    fi
    cp "${ROOT_DIR}/models/${model}" "${OUTPUT_DIR}/models/"
done
cp "${ROOT_DIR}/models/manifest.json" "${OUTPUT_DIR}/models/"

echo "==> Packaging tarball..."
mkdir -p "${ROOT_DIR}/dist/packages"
tar -czf "${ROOT_DIR}/dist/packages/VoiceScan-linux-x64.tar.gz" -C "${ROOT_DIR}/dist" linux-x64

echo "==> Successfully packaged VoiceScan to ${ROOT_DIR}/dist/packages/VoiceScan-linux-x64.tar.gz"
