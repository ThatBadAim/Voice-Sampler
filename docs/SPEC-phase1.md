# Phase 1 Specification: Walking Skeleton

## 1. Objective
Establish a fully functional, end-to-end "walking skeleton" of the VoiceScan system connecting audio ingestion, VAD, embedding extraction, similarity scoring, and CLI output into a verifiable pipeline prior to accuracy tuning.

---

## 2. Core Functional Requirements

### 2.1 Audio Ingest & Decoding
- Stream any standard media format (WAV, MP3, FLAC, OGG, MP4, MKV) via FFmpeg process wrapper or bindings.
- Convert input audio directly to single-channel (mono), 16,000 Hz, 32-bit floating-point PCM.
- Enforce streaming chunk decode so memory consumption remains bounded ($\le 250\,\text{MB}$ RAM) regardless of audio duration (tested on files up to 3 hours).

### 2.2 Voice Activity Detection (VAD)
- Integrate Silero VAD (ONNX format) running through ONNX Runtime.
- Emit contiguous speech intervals $[t_{\text{start}}, t_{\text{end}}]$ and filter out non-speech segments.

### 2.3 Windowing
- Partition active speech intervals into fixed 2.0-second windows with a 1.0-second sliding hop (50% overlap).
- Pad windows shorter than 2.0s with silence/edge-wrap or discard according to configuration.

### 2.4 GPU Embedding Extraction
- Execute ONNX Runtime with CUDA Execution Provider (`CUDAExecutionProvider`).
- If CUDA hardware/drivers are unavailable, fall back cleanly to `CPUExecutionProvider` with a logged warning.
- Batched inference across speech windows yielding fixed-size unit-length embedding vectors.

### 2.5 Profile Enrollment
- Accept one or more reference audio recordings for a target speaker.
- Extract embeddings across all speech windows in reference audio.
- Compute the L2-normalized mean vector (centroid) as the canonical profile representation.

### 2.6 Similarity Scoring & Verdicts
- Compute cosine similarity between window embeddings and target profile centroid:
  $$\text{sim}(e_w, e_{\text{profile}}) = \frac{e_w \cdot e_{\text{profile}}}{\|e_w\| \|e_{\text{profile}}\|}$$
- Baseline aggregation: merge consecutive speech windows exceeding threshold $T$ into detected segments.

### 2.7 CLI Interface
- `VoiceScan.Cli enroll --audio <paths...> --name <profile_name> --output <profile_file>`
- `VoiceScan.Cli scan --input <media_path_or_dir> --profile <profile_file> --output <results.json>`

---

## 3. Measurable Acceptance Tests

| Test ID | Test Scenario | Acceptance Criteria | Measurement Method |
|---|---|---|---|
| **AT-01** | Empty Solution Compilation | Engine Core, CLI, and Test projects build with 0 errors and 0 warnings. | `dotnet build VoiceScan.sln` |
| **AT-02** | Unit Test Execution | Baseline test suite executes and passes 100%. | `dotnet test VoiceScan.sln` |
| **AT-03** | Streaming Memory Bound | Decode 3-hour audio file continuously; working set RAM must not exceed 250 MB. | Process memory profiling / peak working set monitor |
| **AT-04** | GPU Inference Execution | Embeddings generated using `CUDAExecutionProvider` on supported hardware; verified via ONNX Runtime provider query. | Integration test verifying provider string contains `CUDA` |
| **AT-05** | Embedding Determinism | Running inference twice on identical 16 kHz audio input yields identical vectors ($\text{cosine similarity} \ge 0.99999$). | Mathematical assertion in unit test |
| **AT-06** | Profile Centroid Accuracy | Profile enrollment from 3 distinct clips produces unit-normalized centroid vector ($\|v\|_2 = 1.0 \pm 10^{-6}$). | Unit test on profile calculation |
| **AT-07** | Pipeline End-to-End Run | CLI scans a folder of test audio against an enrolled profile and outputs valid JSON matching evaluation schema. | Schema validation test against `results.schema.json` |
| **AT-08** | Baseline Realtime Speed | Processing throughput achieves $\ge 10\times$ realtime on NVIDIA GPU (e.g. 10 minutes of audio processed in $\le 60$ seconds). | Wall-clock elapsed time vs total audio duration |
| **AT-09** | Baseline Accuracy Gate | Scanner run against dev-set clips matches Python feasibility spike baseline scores within $\pm 0.02$ cosine similarity. | Automated diff against dev ground truth |

---

## 4. JSON Output Contract
The CLI `scan` command must emit JSON conforming to the following specification:

```json
{
  "schema_version": "1.0.0",
  "scan_metadata": {
    "timestamp": "2026-10-01T22:00:00Z",
    "profile_name": "TargetSpeakerA",
    "model_id": "wespeaker-resnet34",
    "engine_version": "0.1.0"
  },
  "files": [
    {
      "file_path": "recordings/gameplay_01.mp4",
      "file_hash": "sha256-abc1234...",
      "duration_seconds": 1824.5,
      "audio_track_index": 0,
      "verdict": "Match",
      "max_confidence": 0.842,
      "segments": [
        {
          "start_time_seconds": 142.0,
          "end_time_seconds": 149.0,
          "verdict": "Match",
          "confidence": 0.842,
          "reason_flags": []
        }
      ]
    }
  ]
}
```
