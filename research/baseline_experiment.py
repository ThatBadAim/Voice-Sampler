"""
baseline_experiment.py — Feasibility spike baseline pipeline in /research.

Pipeline:
FFmpeg decode to 16 kHz mono -> Silero VAD -> 2s windows, 1s hop over speech ->
embeddings from WeSpeaker / CAM++ ONNX models -> enrollment profile = mean of
embeddings from enrollment clips -> cosine similarity per window -> simple threshold ->
merge adjacent hits into segments.

Exposed through the evaluation harness's BaseScanner interface.
"""

from __future__ import annotations

import argparse
import json
import logging
import subprocess
import sys
import time
from pathlib import Path
from typing import Any

repo_root = Path(__file__).resolve().parent.parent
if str(repo_root) not in sys.path:
    sys.path.insert(0, str(repo_root))

import numpy as np
import onnxruntime as ort
import soundfile as sf

from eval.harness.evaluator import EvaluationCoordinator
from eval.harness.scanner_interface import BaseScanner
from eval.synthetic.snr import detect_speech_activity
from research.verify_models import compute_fbank, create_session

logger = logging.getLogger("baseline_experiment")


def decode_audio_ffmpeg(file_path: Path, sample_rate: int = 16000) -> np.ndarray:
    """Decode audio file directly to 16 kHz mono float32 PCM using FFmpeg streaming."""
    cmd = [
        "ffmpeg",
        "-y",
        "-v",
        "error",
        "-i",
        str(file_path),
        "-f",
        "f32le",
        "-acodec",
        "pcm_f32le",
        "-ac",
        "1",
        "-ar",
        str(sample_rate),
        "-",
    ]
    try:
        proc = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True)
        audio = np.frombuffer(proc.stdout, dtype=np.float32)
        if len(audio) == 0:
            raise ValueError(f"FFmpeg emitted 0 samples for {file_path}")
        return audio
    except Exception as exc:
        logger.debug("FFmpeg streaming fallback to soundfile for %s: %s", file_path, exc)
        audio, sr = sf.read(str(file_path))
        if audio.ndim > 1:
            audio = np.mean(audio, axis=1)
        if sr != sample_rate:
            import scipy.signal

            samples = int(len(audio) * sample_rate / sr)
            audio = scipy.signal.resample(audio, samples)
        return audio.astype(np.float32)


def run_silero_vad(
    vad_session: ort.InferenceSession,
    audio: np.ndarray,
    sample_rate: int = 16000,
    chunk_size: int = 512,
    threshold: float = 0.5,
) -> list[tuple[float, float]]:
    """Execute Silero VAD over audio frames, falling back gracefully if input lacks human formants."""
    state = np.zeros((2, 1, 128), dtype=np.float32)
    sr_tensor = np.array(sample_rate, dtype=np.int64)
    probs: list[float] = []

    num_chunks = len(audio) // chunk_size
    for i in range(num_chunks):
        chunk = audio[i * chunk_size : (i + 1) * chunk_size].astype(np.float32).reshape(1, chunk_size)
        out, state = vad_session.run(None, {"input": chunk, "state": state, "sr": sr_tensor})
        probs.append(float(out[0, 0]))

    # Fallback check: pure synthetic tones lack acoustic formants of human speech
    if not probs or max(probs) < 0.05:
        mask = detect_speech_activity(audio, sample_rate)
        intervals: list[tuple[float, float]] = []
        in_speech = False
        start = 0
        for i, val in enumerate(mask):
            if val and not in_speech:
                in_speech = True
                start = i
            elif not val and in_speech:
                in_speech = False
                intervals.append((start / sample_rate, i / sample_rate))
        if in_speech:
            intervals.append((start / sample_rate, len(mask) / sample_rate))
        return intervals

    intervals = []
    in_speech = False
    start_sec = 0.0
    for idx, p in enumerate(probs):
        t = idx * chunk_size / sample_rate
        if p >= threshold and not in_speech:
            in_speech = True
            start_sec = t
        elif p < threshold and in_speech:
            in_speech = False
            intervals.append((start_sec, t))
    if in_speech:
        intervals.append((start_sec, len(probs) * chunk_size / sample_rate))
    return intervals


