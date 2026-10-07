import gc
import json
import logging
import math
import os
import shutil
import subprocess
import tempfile
import threading
import time
from collections import OrderedDict
from contextlib import asynccontextmanager, contextmanager
from typing import Any, Callable, Dict, Iterator, List, Optional, Tuple

import numpy as np
import torch
from detoxify import Detoxify
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

from pipeline_logic import (
    TIER1_REGEX,
    DiarizationWindow,
    activity_from_probs,
    asr_language,
    is_hallucination,
    merge_speech_chunks,
    moderation_from_scores,
    regex_moderation,
    speaker_for_span,
    split_line_by_speaker,
    stitch_windows,
    window_starts,
    word_joiner,
)

# Configure logging
logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(name)s: %(message)s")
logger = logging.getLogger("voicescan-sidecar")

# ---------------------------------------------------------------------------
# Model Configuration & Analysis Presets
# ---------------------------------------------------------------------------

# Changes whenever the output for the same models and audio can change; the engine keys its cache on it.
PIPELINE_VERSION = "2"

MODEL_STAGES = ["vad", "diarization", "asr", "alignment", "moderation"]

DEFAULT_MODELS: Dict[str, str] = {
    "vad": "pyannote/segmentation-3.0",
    "diarization": "nvidia/Nemotron-3-Diarization",
    "asr": "large-v3-turbo",
    "alignment": "en",
    "moderation": "unbiased",
}

MODEL_CONFIG_PATH = os.getenv(
    "VOICESCAN_MODEL_CONFIG", os.path.join(os.path.dirname(os.path.abspath(__file__)), "model_config.json")
)

# "detailed" is more permissive for quiet, short or overlapping speech. Both send Whisper chunks of up to 30 s (its
# window); batched decoding uses a single temperature. Values are not evaluated (docs/SPEC-model-swap.md,
# docs/SPEC-scan-speed-accuracy.md).
PRESETS: Dict[str, Dict[str, Any]] = {
    "standard": {
        "vad_onset": 0.50, "vad_offset": 0.40, "min_duration_on": 0.35, "min_duration_off": 0.50,
        "max_chunk_size": 30.0, "beam_size": 5, "temperature": 0.0, "no_speech_threshold": 0.6,
        "log_prob_threshold": -1.0,
    },
    "detailed": {
        "vad_onset": 0.35, "vad_offset": 0.25, "min_duration_on": 0.20, "min_duration_off": 0.30,
        "max_chunk_size": 30.0, "beam_size": 10, "temperature": 0.0, "no_speech_threshold": 0.45,
        "log_prob_threshold": -1.5,
    },
}

SAMPLE_RATE = 16000
# Not evaluated (docs/SPEC-scan-speed-accuracy.md).
DIARIZATION_WINDOW_SECONDS = 90.0
DIARIZATION_OVERLAP_SECONDS = 15.0
WHISPER_BATCH_SIZE = 8
# Speech chunks per batched transcribe call; bounds the mel features held in memory at once.
WHISPER_CLIPS_PER_CALL = 64
MIN_CHUNK_SECONDS = 0.2
MIN_LINE_SECONDS = 0.1
ALIGN_MARGIN_SECONDS = 0.2
MODERATION_BATCH_SIZE = 64
MODERATION_THRESHOLD = 0.5


def load_model_config() -> Dict[str, str]:
    """Active model per stage: defaults overridden by the saved configuration."""
    config = dict(DEFAULT_MODELS)
    try:
        if os.path.exists(MODEL_CONFIG_PATH):
            with open(MODEL_CONFIG_PATH, "r", encoding="utf-8") as f:
                saved = json.load(f)
            config.update({k: str(v) for k, v in saved.items() if k in MODEL_STAGES and str(v).strip()})
    except (OSError, ValueError) as e:
        logger.warning(f"Ignoring unreadable model config {MODEL_CONFIG_PATH}: {e}")
    return config


def save_model_config(config: Dict[str, str]) -> None:
    tmp = MODEL_CONFIG_PATH + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(config, f, indent=2)
    os.replace(tmp, MODEL_CONFIG_PATH)

# ---------------------------------------------------------------------------
# Pipeline State Holder
# ---------------------------------------------------------------------------

