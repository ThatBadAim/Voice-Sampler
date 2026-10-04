# VoiceScan

Find where a specific person speaks in hours of recordings, such as gameplay captures with voice chat. Everything runs on your computer; nothing is uploaded.

## Install

**Windows:** run `VoiceScan-Setup.exe`. It installs VoiceScan for the current user (no administrator rights needed), adds Start menu and optional desktop shortcuts, and downloads FFmpeg (checksum verified) if it is not already installed. Uninstall from Windows Settings, Installed apps; you can keep or delete your saved voices.

**Linux:** extract `VoiceScan-linux-x64.tar.gz`, install FFmpeg (`sudo apt install ffmpeg` or `sudo pacman -S ffmpeg`) and run `./VoiceScan`.

**From source (Windows):** double-click `run.bat`. It needs the .NET SDK and the models described below.

If anything is missing when VoiceScan starts, a setup window says what and how to fix it.

## Use

1. **Voices.** Choose a recording of only that person talking, ideally 30 seconds or more with no music or other voices. VoiceScan checks the quality, you name the voice and confirm the person agreed, then save. To remove a voice, click it in "Your voices" and press Delete (or the Delete button).
2. **Scan recordings.** Pick the voice and a folder of recordings, then Start scan. Recordings of any length are fine: audio is streamed, not loaded into memory.
3. **Results.** Each file is marked Match, Possible or No match. Click a file to see where on the waveform the voice appears and to play it. "Export report…" saves a CSV, a PDF and the matching audio clips. Possible hits can be confirmed or rejected in Review Queue; confirmed hits improve the voice over time.

## Where things are stored

`%LOCALAPPDATA%\VoiceScan` on Windows, `~/.local/share/VoiceScan` on Linux: voice profiles (`profiles/`), the scan cache (`voicescan.db`, so re-scanning is fast) and review decisions. 

## GPU

CPU works out of the box. For an NVIDIA GPU, build with `-p:VoiceScanGpu=true` (CUDA 12 and cuDNN 9). A banner shows when the app is running on CPU.

## Building from source

```bash
dotnet build VoiceScan.sln
dotnet test VoiceScan.sln
dotnet run --project app/VoiceScan.Avalonia
```

Download the ONNX models listed in `models/manifest.json` into `models/` (checksums are in the manifest). Packaging: `scripts/publish-linux.sh` (tarball), `scripts/publish-win.ps1` (zip) and `scripts/build-installer.sh` (`dist/packages/VoiceScan-Setup.exe`, buildable from Linux). All refuse to package without the models.

Design: `DESIGN.md`. Accuracy measurements: `docs/accuracy-log.md`. Model licenses: `docs/LICENSES.md`; third-party code notices: `THIRD-PARTY-NOTICES.md`.
