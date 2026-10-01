# VoiceScan: Project Plan (working title)

**Goal:** a professional, fully local Windows application that enrolls a person's voice, then scans hours of audio (primarily gameplay recordings with other players' voice chat mixed with heavy game audio) and reports exactly where that person speaks, or confirms they don't.

**End state of this plan:** a demo an investor can watch and trust, backed by measured accuracy numbers.

**Assumptions:** ~10-15 hrs/week, code written by AI agents, NVIDIA GPU, Windows, UK-based. Realistic duration: 12 weeks best case, 16 weeks planned.

---

## 1. What the demo must prove

A scripted 5-minute run:

1. Enroll a person in under 60 seconds (with a consent step).
2. Drop in a folder of hours of real gameplay audio.
3. Disconnect the network on screen, then scan. Show the processing speed as a multiple of realtime.
4. Results timeline per file: hits with timestamps, confidence and reasons, click-to-play at each hit, and clear "not present" verdicts.
5. Add a second profile and re-scan the whole library in seconds (embedding cache).
6. Accuracy panel: real measured numbers from a held-out test set, broken down by noise level.
7. Export an evidence report.

Investors respond to **measured accuracy under hard conditions** plus a polished flow. Anything else is optional.

---

## 2. Core technical approach

**Speaker verification with embeddings, not per-person training.** Enrollment averages a few embeddings, so profiles are instant.

Pipeline:

1. **Decode:** FFmpeg to 16 kHz mono float PCM, streamed.
2. **Multi-track check:** if the recording has a separate voice-chat track, use it and skip separation.
3. **VAD:** Silero VAD (ONNX).
4. **Windowing:** 2 s windows, 1 s hop, speech regions only.
5. **Embedding:** WeSpeaker / CAM++ / ECAPA-TDNN class model via ONNX Runtime (CUDA).
6. **Cluster, then match:** diarize within each file into speakers, average each cluster's embeddings, score the **cluster** against the profile. This is far more robust than scoring single noisy windows.
7. **Scoring:** cosine similarity with AS-norm score normalization.
8. **Aggregation:** temporal smoothing, merge into segments, output **Match / Possible / No match** with confidence and reasons (low SNR, overlap, short segment).
9. **Cache:** embeddings stored per file hash + model version + settings.

**Noise handling (experiments, ranked by expected gain per effort):**

1. Multi-condition enrollment (augment enrollment audio with game mixes, Opus/low-bitrate codecs, AGC).
2. Cluster-level scoring and smoothing.
3. AS-norm and SNR-aware thresholds.
4. Light speech enhancement (e.g. DeepFilterNet) before embedding.
5. Full source separation (Demucs / MDX-Net), kept only if it measurably wins. It is music-trained and its artifacts can hurt embeddings.
6. Fine-tuning the embedding model on game-mixed data: **post-demo** unless nothing else passes the gate.

**Stack:**

- Experiments in Python (SpeechBrain / WeSpeaker, PyTorch + CUDA). Python's only output is **ONNX models and config files**.
- Production engine in C#/.NET with ONNX Runtime (CUDA/TensorRT). Evaluate sherpa-onnx as the starting point (VAD, embeddings, diarization, C# bindings).
- UI: WinUI 3.
- Storage: SQLite (profiles, embedding cache, results).
- No Python-to-C# rewrite. The engine loads whatever models the experiments produce.

---

## 3. Success metrics (defined at output level)

Per-window error rates are misleading: a 10-hour file has ~36,000 windows, so 1% window FAR means ~360 false hits.

| Metric | Initial target (revise after baseline) |
|---|---|
| False alarms per hour of audio | ≤ 1 at ≥ 10 dB SNR; ≤ 3 at 0-10 dB |
| Per-file recall (target present, detected) | ≥ 90% at ≥ 10 dB; ≥ 75% at 0-10 dB |
| Per-file precision | ≥ 90% |
| End-to-end speed | ≥ 30x realtime on your NVIDIA GPU |

Always report per-SNR buckets with confidence intervals. Never publish a single headline "accuracy" number.

---

## 4. Phases and gates

### Phase 0: Decisions and feasibility spike (week 1)
- Licensing audit of every model and dataset (VoxCeleb-trained weights are commonly non-commercial; pyannote models are gated; check Demucs/MDX weights).
- Feasibility spike: 3-5 consenting voices mixed with game audio at 3 SNRs, baseline embedding, raw scores.
- **Start customer conversations now** (3-5 calls). Calendar time is the bottleneck.
- Pick a wedge customer (moderation teams, streamers/creators, archive/review companies).
- **Gate:** the spike shows signal at 10 dB SNR. If hopeless, stop and rethink before building anything.