class PipelineState:
    def __init__(self) -> None:
        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        self.vad_pipeline: Any = None
        self.diar_model: Any = None
        self.whisper_model: Any = None
        self.whisper_batched: Any = None
        self.align_model: Any = None
        self.align_metadata: Any = None
        self.moderation_model: Optional[Detoxify] = None
        self.models: Dict[str, str] = dict(DEFAULT_MODELS)
        self.errors: Dict[str, Optional[str]] = {stage: None for stage in MODEL_STAGES}
        # Set once the startup load has finished (whether or not every stage loaded).
        self.ready = threading.Event()
        # Held while a file is processed or a model is swapped, so a swap never replaces a model mid-file.
        self.lock = threading.Lock()

pipeline = PipelineState()


class JobCancelled(Exception):
    pass


class CancelRegistry:
    """Request ids the engine has given up on. Thread-safe: /cancel arrives while /process holds the pipeline lock."""

    MAX_IDS = 256

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._ids: "OrderedDict[str, None]" = OrderedDict()

    def cancel(self, request_id: str) -> None:
        with self._lock:
            self._ids[request_id] = None
            while len(self._ids) > self.MAX_IDS:
                self._ids.popitem(last=False)

    def check(self, request_id: Optional[str]) -> None:
        if request_id is None:
            return
        with self._lock:
            if request_id in self._ids:
                raise JobCancelled(request_id)

    def forget(self, request_id: Optional[str]) -> None:
        if request_id is None:
            return
        with self._lock:
            self._ids.pop(request_id, None)

cancellations = CancelRegistry()

# ---------------------------------------------------------------------------
# Per-stage Model Loaders (each returns the loaded objects or raises)
# ---------------------------------------------------------------------------

def load_vad(name: str) -> Any:
    from pyannote.audio import Model
    from pyannote.audio.pipelines import VoiceActivityDetection
    hf_token = os.getenv("HF_TOKEN")
    try:
        vad_m = Model.from_pretrained(name, token=hf_token)
    except TypeError:
        vad_m = Model.from_pretrained(name, use_auth_token=hf_token)
    vad = VoiceActivityDetection(segmentation=vad_m)
    configure_vad(vad, PRESETS["standard"])
    vad.to(torch.device(pipeline.device))
    return vad


def configure_vad(vad: Any, preset: Dict[str, Any]) -> None:
    """Applies a preset's hysteresis binarization to the VAD pipeline (same attributes the original setup used)."""
    from pyannote.audio.utils.signal import Binarize
    vad.onset = preset["vad_onset"]
    vad.offset = preset["vad_offset"]
    vad.min_duration_on = preset["min_duration_on"]
    vad.min_duration_off = preset["min_duration_off"]
    vad.initialize()
    vad._binarize = Binarize(
        onset=preset["vad_onset"],
        offset=preset["vad_offset"],
        min_duration_on=preset["min_duration_on"],
        min_duration_off=preset["min_duration_off"],
    )


def load_diarization(name: str) -> Any:
    from transformers import AutoModelForAudioFrameClassification
    model = AutoModelForAudioFrameClassification.from_pretrained(
        name, trust_remote_code=True, token=os.getenv("HF_TOKEN")
    ).to(pipeline.device)
    model.eval()
    return model


def load_asr(name: str) -> Any:
    from faster_whisper import WhisperModel
    if torch.cuda.is_available():
        return WhisperModel(name, device="cuda", device_index=0, compute_type="int8_float16")
    return WhisperModel(name, device="cpu", compute_type="int8")


def load_alignment(name: str) -> Tuple[Any, Any]:
    """A language code (e.g. "en") loads whisperx's default aligner for it; anything else is a wav2vec2 model name."""
    import whisperx
    if "/" in name or os.path.exists(name):
        return whisperx.load_align_model(language_code="en", device=pipeline.device, model_name=name)
    return whisperx.load_align_model(language_code=name, device=pipeline.device)


def load_moderation(name: str) -> Detoxify:
    """A Detoxify variant (original, unbiased, multilingual, ...) or a local checkpoint file."""
    if os.path.isfile(name):
        return Detoxify("unbiased", checkpoint=name, device=pipeline.device)
    return Detoxify(name, device=pipeline.device)


