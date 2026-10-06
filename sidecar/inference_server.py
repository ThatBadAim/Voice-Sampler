import math
import logging
import os
import re
import inspect
from contextlib import asynccontextmanager
from typing import Any, Dict, List, Optional, Tuple

import numpy as np
import torch
from detoxify import Detoxify
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

# Configure logging
logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(name)s: %(message)s")
logger = logging.getLogger("voicescan-sidecar")

# ---------------------------------------------------------------------------
# Tier 1 Pre-filter Configuration
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

# ---------------------------------------------------------------------------
# Pipeline State Holder
# ---------------------------------------------------------------------------

class PipelineState:
    def __init__(self):
        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        self.hf_token = os.getenv("HF_TOKEN")
        self.vad_pipeline = None
        self.diar_model = None
        self.whisper_model = None
        self.batched_whisper = None
        self.align_model = None
        self.align_metadata = None
        self.moderation_model: Optional[Detoxify] = None
        self.initialized = False

pipeline = PipelineState()

def init_pipeline():
    """Initializes the 5-stage inference pipeline on CUDA with bounded VRAM and calibrated VAD."""
    logger.info(f"Initializing VoiceScan inference pipeline on device: {pipeline.device}")
    hf_token = os.getenv("HF_TOKEN")

    # 1. Stage 1: VAD (pyannote/segmentation-3.0 with calibrated binarization parameters)
    try:
        logger.info("Stage 1/5: Loading pyannote/segmentation-3.0 VAD...")
        from pyannote.audio import Model
        from pyannote.audio.pipelines import VoiceActivityDetection
        from pyannote.audio.utils.signal import Binarize
        try:
            vad_m = Model.from_pretrained("pyannote/segmentation-3.0", token=hf_token)
        except TypeError:
            vad_m = Model.from_pretrained("pyannote/segmentation-3.0", use_auth_token=hf_token)

        pipeline.vad_pipeline = VoiceActivityDetection(segmentation=vad_m)
        pipeline.vad_pipeline.onset = 0.50
        pipeline.vad_pipeline.offset = 0.40
        pipeline.vad_pipeline.min_duration_on = 0.35
        pipeline.vad_pipeline.min_duration_off = 0.50
        pipeline.vad_pipeline.initialize()
        pipeline.vad_pipeline._binarize = Binarize(
            onset=0.50,
            offset=0.40,
            min_duration_on=0.35,
            min_duration_off=0.50
        )
        pipeline.vad_pipeline.to(torch.device(pipeline.device))
        logger.info("Stage 1/5: pyannote VAD loaded successfully with binarization hyperparameters.")
    except Exception as e:
        logger.warning(f"Stage 1/5: Could not initialize pyannote VAD: {e}")

    # 2. Stage 2: Diarization (nvidia/Nemotron-3-Diarization via Transformers)
    try:
        logger.info("Stage 2/5: Loading nvidia/Nemotron-3-Diarization via Hugging Face Transformers...")
        from transformers import AutoModelForAudioFrameClassification
        pipeline.diar_model = AutoModelForAudioFrameClassification.from_pretrained(
            "nvidia/Nemotron-3-Diarization",
            trust_remote_code=True,
            token=hf_token
        ).to(pipeline.device)
        pipeline.diar_model.eval()
        logger.info(f"Stage 2/5: Nemotron-3 Diarization loaded successfully on {pipeline.device}.")
    except Exception as e:
        logger.warning(f"Stage 2/5: Could not initialize Nemotron Diarization: {e}")
        pipeline.diar_model = None

    # 3. Stage 3: ASR (faster-whisper large-v3-turbo int8_float16)
    try:
        logger.info("Stage 3/5: Loading faster-whisper large-v3-turbo (int8_float16)...")
        from faster_whisper import WhisperModel
        if torch.cuda.is_available():
            pipeline.whisper_model = WhisperModel(
                "large-v3-turbo",
                device="cuda",
                device_index=0,
                compute_type="int8_float16"
            )
        else:
            pipeline.whisper_model = WhisperModel(
                "large-v3-turbo",
                device="cpu",
                compute_type="int8"
            )
        logger.info("Stage 3/5: Faster-Whisper large-v3-turbo loaded successfully.")
    except Exception as e:
        logger.warning(f"Stage 3/5: Could not initialize Faster-Whisper: {e}")

    # 4. Stage 4: Alignment (whisperx wav2vec2)
    try:
        logger.info("Stage 4/5: Loading whisperx wav2vec2 alignment model...")
        import whisperx
        align_m, align_meta = whisperx.load_align_model(language_code="en", device=pipeline.device)
        pipeline.align_model = align_m
        pipeline.align_metadata = align_meta
        logger.info("Stage 4/5: WhisperX alignment model loaded successfully.")
    except Exception as e:
        logger.warning(f"Stage 4/5: Could not initialize WhisperX alignment: {e}")

    # 5. Stage 5: Moderation (Unitary Detoxify unbiased)
    try:
        logger.info("Stage 5/5: Loading Unitary Detoxify (unbiased)...")
        pipeline.moderation_model = Detoxify("unbiased", device=pipeline.device)
        logger.info(f"Stage 5/5: Unitary Detoxify loaded on {pipeline.device}.")
    except Exception as e:
        logger.warning(f"Stage 5/5: Failed to initialize Detoxify ({e}). Falling back to Tier 1 regex.")
        pipeline.moderation_model = None

    pipeline.initialized = True
    if torch.cuda.is_available():
        torch.cuda.empty_cache()
    logger.info("5-stage inference pipeline initialization completed.")

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
            candidate = os.path.join("/data", rel)
            if os.path.exists(candidate):
                return candidate
            return candidate

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
# Audio Chunk Loading (Bounded Memory)
# ---------------------------------------------------------------------------

