# SPEC: Swappable Models and Clip Re-analysis

Lets the user replace any model the app uses without rebuilding or restarting, and re-run a clip (or a section of
it) with the current models or with a more detailed analysis. Builds on `docs/SPEC-phase-moderation.md`.

## Models covered
| Stage | Where it runs | How it is swapped |
|---|---|---|
| Voice embedding (speaker vectors) | C# engine, ONNX Runtime | Models page: pick a built-in or approved imported `.onnx` |
| Speech detection (VAD) | Python sidecar | Models page → sidecar `PUT /models` |
| Diarization | Python sidecar | same |
| Transcription (Whisper) | Python sidecar | same |
| Alignment | Python sidecar | same |
| Offence classifier (Detoxify) | Python sidecar | same |

## Voice-embedding models
- **Built-in:** ECAPA-TDNN and TitaNet-Small, verified against the SHA-256 compiled into the engine (unchanged).
- **Imported (approve on import):** the user picks an `.onnx` file, a name, and the feature front-end it was trained
  with (ECAPA-style SpeechBrain fbank or TitaNet-style NeMo mel). The app copies it to
  `LocalApplicationData/VoiceScan/models/imported/<sha12>.onnx`, records name, front-end and SHA-256 in
  `models/approved-models.json`, and test-loads it; a file that fails to load is removed again. On every load the
  file must match the recorded SHA-256. Files merely placed in a model directory are still refused.
- **Trust change (accepted by the user):** for imported models the check is "matches what you approved", not
  "matches the released weights". Anyone who can write the user's data directory can approve a model.
- **Operating point:** an imported model borrows the measured threshold and cluster distance of the built-in model
  with the same front-end and is labelled **Not evaluated**. It must be measured with `/eval` before its verdicts are
  relied on. Built-in operating points are unchanged.
- **Switching:** the new model is loaded off the UI thread, then swapped in. A switch is refused while a scan or
  re-analysis is using the model, and the old model stays active if the new one fails to load. The choice is saved
  in `settings.json` and used at the next start; if it is unavailable the app starts with ECAPA and says so.
- Voice profiles belong to the model that enrolled them. After a switch the Models page lists the voices that must
  be re-enrolled; scanning with them is refused as before.

## Sidecar models
- The sidecar keeps one active model per stage, persisted in `sidecar/model_config.json` (path overridable with
  `VOICESCAN_MODEL_CONFIG`) and loaded at start. Defaults are today's models.
- `GET /models` returns the active model per stage, whether it is loaded, and the last load error per stage.
- `PUT /models` with `{"models": {stage: value}}` loads each changed stage while the old model stays in place; on
  success the old model is released, on failure the old model stays active and the error is reported. Swaps wait
  for the file being processed to finish. Values are passed to each loader unchanged: Hugging Face ids, faster-whisper
  names or local folders, Detoxify variants or checkpoint paths.
- `/process` responses include the models that produced them, so every analysis records its models.
- Loading a model name that is not cached makes the sidecar download it (the sidecar already does this at start).
  Use local paths to stay offline.

## Re-analysis
- **Presets:** `standard` (today's settings) and `detailed`: VAD onset 0.35 / offset 0.25, minimum speech 0.20 s,
  minimum silence 0.30 s, Whisper beam 10, no-speech cut-off 0.45, log-prob cut-off -1.5. Both presets send Whisper
  chunks of up to 30 s and no temperature fallback (`docs/SPEC-scan-speed-accuracy.md`). **Not evaluated:** the detailed values are chosen to be more permissive, not
  measured. They change sidecar settings only, never the speaker-matching decision boundaries.
- **Range:** a re-analysis covers the whole clip or a section `[start, end]`. A section runs speech detection,
  diarization and transcription on that slice only.
- Re-analysis needs no voice profile: `ClipAnalyzer` sends the clip to the sidecar and computes each line's voice
  embedding with the active embedding model. It fails visibly when the sidecar does not answer.
- Clips page: re-analyse the selected clip (preset, whole clip or From/To), or "Check in detail" on a transcript
  line (that line ±5 s, detailed preset). Models page: re-analyse every clip whose models differ from the active ones.
  Jobs run one at a time in the background with progress and cancel.

## Storing re-analysed results (replace, keep history)
- Every analysis (scan or re-analysis, including failed ones) is recorded in `analysis_runs`: time, trigger, preset,
  range, embedding model, sidecar models, line count, status and error. The Clips page lists them.
- **Whole clip:** the new run replaces the clip's lines and speakers. A failed or unanswered run changes nothing.
- **Section:** only lines whose midpoint lies in the range are replaced. A section run must use the embedding model
  the clip was analysed with; otherwise the whole clip has to be re-analysed (vectors from two models never mix).
- **Speaker identity carries over by time overlap:** each new per-run speaker label takes the speaker of the
  previous appearance it overlaps most, if the overlap is at least half the talk time of the smaller of the two and
  that speaker is not already taken in the clip. Manual assignments keep their manual flag. Remaining labels are
  linked by voice as before (section runs try the clip's own speakers first), else become new speakers.
- **Review decisions carry over by time overlap:** each previously reviewed or annotated line passes its status and
  note to the new line it overlaps most, if the overlap is at least half the shorter line; one new line per old line.
- Appearance totals (talk time, line count, span, voice vector) are recomputed from their lines after every run.
- Speaker voices for linking use only appearances from clips analysed with the same embedding model.
- A clip is **out of date** when its last whole-clip run used a different embedding model or different sidecar
  models than the active ones.
- **Not evaluated:** the 50 % overlap rules are heuristics.

## Acceptance criteria
1. `dotnet build VoiceScan.sln` has 0 errors; all new tests pass; `sidecar/inference_server.py` compiles.
2. Importing an `.onnx` records it and survives a restart; a file changed after import is refused; a non-`.onnx`
   file is refused; removing an imported model deletes it.
3. Switching the embedding model is refused while it is in use, keeps the old model when loading fails, and is saved.
4. The sidecar client reads and sets models and sends preset and range with `/process`.
5. A whole-clip re-analysis with different speaker labels keeps speaker identities and review decisions by overlap.
6. A section re-analysis replaces only lines in the range and keeps the rest; it is refused when the clip was
   analysed with another embedding model.
7. Every run, including a failed one, appears in the clip's history; a failed run leaves the data unchanged.
8. A clip analysed with another embedding model never links to speakers through vectors of a different model.
9. Re-analysis from the Clips page and bulk re-analysis from the Models page update the database and pages.

## Out of scope
- Measuring imported models, the detailed preset, or the overlap rules (`/eval`).
- Re-enrolling voices automatically (profiles store embeddings, not audio).
- Downloading models from the app, and sidecar hardening (bind address, auth, pinned revisions).