def install_stage(stage: str, loaded: Any) -> None:
    if stage == "vad":
        pipeline.vad_pipeline = loaded
    elif stage == "diarization":
        pipeline.diar_model = loaded
    elif stage == "asr":
        from faster_whisper import BatchedInferencePipeline
        pipeline.whisper_model = loaded
        pipeline.whisper_batched = BatchedInferencePipeline(model=loaded)
    elif stage == "alignment":
        pipeline.align_model, pipeline.align_metadata = loaded
    elif stage == "moderation":
        pipeline.moderation_model = loaded


def stage_loaded(stage: str) -> bool:
    return {
        "vad": pipeline.vad_pipeline is not None,
        "diarization": pipeline.diar_model is not None,
        "asr": pipeline.whisper_batched is not None,
        "alignment": pipeline.align_model is not None,
        "moderation": pipeline.moderation_model is not None,
    }[stage]


STAGE_LOADERS: Dict[str, Callable[[str], Any]] = {
    "vad": load_vad,
    "diarization": load_diarization,
    "asr": load_asr,
    "alignment": load_alignment,
    "moderation": load_moderation,
}


def load_stage(stage: str, name: str) -> bool:
    """Loads `name` for `stage`; on success it replaces the stage's model, on failure the previous model stays."""
    logger.info(f"Loading {stage} model: {name}")
    try:
        loaded = STAGE_LOADERS[stage](name)
        install_stage(stage, loaded)
    except Exception as e:
        logger.warning(f"Could not load {stage} model '{name}': {e}")
        pipeline.errors[stage] = f"{name}: {e}"
        return False
    pipeline.models[stage] = name
    pipeline.errors[stage] = None
    if torch.cuda.is_available():
        torch.cuda.empty_cache()
    logger.info(f"Loaded {stage} model: {name}")
    return True


def init_pipeline() -> None:
    """Loads the configured model for every stage. A stage that fails to load is reported by /models."""
    try:
        logger.info(f"Initializing VoiceScan inference pipeline on device: {pipeline.device}")
        config = load_model_config()
        for stage in MODEL_STAGES:
            if not load_stage(stage, config[stage]):
                # The stage has no model yet; record the configured name so /models shows what failed.
                pipeline.models[stage] = config[stage]
        if pipeline.moderation_model is None:
            logger.warning("Moderation model unavailable. Falling back to Tier 1 regex.")
        logger.info("Inference pipeline initialization completed.")
    finally:
        pipeline.ready.set()


def model_status() -> Dict[str, Any]:
    return {
        "models": dict(pipeline.models),
        "loaded": {stage: stage_loaded(stage) for stage in MODEL_STAGES},
        "errors": dict(pipeline.errors),
        "presets": list(PRESETS.keys()),
        "ready": pipeline.ready.is_set(),
        "pipeline_version": PIPELINE_VERSION,
    }

# ---------------------------------------------------------------------------
# Path Resolution Helper
# ---------------------------------------------------------------------------

def resolve_audio_path(audio_path: str) -> str:
    """Translates incoming Windows paths or mapped container paths to accessible filesystem paths."""
    if os.path.exists(audio_path):
        return audio_path

    normalized = audio_path.replace("\\", "/")

    for prefix in ["C:/AudioData", "c:/AudioData", "C:/audiodata", "c:/audiodata"]:
        if normalized.lower().startswith(prefix.lower()):
            rel = normalized[len(prefix):].lstrip("/")
            return os.path.join("/data", rel)

    if len(normalized) >= 2 and normalized[1] == ":":
        rel = normalized[2:].lstrip("/")
        candidate = os.path.join("/data", rel)
        if os.path.exists(candidate):
            return candidate

    base = os.path.basename(normalized)
    candidate = os.path.join("/data", base)
    if os.path.exists(candidate):
        return candidate

    return audio_path

# ---------------------------------------------------------------------------
# Audio: one decode per request
# ---------------------------------------------------------------------------

