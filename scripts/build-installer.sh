#!/usr/bin/env bash
# Builds dist/packages/VoiceScan-Setup.exe, a Windows installer that installs VoiceScan and, if missing, FFmpeg.
# Runs on Linux, macOS or Windows (Git Bash). Needs the .NET SDK, python3 and the models in models/.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
APP_DIR="${ROOT_DIR}/dist/win-x64"
PAYLOAD="${ROOT_DIR}/dist/payload.zip"
PACKAGES="${ROOT_DIR}/dist/packages"

for model in ecapa_tdnn.onnx titanet_small.onnx; do
    if [ ! -f "${ROOT_DIR}/models/${model}" ]; then
        echo "ERROR: models/${model} is missing. See models/manifest.json for the download link and checksum." >&2
        exit 1
    fi
done

echo "==> Publishing VoiceScan for win-x64..."
rm -rf "${APP_DIR}" "${PAYLOAD}"
dotnet publish "${ROOT_DIR}/app/VoiceScan.Avalonia/VoiceScan.Avalonia.csproj" \
    -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=false -p:PublishTrimmed=false \
    -o "${APP_DIR}"

echo "==> Bundling models and licenses..."
mkdir -p "${APP_DIR}/models"
cp "${ROOT_DIR}/models/ecapa_tdnn.onnx" "${ROOT_DIR}/models/titanet_small.onnx" "${ROOT_DIR}/models/manifest.json" "${APP_DIR}/models/"
cp "${ROOT_DIR}/THIRD-PARTY-NOTICES.md" "${APP_DIR}/"
cp "${ROOT_DIR}/docs/LICENSES.md" "${APP_DIR}/LICENSES.md"

echo "==> Creating installer payload..."
python3 - "${APP_DIR}" "${PAYLOAD}" <<'PY'
import sys, shutil, pathlib
src, dst = sys.argv[1], pathlib.Path(sys.argv[2])
shutil.make_archive(str(dst.with_suffix("")), "zip", src)
PY

echo "==> Building VoiceScan-Setup.exe..."
dotnet publish "${ROOT_DIR}/installer/VoiceScan.Installer/VoiceScan.Installer.csproj" \
    -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:PayloadPath="${PAYLOAD}" \
    -o "${ROOT_DIR}/dist/installer"

mkdir -p "${PACKAGES}"
cp "${ROOT_DIR}/dist/installer/VoiceScan-Setup.exe" "${PACKAGES}/VoiceScan-Setup.exe"
echo "==> Built ${PACKAGES}/VoiceScan-Setup.exe"
