# Model & Dataset Licensing Registry

This document tracks verified licenses for neural network weights, training datasets, frameworks, and binary dependencies evaluated for VoiceScan.

---

## 1. Verified Model & Tool Inventory

| Name | Official Source / Repository | Origin / Lab | Code License | Weights License | Commercial Safety | Notes & Specifications |
|---|---|---|---|---|:---:|---|
| **Google WebRTC VAD** | [`webrtc.googlesource.com`](https://webrtc.googlesource.com/) | Google (USA) | BSD-3-Clause | Pure DSP / GMM (Zero Weights) | **100% Safe** | Ported to C# (`WebRtcVad.cs`), verified frame-for-frame against the reference. BSD notice in `THIRD-PARTY-NOTICES.md` (must ship with binaries). |
| **SpeechBrain ECAPA-TDNN** | [`speechbrain/speechbrain`](https://github.com/speechbrain/speechbrain) | SpeechBrain Consortium (Mila Canada / Inria France) | Apache-2.0 | Apache-2.0 | **Yes (Permitted)** | 192-dimensional speaker embeddings. Gold standard in Western speaker recognition benchmarks. |
| **NVIDIA NeMo TitaNet** | [`NVIDIA/NeMo`](https://github.com/NVIDIA/NeMo) | NVIDIA Speech AI Lab (USA) | Apache-2.0 | CC-BY-4.0 / Apache-2.0 | **Yes (Permitted)** | 192-dimensional speaker embeddings. High-speed 1D Depthwise Separable Convolutions. |
| **DeepFilterNet** | [`Rikorose/DeepFilterNet`](https://github.com/Rikorose/DeepFilterNet) | FAU Erlangen-Nürnberg (Germany) | Dual MIT / Apache-2.0 | DNS Challenge 4, Common Voice, VCTK | **Yes (Permitted)** | Fully unencumbered audio denoising & speech enhancement. |
| **Avalonia UI** | [`AvaloniaUI/Avalonia`](https://github.com/AvaloniaUI/Avalonia) | Avalonia Community (Europe / USA) | MIT | N/A (UI framework; bundles SkiaSharp, MIT) | **Yes** | Cross-platform UI framework for Windows and Linux. Inter font (OFL-1.1). |
| **LibriSpeech ASR corpus** | [`openslr.org/12`](https://www.openslr.org/12) | Panayotov et al., Johns Hopkins (USA), from LibriVox public-domain audiobooks | N/A | CC-BY-4.0 (data) | **Yes (Attribution)** | Development evaluation only (`eval/real_speech_eval.py`, dev-clean). `models/impostor_cohort.json` holds ECAPA embeddings of 73 train-clean-100 utterances; attribution: V. Panayotov, G. Chen, D. Povey, S. Khudanpur, "LibriSpeech: an ASR corpus based on public domain audio books", ICASSP 2015. Test splits are never used. |
| **FFmpeg** | [`FFmpeg/FFmpeg`](https://ffmpeg.org/) | International project (EU/US/Global) | LGPL v2.1+ | N/A (Media decoding library) | **Yes (Isolated CLI Process)** | Executed strictly via sub-process standard pipes (no static/dynamic linking). |

---

## 2. Audio Processing Architecture

### Primary Pipeline
- **VAD (Voice Activity Detection):** Google WebRTC VAD (BSD-3-Clause).
- **Speaker Embedding Extraction (Primary):** SpeechBrain ECAPA-TDNN (192-dim, Apache-2.0).
- **Speaker Embedding Extraction (Alternative):** NVIDIA NeMo TitaNet-Small (192-dim, Apache-2.0 / CC-BY-4.0).
- **Score Normalization:** Adaptive S-Norm with imposter cohort distribution matching.