def decode_audio(path: str, track: int, start: float, end: Optional[float], directory: str) -> np.ndarray:
    """Decodes one audio track (or a section of it) once to 16 kHz mono float32 in `directory` and memory-maps it,
    so every stage slices it without holding the recording in RAM."""
    out_path = os.path.join(directory, "audio.f32")
    cmd = ["ffmpeg", "-nostdin", "-v", "error", "-y"]
    if start > 0:
        cmd += ["-ss", f"{start:.3f}"]
    cmd += ["-i", path]
    if end is not None:
        cmd += ["-t", f"{end - start:.3f}"]
    cmd += ["-map", f"0:a:{max(0, track)}", "-vn", "-ac", "1", "-ar", str(SAMPLE_RATE), "-f", "f32le", out_path]
    try:
        result = subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    except FileNotFoundError:
        raise HTTPException(status_code=500, detail="ffmpeg was not found on the sidecar's PATH")
    if result.returncode != 0:
        message = result.stderr.decode("utf-8", errors="replace").strip()[-500:]
        raise HTTPException(status_code=422, detail=f"Could not decode audio track {track} of {path}: {message}")
    if not os.path.exists(out_path) or os.path.getsize(out_path) == 0:
        return np.zeros(0, dtype=np.float32)
    # Copy-on-write: torch.from_numpy needs a writable array; the file itself is never modified.
    return np.memmap(out_path, dtype=np.float32, mode="c")


@contextmanager
def timed(timings: Dict[str, float], name: str) -> Iterator[None]:
    started = time.perf_counter()
    try:
        yield
    finally:
        timings[name] = timings.get(name, 0.0) + time.perf_counter() - started

# ---------------------------------------------------------------------------
# Pipeline Stages
# ---------------------------------------------------------------------------

def detect_speech(audio: np.ndarray, preset: Dict[str, Any], time_offset: float) -> List[Tuple[float, float]]:
    """Speech chunks for Whisper (absolute seconds). Raises when the VAD is missing or fails, so the engine never
    mistakes a broken VAD for silence."""
    if pipeline.vad_pipeline is None:
        raise HTTPException(status_code=503, detail=f"VAD model is not loaded: {pipeline.errors.get('vad')}")
    configure_vad(pipeline.vad_pipeline, preset)
    try:
        vad_result = pipeline.vad_pipeline({"waveform": torch.from_numpy(audio).unsqueeze(0), "sample_rate": SAMPLE_RATE})
        timeline = vad_result.get_timeline().support()
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"VAD failed: {e}")
    return merge_speech_chunks(
        [(float(seg.start), float(seg.end)) for seg in timeline],
        preset["min_duration_on"],
        preset["min_duration_off"],
        preset["max_chunk_size"],
        time_offset,
    )


def diarize(audio: np.ndarray, time_offset: float, request_id: Optional[str]) -> List[Dict[str, Any]]:
    """Speaker turns (absolute seconds) from overlapping windows; empty when no diarization model is loaded or it fails."""
    if pipeline.diar_model is None:
        return []
    total_seconds = audio.shape[0] / SAMPLE_RATE
    windows: List[DiarizationWindow] = []
    try:
        for start in window_starts(total_seconds, DIARIZATION_WINDOW_SECONDS, DIARIZATION_OVERLAP_SECONDS):
            cancellations.check(request_id)
            a = int(start * SAMPLE_RATE)
            b = min(audio.shape[0], int((start + DIARIZATION_WINDOW_SECONDS) * SAMPLE_RATE))
            if b <= a:
                break
            inputs = torch.from_numpy(np.ascontiguousarray(audio[a:b])).unsqueeze(0).to(pipeline.device)
            with torch.no_grad():
                outputs = pipeline.diar_model(inputs)
            logits = outputs.logits if hasattr(outputs, "logits") else outputs
            frame_logits = logits[0] if logits.dim() == 3 else logits
            if frame_logits.dim() == 1:
                frame_logits = frame_logits.unsqueeze(1)
            probs = torch.sigmoid(frame_logits).float().cpu().numpy()
            frame_seconds = (b - a) / SAMPLE_RATE / max(1, probs.shape[0])
            windows.append(DiarizationWindow(start=start, frame_seconds=frame_seconds, active=activity_from_probs(probs)))
    except JobCancelled:
        raise
    except Exception as e:
        logger.warning(f"Diarization error ({e}); every line gets the default speaker.")
        return []
    turns = stitch_windows(windows, total_seconds, time_offset)
    logger.info(f"Diarization produced {len(turns)} speaker turns from {len(windows)} window(s).")
    return turns


