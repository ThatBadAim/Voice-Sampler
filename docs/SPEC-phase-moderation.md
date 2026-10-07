# SPEC: Moderation Review (Incidents, Clips, Speakers)

Adds a moderation workflow to the desktop app: potential offences from every scanned clip are collected into one
sortable list you can listen back to, every clip lists the speakers heard in it, and every speaker has a page with
everything known about them. Speakers are linked across clips by voice.

This phase covers storage, linking and the UI. Transcription, diarization and the toxicity classifier stay in the
Python sidecar (`sidecar/inference_server.py`, CUDA), whose models will be reworked separately. The app consumes the
sidecar's existing `/process` contract unchanged.

## Data flow
1. A scan runs as today. When the sidecar answers, each `DetectedSegment` carries `speaker_label` (per file, e.g.
   `SPEAKER_00`), `transcript`, `moderation_violations`, `moderation_scores`, and the C# engine's voice embedding.
2. After each file, `BackgroundScanController` hands the `FileScanResult` to `ModerationStore.IngestAsync`. Ingest
   failures are logged and never fail the scan.
3. `ModerationStore` (`LocalApplicationData/VoiceScan/moderation.db`) records the clip, links each per-file speaker to
   a global speaker, stores every utterance, and applies the word list.

## Offence detection
- **Classifier:** the sidecar's per-category scores (`toxicity`, `severe_toxicity`, `obscene`, `threat`, `insult`,
  `identity_attack`, `sexual_explicit`). Labels the sidecar reports in `moderation_violations` without a score
  (its regex fallback) are stored with score 1.0.
- **Word list:** user-maintained phrases with a category. Matching is case-insensitive on whole words, ignoring
  punctuation; a trailing `*` matches any word ending (`idiot*` matches `idiots`). Editing the list re-checks every
  stored transcript.
- **Incident:** an utterance with a word-list hit, or any classifier score at or above the sensitivity slider
  (default 0.5, the sidecar's own threshold). Scores are stored, so changing sensitivity needs no rescan.
- Each incident has a review status (Unreviewed / Confirmed / Dismissed) and a note. Rescanning a clip keeps the
  status of utterances with the same start and end time.

## Speaker linking
- Per clip, utterance embeddings sharing a `speaker_label` are summed into one appearance vector.
- A global speaker's voice is the normalized sum of all its appearance vectors (same model version only).
- An appearance joins the most similar existing speaker if cosine similarity is at or above the loaded model's
  match threshold (`ModelOperatingPoint.Threshold`), and never joins a speaker already used by another label in the
  same clip. Otherwise a new speaker is created.
- A speaker whose voice matches an enrolled voice profile at the same threshold is named after that profile.
- **Not evaluated:** the match threshold was measured for target-speaker detection on 2 s windows, not for linking
  whole appearances across clips. Every appearance shows its match score, and the user can move an appearance to
  another speaker or to a new one. Linking accuracy must be measured in `/eval` before it is relied on.

## Screens
- **Incidents:** every incident across all clips. Sort by score, clip, time, speaker or category; filter by
  category, speaker, status and text; sensitivity slider. Selecting one shows the full transcript line, scores,
  word-list hits and the speaker. Play plays exactly that utterance (with 1 s of context either side). Confirm,
  Dismiss and a note. The word list is edited from this page.
- **Clips:** every analysed clip with duration, speaker count, incident count and analysis status (Analysed,
  No speech, Not analysed when the sidecar was unavailable, Error). Selecting a clip lists its speakers with talk
  time, utterances, incidents and match score. Opening a speaker goes to their page.
- **Speakers:** every known speaker with clip count, talk time, incidents and last seen. The speaker page shows:
  name (editable) and notes; a voice sample (their longest utterance, playable); appearances (clip, talk time,
  first/last time in clip, match score, move to another speaker); incidents; and the full transcript, each line
  playable.
- Each page owns its own audio player.

## Acceptance criteria
1. `dotnet build VoiceScan.sln` has 0 errors; all new tests pass.
2. Ingesting a sidecar-style result stores the clip, speakers, utterances, scores and word-list hits; re-ingesting the
   same file replaces its data and keeps review status.
3. The same voice in two clips links to one speaker; two labels in one clip never share a speaker; a voice below the
   threshold creates a new speaker; an enrolled-profile match names the speaker.
4. Changing sensitivity or the word list changes the incident list without rescanning.
5. Renaming a speaker, notes, review status and moving an appearance persist across restarts.
6. A file scanned without the sidecar is listed as "Not analysed", never as clean.
7. No network calls from the app beyond the existing localhost sidecar; no detection thresholds changed.

## Out of scope (follow-ups)
- Scanning without selecting a target voice profile (the scanner computes utterance embeddings only on the profile
  path today).
- Sidecar hardening noted in the audit (loopback binding, authentication, model pinning, missing `detoxify`
  dependency).
- Evaluating linking accuracy and classifier precision on real gameplay recordings.
