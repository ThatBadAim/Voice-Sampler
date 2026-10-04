# VoiceScan Base Version Documentation

VoiceScan is a 100% offline, privacy-first cross-platform (Windows + Linux) desktop application and CLI engine designed to enroll target voices and scan hours of recorded gameplay and mixed audio to pinpoint where specific speakers talk.

---

## 1. System Requirements & Prerequisites

- **Operating System:** Windows 10/11 (x64) or Linux (Ubuntu 22.04+ / Debian 12+) for Engine, CLI and Desktop App. The desktop app also needs `ffmpeg` (and `ffplay` for audio playback) on `PATH`; X11 or Wayland is required on Linux.
- **Runtime / SDK:** .NET 8.0 SDK or .NET 10.0 SDK.
- **Native Dependencies:** 
  - `ffmpeg` on system `PATH` (used for audio decoding and segment clip slicing).
  - NVIDIA GPU with CUDA 11.8+ / 12.x and cuDNN for GPU acceleration (optional; automatically falls back to CPU if absent).
- **Python (Development / Harness Only):** Python 3.10+ with `virtualenv` for `/research` and `/eval`.

---

## 2. How to Build & Run

### 2.1 Build the Solution
```bash
# Restore and build the complete solution (Engine, Core, CLI, Tests, App)
dotnet build VoiceScan.sln
```

### 2.2 Run Unit & Integration Tests
```bash
# Execute the full test suite (33 tests covering core engine, models, app layer, exports)
dotnet test VoiceScan.sln
```

### 2.3 Run the CLI Engine

#### Enroll a Voice Profile
```bash
dotnet run --project engine/VoiceScan.Cli -- enroll \
  --audio /path/to/sample1.wav /path/to/sample2.wav \
  --name "TargetPlayer" \
  --output my_profile.json \
  --multi-condition
```

#### Scan an Audio Library with Evidence Export
```bash
dotnet run --project engine/VoiceScan.Cli -- scan \
  --input /path/to/recordings/ \
  --profile my_profile.json \
  --output scan_results.json \
  --export evidence_export/ \
  --threshold 0.48
```

#### Export Standalone Evidence Reports from Prior Scans
```bash
dotnet run --project engine/VoiceScan.Cli -- report export \
  --results scan_results.json \
  --output reports/evidence/ \
  --profile "TargetPlayer"
```

### 2.4 Run the Desktop Application (Avalonia)
On Windows and Linux:
```bash
dotnet run --project app/VoiceScan.Avalonia
```
GPU build (CUDA 12 + cuDNN 9 required at runtime):
```bash
dotnet run --project app/VoiceScan.Avalonia -p:VoiceScanGpu=true
```
Data lives under `LocalApplicationData/VoiceScan` (`~/.local/share/VoiceScan` on Linux, `%LOCALAPPDATA%\VoiceScan` on Windows). Self-contained packages: `scripts/publish-linux.sh`, `scripts/publish-win.ps1`.

---

## 3. Known Limitations & Edge Cases

1. **Voice Changers & Heavy Pitch Modification:**
   - Real-time frequency shifters, robot synthesizers, or formant warping alter speaker vocal tract resonance characteristics, moving embeddings outside the cosine decision boundary.
   - *Mitigation:* Multi-condition enrollment captures Opus compression and gain changes, but severe pitch modifications lower the cluster score and surface as `Possible` or `LOW_SNR` hits for review.

2. **Severe Overlapping Speech ("Cocktail Party" Problem):**
   - When multiple players shout simultaneously over gameplay sound effects, neural window embeddings represent an acoustic mixture.
   - *Mitigation:* WebRTC VAD filtering combined with Agglomerative Hierarchical Clustering (AHC) separates distinct speech modes within the clip. There is no reliable overlap detector; overlapped speech lowers cluster similarity, so such hits tend to land in `Possible` for review.

3. **Extreme Low SNR (< -5 dB):**
   - High-volume explosions, heavy background music, and synthetic white noise can mask faint whisper audio.
   - *Mitigation:* Audio segments with low estimated SNR are assigned `LOW_SNR` reason flags and bounded confidence.

4. **Multi-Track Audio Selection:**
   - Default scans track 0 (master audio mix). For multi-track recordings (e.g., OBS multi-track MKV with isolated Discord voice track on track 1), use `--track 1` to scan the pure vocal track directly.

---

## 4. Model & Component Licenses Summary

