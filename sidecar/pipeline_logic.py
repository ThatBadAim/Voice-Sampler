"""Model-free analysis logic for the sidecar (numpy only), kept apart from inference_server.py so it can be unit-tested
without torch or the models. Behaviour is specified in docs/SPEC-scan-speed-accuracy.md."""

import re
from dataclasses import dataclass
from typing import Any, Dict, List, Optional, Sequence, Tuple

import numpy as np

DEFAULT_SPEAKER = "SPEAKER_00"
NON_SPEECH_TAGS = frozenset({"[music]", "(music)", "[applause]", "(applause)", "[laughter]", "(laughter)"})
# faster-whisper's default compression_ratio_threshold: above it a line is a repetition loop.
COMPRESSION_RATIO_LIMIT = 2.4
# Not evaluated (docs/SPEC-scan-speed-accuracy.md).
MIN_SPEAKER_RUN_SECONDS = 0.3
MIN_TURN_SECONDS = 0.1
LANGUAGES_WITHOUT_SPACES = frozenset({"ja", "zh"})

Turn = Dict[str, Any]

# ---------------------------------------------------------------------------
# Tier 1 regex moderation (used alone when no classifier is loaded, and as a safety net next to it)
# ---------------------------------------------------------------------------

TIER1_PATTERNS = [
    # Violent threats & direct harm
    r"\b(?:kill|murder|shoot|stab|slit|strangle|hang|die|behead|torture)\s+(?:you|him|her|them|myself|yourself|all)\b",
    r"\b(?:go\s+die|hope\s+you\s+die|want\s+you\s+dead|kill\s+yourself|kms|kys)\b",
    r"\b(?:bomb|shoot\s+up|terrorist|massacre|genocide)\b",
    # Hate speech, slurs & severe toxicity
    r"\b(?:nigg[aer]+s?|fagg?ots?|fags?|kikes?|spics?|chinks?|wetbacks?|trann(?:y|ies))\b",
    r"\b(?:retards?|retarded|subhuman|mongoloid)\b",
    # Targeted harassment & abuse
    r"\b(?:bitch|cunt|whore|slut|motherfucker|asshole|bastard|dipshit|cocksucker)\b",
    r"\b(?:abusive\s+insult|flagged\s+abusive|hate\s+speech|harassment|toxic\s+insult)\b",
    # Sexual violence & exploitation
    r"\b(?:rape|rapist|molest|pedophile|paedophile|nonce)\b",
]
TIER1_REGEX = re.compile("|".join(TIER1_PATTERNS), re.IGNORECASE)

_CLEAN: Dict[str, Any] = {"is_offensive": False, "violations": [], "scores": {}}


def regex_moderation(text: str) -> Dict[str, Any]:
    match = TIER1_REGEX.search(text)
    if not match:
        return dict(_CLEAN)
    term = match.group(0).lower()
    if any(k in term for k in ["kill", "murder", "shoot", "stab", "slit", "die", "bomb", "terrorist", "massacre"]):
        return {"is_offensive": True, "violations": ["threat"], "scores": {"threat": 1.0}}
    if any(k in term for k in ["nigg", "fag", "kike", "spic", "chink", "retard", "subhuman", "mongoloid"]):
        return {"is_offensive": True, "violations": ["identity_attack"], "scores": {"identity_attack": 1.0}}
    if any(k in term for k in ["rape", "molest", "pedophile"]):
        return {"is_offensive": True, "violations": ["sexual_explicit"], "scores": {"sexual_explicit": 1.0}}
    return {"is_offensive": True, "violations": ["insult"], "scores": {"insult": 1.0}}


def moderation_from_scores(text: str, scores: Dict[str, float], threshold: float) -> Dict[str, Any]:
    """Result for one line from classifier scores; the regex flags a line the classifier let through."""
    flagged = [label for label, score in scores.items() if score >= threshold]
    if not flagged and TIER1_REGEX.search(text):
        flagged = ["toxic_content"]
    return {
        "is_offensive": len(flagged) > 0,
        "violations": flagged,
        "scores": {k: round(float(v), 3) for k, v in scores.items()},
    }

# ---------------------------------------------------------------------------
# Speech chunks for Whisper
# ---------------------------------------------------------------------------