def transcribe(
    audio: np.ndarray,
    chunks: List[Tuple[float, float]],
    time_offset: float,
    preset: Dict[str, Any],
    language: Optional[str],
    request_id: Optional[str],
) -> List[Dict[str, Any]]:
    """Batched Whisper over the speech chunks; returns lines (absolute seconds) that pass the hallucination filter."""
    if pipeline.whisper_batched is None:
        raise HTTPException(status_code=503, detail=f"Whisper model is not loaded: {pipeline.errors.get('asr')}")
    clips = [{"start": s - time_offset, "end": e - time_offset} for s, e in chunks if e - s >= MIN_CHUNK_SECONDS]
    lines: List[Dict[str, Any]] = []
    calls = failures = 0
    last_error = ""
    for i in range(0, len(clips), WHISPER_CLIPS_PER_CALL):
        cancellations.check(request_id)
        group = clips[i:i + WHISPER_CLIPS_PER_CALL]
        calls += 1
        try:
            segments, _ = pipeline.whisper_batched.transcribe(
                audio,
                language=language,
                clip_timestamps=group,
                batch_size=WHISPER_BATCH_SIZE,
                beam_size=preset["beam_size"],
                temperature=preset["temperature"],
                no_speech_threshold=preset["no_speech_threshold"],
                log_prob_threshold=preset["log_prob_threshold"],
                without_timestamps=False,
                word_timestamps=False,
                vad_filter=False,
            )
            segments = list(segments)
        except Exception as e:
            failures += 1
            last_error = str(e)
            logger.warning(f"Whisper transcription error on chunks {i}-{i + len(group) - 1}: {e}")
            continue
        for seg in segments:
            if is_hallucination(seg.text, seg.compression_ratio, seg.no_speech_prob, seg.avg_logprob,
                                preset["no_speech_threshold"], preset["log_prob_threshold"]):
                continue
            start = round(float(seg.start) + time_offset, 3)
            end = round(float(seg.end) + time_offset, 3)
            if end - start < MIN_LINE_SECONDS:
                continue
            lines.append({"start": start, "end": end, "text": seg.text.strip(), "avg_logprob": float(seg.avg_logprob)})
    if calls > 0 and failures == calls:
        raise HTTPException(status_code=500, detail=f"Transcription failed: {last_error}")
    return lines


def align_words(audio: np.ndarray, line: Dict[str, Any], time_offset: float) -> List[Dict[str, Any]]:
    """Aligned words of one line (absolute seconds; words the aligner could not place have no times). Empty when no
    aligner is loaded or alignment fails."""
    if pipeline.align_model is None or pipeline.align_metadata is None:
        return []
    a = max(0, int((line["start"] - time_offset) * SAMPLE_RATE))
    b = min(audio.shape[0], int((line["end"] - time_offset) * SAMPLE_RATE))
    if b <= a:
        return []
    try:
        import whisperx
        aligned = whisperx.align(
            [{"text": line["text"], "start": 0.0, "end": (b - a) / SAMPLE_RATE}],
            pipeline.align_model,
            pipeline.align_metadata,
            np.array(audio[a:b], dtype=np.float32),
            pipeline.device,
            return_char_alignments=False,
        )
    except Exception as e:
        logger.debug(f"Forced alignment error on line {line['start']:.2f}-{line['end']:.2f}: {e}")
        return []
    words = aligned.get("word_segments") or [w for s in aligned.get("segments", []) for w in s.get("words", [])]
    base = a / SAMPLE_RATE + time_offset
    result: List[Dict[str, Any]] = []
    for w in words:
        item: Dict[str, Any] = {"word": w.get("word", "")}
        start, end = w.get("start"), w.get("end")
        if isinstance(start, (int, float)) and isinstance(end, (int, float)) and math.isfinite(start) and math.isfinite(end):
            item["start"] = base + float(start)
            item["end"] = base + float(end)
        result.append(item)
    return result


def _clamp_to_line(line: Dict[str, Any], start: float, end: float) -> Tuple[float, float]:
    start = max(line["start"] - ALIGN_MARGIN_SECONDS, start)
    end = min(line["end"] + ALIGN_MARGIN_SECONDS, max(start + MIN_LINE_SECONDS, end))
    return round(start, 3), round(end, 3)