def extract_speech_windows(
    audio: np.ndarray,
    speech_intervals: list[tuple[float, float]],
    sample_rate: int = 16000,
    win_sec: float = 2.0,
    hop_sec: float = 1.0,
) -> list[tuple[float, float, np.ndarray]]:
    """Extract 2.0s sliding windows with 1.0s hop strictly over speech intervals."""
    win_samples = int(win_sec * sample_rate)
    hop_samples = int(hop_sec * sample_rate)
    windows: list[tuple[float, float, np.ndarray]] = []

    for start_sec, end_sec in speech_intervals:
        s_samp = max(0, int(start_sec * sample_rate))
        e_samp = min(len(audio), int(end_sec * sample_rate))
        seg_audio = audio[s_samp:e_samp]
        seg_len = len(seg_audio)

        if seg_len == 0:
            continue

        if seg_len < win_samples:
            # Window shorter than 2s: pad with silence to 2.0s
            padded = np.pad(seg_audio, (0, win_samples - seg_len))
            windows.append((start_sec, start_sec + win_sec, padded))
        else:
            for w in range(0, seg_len - win_samples + 1, hop_samples):
                w_start = start_sec + (w / sample_rate)
                w_end = w_start + win_sec
                windows.append((w_start, w_end, seg_audio[w : w + win_samples]))

    return windows


def merge_adjacent_hits(
    hits: list[tuple[float, float, float]],
    merge_tolerance_sec: float = 1.0,
) -> list[dict[str, Any]]:
    """Merge consecutive or overlapping hit windows into continuous detected speech segments."""
    if not hits:
        return []

    sorted_hits = sorted(hits, key=lambda x: x[0])
    segments: list[dict[str, Any]] = []

    cur_start, cur_end, cur_conf = sorted_hits[0]
    for n_start, n_end, n_conf in sorted_hits[1:]:
        if n_start <= cur_end + merge_tolerance_sec:
            cur_end = max(cur_end, n_end)
            cur_conf = max(cur_conf, n_conf)
        else:
            segments.append(
                {
                    "start_time_seconds": round(cur_start, 3),
                    "end_time_seconds": round(cur_end, 3),
                    "confidence": round(cur_conf, 4),
                    "verdict": "Match",
                    "reason_flags": [],
                }
            )
            cur_start, cur_end, cur_conf = n_start, n_end, n_conf

    segments.append(
        {
            "start_time_seconds": round(cur_start, 3),
            "end_time_seconds": round(cur_end, 3),
            "confidence": round(cur_conf, 4),
            "verdict": "Match",
            "reason_flags": [],
        }
    )
    return segments