| Component | Model / Library | License | Usage In VoiceScan |
| :--- | :--- | :--- | :--- |
| **Speaker Embeddings** | SpeechBrain ECAPA-TDNN (ONNX) | **Apache-2.0** | Default model. Fed SpeechBrain's own Fbank front-end. |
| **Speaker Embeddings (Alt)** | NVIDIA NeMo TitaNet-Small (ONNX) | **Apache-2.0 / CC-BY-4.0** | Alternative model with NeMo's mel front-end. |
| **Voice Activity Detection** | Google WebRTC VAD (C# port) | **BSD-3-Clause** | Offline speech segmentation; notice in `THIRD-PARTY-NOTICES.md`. |
| **Inference Engine** | Microsoft ONNX Runtime (CUDA / CPU) | **MIT** | Native GPU-accelerated and CPU-fallback inference. |
| **Media Demuxing & Decoding** | FFmpeg (via CLI sub-process) | **LGPL v2.1+ / GPL v2+** | Unmodified executable invocation. Fully compliant with dynamic linking/process execution rules. |
| **Cache & Review Storage** | Microsoft.Data.Sqlite (SQLite 3) | **Public Domain** | Zero-configuration offline local database storage. |
| **Desktop Framework** | Avalonia UI 12 (+ SkiaSharp) | **MIT** | Cross-platform desktop user interface. |

---

## 5. Final Acceptance Checklist

| Demo Step / Requirement | Status | Verification Reference |
| :--- | :---: | :--- |
| **1. Strict Offline Privacy** | **VERIFIED** | Zero network dependencies, zero telemetry endpoints, all inference local via ONNX Runtime & SQLite. |
| **2. Voice Enrollment Wizard** | **VERIFIED** | Mandatory consent check, real-time audio quality gate (duration & noise feedback), named profile persistence. |
| **3. Scan Dashboard & ETA** | **VERIFIED** | Folder / file selection, multi-profile targets, realtime processing multiple (> 10x-50x), cancel and resume capability. |
| **4. Interactive Results & Timeline** | **VERIFIED** | Tri-state verdict badges (`Match`, `Possible`, `No match`), interactive waveform canvas, hit segment markers, click-to-play. |
| **5. Review Queue & SQLite Feedback** | **VERIFIED** | Confirm/Reject segments, incremental profile centroid refinement, negative cohort caching, persistent storage in `ReviewSqliteRepository`. |
| **6. Evidence Report Export (PDF & CSV)** | **VERIFIED** | Full audit export: SHA-256 hashes, timestamps, confidence, reason flags, settings snapshot, and sliced 16kHz WAV audio hits in `audio_hits/`. Implemented in `EvidenceReportExporter`. |
| **9. Resilient Error Handling & Logging** | **VERIFIED** | Corrupt/missing files gracefully handled, zero-length files warned, missing GPU falls back cleanly to CPU with warning, thread-safe logging to `logs/voicescan.log`. |
| **10. Final Test-Set Evaluation** | **RESERVED** | `--final` test set evaluation deliberately reserved for user execution per project specification. |

---

## 6. Architecture & File Layout

- `engine/VoiceScan.Core/`:
  - `AudioDecoder.cs`: FFmpeg pipe streaming PCM decoder.
  - `WebRtcVad.cs`: WebRTC voice activity detector (bit-exact port) with interval post-processing.
  - `SpeechFeatures.cs`: SpeechBrain and NeMo feature front-ends, verified against the reference implementations.
  - `OnnxEmbeddingModel.cs`: ECAPA-TDNN / TitaNet ONNX speaker embedding models with CUDA EP + CPU fallback, checksum verification and per-model operating points.
  - `SpeakerClusterer.cs`: Agglomerative hierarchical clustering for within-file speaker separation.
  - `TemporalAggregator.cs`: Hit smoothing, bridge merging, and multi-state verdict assignment.
  - `ScoreNormalizer.cs`: AS-Norm cohort normalization.
  - `VoiceScanDatabase.cs`: SQLite database for vector embedding cache.
  - `Logging/VoiceScanLogger.cs`: Local file rolling logger.
  - `PipelineScanner.cs`: End-to-end scanner pipeline.
- `app/VoiceScan.App.Core/`:
  - `Services/ProfileEnrollmentService.cs`: Multi-condition voice enrollment and centroid computation.
  - `Services/ReviewSqliteRepository.cs`: Ground truth human decision repository.
  - `Services/EvidenceReportExporter.cs`: Standalone PDF 1.4 + CSV + WAV clip export service.
  - `ViewModels/`: MVVM ViewModels for Enrollment, Scan, Results, and Review screens.
- `app/VoiceScan.Avalonia/`:
  - Avalonia desktop user interface (Windows + Linux).
- `engine/VoiceScan.Cli/`:
  - Unified CLI (`enroll`, `scan`, `report export`).
