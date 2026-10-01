# VoiceScan: 10 Agent Prompts (concept → base version)

**Base version means:** enroll a voice, scan a folder of audio, get timestamped Match / Possible / No match results with click-to-play, fully offline on an NVIDIA GPU, with a working evaluation harness reporting honest numbers.

**How to use:** run the prompts in order, one agent session each. Don't move on until the acceptance checks pass. Paste the shared preamble at the start of every session (or put it in `AGENTS.md` after Prompt 1).

**Your jobs between prompts:** record consenting speakers' audio, keep the final test set somewhere agents can't write, and run the final test-set evaluation yourself.

---

## Shared preamble (paste at the top of every prompt)

```text
Project: VoiceScan, a fully local Windows app that enrolls a person's voice and finds where they speak in long audio files, especially gameplay recordings where other players' voice chat is mixed with heavy game audio.

Rules:
- Read AGENTS.md, DESIGN.md and the current SPEC before doing anything.
- Query the graphify knowledge graph before exploring the codebase. Update it before you finish any task.
- Stack: Python (experiments only), C#/.NET + ONNX Runtime (CUDA) for the engine, WinUI 3 for the UI, SQLite for storage, FFmpeg for decoding.
- Everything runs 100% locally. No network calls at runtime, no telemetry.
- Never read, modify or write to the held-out test set directory. Never change decision thresholds without re-running the evaluation harness and reporting the numbers.
- Small commits, one per task. Write tests for new logic. Report what you changed, what you tested, and anything unfinished.
- Do not add features beyond the task. If you find a problem outside the task, note it and stop.
```

---

## Prompt 1: Foundation, rules and specs

```text
Set up the VoiceScan repository foundation.

Create:
1. Repo structure: /engine (C# core library + CLI), /app (WinUI 3, empty for now), /research (Python experiments), /eval (harness + dev data config only), /models (ONNX files, gitignored), /docs.
2. AGENTS.md, under 100 lines: project summary, stack, build/test commands, the shared rules above, coding conventions, and where specs live.
3. DESIGN.md: product description, the Match / Possible / No match output model, the pipeline (decode -> optional multi-track select -> VAD -> 2s windows with 1s hop -> embedding -> within-file speaker clustering -> cluster scoring with score normalization -> smoothing/aggregation -> segments), the embedding cache design (keyed by file hash + model version + settings), and the success metrics: false alarms per hour of audio, per-file recall, per-file precision, speed as a multiple of realtime, all reported per SNR bucket with confidence intervals.
4. docs/SPEC-phase1.md with measurable acceptance tests for the walking skeleton.
5. docs/LICENSES.md: a table to fill in, with one row per model/dataset (name, source, license, commercial use allowed?, notes). Pre-fill candidates: Silero VAD, WeSpeaker ResNet34, CAM++ (3D-Speaker), ECAPA-TDNN (SpeechBrain), pyannote segmentation, DeepFilterNet, Demucs/MDX-Net, sherpa-onnx, FFmpeg. Mark every entry "UNVERIFIED" until I confirm.
6. Initialize graphify for the repo.

Acceptance: repo builds (empty projects compile), AGENTS.md is under 100 lines, graphify graph exists, all docs created. Do not write any pipeline code yet.
```

---

## Prompt 2: Models and licensing audit

```text
Acquire and verify the baseline models, and complete the licensing table.

1. Evaluate sherpa-onnx as the starting point: does it provide VAD, speaker embedding extraction, and diarization with working C# bindings and CUDA support on Windows? Report exactly what works and what doesn't, with a minimal working C# sample that loads a model on the GPU.
2. Download ONNX versions of: Silero VAD, a WeSpeaker ResNet34 embedding model, a CAM++ embedding model. Place them in /models with a manifest (name, source URL, file hash, license text location). Do not commit the model files.
3. For each, find the actual license of the weights AND the training data from the official source. Fill docs/LICENSES.md with sources and a plain summary of whether commercial use is permitted. Flag anything uncertain. Do not guess; mark unknowns as UNVERIFIED.
4. Write a Python script in /research that loads each embedding model and confirms it produces a fixed-length vector for a test WAV on the GPU.

Acceptance: C# sample runs a model on CUDA; Python script runs; LICENSES.md filled with sources; a short summary of risks (what blocks commercial use).
```

---

## Prompt 3: Synthetic data generator