def merge_speech_chunks(
    segments: Sequence[Tuple[float, float]],
    min_duration_on: float,
    min_duration_off: float,
    max_chunk_size: float,
    time_offset: float = 0.0,
) -> List[Tuple[float, float]]:
    """Drops speech shorter than min_duration_on, joins speech separated by at most min_duration_off while the result
    stays within max_chunk_size, and cuts anything longer into max_chunk_size pieces. Times are shifted by time_offset."""
    raw = [(s + time_offset, e + time_offset) for s, e in segments if e - s >= min_duration_on]
    if not raw:
        return []

    merged: List[Tuple[float, float]] = []
    cur_start, cur_end = raw[0]
    for start, end in raw[1:]:
        if start - cur_end <= min_duration_off and end - cur_start <= max_chunk_size:
            cur_end = end
        else:
            merged.append((cur_start, cur_end))
            cur_start, cur_end = start, end
    merged.append((cur_start, cur_end))

    chunks: List[Tuple[float, float]] = []
    for start, end in merged:
        t = start
        while t < end:
            t_end = min(t + max_chunk_size, end)
            chunks.append((round(t, 3), round(t_end, 3)))
            t = t_end
    return chunks

# ---------------------------------------------------------------------------
# Windowed diarization
# ---------------------------------------------------------------------------

@dataclass
class DiarizationWindow:
    start: float  # seconds from the start of the analysed audio
    frame_seconds: float
    active: np.ndarray  # bool [frames, speakers]

    @property
    def end(self) -> float:
        return self.start + self.active.shape[0] * self.frame_seconds


def window_starts(total_seconds: float, window_seconds: float, overlap_seconds: float) -> List[float]:
    step = window_seconds - overlap_seconds
    starts = [0.0]
    while starts[-1] + window_seconds < total_seconds:
        starts.append(starts[-1] + step)
    return starts


def activity_from_probs(probs: np.ndarray, threshold: float = 0.5) -> np.ndarray:
    """Speaker activity per frame. When no class passes the threshold anywhere (classes that behave as mutually
    exclusive), each frame takes its arg-max class instead."""
    active = probs > threshold
    if not active.any() and probs.ndim == 2 and probs.shape[0] > 0:
        active = np.zeros_like(active)
        active[np.arange(probs.shape[0]), probs.argmax(axis=1)] = True
    return active


def _runs(active: np.ndarray) -> List[Tuple[int, int]]:
    padded = np.concatenate(([0], active.astype(np.int8), [0]))
    diff = np.diff(padded)
    return list(zip(np.where(diff == 1)[0].tolist(), np.where(diff == -1)[0].tolist()))


def _match_speakers(prev: DiarizationWindow, prev_labels: Dict[int, int], cur: DiarizationWindow) -> Dict[int, int]:
    """Maps cur's local speakers to prev's global ids by frames where both are active in the overlap (greedy, best first)."""
    overlap_end = min(prev.end, cur.end)
    if overlap_end <= cur.start or not prev_labels:
        return {}
    idx = np.arange(cur.active.shape[0])
    centres = cur.start + (idx + 0.5) * cur.frame_seconds
    sel = idx[centres < overlap_end]
    if sel.size == 0:
        return {}
    prev_idx = np.clip(((centres[sel] - prev.start) / prev.frame_seconds).astype(int), 0, prev.active.shape[0] - 1)
    agreement = prev.active[prev_idx].T.astype(np.int64) @ cur.active[sel].astype(np.int64)  # [prev speakers, cur speakers]

    pairs = sorted(
        ((int(agreement[i, j]), i, j) for i in prev_labels for j in range(agreement.shape[1]) if agreement[i, j] > 0),
        key=lambda p: (-p[0], p[1], p[2]),
    )
    used_prev: set = set()
    mapping: Dict[int, int] = {}
    for _, i, j in pairs:
        if i in used_prev or j in mapping:
            continue
        mapping[j] = prev_labels[i]
        used_prev.add(i)
    return mapping


def stitch_windows(windows: Sequence[DiarizationWindow], total_seconds: float, time_offset: float = 0.0) -> List[Turn]:
    """Speaker turns for the whole audio from overlapping diarization windows. Labels are made consistent across
    windows; each window contributes frames up to the middle of its overlaps; touching turns of a speaker are joined."""
    if not windows:
        return []

    labels: List[Dict[int, int]] = []
    next_id = 0
    for k, win in enumerate(windows):
        mapping = _match_speakers(windows[k - 1], labels[k - 1], win) if k > 0 else {}
        for j in range(win.active.shape[1]):
            if j not in mapping and win.active[:, j].any():
                mapping[j] = next_id
                next_id += 1
        labels.append(mapping)

    spans: Dict[int, List[List[float]]] = {}
    for k, win in enumerate(windows):
        own_start = 0.0 if k == 0 else (win.start + windows[k - 1].end) / 2.0
        own_end = total_seconds if k == len(windows) - 1 else (windows[k + 1].start + win.end) / 2.0
        for j, speaker in labels[k].items():
            for s, e in _runs(win.active[:, j]):
                start = max(own_start, win.start + s * win.frame_seconds)
                end = min(own_end, win.start + e * win.frame_seconds)
                if end > start:
                    spans.setdefault(speaker, []).append([start, end])

    join_gap = 1.5 * max(w.frame_seconds for w in windows)
    turns: List[Turn] = []
    for speaker, items in spans.items():
        items.sort()
        merged = [items[0]]
        for start, end in items[1:]:
            if start - merged[-1][1] <= join_gap:
                merged[-1][1] = max(merged[-1][1], end)
            else:
                merged.append([start, end])
        for start, end in merged:
            if end - start >= MIN_TURN_SECONDS:
                turns.append({
                    "start": round(start + time_offset, 2),
                    "end": round(end + time_offset, 2),
                    "speaker": f"SPEAKER_{speaker:02d}",
                })
    turns.sort(key=lambda t: (t["start"], t["speaker"]))
    return turns