def load_audio_slice(audio_path: str, start_sec: float, end_sec: float) -> Optional[np.ndarray]:
    """Loads a bounded time slice of an audio file directly to 16kHz mono float32 without loading the full file."""
    try:
        import torchaudio
        info = torchaudio.info(audio_path)
        sr = info.sample_rate
        offset = max(0, int(start_sec * sr))
        frames = max(1, int((end_sec - start_sec) * sr))
        waveform, sample_rate = torchaudio.load(audio_path, frame_offset=offset, num_frames=frames)
        if waveform.shape[0] > 1:
            waveform = waveform.mean(dim=0, keepdim=True)
        if sample_rate != 16000:
            resampler = torchaudio.transforms.Resample(orig_freq=sample_rate, new_freq=16000)
            waveform = resampler(waveform)
        data = waveform.squeeze().cpu().numpy()
        return np.asarray(data, dtype=np.float32).flatten()
    except Exception as e:
        logger.debug(f"torchaudio slice reading failed ({e}); trying soundfile fallback")
        try:
            import soundfile as sf
            info = sf.info(audio_path)
            sr = info.samplerate
            start_frame = max(0, int(start_sec * sr))
            frames = max(1, int((end_sec - start_sec) * sr))
            data, sample_rate = sf.read(audio_path, start=start_frame, frames=frames, dtype="float32")
            if data.ndim > 1:
                data = data.mean(axis=1)
            if sample_rate != 16000:
                import resampy
                data = resampy.resample(data, sample_rate, 16000)
            return np.asarray(data, dtype=np.float32).flatten()
        except Exception as e2:
            logger.debug(f"soundfile slice reading failed: {e2}")
            return None

def load_audio_waveform(audio_path: str, target_sr: int = 16000) -> Optional[np.ndarray]:
    """Loads full audio file into 16kHz mono float32 numpy array."""
    try:
        import torchaudio
        waveform, sr = torchaudio.load(audio_path)
        if waveform.shape[0] > 1:
            waveform = waveform.mean(dim=0, keepdim=True)
        if sr != target_sr:
            resampler = torchaudio.transforms.Resample(orig_freq=sr, new_freq=target_sr)
            waveform = resampler(waveform)
        data = waveform.squeeze().cpu().numpy()
        return np.asarray(data, dtype=np.float32).flatten()
    except Exception as e:
        logger.debug(f"torchaudio full load failed ({e}); trying soundfile fallback")
        try:
            import soundfile as sf
            data, sr = sf.read(audio_path, dtype="float32")
            if data.ndim > 1:
                data = data.mean(axis=1)
            if sr != target_sr:
                import resampy
                data = resampy.resample(data, sr, target_sr)
            return np.asarray(data, dtype=np.float32).flatten()
        except Exception as e2:
            logger.debug(f"soundfile full load failed: {e2}")
            return None