```text
Build the synthetic test data generator in /eval.

Inputs I will provide in /eval/data_config: a folder of clean speech clips per consenting speaker (speaker_id subfolders), a folder of game audio clips, and a config file.

Generate mixed clips:
1. Mix speech into game audio at controlled SNRs: -5, 0, 5, 10, 20 dB (state how SNR is computed, using speech-active regions).
2. Apply degradation chains: Opus encode/decode at several bitrates (via FFmpeg), automatic gain control simulation, optional band-limiting, optional light noise suppression simulation.
3. Support positive clips (target speaker present), negative clips (target absent, other speakers present), and distractor clips (game audio only, including game dialogue).
4. Support overlapping speakers in some mixes.
5. Emit exact ground-truth JSON per clip: speaker labels, start/end times, SNR, degradation settings.
6. Split speakers into dev and test sets. Enrollment audio, dev set and test set must never share speakers or source clips. The test split output directory must be configurable to a path outside the repo, and the generator must refuse to write test output inside the repo.
7. Deterministic with a seed.

Acceptance: running one command produces N clips with ground truth, a printed summary table of counts per SNR/degradation, and tests proving the SNR is within 0.5 dB of the target and the speaker split has no leakage.
```

---

## Prompt 4: Evaluation harness

```text
Build the evaluation harness in /eval.

It runs a scan pipeline (via a command-line interface I define: input audio folder + profile -> JSON of detected segments) against the dataset's ground truth and reports:
1. False alarms per hour of audio.
2. Per-file recall and precision (a file-level hit counts as correct if a detected segment overlaps a true target segment; define the overlap rule in the docs).
3. Segment-level timing error.
4. DET curve and EER for the underlying scores.
5. Speed as a multiple of realtime.
6. All metrics per SNR bucket and per degradation type, with 95% bootstrap confidence intervals.
7. Output: a JSON file, a Markdown report, and PNG plots.

Rules: it must support running on the dev set by default and on the test set only when a --final flag is passed AND the test path is outside the repo. Log every --final run with a timestamp to a file. Provide a way to compare two runs and show metric deltas, and make a regression exit code (non-zero if any headline metric worsens beyond a configurable tolerance).

Acceptance: harness runs against a stub scanner that returns random scores and a stub that returns perfect ground truth, and reports sensible numbers for both (random ≈ chance, perfect ≈ 100%). Unit tests for the metric math.
```

---

## Prompt 5: Feasibility spike (Python)

```text
Write the baseline experiment in /research and run it through the harness.

Pipeline: FFmpeg decode to 16 kHz mono -> Silero VAD -> 2s windows, 1s hop over speech -> embeddings from the WeSpeaker and CAM++ models -> enrollment profile = mean of embeddings from the enrollment clips -> cosine similarity per window -> a simple threshold -> merge adjacent hits into segments. Expose it through the harness's scanner interface.

Run on the dev set for both models. Produce the harness report. Then write docs/baseline-report.md that states: where it works, where it fails (by SNR and degradation), the false alarms per hour, and your honest assessment of whether this approach is viable at 10 dB SNR and at 0-10 dB SNR. Do not tune thresholds on the test set. Do not run --final.

Acceptance: reproducible dev-set numbers for both models, a clear viability assessment, and a ranked list of the three most promising improvements based on the failure analysis.
```

---

## Prompt 6: C# engine core

```text
Build the C# engine core library in /engine, plus a CLI.

Implement:
1. Audio decode via FFmpeg to 16 kHz mono float PCM, streamed in chunks so multi-hour files never load into RAM. Support common audio and video containers. If the file has multiple audio tracks, list them and allow selecting a track in the options.
2. Silero VAD via ONNX Runtime.
3. Windowing: 2s windows, 1s hop, speech regions only (configurable).
4. Embedding extraction via ONNX Runtime on CUDA, batched. Make the model swappable behind an interface and select it by config.
5. Profile creation from enrollment audio (multiple embeddings stored, plus a centroid).
6. Scoring: cosine similarity against a profile.
7. CLI: enroll --audio <files> --name <n>; scan --input <folder> --profile <n> --output <json>. JSON output uses the format the evaluation harness expects.

Make decode and GPU inference run concurrently (pipelined), not sequentially.

Acceptance: the CLI scores the same dev-set clips and, through the harness, gives results within tolerance of the Python baseline from Prompt 5 (report any difference and why). Report speed as a multiple of realtime on a multi-hour input. No memory growth on long files.
```

---

## Prompt 7: Storage, profiles and embedding cache