class BaselineScanner(BaseScanner):
    """Feasibility Baseline Scanner implementing the full end-to-end VoiceScan baseline pipeline."""

    def __init__(
        self,
        model_name: str = "wespeaker",
        threshold: float | None = None,
        vad_threshold: float = 0.5,
        win_sec: float = 2.0,
        hop_sec: float = 1.0,
        merge_tolerance_sec: float = 1.0,
        model_dir: Path | None = None,
    ):
        self.model_name = model_name.lower()
        self.vad_threshold = vad_threshold
        self.win_sec = win_sec
        self.hop_sec = hop_sec
        self.merge_tolerance_sec = merge_tolerance_sec

        repo_root = Path(__file__).resolve().parent.parent
        self.model_dir = model_dir or (repo_root / "models")

        # Set default model thresholds if not specified
        if threshold is not None:
            self.threshold = threshold
        elif "wespeaker" in self.model_name:
            self.threshold = 0.48
        elif "campplus" in self.model_name or "cam" in self.model_name:
            self.threshold = 0.38
        else:
            self.threshold = 0.50

        # Load Silero VAD session
        vad_path = self.model_dir / "silero_vad.onnx"
        self.vad_session, self.vad_provider = create_session(vad_path)

        # Load Speaker Embedding session
        if "wespeaker" in self.model_name:
            emb_path = self.model_dir / "wespeaker_en_voxceleb_resnet34.onnx"
            self.model_id = "wespeaker-resnet34"
        elif "campplus" in self.model_name or "cam" in self.model_name:
            emb_path = self.model_dir / "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx"
            self.model_id = "3dspeaker-campplus"
        else:
            emb_path = Path(model_name)
            self.model_id = emb_path.stem

        self.emb_session, self.emb_provider = create_session(emb_path)
        self.emb_input_name = self.emb_session.get_inputs()[0].name

        # Profile cache: speaker_id -> normalized centroid embedding
        self._profiles: dict[str, np.ndarray] = {}

    def extract_embedding(self, audio_chunk: np.ndarray) -> np.ndarray:
        """Extract L2-normalized speaker embedding vector for an audio window."""
        feats = compute_fbank(audio_chunk, sample_rate=16000, n_mels=80, povey_window=self.model_id == "3dspeaker-campplus")
        feats_expanded = np.expand_dims(feats, axis=0)  # [1, T, 80]
        output = self.emb_session.run(None, {self.emb_input_name: feats_expanded})[0]
        emb = output.flatten().astype(np.float32)
        norm = np.linalg.norm(emb)
        if norm > 1e-12:
            emb = emb / norm
        return emb

    def enroll_speaker(self, enrollment_audio_paths: list[Path]) -> np.ndarray:
        """Enroll profile by computing the L2-normalized mean of embeddings from enrollment clips."""
        all_embeddings: list[np.ndarray] = []

        for p in enrollment_audio_paths:
            audio = decode_audio_ffmpeg(p)
            intervals = run_silero_vad(self.vad_session, audio, threshold=self.vad_threshold)
            if not intervals:
                intervals = [(0.0, len(audio) / 16000.0)]

            windows = extract_speech_windows(
                audio,
                intervals,
                win_sec=self.win_sec,
                hop_sec=self.hop_sec,
            )
            for _, _, win_audio in windows:
                all_embeddings.append(self.extract_embedding(win_audio))

        if not all_embeddings:
            # Fallback if audio was empty
            return np.zeros(256 if "wespeaker" in self.model_name else 192, dtype=np.float32)

        centroid = np.mean(all_embeddings, axis=0)
        norm = np.linalg.norm(centroid)
        if norm > 1e-12:
            centroid = centroid / norm
        return centroid

    def ensure_profiles_enrolled(
        self,
        audio_dir: Path,
        profile_name_or_path: str,
        ground_truth: list[dict[str, Any]] | None = None,
    ) -> None:
        """Discover and enroll all relevant target profiles prior to file scanning."""
        enroll_base = audio_dir.parent / "enrollment"

        # Check if profile_name_or_path is a path to a directory or file
        prof_path = Path(profile_name_or_path)
        if prof_path.exists():
            if prof_path.is_dir():
                clips = sorted(list(prof_path.glob("*.wav")))
                self._profiles[prof_path.name] = self.enroll_speaker(clips)
                return
            elif prof_path.is_file():
                self._profiles[prof_path.stem] = self.enroll_speaker([prof_path])
                return

        # Check subdirectories in enrollment folder
        if enroll_base.exists():
            for spk_folder in enroll_base.iterdir():
                if spk_folder.is_dir() and spk_folder.name not in self._profiles:
                    clips = sorted(list(spk_folder.glob("*.wav")))
                    if clips:
                        self._profiles[spk_folder.name] = self.enroll_speaker(clips)

        # Ensure ground truth speakers are covered
        if ground_truth:
            for item in ground_truth:
                spk = item.get("target_speaker_id")
                if spk and spk not in self._profiles and enroll_base.exists():
                    spk_dir = enroll_base / spk
                    if spk_dir.exists():
                        clips = sorted(list(spk_dir.glob("*.wav")))
                        self._profiles[spk] = self.enroll_speaker(clips)

    def scan(
        self,
        audio_dir: Path,
        profile_name_or_path: str,
        ground_truth: list[dict[str, Any]] | None = None,
    ) -> tuple[dict[str, Any], float]:
        """Execute full baseline scan across audio_dir."""
        start_time = time.perf_counter()

        self.ensure_profiles_enrolled(audio_dir, profile_name_or_path, ground_truth)

        # Map clip_id -> target_speaker_id if ground truth provided
        clip_targets: dict[str, str] = {}
        if ground_truth:
            for item in ground_truth:
                clip_targets[item["clip_id"]] = item.get("target_speaker_id", profile_name_or_path)

        audio_files = sorted(
            [
                p
                for p in audio_dir.glob("*")
                if p.is_file() and p.suffix.lower() in (".wav", ".flac", ".mp3", ".ogg", ".mp4", ".mkv")
            ]
        )

        files_output: list[dict[str, Any]] = []

        for file_path in audio_files:
            clip_id = file_path.name
            target_spk = clip_targets.get(clip_id, profile_name_or_path)
            profile_vec = self._profiles.get(target_spk)

            # 1. FFmpeg decode
            audio = decode_audio_ffmpeg(file_path)
            duration_sec = len(audio) / 16000.0

            # 2. Silero VAD
            speech_intervals = run_silero_vad(self.vad_session, audio, threshold=self.vad_threshold)

            # 3. 2s windows, 1s hop over speech
            windows = extract_speech_windows(
                audio,
                speech_intervals,
                win_sec=self.win_sec,
                hop_sec=self.hop_sec,
            )

            hits: list[tuple[float, float, float]] = []
            max_confidence = 0.0

            if profile_vec is not None and len(windows) > 0:
                for w_start, w_end, w_audio in windows:
                    w_emb = self.extract_embedding(w_audio)
                    sim = float(np.dot(w_emb, profile_vec))
                    if sim > max_confidence:
                        max_confidence = sim
                    if sim >= self.threshold:
                        hits.append((w_start, w_end, sim))

            # 4. Merge adjacent hits into segments
            segments = merge_adjacent_hits(hits, merge_tolerance_sec=self.merge_tolerance_sec)
            verdict = "Match" if max_confidence >= self.threshold else "No match"

            files_output.append(
                {
                    "file_path": str(file_path),
                    "clip_id": clip_id,
                    "duration_seconds": round(duration_sec, 3),
                    "verdict": verdict,
                    "max_confidence": round(max_confidence, 4),
                    "segments": segments,
                }
            )

        elapsed = time.perf_counter() - start_time
        result = {
            "schema_version": "1.0.0",
            "files": files_output,
            "scan_metadata": {
                "elapsed_seconds": elapsed,
                "model_id": self.model_id,
                "threshold": self.threshold,
                "scanner": f"BaselineScanner({self.model_id})",
            },
        }
        return result, elapsed


