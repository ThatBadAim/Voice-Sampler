# VoiceScan Application (Avalonia)

- `VoiceScan.Avalonia/`: Avalonia UI 12 desktop app (Windows + Linux): guided first-run setup, voice enrollment with explicit consent, scan, results waveform timeline, review queue.
- `VoiceScan.App.Core/`: UI-framework-neutral view models and services.

Run: `dotnet run --project app/VoiceScan.Avalonia` (needs `ffmpeg` and `ffplay` on PATH).
GPU: add `-p:VoiceScanGpu=true` (CUDA 12 + cuDNN 9).