# ---------------------------------------------------------------------------
# VAD & Speech Chunking with Explicit Hysteresis Binarization
# ---------------------------------------------------------------------------

def extract_speech_chunks(
    audio_path: str,
    vad_pipeline: Any,
    onset: float = 0.50,
    offset: float = 0.40,
    min_duration_on: float = 0.35,
    min_duration_off: float = 0.50,
    max_chunk_size: float = 10.0
) -> List[Tuple[float, float]]:
    """
    Extracts speech intervals using VAD with explicit binarization hysteresis.
    - onset: 0.50 (minimum speech probability to trigger voice start)
    - offset: 0.40 (probability threshold to end voice utterance)
    - min_duration_on: 0.35s (reject isolated acoustic transients, clicks, and background taps)
    - min_duration_off: 0.50s (split sentences if silence or music exceeds 500ms)
    - Merges contiguous speech fragments into reasonable utterance windows (8.0s - 12.0s).
    - If the entire track contains NO speech passing onset, returns an empty list.
    """
    if vad_pipeline is None:
        logger.warning("VAD pipeline unavailable.")
        return []

    try:
        vad_result = vad_pipeline(audio_path)
        timeline = vad_result.get_timeline().support()
    except Exception as e:
        logger.error(f"VAD execution failed on {audio_path}: {e}")
        return []

    if len(timeline) == 0:
        return []

    # Filter intervals by min_duration_on (0.35s)
    raw_segments: List[Tuple[float, float]] = []
    for seg in timeline:
        dur = seg.end - seg.start
        if dur >= min_duration_on:
            raw_segments.append((float(seg.start), float(seg.end)))

    if not raw_segments:
        return []

    # Merge contiguous fragments into reasonable utterance windows (max chunk size: 8.0s to 12.0s)
    # Split sentences if silence exceeds min_duration_off (0.50s)
    chunks: List[Tuple[float, float]] = []
    current_start, current_end = raw_segments[0]

    for next_start, next_end in raw_segments[1:]:
        silence_gap = next_start - current_end
        combined_dur = next_end - current_start

        if silence_gap <= min_duration_off and combined_dur <= max_chunk_size:
            current_end = next_end
        else:
            # Check if current_end - current_start exceeds max_chunk_size
            if (current_end - current_start) > 12.0:
                t = current_start
                while t < current_end:
                    t_end = min(t + max_chunk_size, current_end)
                    chunks.append((round(t, 3), round(t_end, 3)))
                    t = t_end
            else:
                chunks.append((round(current_start, 3), round(current_end, 3)))
            current_start, current_end = next_start, next_end

    # Flush final segment
    if (current_end - current_start) > 12.0:
        t = current_start
        while t < current_end:
            t_end = min(t + max_chunk_size, current_end)
            chunks.append((round(t, 3), round(t_end, 3)))
            t = t_end
    else:
        chunks.append((round(current_start, 3), round(current_end, 3)))

    return chunks

# ---------------------------------------------------------------------------
# Forced Alignment per Turn Helper
# ---------------------------------------------------------------------------

