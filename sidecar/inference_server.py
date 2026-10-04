#!/usr/bin/env python3
"""VoiceScan Standalone Inference Server

High-performance offline audio analysis service hosting:
1. Voice Activity Detection: pyannote/segmentation-3.0
2. Speaker Diarization: nvidia/Nemotron-3-Diarization (SortformerEncLabelModel)
3. Speech Transcription: faster-whisper (large-v3, bfloat16)
4. Word-to-Speaker Alignment
5. Content Moderation: meta-llama/Llama-Guard-3-1B
"""

from __future__ import annotations

import logging
import os
import re
import sys
from contextlib import asynccontextmanager
from typing import Any, Dict, List, Optional, Tuple

import torch
import uvicorn
from fastapi import FastAPI, HTTPException, status
from pydantic import BaseModel, Field

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
)
logger = logging.getLogger("voicescan-sidecar")


# ---------------------------------------------------------------------------
# Device and Precision Helper
# ---------------------------------------------------------------------------

def get_device_info() -> Tuple[bool, str, str, bool]:
    """Inspects hardware acceleration availability and BF16 Tensor Core support."""
    cuda_available = torch.cuda.is_available()
    if cuda_available:
        device_name = torch.cuda.get_device_name(0)
        device = "cuda"
        # Check for Ampere/Ada/Hopper or newer native BF16 tensor core support
        use_bf16 = torch.cuda.is_bf16_supported()
    else:
        device_name = "CPU"
        device = "cpu"
        use_bf16 = False
    return cuda_available, device_name, device, use_bf16


CUDA_AVAILABLE, DEVICE_NAME, INFERENCE_DEVICE, USE_BF16 = get_device_info()
TORCH_DTYPE = torch.bfloat16 if USE_BF16 else (torch.float16 if CUDA_AVAILABLE else torch.float32)
WHISPER_COMPUTE_TYPE = "bfloat16" if USE_BF16 else ("float16" if CUDA_AVAILABLE else "float32")

logger.info(
    "Sidecar device configured: %s (CUDA=%s, BF16=%s, ComputeType=%s)",
    DEVICE_NAME,
    CUDA_AVAILABLE,
    USE_BF16,
    WHISPER_COMPUTE_TYPE,
)


# ---------------------------------------------------------------------------
# API Data Models (Contract aligned with VoiceScan C# DetectedSegment)
# ---------------------------------------------------------------------------

class HealthResponse(BaseModel):
    status: str = "ready"
    cuda_available: bool
    device_name: str


class ProcessRequest(BaseModel):
    audio_path: str = Field(..., description="Path to the audio or video file to process")


class DetectedSegmentModel(BaseModel):
    start_time_seconds: float
    end_time_seconds: float
    confidence: float
    verdict: str = "Match"
    reason_flags: List[str] = Field(default_factory=list)
    speaker_label: Optional[str] = None
    transcript: Optional[str] = None
    is_offensive: bool = False
    moderation_violations: List[str] = Field(default_factory=list)


class ProcessResponse(BaseModel):
    has_speech: bool
    segments: List[DetectedSegmentModel]


# ---------------------------------------------------------------------------
# Model Pipeline Manager
# ---------------------------------------------------------------------------

class ModelPipelineManager:
    """Manages lifecycle, lazy initialization, and inference of ML models."""

    def __init__(self) -> None:
        self.vad_model: Any = None
        self.diarizer_model: Any = None
        self.asr_model: Any = None
        self.moderation_model: Any = None
        self.moderation_tokenizer: Any = None

    def get_vad(self) -> Any:
        if self.vad_model is None:
            logger.info("Loading Pyannote VAD model (pyannote/segmentation-3.0)...")
            from pyannote.audio import Model
            model = Model.from_pretrained("pyannote/segmentation-3.0")
            if CUDA_AVAILABLE:
                model = model.to(torch.device("cuda"))
            self.vad_model = model
        return self.vad_model

    def get_diarizer(self) -> Any:
        if self.diarizer_model is None:
            logger.info("Loading Sortformer diarization model (nvidia/Nemotron-3-Diarization)...")
            try:
                from nemo.collections.asr.models import SortformerEncLabelModel
                diarizer = SortformerEncLabelModel.from_pretrained("nvidia/Nemotron-3-Diarization")
                if CUDA_AVAILABLE:
                    diarizer = diarizer.to(torch.device("cuda"))
                    if USE_BF16:
                        diarizer = diarizer.to(dtype=torch.bfloat16)
                diarizer.eval()
                self.diarizer_model = diarizer
            except Exception as exc:
                logger.warning("SortformerEncLabelModel load fallback: %s", exc)
                self.diarizer_model = None
        return self.diarizer_model

    def get_asr(self) -> Any:
        if self.asr_model is None:
            logger.info("Loading Faster-Whisper ASR model (large-v3, %s)...", WHISPER_COMPUTE_TYPE)
            from faster_whisper import WhisperModel
            self.asr_model = WhisperModel(
                "large-v3",
                device=INFERENCE_DEVICE,
                compute_type=WHISPER_COMPUTE_TYPE,
            )
        return self.asr_model

    def get_moderation(self) -> Tuple[Any, Any]:
        if self.moderation_model is None or self.moderation_tokenizer is None:
            logger.info("Loading Llama-Guard moderation model (meta-llama/Llama-Guard-3-1B)...")
            from transformers import AutoModelForCausalLM, AutoTokenizer
            model_id = "meta-llama/Llama-Guard-3-1B"
            tokenizer = AutoTokenizer.from_pretrained(model_id)
            model = AutoModelForCausalLM.from_pretrained(
                model_id,
                torch_dtype=TORCH_DTYPE,
                device_map="auto" if CUDA_AVAILABLE else None,
            )
            model.eval()
            self.moderation_tokenizer = tokenizer
            self.moderation_model = model
        return self.moderation_model, self.moderation_tokenizer