```text
Add the SQLite storage layer to the engine.

1. Schema and migrations for: profiles (name, creation date, enrollment embeddings, centroid, model version), files (path, hash, duration, tracks), embeddings cache (file hash + model version + VAD/window settings -> window embeddings with timestamps), and scan results (file, profile, segments, verdicts, settings snapshot).
2. A cache that makes re-scanning with a new or updated profile skip decode/VAD/embedding for any file already processed with the same model and settings.
3. Hashing that is fast for large files (document the approach, e.g. size + sampled content hash + full hash on demand).
4. Invalidation: changing the model or window settings invalidates the relevant cache entries only.
5. CLI additions: profiles list/delete, cache stats, scan reuse of cache.

Acceptance: scanning 50 files for profile A, then profile B, runs profile B in a small fraction of the time (report both timings). Tests for cache hit/miss/invalidation. Results persist across runs.
```

---

## Prompt 8: Cluster-level scoring and aggregation

```text
Implement the accuracy layer in the engine, one change at a time, measuring each through the harness on the dev set.

Order:
1. Within-file speaker clustering of window embeddings (agglomerative clustering with a configurable threshold; choose and justify). Score each cluster's mean embedding against the profile instead of scoring single windows.
2. Temporal smoothing and segment aggregation: merge adjacent hits, require neighboring support or a high peak score, drop isolated weak windows.
3. Score normalization (AS-norm) using a cohort of impostor embeddings. Provide a tool to build the cohort from non-target speakers.
4. Three-state output: Match / Possible / No match, with per-segment confidence and a reason flag (low SNR estimate, short segment, suspected overlap).
5. Multi-condition enrollment: augment enrollment audio with game mixes and Opus/AGC degradation to build a more robust profile.

After each step, run the harness and record the delta in docs/accuracy-log.md (metric, before, after, runtime cost). Keep a step only if it improves the headline metrics or the speed/accuracy trade-off justifies it.

Acceptance: accuracy-log.md with measured gains per step; the engine defaults updated to the best-performing configuration; no test-set (--final) runs.
```

---

## Prompt 9: Desktop application

```text
Build the WinUI 3 desktop app in /app, using the engine library.

Screens and flows:
1. Enrollment wizard: record from the microphone or import audio clips. A mandatory consent step ("the person whose voice this is has consented"). Quality check on the audio (minimum speech duration, noise level) with plain feedback. Creates a named profile.
2. Scan: choose a folder or files, choose profile(s), start. Show progress per file and overall ETA, the realtime multiple, and GPU utilization. Cancel and resume.
3. Results: list of files with verdict badges (Match / Possible / No match), sortable and filterable. Selecting a file shows a waveform timeline with hit segments marked, confidence per segment with the reason flag, and click-to-play at the hit with a playback cursor.
4. Review: confirm or reject a segment. Confirmed segments can be added to the profile. Rejections are stored as labeled negatives. Store all decisions in SQLite.
5. Fully offline: the app has no network code.

Follow DESIGN.md for a clean, professional look: consistent spacing, one type scale, Mica backdrop, dark and light themes, no placeholder text, proper empty states and error states. Run long scans on background threads and keep the UI responsive.

Acceptance: complete flow works on a 5-hour folder without the UI freezing; screenshots of each screen saved in /docs; manual test checklist in docs/app-test-checklist.md.
```

---

## Prompt 10: Reports, benchmark screen and base-version acceptance

```text
Finish the base version.

1. Evidence report export (PDF and CSV): per file and segment: timestamps, verdict, confidence, reason flags, file hash, profile name, model name/version, settings snapshot, scan date, and short audio clips of each hit.
2. A "Benchmark" screen in the app that displays the latest evaluation report numbers (per-SNR metrics with confidence intervals) loaded from the harness output JSON, so the accuracy claims shown in the demo come from real measured runs.
3. Instant re-scan demo path: add a profile and rescan a previously scanned folder using the cache, showing the time saved.
4. Error handling pass: corrupt files, unsupported formats, zero-length audio, missing GPU (fall back to CPU with a warning), disk full, cancelled scans. Logs go to a local file.
5. docs/BASE-VERSION.md: how to build and run, known limitations (voice changers, heavy overlap, low SNR), model licenses summary, and a final acceptance checklist covering every demo step in the project plan.
6. Update AGENTS.md and DESIGN.md to reflect the final architecture. Update graphify.

Acceptance: the full demo script runs end to end on a clean build; a report exports correctly; the benchmark screen shows real numbers; every checklist item is ticked or listed as a known gap. Do not run --final. I will run the final test-set evaluation myself.
```

---

## After Prompt 10 (you)

1. Run the final test-set evaluation once (`--final`) and record the numbers. Do not tune afterwards.
2. Compare against the success metrics in the project plan and decide at the gate: ship, narrow the claim, or fine-tune.
3. Test with real recordings you labeled by hand.
4. Run the first customer conversations using the working base version.
