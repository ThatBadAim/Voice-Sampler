"""
scanner_interface.py — Scanner invocation contracts and verification stubs.

Defines:
- BaseScanner: abstract scanner runner interface.
- SubprocessCliScanner: executes CLI scanners: --input <dir> --profile <prof> --output <json>.
- PerfectGroundTruthScanner: oracle stub returning perfect segment detection.
- RandomStubScanner: stochastic stub returning uniformly random scores and verdicts.
"""

from __future__ import annotations

import abc
import json
import subprocess
import time
from pathlib import Path
from typing import Any
import numpy as np


class BaseScanner(abc.ABC):
    @abc.abstractmethod
    def scan(
        self,
        audio_dir: Path,
        profile_name_or_path: str,
        ground_truth: list[dict[str, Any]] | None = None,
    ) -> tuple[dict[str, Any], float]:
        """Execute scan across audio_dir.

        Returns:
            result_json: Dictionary adhering to VoiceScan JSON output schema.
            elapsed_seconds: Wall-clock processing time in seconds.
        """
        raise NotImplementedError


class SubprocessCliScanner(BaseScanner):
    """Executes a command-line scanner binary or script conforming to the contract:
    <executable> --input <audio_dir> --profile <profile> --output <output_json>
    """

    def __init__(self, command_prefix: list[str], temp_output_dir: Path | None = None):
        self.command_prefix = command_prefix
        self.temp_output_dir = temp_output_dir or Path("/tmp")

    def scan(
        self,
        audio_dir: Path,
        profile_name_or_path: str,
        ground_truth: list[dict[str, Any]] | None = None,
    ) -> tuple[dict[str, Any], float]:
        out_json_path = self.temp_output_dir / f"scan_output_{int(time.time() * 1000)}.json"

        cmd = list(self.command_prefix) + [
            "--input",
            str(audio_dir),
            "--profile",
            str(profile_name_or_path),
            "--output",
            str(out_json_path),
        ]

        start_time = time.perf_counter()
        proc = subprocess.run(cmd, capture_output=True, text=True)
        elapsed = time.perf_counter() - start_time

        if proc.returncode != 0:
            raise RuntimeError(
                f"Scanner CLI failed with code {proc.returncode}.\nSTDOUT: {proc.stdout}\nSTDERR: {proc.stderr}"
            )

        if not out_json_path.exists():
            raise FileNotFoundError(f"Scanner CLI did not write output JSON to {out_json_path}")

        with open(out_json_path, "r", encoding="utf-8") as f:
            data = json.load(f)

        try:
            out_json_path.unlink()
        except OSError:
            pass

        return data, elapsed


class PerfectGroundTruthScanner(BaseScanner):
    """Oracle stub scanner: emits perfect detections with 1.0 confidence for target turns."""

    def __init__(self, processing_delay_per_file_sec: float = 0.001):
        self.processing_delay = processing_delay_per_file_sec

    def scan(
        self,
        audio_dir: Path,
        profile_name_or_path: str,
        ground_truth: list[dict[str, Any]] | None = None,
    ) -> tuple[dict[str, Any], float]:
        if ground_truth is None:
            manifest_path = audio_dir.parent / "ground_truth.json"
            if manifest_path.exists():
                with open(manifest_path, "r", encoding="utf-8") as f:
                    ground_truth = json.load(f)
            else:
                ground_truth = []

        start_time = time.perf_counter()
        files_output = []
        target_spk = Path(profile_name_or_path).stem

        for item in ground_truth:
            clip_id = item["clip_id"]
            target_present = item.get("target_present", False)
            # If target speaker specified, match against it
            if "target_speaker_id" in item:
                target_present = item.get("target_present", False)

            if target_present:
                segments = []
                for turn in item.get("speaker_turns", []):
                    if turn.get("is_target", False) or turn.get("speaker_id") == item.get("target_speaker_id"):
                        segments.append(
                            {
                                "start_time_seconds": turn["start_time_seconds"],
                                "end_time_seconds": turn["end_time_seconds"],
                                "confidence": 0.99,
                                "verdict": "Match",
                                "reason_flags": [],
                            }
                        )

                files_output.append(
                    {
                        "file_path": str(audio_dir / clip_id),
                        "clip_id": clip_id,
                        "verdict": "Match",
                        "max_confidence": 0.99,
                        "segments": segments,
                    }
                )
            else:
                files_output.append(
                    {
                        "file_path": str(audio_dir / clip_id),
                        "clip_id": clip_id,
                        "verdict": "No match",
                        "max_confidence": 0.02,
                        "segments": [],
                    }
                )

        if self.processing_delay > 0:
            time.sleep(self.processing_delay * len(ground_truth))

        elapsed = time.perf_counter() - start_time
        result = {
            "schema_version": "1.0.0",
            "files": files_output,
            "scan_metadata": {
                "elapsed_seconds": elapsed,
                "scanner": "PerfectGroundTruthScanner",
            },
        }
        return result, elapsed


class RandomStubScanner(BaseScanner):
    """Stochastic stub scanner: returns random confidence scores ~ U(0, 1)."""

    def __init__(self, seed: int = 42, match_threshold: float = 0.5):
        self.rng = np.random.default_rng(seed)
        self.match_threshold = match_threshold

    def scan(
        self,
        audio_dir: Path,
        profile_name_or_path: str,
        ground_truth: list[dict[str, Any]] | None = None,
    ) -> tuple[dict[str, Any], float]:
        start_time = time.perf_counter()
        files_output = []

        audio_files = sorted(list(audio_dir.glob("*.wav")))
        if not audio_files and ground_truth:
            audio_files = [audio_dir / item["clip_id"] for item in ground_truth]

        for p in audio_files:
            clip_id = p.name
            # Random score
            score = float(self.rng.uniform(0.0, 1.0))
            is_match = score >= self.match_threshold

            segments = []
            if is_match:
                # Random segment timing within [0, 8]s
                s_start = float(self.rng.uniform(0.5, 4.0))
                s_dur = float(self.rng.uniform(1.0, 3.0))
                segments.append(
                    {
                        "start_time_seconds": round(s_start, 2),
                        "end_time_seconds": round(s_start + s_dur, 2),
                        "confidence": round(score, 3),
                        "verdict": "Match",
                        "reason_flags": [],
                    }
                )

            files_output.append(
                {
                    "file_path": str(p),
                    "clip_id": clip_id,
                    "verdict": "Match" if is_match else "No match",
                    "max_confidence": round(score, 3),
                    "segments": segments,
                }
            )

        elapsed = time.perf_counter() - start_time
        result = {
            "schema_version": "1.0.0",
            "files": files_output,
            "scan_metadata": {
                "elapsed_seconds": elapsed,
                "scanner": "RandomStubScanner",
            },
        }
        return result, elapsed


def __getattr__(name: str) -> Any:
    if name == "BaselineScanner":
        from research.baseline_experiment import BaselineScanner

        return BaselineScanner
    raise AttributeError(f"module '{__name__}' has no attribute '{name}'")