def align_and_assign(
    audio: np.ndarray,
    lines: List[Dict[str, Any]],
    turns: List[Dict[str, Any]],
    time_offset: float,
    language: Optional[str],
    request_id: Optional[str],
) -> List[Dict[str, Any]]:
    """Tightens each line to its aligned words, gives it the speaker that overlaps it most, and splits it where the
    speaker changes."""
    joiner = word_joiner(language)
    result: List[Dict[str, Any]] = []
    for line in lines:
        cancellations.check(request_id)
        words = align_words(audio, line, time_offset)
        runs = split_line_by_speaker(words, turns, joiner) if words else []
        if not runs:
            result.append({**line, "speaker": speaker_for_span(line["start"], line["end"], turns)})
            continue
        for run in runs:
            start, end = _clamp_to_line(line, run["start"], run["end"])
            text = line["text"] if len(runs) == 1 else run["text"]
            result.append({**line, "start": start, "end": end, "text": text, "speaker": run["speaker"]})
    return result


def moderate(texts: List[str]) -> List[Dict[str, Any]]:
    """Detoxify in batches (regex rules alongside); regex only when no classifier is loaded or it fails."""
    model = pipeline.moderation_model
    if model is None:
        return [regex_moderation(t) for t in texts]
    results: List[Dict[str, Any]] = []
    for i in range(0, len(texts), MODERATION_BATCH_SIZE):
        batch = texts[i:i + MODERATION_BATCH_SIZE]
        try:
            scores = model.predict(batch)
            for k, text in enumerate(batch):
                results.append(moderation_from_scores(text, {label: float(v[k]) for label, v in scores.items()},
                                                      MODERATION_THRESHOLD))
        except Exception as e:
            logger.warning(f"Detoxify inference error: {e}")
            for text in batch:
                flagged = TIER1_REGEX.search(text) is not None
                results.append({"is_offensive": flagged, "violations": ["toxic_content"] if flagged else [], "scores": {}})
    return results

# ---------------------------------------------------------------------------
# FastAPI Application & Lifespan
# ---------------------------------------------------------------------------

@asynccontextmanager
async def lifespan(app: FastAPI):
    # Models load in the background so /health can answer "loading" instead of refusing connections.
    threading.Thread(target=init_pipeline, name="model-loader", daemon=True).start()
    yield
    if torch.cuda.is_available():
        torch.cuda.empty_cache()

app = FastAPI(title="VoiceScan Inference Worker", lifespan=lifespan)

class ScanRequest(BaseModel):
    audio_path: str
    preset: str = "standard"
    start_seconds: Optional[float] = None
    end_seconds: Optional[float] = None
    audio_track: int = 0
    request_id: Optional[str] = None


class ModelsUpdate(BaseModel):
    models: Dict[str, str]


class CancelRequest(BaseModel):
    request_id: str

@app.get("/health")
def health_check():
    cuda_ok = torch.cuda.is_available()
    device_name = torch.cuda.get_device_name(0) if cuda_ok else "CPU"
    vram_free = 0.0
    vram_total = 0.0
    if cuda_ok:
        try:
            free_b, total_b = torch.cuda.mem_get_info()
            vram_free = round(free_b / (1024**3), 2)
            vram_total = round(total_b / (1024**3), 2)
        except Exception as e:
            logger.warning(f"Error fetching VRAM info: {e}")

    return {
        "status": "ready" if pipeline.ready.is_set() else "loading",
        "cuda_available": cuda_ok,
        "device_name": device_name,
        "vram_free_gb": vram_free,
        "vram_total_gb": vram_total
    }

@app.get("/models")
def get_models():
    return model_status()


@app.put("/models")
def put_models(update: ModelsUpdate):
    unknown = [stage for stage in update.models if stage not in MODEL_STAGES]
    if unknown:
        raise HTTPException(status_code=400, detail=f"Unknown model stage(s): {', '.join(unknown)}. Stages: {', '.join(MODEL_STAGES)}")
    pipeline.ready.wait()
    with pipeline.lock:
        for stage, name in update.models.items():
            name = name.strip()
            if not name:
                continue
            if name == pipeline.models.get(stage) and stage_loaded(stage):
                continue
            load_stage(stage, name)
        save_model_config({stage: pipeline.models[stage] for stage in MODEL_STAGES})
        return model_status()


@app.post("/cancel")
def cancel(request: CancelRequest):
    cancellations.cancel(request.request_id)
    return {"cancelled": request.request_id}