def align_turn_segment(
    audio_path: str,
    seg_text: str,
    start_sec: float,
    end_sec: float,
    align_model: Any,
    align_metadata: Any,
    device: str
) -> Tuple[float, float]:
    """Runs forced alignment on a single speaker turn slice, strictly bounding memory."""
    if not align_model or not align_metadata or not seg_text.strip():
        return start_sec, end_sec

    try:
        import whisperx
        audio_slice = load_audio_slice(audio_path, start_sec, end_sec)
        if audio_slice is None or len(audio_slice) == 0:
            return start_sec, end_sec

        turn_duration = max(0.1, end_sec - start_sec)
        transcript_item = [{"text": seg_text.strip(), "start": 0.0, "end": turn_duration}]

        aligned = whisperx.align(
            transcript_item,
            align_model,
            align_metadata,
            audio_slice,
            device,
            return_char_alignments=False
        )

        segments = aligned.get("segments", [])
        if segments:
            words = segments[0].get("words", [])
            valid_words = [w for w in words if "start" in w and "end" in w]
            if valid_words:
                aligned_start = start_sec + valid_words[0]["start"]
                aligned_end = start_sec + valid_words[-1]["end"]
                aligned_start = max(start_sec - 0.2, aligned_start)
                aligned_end = min(end_sec + 0.2, max(aligned_start + 0.1, aligned_end))
                return round(aligned_start, 3), round(aligned_end, 3)
    except Exception as e:
        logger.debug(f"Forced alignment error on segment {start_sec:.2f}-{end_sec:.2f}: {e}")

    return start_sec, end_sec

# ---------------------------------------------------------------------------
# Moderation Evaluator
# ---------------------------------------------------------------------------

def evaluate_moderation(text: str, threshold: float = 0.5) -> Dict[str, Any]:
    """Runs multi-label moderation inference with regex fallback."""
    cleaned = text.strip()
    if not cleaned:
        return {"is_offensive": False, "violations": [], "scores": {}}

    if pipeline.moderation_model is None:
        match = TIER1_REGEX.search(cleaned)
        if match:
            matched_term = match.group(0).lower()
            if any(k in matched_term for k in ["kill", "murder", "shoot", "stab", "slit", "die", "bomb", "terrorist", "massacre"]):
                return {"is_offensive": True, "violations": ["threat"], "scores": {"threat": 1.0}}
            if any(k in matched_term for k in ["nigg", "fag", "kike", "spic", "chink", "retard", "subhuman", "mongoloid"]):
                return {"is_offensive": True, "violations": ["identity_attack"], "scores": {"identity_attack": 1.0}}
            if any(k in matched_term for k in ["rape", "molest", "pedophile"]):
                return {"is_offensive": True, "violations": ["sexual_explicit"], "scores": {"sexual_explicit": 1.0}}
            return {"is_offensive": True, "violations": ["insult"], "scores": {"insult": 1.0}}
        return {"is_offensive": False, "violations": [], "scores": {}}

    try:
        scores = pipeline.moderation_model.predict(cleaned)
        flagged = [label for label, score in scores.items() if score >= threshold]

        # Tier 1 regex fast check as a safety net
        match = TIER1_REGEX.search(cleaned)
        if match and not flagged:
            flagged = ["toxic_content"]

        return {
            "is_offensive": len(flagged) > 0,
            "violations": flagged,
            "scores": {k: round(float(v), 3) for k, v in scores.items()}
        }
    except Exception as e:
        logger.warning(f"Detoxify inference error: {e}")
        match = TIER1_REGEX.search(cleaned)
        if match:
            return {"is_offensive": True, "violations": ["toxic_content"], "scores": {}}
        return {"is_offensive": False, "violations": [], "scores": {}}

# ---------------------------------------------------------------------------
# Speaker Label Formatting & Matching
# ---------------------------------------------------------------------------

def find_speaker_for_interval(start: float, end: float, speaker_turns: List[Dict[str, Any]]) -> str:
    mid = (start + end) / 2.0
    for turn in speaker_turns:
        if turn["start"] <= mid <= turn["end"]:
            return turn["speaker"]
    if speaker_turns:
        closest = min(speaker_turns, key=lambda t: abs((t["start"] + t["end"]) / 2.0 - mid))
        return closest["speaker"]
    return "SPEAKER_00"

# ---------------------------------------------------------------------------
# FastAPI Application & Lifespan
# ---------------------------------------------------------------------------