pipeline_manager = ModelPipelineManager()


# ---------------------------------------------------------------------------
# Pipeline Steps
# ---------------------------------------------------------------------------

def run_vad_step(audio_path: str) -> List[Tuple[float, float]]:
    """Step 1: Check voice activity using pyannote/segmentation-3.0."""
    logger.info("Running Step 1: Voice Activity Detection for %s", audio_path)
    try:
        from pyannote.audio import Inference
        from pyannote.core import Segment

        model = pipeline_manager.get_vad()
        inference = Inference(model, step=2.5)
        segmentation = inference(audio_path)

        speech_intervals: List[Tuple[float, float]] = []
        binarized = segmentation.binarize(onset=0.5, offset=0.5, min_duration_on=0.2, min_duration_off=0.2)
        for segment in binarized.itersegments():
            speech_intervals.append((round(segment.start, 3), round(segment.end, 3)))

        return speech_intervals
    except Exception as exc:
        logger.warning("VAD execution encountered error or fallback needed: %s", exc)
        import soundfile as sf
        info = sf.info(audio_path)
        if info.duration > 0:
            return [(0.0, round(info.duration, 3))]
        return []


def run_diarization_step(audio_path: str) -> List[Dict[str, Any]]:
    """Step 2: Speaker Diarization using Sortformer (nvidia/Nemotron-3-Diarization)."""
    logger.info("Running Step 2: Speaker Diarization for %s", audio_path)
    speaker_turns: List[Dict[str, Any]] = []
    diarizer = pipeline_manager.get_diarizer()

    if diarizer is not None:
        try:
            output = diarizer.forward(audio_path) if hasattr(diarizer, "forward") else None
            if hasattr(diarizer, "diarize"):
                output = diarizer.diarize(audio_path)

            if isinstance(output, list):
                for turn in output:
                    speaker_turns.append({
                        "speaker": getattr(turn, "speaker", str(turn.get("speaker", "SPEAKER_00"))),
                        "start": float(getattr(turn, "start", turn.get("start", 0.0))),
                        "end": float(getattr(turn, "end", turn.get("end", 0.0))),
                    })
        except Exception as exc:
            logger.warning("Sortformer inference error: %s", exc)

    if not speaker_turns:
        import soundfile as sf
        info = sf.info(audio_path)
        speaker_turns.append({
            "speaker": "SPEAKER_00",
            "start": 0.0,
            "end": round(info.duration, 3),
        })

    return speaker_turns


def run_asr_step(audio_path: str) -> List[Dict[str, Any]]:
    """Step 3: ASR transcription using faster-whisper (large-v3, bfloat16)."""
    logger.info("Running Step 3: ASR Transcription for %s", audio_path)
    asr = pipeline_manager.get_asr()
    segments_gen, _ = asr.transcribe(audio_path, word_timestamps=True)

    asr_results: List[Dict[str, Any]] = []
    for s in segments_gen:
        words = []
        if s.words:
            for w in s.words:
                words.append({
                    "start": w.start,
                    "end": w.end,
                    "word": w.word,
                    "probability": w.probability,
                })
        asr_results.append({
            "start": round(s.start, 3),
            "end": round(s.end, 3),
            "text": s.text.strip(),
            "confidence": round(float(s.avg_logprob), 4) if hasattr(s, "avg_logprob") else 0.9,
            "words": words,
        })

    return asr_results