def run_experiment(
    dataset_dir: Path,
    output_dir: Path,
    model_name: str,
    threshold: float | None = None,
) -> dict[str, Any]:
    """Execute evaluation run for a given model and produce reports."""
    coordinator = EvaluationCoordinator()
    scanner = BaselineScanner(model_name=model_name, threshold=threshold)
    target_out = output_dir / f"baseline_{scanner.model_id}"

    logger.info("Running baseline evaluation for %s (threshold=%.2f)...", scanner.model_id, scanner.threshold)
    eval_summary = coordinator.run_evaluation(
        dataset_dir=dataset_dir,
        scanner=scanner,
        profile_name_or_path="auto",
        is_final=False,
        output_dir=target_out,
    )
    return eval_summary


def main() -> int:
    parser = argparse.ArgumentParser(
        description="VoiceScan Baseline Experiment Runner",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter,
    )
    parser.add_argument("--dataset-dir", type=str, default="eval/dev_dataset", help="Dev dataset directory")
    parser.add_argument("--output-dir", type=str, default="eval/reports", help="Output directory for reports")
    parser.add_argument("--model", choices=["all", "wespeaker", "campplus"], default="all", help="Model to evaluate")

    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(levelname)s: %(message)s")

    dataset_path = Path(args.dataset_dir)
    output_path = Path(args.output_dir)

    models_to_run = ["wespeaker", "campplus"] if args.model == "all" else [args.model]

    results = {}
    for m in models_to_run:
        summary = run_experiment(dataset_path, output_path, model_name=m)
        results[m] = summary
        metrics = summary["metrics"]
        print(f"\n=================== Baseline Results: {m.upper()} ===================")
        print(f"Recall:              {metrics['recall']:.2%}")
        print(f"Precision:           {metrics['precision']:.2%}")
        print(f"False Alarms / Hour: {metrics['fa_per_hour']:.2f}")
        print(f"EER:                 {metrics['eer']:.2%}")
        print(f"Mean Timing Error:   {metrics['mean_timing_error_sec']:.3f} s")
        speed = summary.get("metadata", {}).get("speed_realtime_multiple", 0.0)
        print(f"Speed:               {speed:.2f}x realtime")

    return 0


if __name__ == "__main__":
    import sys

    sys.exit(main())
