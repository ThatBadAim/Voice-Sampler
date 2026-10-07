# SPEC: Scan Speed and Analysis Reliability

Makes scans faster and stops the sidecar analysis from silently failing or degrading, without changing any model
and without changing how voices are matched. Builds on `docs/SPEC-model-swap.md`.

## Out of scope (needs `/eval` first)
How a sidecar line is scored against the target voice stays as it is: a line's vector is the mean of the 2 s windows
that overlap it. Embedding each line from its own audio and scoring per diarized speaker changes match scores, so it
waits until the evaluation harness covers the sidecar path (AGENTS.md: no decision-boundary changes without `/eval`).

## Engine (C#)
1. **Sidecar answers are cached.** A scan stores the sidecar response in the scan database, keyed by file hash,
   audio track, preset, the sidecar's `pipeline_version` and its active and loaded models. A rescan with the same
   key does not call `/process`. Nothing is cached while the sidecar reports `ready: false`, when it does not report
   a `pipeline_version`, or when the models in the response differ from the models read before the call.
   Re-analysis never reads the cache.
2. **Sidecar and voice embedding run together.** A scan starts the sidecar request, then decodes, runs VAD and embeds
   windows while the sidecar works, and waits for the sidecar at the end. A "no speech" answer still gives the
   `NO_SPEECH_DETECTED` result; it now also carries the waveform and caches the window embeddings.
3. **The audio track reaches the sidecar.** `/process` receives `audio_track`; the sidecar analyses that track.
4. **Long files do not time out.** The `/process` timeout is `max(15 min, 2 s per second of audio)`; audio length
   comes from the section, or from ffprobe for a whole file.
5. **Cancelled or timed-out requests stop the sidecar.** Each `/process` carries a `request_id`; when the request is
   cancelled or times out the client sends `POST /cancel` with it, and the sidecar stops that job at its next
   checkpoint (or skips it if it has not started).
6. **A loading sidecar counts as running.** `/health` reports `loading` while models load. `SidecarManager` treats
   `loading` as started and does not launch a second process; `/process` waits for loading to finish.
7. **cuDNN uses heuristic algorithm search** (`cudnn_conv_algo_search=HEURISTIC`) so each new input length does not
   trigger an exhaustive benchmark. Line embeddings are not batched: ECAPA has no length input, so zero-padding
   shorter lines in a batch would change their vectors.

## Sidecar (Python)
1. **One decode per request.** The requested track and range are decoded once with ffmpeg to 16 kHz mono float32 in
   a temporary file and memory-mapped; every stage slices from it. The file is removed after the request.
2. **Diarization runs in overlapping windows** of 90 s with 15 s overlap, so memory does not grow with file length.
   Speakers are matched across windows by how much their activity agrees in the overlap; each window's frames count
   up to the middle of its overlaps; turns of the same speaker that touch across a boundary are joined. A speaker
   silent in an overlap and heard again later gets a new label. **Not evaluated:** window and overlap lengths.
3. **Batched transcription** with faster-whisper's `BatchedInferencePipeline` (faster-whisper >= 1.2.1), speech
   chunks passed as `clip_timestamps`, up to 64 chunks per call. Chunks merge up to 30 s (Whisper's window) in both
   presets. Batched decoding has no temperature fallback.
4. **Language is fixed** when the alignment stage is a language code (`en`, `de`, ...); otherwise Whisper detects it.
   Whisper word timestamps are off; word timings come from the aligner.
5. **Hallucination filter:** a Whisper line is dropped when it is a bare non-speech tag, when its compression ratio
   is above 2.4 (faster-whisper's default repetition cut-off), or when its no-speech probability is above the
   preset's no-speech cut-off while its average log-probability is below the preset's log-prob cut-off (Whisper's
   own silence rule).
6. **Speakers by overlap, lines split at speaker changes.** Each aligned word gets the speaker whose turns overlap it
   most (nearest turn when none overlaps). A line is split where the speaker changes; a run shorter than 0.3 s joins
   its neighbour. Without word timings a line gets the speaker that overlaps it most. **Not evaluated:** 0.3 s.
7. **Moderation is batched:** Detoxify scores all lines of a file in batches of 64; the regex rules are unchanged.
8. **Stage timings** (decode, VAD, diarization, transcription, alignment, moderation) are logged for every request.
9. **Model loading runs in the background** after the server starts; `/health` says `loading` until it ends.
10. `pipeline_version` (in `/models` and every `/process` response) changes whenever the sidecar's output for the
    same models can change. It is `2` for this spec.

## Acceptance criteria
1. A second scan of a file with unchanged sidecar models and version makes no `/process` call; changing a model makes one.
2. The sidecar request starts before the scan's decode finishes and its answer is still applied.
3. `/process` requests carry `audio_track` and `request_id`; cancelling sends `POST /cancel` with the same id.
4. The `/process` timeout grows with audio length and never falls below 15 minutes.
5. `/health` with status `loading` is parsed; `SidecarManager` does not start a second process for it.
6. The pure sidecar logic (chunk merging, window stitching, speaker overlap, line splitting, hallucination filter)
   has unit tests runnable with only numpy (`python -m unittest discover sidecar/tests`).