# ---------------------------------------------------------------------------
# Lines and speakers
# ---------------------------------------------------------------------------

def speaker_for_span(start: float, end: float, turns: Sequence[Turn]) -> str:
    """The speaker whose turns overlap [start, end] most; the nearest turn's speaker when none overlaps."""
    totals: Dict[str, float] = {}
    for t in turns:
        overlap = min(end, t["end"]) - max(start, t["start"])
        if overlap > 0:
            totals[t["speaker"]] = totals.get(t["speaker"], 0.0) + overlap
    if totals:
        return max(totals, key=lambda s: totals[s])
    if turns:
        return min(turns, key=lambda t: max(t["start"] - end, start - t["end"], 0.0))["speaker"]
    return DEFAULT_SPEAKER


def word_joiner(language: Optional[str]) -> str:
    return "" if language in LANGUAGES_WITHOUT_SPACES else " "


def split_line_by_speaker(
    words: Sequence[Dict[str, Any]],
    turns: Sequence[Turn],
    joiner: str = " ",
    min_run_seconds: float = MIN_SPEAKER_RUN_SECONDS,
) -> List[Dict[str, Any]]:
    """Splits a line's aligned words (absolute "start"/"end"; words without timings follow their neighbour) into runs
    of one speaker. A run shorter than min_run_seconds joins its neighbour. Empty when no word has timings."""
    runs: List[Dict[str, Any]] = []
    leading: List[str] = []
    for w in words:
        text = str(w.get("word", "")).strip()
        if "start" not in w or "end" not in w:
            if runs:
                runs[-1]["words"].append(text)
            else:
                leading.append(text)
            continue
        speaker = speaker_for_span(float(w["start"]), float(w["end"]), turns)
        if runs and runs[-1]["speaker"] == speaker:
            runs[-1]["end"] = max(runs[-1]["end"], float(w["end"]))
            runs[-1]["words"].append(text)
        else:
            runs.append({"start": float(w["start"]), "end": float(w["end"]), "speaker": speaker, "words": leading + [text]})
            leading = []
    if not runs:
        return []

    while len(runs) > 1:
        short = next((i for i, r in enumerate(runs) if r["end"] - r["start"] < min_run_seconds), None)
        if short is None:
            break
        run = runs.pop(short)
        if short > 0:
            target = runs[short - 1]
            target["end"] = max(target["end"], run["end"])
            target["words"].extend(run["words"])
        else:
            target = runs[0]
            target["start"] = min(target["start"], run["start"])
            target["words"] = run["words"] + target["words"]
        coalesced = [runs[0]]
        for r in runs[1:]:
            if r["speaker"] == coalesced[-1]["speaker"]:
                coalesced[-1]["end"] = max(coalesced[-1]["end"], r["end"])
                coalesced[-1]["words"].extend(r["words"])
            else:
                coalesced.append(r)
        runs = coalesced

    return [
        {"start": r["start"], "end": r["end"], "speaker": r["speaker"], "text": joiner.join(x for x in r["words"] if x).strip()}
        for r in runs
    ]


def is_hallucination(
    text: str,
    compression_ratio: Optional[float],
    no_speech_prob: float,
    avg_logprob: float,
    no_speech_threshold: float,
    log_prob_threshold: float,
) -> bool:
    cleaned = text.strip()
    if not cleaned or cleaned.lower() in NON_SPEECH_TAGS:
        return True
    if compression_ratio is not None and compression_ratio > COMPRESSION_RATIO_LIMIT:
        return True
    # Whisper's own silence rule.
    return no_speech_prob > no_speech_threshold and avg_logprob < log_prob_threshold


def asr_language(alignment_setting: str) -> Optional[str]:
    """The transcription language when the alignment stage is set to a language code; None lets Whisper detect it."""
    value = alignment_setting.strip().lower()
    return value if re.fullmatch(r"[a-z]{2,3}", value) else None