@asynccontextmanager
async def lifespan(app: FastAPI):
    init_pipeline()
    yield
    if torch.cuda.is_available():
        torch.cuda.empty_cache()

app = FastAPI(title="VoiceScan Inference Worker", lifespan=lifespan)

class ScanRequest(BaseModel):
    audio_path: str

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
        "status": "ready",
        "cuda_available": cuda_ok,
        "device_name": device_name,
        "vram_free_gb": vram_free,
        "vram_total_gb": vram_total
    }

@app.post("/process")
def process_audio(request: ScanRequest):
    if not request.audio_path:
        raise HTTPException(status_code=400, detail="audio_path cannot be empty")

    resolved_path = resolve_audio_path(request.audio_path)
    if not os.path.exists(resolved_path):
        raise HTTPException(status_code=404, detail=f"File not found: {request.audio_path}")

    logger.info(f"Processing audio: {resolved_path} (original: {request.audio_path})")

    if not pipeline.initialized:
        init_pipeline()

    try:
        # Stage 1: VAD & Speech Utterance Chunking
        # Cleanly separates speech from silence/music using hysteresis thresholding
        speech_chunks = extract_speech_chunks(
            resolved_path,
            pipeline.vad_pipeline,
            onset=0.50,
            offset=0.40,
            min_duration_on=0.35,
            min_duration_off=0.50,
            max_chunk_size=10.0
        )

        if not speech_chunks:
            logger.info("VAD detected zero speech activity passing onset threshold. Short-circuiting.")
            return {
                "audio_path": request.audio_path,
                "has_speech": False,
                "segments": []
            }

        logger.info(f"VAD detected {len(speech_chunks)} bounded speech chunk(s).")

        # Stage 2: Diarization (Nemotron-3 Diarization via Transformers)
        speaker_turns = []
        if pipeline.diar_model is not None:
            try:
                waveform = load_audio_waveform(resolved_path)
                if waveform is not None and len(waveform) > 0:
                    inputs = torch.tensor(waveform, dtype=torch.float32).unsqueeze(0).to(pipeline.device)
                    with torch.no_grad():
                        outputs = pipeline.diar_model(inputs)

                    logits = outputs.logits if hasattr(outputs, "logits") else outputs
                    batch_logits = logits[0] if logits.dim() == 3 else logits

                    audio_duration = len(waveform) / 16000.0
                    num_frames = batch_logits.shape[0]
                    num_classes = batch_logits.shape[1] if batch_logits.dim() > 1 else 1
                    time_per_frame = audio_duration / max(1, num_frames)

                    probs = torch.sigmoid(batch_logits).cpu().numpy()
                    for spk_idx in range(num_classes):
                        active = probs[:, spk_idx] > 0.5
                        if not np.any(active):
                            continue
                        diff = np.diff(active.astype(np.int8))
                        starts = np.where(diff == 1)[0] + 1
                        if active[0]:
                            starts = np.r_[0, starts]
                        ends = np.where(diff == -1)[0] + 1
                        if active[-1]:
                            ends = np.r_[ends, len(active)]

                        spk_label = f"SPEAKER_{spk_idx:02d}"
                        for s_idx, e_idx in zip(starts, ends):
                            start_time = s_idx * time_per_frame
                            end_time = e_idx * time_per_frame
                            if end_time - start_time >= 0.1:
                                speaker_turns.append({
                                    "start": round(start_time, 2),
                                    "end": round(end_time, 2),
                                    "speaker": spk_label
                                })

                    if not speaker_turns and batch_logits.dim() > 1:
                        preds = torch.argmax(batch_logits, dim=-1).cpu().numpy()
                        current_spk = None
                        start_f = 0
                        for f_idx, pred_spk in enumerate(preds):
                            if pred_spk != current_spk:
                                if current_spk is not None:
                                    speaker_turns.append({
                                        "start": round(start_f * time_per_frame, 2),
                                        "end": round(f_idx * time_per_frame, 2),
                                        "speaker": f"SPEAKER_{current_spk:02d}"
                                    })
                                current_spk = pred_spk
                                start_f = f_idx
                        if current_spk is not None:
                            speaker_turns.append({
                                "start": round(start_f * time_per_frame, 2),
                                "end": round(len(preds) * time_per_frame, 2),
                                "speaker": f"SPEAKER_{current_spk:02d}"
                            })

                    speaker_turns.sort(key=lambda x: x["start"])
                    logger.info(f"Nemotron Diarization produced {len(speaker_turns)} speaker intervals.")
            except Exception as e:
                logger.warning(f"Nemotron Diarization error ({e}); defaulting to SPEAKER_00.")

        # Stage 3: ASR Inference on Validated Speech Chunks
        if pipeline.whisper_model is None:
            raise HTTPException(status_code=500, detail="Faster-Whisper model is not initialized.")

        # Dynamic parameter resolution for faster-whisper (log_prob_threshold vs logprob_threshold)
        whisper_sig = inspect.signature(pipeline.whisper_model.transcribe).parameters
        transcribe_kwargs = {
            "beam_size": 5,
            "word_timestamps": True,
            "condition_on_previous_text": False,
            "no_speech_threshold": 0.6,
            "temperature": 0.0,
        }
        if "log_prob_threshold" in whisper_sig:
            transcribe_kwargs["log_prob_threshold"] = -1.0
        elif "logprob_threshold" in whisper_sig:
            transcribe_kwargs["logprob_threshold"] = -1.0

        result_segments = []
        for chunk_start, chunk_end in speech_chunks:
            chunk_duration = chunk_end - chunk_start
            if chunk_duration < 0.2:
                continue

            audio_segment = load_audio_slice(resolved_path, chunk_start, chunk_end)
            if audio_segment is None or len(audio_segment) < int(0.2 * 16000):
                continue

            try:
                segments_gen, _ = pipeline.whisper_model.transcribe(
                    audio_segment,
                    **transcribe_kwargs
                )
                chunk_segments = list(segments_gen)
            except Exception as e:
                logger.warning(f"Whisper transcription error on chunk [{chunk_start}-{chunk_end}]: {e}")
                continue

            for seg in chunk_segments:
                text = seg.text.strip()
                if not text:
                    continue

                # Filter pure non-speech / hallucination tokens
                if text.lower() in ["[music]", "(music)", "[applause]", "(applause)", "[laughter]", "(laughter)"]:
                    continue

                raw_start = round(chunk_start + float(seg.start), 3)
                raw_end = round(chunk_start + float(seg.end), 3)
                if raw_end <= raw_start or (raw_end - raw_start) < 0.1:
                    continue

                speaker = find_speaker_for_interval(raw_start, raw_end, speaker_turns)

                # Stage 4: Forced alignment on utterance
                final_start, final_end = align_turn_segment(
                    resolved_path,
                    text,
                    raw_start,
                    raw_end,
                    pipeline.align_model,
                    pipeline.align_metadata,
                    pipeline.device
                )

                # Stage 5: Multi-label moderation via Detoxify
                mod_result = evaluate_moderation(text, threshold=0.5)

                avg_logprob = getattr(seg, "avg_logprob", -0.1)
                conf = min(1.0, max(0.01, math.exp(avg_logprob)))

                result_segments.append({
                    "start_time_seconds": round(float(final_start), 3),
                    "end_time_seconds": round(float(final_end), 3),
                    "confidence": round(float(conf), 4),
                    "verdict": "Match",
                    "reason_flags": [],
                    "speaker_label": speaker,
                    "transcript": text,
                    "is_offensive": mod_result["is_offensive"],
                    "moderation_violations": mod_result["violations"],
                    "moderation_scores": mod_result["scores"]
                })

        logger.info(f"Processing complete: {len(result_segments)} speech segments produced.")
        return {
            "audio_path": request.audio_path,
            "has_speech": len(result_segments) > 0,
            "segments": result_segments
        }

    finally:
        if torch.cuda.is_available():
            torch.cuda.empty_cache()

if __name__ == "__main__":
    import uvicorn
    uvicorn.run("inference_server:app", host="0.0.0.0", port=54321)