@app.post("/process")
def process_audio(request: ScanRequest):
    if not request.audio_path:
        raise HTTPException(status_code=400, detail="audio_path cannot be empty")
    preset = PRESETS.get(request.preset.lower())
    if preset is None:
        raise HTTPException(status_code=400, detail=f"Unknown preset '{request.preset}'. Presets: {', '.join(PRESETS)}")
    range_start = max(0.0, request.start_seconds or 0.0)
    range_end = request.end_seconds
    if range_end is not None and range_end <= range_start:
        raise HTTPException(status_code=400, detail="end_seconds must be greater than start_seconds")

    resolved_path = resolve_audio_path(request.audio_path)
    if not os.path.exists(resolved_path):
        raise HTTPException(status_code=404, detail=f"File not found: {request.audio_path}")

    logger.info(f"Processing audio: {resolved_path} (original: {request.audio_path}), track {request.audio_track}, "
                f"preset {request.preset}, range {range_start}-{range_end if range_end is not None else 'end'}")

    pipeline.ready.wait()
    try:
        with pipeline.lock:
            cancellations.check(request.request_id)
            return process_locked(request, resolved_path, preset, range_start, range_end)
    except JobCancelled:
        logger.info(f"Request {request.request_id} cancelled by the engine.")
        raise HTTPException(status_code=499, detail="Request was cancelled")
    finally:
        cancellations.forget(request.request_id)


def process_locked(
    request: ScanRequest, resolved_path: str, preset: Dict[str, Any], range_start: float, range_end: Optional[float]
) -> Dict[str, Any]:
    used = {"models": dict(pipeline.models), "preset": request.preset.lower(), "pipeline_version": PIPELINE_VERSION}
    timings: Dict[str, float] = {}
    workdir = tempfile.mkdtemp(prefix="voicescan-")
    try:
        return {**analyse(request, resolved_path, preset, range_start, range_end, workdir, timings), **used}
    finally:
        # Drops the memory map before its file is removed.
        gc.collect()
        shutil.rmtree(workdir, ignore_errors=True)
        if torch.cuda.is_available():
            torch.cuda.empty_cache()
        logger.info("Stage timings (s): " + ", ".join(f"{name} {secs:.2f}" for name, secs in timings.items()))


def analyse(
    request: ScanRequest,
    resolved_path: str,
    preset: Dict[str, Any],
    range_start: float,
    range_end: Optional[float],
    workdir: str,
    timings: Dict[str, float],
) -> Dict[str, Any]:
    rid = request.request_id
    with timed(timings, "decode"):
        audio = decode_audio(resolved_path, request.audio_track, range_start, range_end, workdir)
    if audio.size == 0:
        raise HTTPException(status_code=422, detail=f"No audio decoded from track {request.audio_track} of {request.audio_path}")

    cancellations.check(rid)
    with timed(timings, "vad"):
        speech_chunks = detect_speech(audio, preset, range_start)
    if not speech_chunks:
        logger.info("VAD detected zero speech activity passing onset threshold. Short-circuiting.")
        return {"audio_path": request.audio_path, "has_speech": False, "segments": []}
    logger.info(f"VAD detected {len(speech_chunks)} speech chunk(s).")

    cancellations.check(rid)
    with timed(timings, "diarization"):
        turns = diarize(audio, range_start, rid)

    language = asr_language(pipeline.models["alignment"])
    with timed(timings, "transcription"):
        lines = transcribe(audio, speech_chunks, range_start, preset, language, rid)

    with timed(timings, "alignment"):
        lines = align_and_assign(audio, lines, turns, range_start, language, rid)

    cancellations.check(rid)
    with timed(timings, "moderation"):
        verdicts = moderate([line["text"] for line in lines])

    segments = []
    for line, mod in sorted(zip(lines, verdicts), key=lambda pair: pair[0]["start"]):
        segments.append({
            "start_time_seconds": line["start"],
            "end_time_seconds": line["end"],
            "confidence": round(min(1.0, max(0.01, math.exp(line["avg_logprob"]))), 4),
            "verdict": "Match",
            "reason_flags": [],
            "speaker_label": line["speaker"],
            "transcript": line["text"],
            "is_offensive": mod["is_offensive"],
            "moderation_violations": mod["violations"],
            "moderation_scores": mod["scores"],
        })

    logger.info(f"Processing complete: {len(segments)} speech segments produced.")
    return {"audio_path": request.audio_path, "has_speech": len(segments) > 0, "segments": segments}

if __name__ == "__main__":
    import uvicorn
    uvicorn.run("inference_server:app", host="0.0.0.0", port=54321)
