"""
dataset_config.py — Configuration models and validation for synthetic data generation.
"""

from __future__ import annotations

import json
from pathlib import Path
from dataclasses import dataclass, field
from typing import Any


@dataclass
class DatasetConfig:
    speech_dir: str = "eval/data_config/speech"
    game_dir: str = "eval/data_config/game_audio"
    output_dir: str = "eval/dev_dataset"
    test_output_dir: str = "/tmp/voicescan_test_dataset"
    snr_targets_db: list[float] = field(default_factory=lambda: [-5.0, 0.0, 5.0, 10.0, 20.0])
    degradation_chains: list[list[str]] = field(
        default_factory=lambda: [
            ["none"],
            ["opus_16k"],
            ["opus_24k"],
            ["opus_32k"],
            ["agc"],
            ["bandlimit"],
            ["light_denoise"],
            ["opus_24k", "agc"],
        ]
    )
    clip_duration_seconds: float = 10.0
    overlap_probability: float = 0.25
    dev_speaker_ratio: float = 0.5
    enrollment_clips_per_speaker: int = 2
    sample_rate: int = 16000
    seed: int = 42

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> DatasetConfig:
        return cls(**{k: v for k, v in data.items() if k in cls.__dataclass_fields__})

    @classmethod
    def from_json(cls, path: str | Path) -> DatasetConfig:
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
        return cls.from_dict(data)

    def to_dict(self) -> dict[str, Any]:
        return {
            "speech_dir": self.speech_dir,
            "game_dir": self.game_dir,
            "output_dir": self.output_dir,
            "test_output_dir": self.test_output_dir,
            "snr_targets_db": self.snr_targets_db,
            "degradation_chains": self.degradation_chains,
            "clip_duration_seconds": self.clip_duration_seconds,
            "overlap_probability": self.overlap_probability,
            "dev_speaker_ratio": self.dev_speaker_ratio,
            "enrollment_clips_per_speaker": self.enrollment_clips_per_speaker,
            "sample_rate": self.sample_rate,
            "seed": self.seed,
        }