### Phase 1: Walking skeleton (weeks 2-4)
- C# engine with baseline pipeline, CLI, basic UI (enroll, scan folder, results list, playback).
- Evaluation harness growing alongside (synthetic set generator, dev/test split, per-SNR reports).
- **Gate:** the whole flow works end to end, even with weak accuracy.

### Phase 2: Accuracy loop (weeks 4-9, time-boxed)
- Run the experiments in section 2 in order. Measure each independently, keep what pays for its runtime cost.
- Model bake-off (WeSpeaker, CAM++, ECAPA) on game-mixed data.
- **Gate:** metrics hit, or the ceiling is known. If missed, choose one: fine-tune (+2-3 weeks), narrow the claim (e.g. "≥ 5 dB SNR"), or rely on multi-track ingest. Do not polish the UI on bad numbers.

### Phase 3: Product polish (weeks 9-11)
- Enrollment wizard with quality check and consent step, waveform timeline, review queue, evidence export, benchmark screen, curated demo dataset.
- Design matters here. Invest in typography and layout early, not last.

### Phase 4: Demo prep (week 12, plus buffer)
- Precomputed cache plus a backup screen recording in case the live demo fails.
- One-page metrics sheet, 3-minute narration, rehearsal on the demo machine.
- Prepare for hard questions: failure cases, voice changers, spoofing, false accusations, data handling, licensing.

Skip for the demo: signed installer, clean-machine hardening, auto-update. Run from a build folder on a machine you control.

---

## 5. Evaluation harness rules

- **Synthetic set:** 20+ consenting speakers' clean speech mixed into varied game audio at -5 to 20 dB SNR, with codec simulation. Gives volume and exact ground truth.
- **Real set:** hand-labeled gameplay recordings. Small sets (20-50 clips) give wide error bars, so report confidence intervals and grow it over time.
- **Dev/test split.** The final test set lives **outside any directory agents can write to** and is run only by you. Otherwise agents will tune to it, intentionally or not.
- One command produces: false alarms/hour, per-file recall/precision, DET curve, per SNR bucket, per model configuration.
- Any change that regresses harness numbers does not get merged.

---

## 6. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Matching strangers' voices is biometric processing (UK GDPR special category) | Demo only with consenting speakers' data. Get a proper legal check before a real customer. Frame the product around a lawful use. This plan is not legal advice |
| Local desktop app is hard to monetize / not a moat | In customer calls, test whether buyers want an engine/SDK or on-prem tool. The feedback data from the review queue is the moat |
| Model/data licensing blocks commercial use | Audit in week 1; plan a retrain on commercially licensable data after funding |
| Heavy overlap / low SNR caps accuracy | Phase 2 gate, honest per-SNR reporting, multi-track ingest |
| Voice changers defeat embeddings | State as a known limit; roadmap item |
| Demucs ONNX export is awkward (STFT ops) | Test export early; MDX-Net as fallback; treat separation as optional |
| Phase 2 research eats the schedule | Hard time-box with a decision at the gate |
| Game audio in demo is copyrighted | Use your own recordings or licensed audio for anything shown or shared |
| Live demo failure | Precomputed cache plus backup recording |
| Scope creep (live mode, multi-speaker UI, mobile) | Parked until after the demo |
| Overclaiming statistics | Confidence intervals, per-SNR numbers, show failure cases yourself |

---

## 7. Features that improve the product (ranked)

1. **Multi-track ingest.** OBS and similar tools can record game, mic and Discord to separate tracks. An isolated voice-chat track skips separation: faster and much more accurate.
2. **Enroll from the timeline.** Mark a segment in any recording as "this is them"; bootstrap to find more; confirmed hits improve the profile (confirmed only, to avoid drift).
3. **Review queue sorted by uncertainty.** Fast triage UI where every decision becomes labeled data.
4. **Instant multi-profile re-scan** from the embedding cache.
5. **Per-hit confidence with reasons.**
6. **Evidence export:** clips, timestamps, file hashes, model version and settings.
7. **Watch-folder mode** for background scanning.

---

## 8. Agent workflow rules

- Per project: `AGENTS.md` (under 100 lines), `DESIGN.md`, and a `SPEC.md` per phase with measurable acceptance tests before any code.
- Use graphify: agents query the graph before exploring and update it before finishing any task.
- Small tasks, one commit per task, harness run on every engine change.
- Agents never edit the held-out test set or change thresholds without rerunning the harness.
- Keep the Python reference permanently as the oracle for engine behavior.

---

## 9. First week checklist

- [ ] Run the feasibility spike
- [ ] Book 3-5 customer conversations
- [ ] Complete the licensing table (every model and dataset)
- [ ] Record test audio from consenting speakers, plus your own gameplay clips
- [ ] Rewrite success metrics against the baseline once you have it
