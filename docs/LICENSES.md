# Model & Dataset Licensing Registry

This document tracks verified licenses for neural network weights, training datasets, frameworks, and binary dependencies evaluated for VoiceScan.

> [!IMPORTANT]
> In commercial speech and biometric verification systems, model code licenses frequently conflict with pre-trained weight licenses. Pre-trained weights trained on academic or YouTube-scraped datasets (such as VoxCeleb or MusDB-18) carry non-commercial, copyright, and personality-rights restrictions even when the model architecture code is released under Apache-2.0 or MIT.

---

## 1. Verified Model & Tool Inventory

| Name | Official Source / Repository | Code License | Training Data & Weights License | Commercial Use Allowed? | Notes & Critical Risks |
|---|---|---|---|---|---|
| **Silero VAD** | [`snakers4/silero-vad`](https://github.com/snakers4/silero-vad) | MIT | MIT | **Yes (Permitted)** | Pre-trained ONNX weights are licensed under MIT with no strings attached. Validated for commercial use. |
| **WeSpeaker ResNet34** | [`wenet-e2e/wespeaker`](https://github.com/wenet-e2e/wespeaker) | Apache-2.0 | VoxCeleb 1 & 2 (Research-only / YouTube copyright) | **Restricted (High Risk)** | Code is Apache-2.0, but public weights are trained on VoxCeleb. VoxCeleb is limited to academic research; YouTube clip copyright remains with video owners and personality rights are not licensed. Retraining on commercially cleared data (e.g. Common Voice) required before commercial release. |
| **CAM++ (3D-Speaker)** | [`alibaba-damo-academy/3D-Speaker`](https://github.com/alibaba-damo-academy/3D-Speaker) | Apache-2.0 | 3D-Speaker dataset / CN-Celeb (Apache-2.0) | **Yes (Permitted with Attribution)** | Model architecture and official weights are released under Apache-2.0. Requires standard Apache-2.0 copyright attribution. |
| **ECAPA-TDNN (SpeechBrain)** | [`speechbrain/speechbrain`](https://github.com/speechbrain/speechbrain) | Apache-2.0 | VoxCeleb 1 & 2 (Research-only / YouTube copyright) | **Restricted (High Risk)** | SpeechBrain library is Apache-2.0, but canonical Hugging Face checkpoint weights are trained on VoxCeleb 1+2. Same commercial risk as WeSpeaker VoxCeleb weights. |
| **pyannote segmentation** | [`pyannote/pyannote-audio`](https://github.com/pyannote/pyannote-audio) | MIT | Gated on Hugging Face (DIHARD, VoxConverse) | **Conditionally Permitted** | `pyannote/segmentation-3.0` weights are open-access under MIT on Hugging Face, but requires an authenticated Hugging Face token and agreement to user conditions. Enterprise support and production-optimized variants require proprietary license from pyannoteAI. |
| **DeepFilterNet** | [`Rikorose/DeepFilterNet`](https://github.com/Rikorose/DeepFilterNet) | Dual MIT / Apache-2.0 | DNS Challenge 4, Common Voice, VCTK | **Yes (Permitted)** | Software and released pre-trained weights are dual-licensed MIT / Apache-2.0. Fully safe for commercial integration. |
| **Demucs / MDX-Net** | [`facebookresearch/demucs`](https://github.com/facebookresearch/demucs) | MIT | MUSDB18-HQ (CC BY-NC-SA 4.0) | **No (Blocked for pre-trained weights)** | Code is MIT, but Meta's official weights were trained on MUSDB18-HQ, which strictly forbids commercial use. Demucs cannot be deployed in a commercial product without retraining on licensed music/audio stems. |
| **sherpa-onnx** | [`k2-fsa/sherpa-onnx`](https://github.com/k2-fsa/sherpa-onnx) | Apache-2.0 | N/A (Inference runtime framework) | **Yes (Permitted)** | Permissive Apache-2.0 runtime. Note: Default NuGet packages are CPU-only; CUDA on Windows requires manual native DLL deployment or building with GPU flags. Direct ONNX Runtime C# bindings are preferred. |
| **FFmpeg** | [`FFmpeg/FFmpeg`](https://ffmpeg.org/) | LGPL v2.1+ | N/A (Media decoding library) | **Yes (Permitted under LGPL)** | Chunked decoding must link dynamically to LGPL-configured builds (avoid `--enable-gpl` or `--enable-nonfree` to prevent GPL infection of commercial binaries). |

---

## 2. Summary of Commercial Blockers & Strategic Path

### Blockers:
1. **VoxCeleb-Trained Weights (WeSpeaker ResNet34 & ECAPA-TDNN):**
   - The primary open-source speaker recognition checkpoints are trained on VoxCeleb. VoxCeleb data is scraped from YouTube and restricted to academic research. Distributing products with VoxCeleb weights exposes the company to biometric and copyright liabilities.
2. **MUSDB-Trained Weights (Demucs):**
   - Music stem separation using stock Demucs weights is strictly prohibited commercially by the MUSDB18 license.

### Safe Commercial Baseline Path:
- **VAD:** Silero VAD (MIT, commercially unencumbered).
- **Denoising/Enhancement:** DeepFilterNet (MIT/Apache-2.0, commercially unencumbered).
- **Speaker Embedding Extraction (Baseline & Demo):** CAM++ (3D-Speaker) under Apache-2.0 for commercial viability, with WeSpeaker ResNet34 evaluated side-by-side as an academic performance reference.
- **Post-Demo Commercial Step:** Retrain or fine-tune embedding models on permissible multi-speaker corpora (e.g., Mozilla Common Voice, LibriSpeech, and synthetic consenting game audio).