def run_alignment_step(
    speaker_turns: List[Dict[str, Any]],
    asr_results: List[Dict[str, Any]],
) -> List[Dict[str, Any]]:
    """Step 4: Align transcribed words to speaker turn timestamps."""
    logger.info("Running Step 4: Aligning words to speaker turns")
    aligned_segments: List[Dict[str, Any]] = []

    all_words: List[Dict[str, Any]] = []
    for seg in asr_results:
        all_words.extend(seg.get("words", []))

    if all_words and speaker_turns:
        for turn in speaker_turns:
            t_start = turn["start"]
            t_end = turn["end"]
            speaker = turn["speaker"]

            turn_words = [
                w for w in all_words
                if t_start <= ((w["start"] + w["end"]) / 2.0) <= t_end
            ]

            if turn_words:
                segment_text = " ".join(w["word"].strip() for w in turn_words).strip()
                avg_prob = sum(w["probability"] for w in turn_words) / len(turn_words)
                aligned_segments.append({
                    "start_time_seconds": round(turn_words[0]["start"], 3),
                    "end_time_seconds": round(turn_words[-1]["end"], 3),
                    "confidence": round(avg_prob, 4),
                    "speaker_label": speaker,
                    "transcript": segment_text,
                })
            else:
                aligned_segments.append({
                    "start_time_seconds": round(t_start, 3),
                    "end_time_seconds": round(t_end, 3),
                    "confidence": 0.85,
                    "speaker_label": speaker,
                    "transcript": "",
                })
    else:
        for seg in asr_results:
            seg_mid = (seg["start"] + seg["end"]) / 2.0
            matched_speaker = "SPEAKER_00"
            for turn in speaker_turns:
                if turn["start"] <= seg_mid <= turn["end"]:
                    matched_speaker = turn["speaker"]
                    break

            aligned_segments.append({
                "start_time_seconds": seg["start"],
                "end_time_seconds": seg["end"],
                "confidence": 0.90,
                "speaker_label": matched_speaker,
                "transcript": seg["text"],
            })

    return aligned_segments


def run_moderation_step(transcript: str) -> Tuple[bool, List[str]]:
    """Step 5: Content moderation evaluation using meta-llama/Llama-Guard-3-1B."""
    if not transcript or not transcript.strip():
        return False, []

    logger.debug("Running Step 5: Content Moderation for utterance: '%s'", transcript)
    try:
        model, tokenizer = pipeline_manager.get_moderation()
        chat = [{"role": "user", "content": transcript}]
        inputs = tokenizer.apply_chat_template(chat, return_tensors="pt")
        if CUDA_AVAILABLE:
            inputs = inputs.to("cuda")

        with torch.no_grad():
            output_tokens = model.generate(
                inputs,
                max_new_tokens=20,
                pad_token_id=tokenizer.eos_token_id,
            )

        response = tokenizer.decode(
            output_tokens[0][inputs.shape[1]:],
            skip_special_tokens=True,
        ).strip()

        lines = [line.strip() for line in response.splitlines() if line.strip()]
        if lines and lines[0].lower() == "unsafe":
            violations = []
            if len(lines) > 1:
                raw_violations = re.split(r"[,\s]+", " ".join(lines[1:]))
                violations = [v for v in raw_violations if v]
            else:
                violations = ["unsafe"]
            return True, violations
        return False, []
    except Exception as exc:
        logger.warning("Moderation evaluation error or fallback: %s", exc)
        return False, []


# ---------------------------------------------------------------------------
# FastAPI Application & Endpoints
# ---------------------------------------------------------------------------

@asynccontextmanager
async def lifespan(app: FastAPI):
    logger.info("VoiceScan Sidecar initialized and ready on 127.0.0.1:54321")
    yield
    logger.info("VoiceScan Sidecar shutting down")


app = FastAPI(
    title="VoiceScan Inference Sidecar",
    description="Offline speech transcription, diarization, and moderation service",
    version="1.1.0",
    lifespan=lifespan,
)


@app.get("/health", response_model=HealthResponse)
def health_check() -> HealthResponse:
    return HealthResponse(
        status="ready",
        cuda_available=CUDA_AVAILABLE,
        device_name=DEVICE_NAME,
    )


@app.post("/process", response_model=ProcessResponse)
def process_audio(request: ProcessRequest) -> ProcessResponse:
    audio_path = os.path.abspath(request.audio_path)
    if not os.path.exists(audio_path):
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Audio file not found at: {audio_path}",
        )

    # Step 1: Voice Activity Detection
    speech_intervals = run_vad_step(audio_path)
    if not speech_intervals:
        logger.info("VAD detected no speech activity in %s", audio_path)
        return ProcessResponse(has_speech=False, segments=[])

    # Step 2: Diarization
    speaker_turns = run_diarization_step(audio_path)

    # Step 3: ASR Transcription
    asr_results = run_asr_step(audio_path)

    # Step 4: Word-to-Speaker Alignment
    aligned_segments = run_alignment_step(speaker_turns, asr_results)

    # Step 5: Content Moderation & Segment Assembly
    detected_segments: List[DetectedSegmentModel] = []
    for seg in aligned_segments:
        transcript = seg.get("transcript", "")
        is_offensive, violations = run_moderation_step(transcript)

        detected_segments.append(
            DetectedSegmentModel(
                start_time_seconds=seg["start_time_seconds"],
                end_time_seconds=seg["end_time_seconds"],
                confidence=seg.get("confidence", 0.90),
                verdict="Match",
                reason_flags=[],
                speaker_label=seg.get("speaker_label"),
                transcript=transcript if transcript else None,
                is_offensive=is_offensive,
                moderation_violations=violations,
            )
        )

    return ProcessResponse(has_speech=True, segments=detected_segments)


if __name__ == "__main__":
    uvicorn.run("inference_server:app", host="127.0.0.1", port=54321, reload=False)
