# Model & Dataset Licensing Registry

This document tracks verified licenses for neural network weights, training datasets, frameworks, and binary dependencies evaluated for VoiceScan.

---

## 1. Verified Model & Tool Inventory

| Name | Official Source / Repository | Origin / Lab | Code License | Weights License | Commercial Safety | Notes & Specifications |
|---|---|---|---|---|:---:|---|
| **Google WebRTC VAD** | [`webrtc.googlesource.com`](https://webrtc.googlesource.com/) | Google (USA) | BSD-3-Clause | Pure DSP / GMM (Zero Weights) | **100% Safe** | Statistical Gaussian Mixture Model. No downloaded neural weights, zero supply chain attack surface. |
| **SpeechBrain ECAPA-TDNN** | [`speechbrain/speechbrain`](https://github.com/speechbrain/speechbrain) | SpeechBrain Consortium (Mila Canada / Inria France) | Apache-2.0 | Apache-2.0 | **Yes (Permitted)** | 192-dimensional speaker embeddings. Gold standard in Western speaker recognition benchmarks. |
| **NVIDIA NeMo TitaNet** | [`NVIDIA/NeMo`](https://github.com/NVIDIA/NeMo) | NVIDIA Speech AI Lab (USA) | Apache-2.0 | CC-BY-4.0 / Apache-2.0 | **Yes (Permitted)** | 192-dimensional speaker embeddings. High-speed 1D Depthwise Separable Convolutions. |
| **DeepFilterNet** | [`Rikorose/DeepFilterNet`](https://github.com/Rikorose/DeepFilterNet) | FAU Erlangen-Nürnberg (Germany) | Dual MIT / Apache-2.0 | DNS Challenge 4, Common Voice, VCTK | **Yes (Permitted)** | Fully unencumbered audio denoising & speech enhancement. |
| **Avalonia UI** | [`AvaloniaUI/Avalonia`](https://github.com/AvaloniaUI/Avalonia) | Avalonia Community (Europe / USA) | MIT | N/A (UI framework; bundles SkiaSharp, MIT) | **Yes** | Cross-platform UI framework for Windows and Linux. Inter font (OFL-1.1). |
| **FFmpeg** | [`FFmpeg/FFmpeg`](https://ffmpeg.org/) | International project (EU/US/Global) | LGPL v2.1+ | N/A (Media decoding library) | **Yes (Isolated CLI Process)** | Executed strictly via sub-process standard pipes (no static/dynamic linking). |

---

## 2. Audio Processing Architecture

### Primary Pipeline
- **VAD (Voice Activity Detection):** Google WebRTC VAD (BSD-3-Clause).
- **Speaker Embedding Extraction (Primary):** SpeechBrain ECAPA-TDNN (192-dim, Apache-2.0).
- **Speaker Embedding Extraction (Alternative):** NVIDIA NeMo TitaNet-Small (192-dim, Apache-2.0 / CC-BY-4.0).
- **Score Normalization:** Adaptive S-Norm with imposter cohort distribution matching.
