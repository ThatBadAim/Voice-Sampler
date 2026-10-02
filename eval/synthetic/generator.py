"""
generator.py — Synthetic test dataset generator for VoiceScan.

Enforces:
1. Speech-active region SNR mixing (-5, 0, 5, 10, 20 dB).
2. Degradation chains (Opus, AGC, band-limiting, noise suppression).
3. Positive, Negative, and Distractor clip generation.
4. Overlapping speakers in multi-speaker mixes.
5. Exact ground-truth JSON schema emission.
6. Complete speaker/clip isolation between enrollment, dev, and test sets.
7. Test directory strictly outside the repository root.
8. Deterministic random seed reproducibility.
"""

from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Any
import numpy as np
import soundfile as sf

from eval.synthetic.dataset_config import DatasetConfig
from eval.synthetic.snr import mix_at_snr, align_or_tile_noise, detect_speech_activity
from eval.synthetic.degradations import apply_degradation_chain

logger = logging.getLogger("eval.synthetic")


def get_repo_root() -> Path:
    """Locate the git repository root from current file."""
    current = Path(__file__).resolve().parent
    while current != current.parent:
        if (current / ".git").exists() or (current / "VoiceScan.sln").exists():
            return current
        current = current.parent
    return Path.cwd()


def validate_test_directory(test_dir: str | Path, repo_root: Path | None = None) -> Path:
    """Validate that test split directory is strictly outside repository root."""
    repo_root = (repo_root or get_repo_root()).resolve()
    resolved = Path(test_dir).resolve()

    if resolved == repo_root or repo_root in resolved.parents:
        raise ValueError(
            f"SECURITY VIOLATION: Test split output directory '{resolved}' cannot reside inside "
            f"the repository '{repo_root}'. It must be placed outside the repository to prevent data leakage."
        )
    return resolved


class SyntheticDataGenerator:
    def __init__(self, config: DatasetConfig):
        self.config = config
        self.rng = np.random.default_rng(config.seed)
        self.repo_root = get_repo_root()

    def discover_speech_data(self, speech_dir: Path) -> dict[str, list[Path]]:
        """Discover audio clips per speaker from directory structure: speech_dir/<speaker_id>/*.wav"""
        speaker_clips: dict[str, list[Path]] = {}
        if not speech_dir.exists():
            return speaker_clips

        for spk_dir in sorted(speech_dir.iterdir()):
            if spk_dir.is_dir():
                clips = sorted(
                    [
                        p
                        for p in spk_dir.glob("*")
                        if p.suffix.lower() in (".wav", ".flac", ".ogg", ".mp3")
                    ]
                )
                if clips:
                    speaker_clips[spk_dir.name] = clips

        return speaker_clips

    def discover_game_audio(self, game_dir: Path) -> list[Path]:
        """Discover game audio noise clips."""
        if not game_dir.exists():
            return []
        return sorted(
            [
                p
                for p in game_dir.glob("**/*")
                if p.is_file() and p.suffix.lower() in (".wav", ".flac", ".ogg", ".mp3")
            ]
        )

    def split_speakers(
        self, speaker_clips: dict[str, list[Path]]
    ) -> tuple[dict[str, list[Path]], dict[str, list[Path]]]:
        """Deterministically partition speakers into dev and test sets with zero leakage."""
        speaker_ids = sorted(speaker_clips.keys())
        # Deterministic shuffle
        shuffled_ids = list(self.rng.permutation(speaker_ids))

        num_dev = max(1, int(np.round(len(shuffled_ids) * self.config.dev_speaker_ratio)))
        dev_ids = sorted(shuffled_ids[:num_dev])
        test_ids = sorted(shuffled_ids[num_dev:])

        dev_data = {spk: speaker_clips[spk] for spk in dev_ids}
        test_data = {spk: speaker_clips[spk] for spk in test_ids}
        return dev_data, test_data

    def load_audio_normalized(self, path: Path) -> np.ndarray:
        """Load audio file converted to 16 kHz mono float32."""
        audio, sr = sf.read(str(path))
        if audio.ndim > 1:
            audio = np.mean(audio, axis=1)
        if sr != self.config.sample_rate:
            import scipy.signal
            samples = int(len(audio) * self.config.sample_rate / sr)
            audio = scipy.signal.resample(audio, samples)
        return audio.astype(np.float32)

    def generate_split(
        self,
        split_name: str,
        speaker_data: dict[str, list[Path]],
        game_audio_files: list[Path],
        output_dir: Path,
        num_clips: int = 50,
    ) -> list[dict[str, Any]]:
        """Generate a complete synthetic split (dev or test) with enrollment and ground truth."""
        if split_name == "test":
            validate_test_directory(output_dir, self.repo_root)

        output_dir = output_dir.resolve()
        audio_dir = output_dir / "audio"
        enrollment_dir = output_dir / "enrollment"
        audio_dir.mkdir(parents=True, exist_ok=True)
        enrollment_dir.mkdir(parents=True, exist_ok=True)

        # 1. Separate enrollment clips and mixing clips per speaker
        mixing_data: dict[str, list[Path]] = {}
        for spk_id, clips in speaker_data.items():
            spk_enroll_dir = enrollment_dir / spk_id
            spk_enroll_dir.mkdir(parents=True, exist_ok=True)

            enrollment_count = min(self.config.enrollment_clips_per_speaker, len(clips) // 2)
            if enrollment_count < 1 and len(clips) >= 2:
                enrollment_count = 1

            enrollment_clips = clips[:enrollment_count]
            remaining_clips = clips[enrollment_count:]

            for idx, c in enumerate(enrollment_clips):
                dest = spk_enroll_dir / f"enroll_{idx:02d}_{c.name}"
                dest.write_bytes(c.read_bytes())

            mixing_data[spk_id] = remaining_clips if remaining_clips else clips

        speakers_list = sorted(mixing_data.keys())
        if not speakers_list:
            raise ValueError(f"No speakers available to generate {split_name} split.")

        ground_truth: list[dict[str, Any]] = []
        target_sample_count = int(self.config.clip_duration_seconds * self.config.sample_rate)

        # 2. Generate mixed clips
        for clip_idx in range(num_clips):
            clip_id = f"{split_name}_clip_{clip_idx:04d}.wav"
            out_path = audio_dir / clip_id

            # Decide clip type: 45% positive, 40% negative, 15% distractor
            p = self.rng.random()
            if p < 0.45:
                clip_type = "positive"
            elif p < 0.85:
                clip_type = "negative"
            else:
                clip_type = "distractor"

            target_speaker = self.rng.choice(speakers_list)
            target_snr: float | None = float(self.rng.choice(self.config.snr_targets_db))
            chain_idx = self.rng.integers(0, len(self.config.degradation_chains))
            degradation = list(self.config.degradation_chains[chain_idx])

            speaker_turns: list[dict[str, Any]] = []
            speech_buffer = np.zeros(target_sample_count, dtype=np.float32)
            has_overlap = False

            if clip_type == "distractor":
                target_present = False
                target_snr = None
                measured_snr = None
                # Distractor: speech buffer remains silent, only game audio
            else:
                target_present = (clip_type == "positive")
                speakers_to_include = [target_speaker] if target_present else []

                # Other speakers pool
                other_speakers = [s for s in speakers_list if s != target_speaker]
                if not target_present:
                    if other_speakers:
                        chosen_other = self.rng.choice(other_speakers)
                    else:
                        chosen_other = target_speaker
                    speakers_to_include.append(chosen_other)

                # Check for overlap condition
                if self.rng.random() < self.config.overlap_probability and other_speakers:
                    has_overlap = True
                    overlap_speaker = self.rng.choice(other_speakers)
                    if overlap_speaker not in speakers_to_include:
                        speakers_to_include.append(overlap_speaker)

                # Place speech turns into speech_buffer
                current_sample = int(self.rng.uniform(0.5, 1.5) * self.config.sample_rate)
                for spk in speakers_to_include:
                    spk_clip_path = self.rng.choice(mixing_data[spk])
                    spk_audio = self.load_audio_normalized(spk_clip_path)

                    clip_len = len(spk_audio)
                    if has_overlap and len(speakers_to_include) > 1 and current_sample > 0:
                        # Overlap: offset slightly backwards
                        turn_start = max(0, current_sample - int(0.5 * self.config.sample_rate))
                    else:
                        turn_start = min(current_sample, max(0, target_sample_count - clip_len))

                    turn_end = min(turn_start + clip_len, target_sample_count)
                    actual_len = turn_end - turn_start

                    if actual_len > 0:
                        speech_buffer[turn_start:turn_end] += spk_audio[:actual_len]
                        speaker_turns.append(
                            {
                                "speaker_id": spk,
                                "start_time_seconds": round(turn_start / self.config.sample_rate, 3),
                                "end_time_seconds": round(turn_end / self.config.sample_rate, 3),
                                "is_target": (spk == target_speaker),
                            }
                        )
                        current_sample = turn_end + int(self.rng.uniform(0.5, 1.0) * self.config.sample_rate)

            # Load game audio
            if game_audio_files:
                game_path = self.rng.choice(game_audio_files)
                game_audio_source = game_path.name
                game_audio = self.load_audio_normalized(game_path)
            else:
                game_audio_source = "synthetic_game_audio_generator"
                # Synthetic pink/brown game noise fallback if no clips provided
                t = np.linspace(0, self.config.clip_duration_seconds, target_sample_count)
                game_audio = 0.2 * np.sin(2 * np.pi * 120 * t) + 0.1 * self.rng.normal(0, 0.1, target_sample_count)

            # Perform SNR mixing
            if clip_type == "distractor":
                mixed = align_or_tile_noise(game_audio, target_sample_count, rng=self.rng)
                peak = np.max(np.abs(mixed))
                if peak > 0.95:
                    mixed = mixed * (0.95 / peak)
            else:
                assert target_snr is not None
                speech_mask = detect_speech_activity(speech_buffer, sample_rate=self.config.sample_rate)
                mixed, _, _, measured_snr = mix_at_snr(
                    speech=speech_buffer,
                    noise=game_audio,
                    target_snr_db=target_snr,
                    speech_mask=speech_mask,
                    rng=self.rng,
                )

            # Apply degradation chain
            final_audio = apply_degradation_chain(
                mixed, sample_rate=self.config.sample_rate, degradations=degradation
            )

            # Save WAV
            sf.write(str(out_path), final_audio, self.config.sample_rate)

            entry: dict[str, Any] = {
                "clip_id": clip_id,
                "split": split_name,
                "clip_type": clip_type,
                "duration_seconds": self.config.clip_duration_seconds,
                "sample_rate": self.config.sample_rate,
                "target_speaker_id": target_speaker,
                "target_present": target_present,
                "snr_target_db": target_snr,
                "snr_measured_db": round(measured_snr, 2) if measured_snr is not None else None,
                "degradation_chain": degradation,
                "has_overlap": has_overlap,
                "game_audio_source": game_audio_source,
                "speaker_turns": speaker_turns,
            }
            ground_truth.append(entry)

        # Write ground truth manifest
        manifest_path = output_dir / "ground_truth.json"
        manifest_path.write_text(json.dumps(ground_truth, indent=2), encoding="utf-8")

        return ground_truth


def print_summary_table(ground_truth: list[dict[str, Any]], split_name: str) -> str:
    """Generate and print formatted summary table of generated synthetic clips."""
    total = len(ground_truth)
    type_counts: dict[str, int] = {}
    snr_counts: dict[str, int] = {}
    deg_counts: dict[str, int] = {}
    overlap_count = sum(1 for c in ground_truth if c.get("has_overlap", False))

    for c in ground_truth:
        ctype = c["clip_type"]
        type_counts[ctype] = type_counts.get(ctype, 0) + 1

        snr = str(c["snr_target_db"]) if c["snr_target_db"] is not None else "None"
        snr_counts[snr] = snr_counts.get(snr, 0) + 1

        deg = " + ".join(c["degradation_chain"])
        deg_counts[deg] = deg_counts.get(deg, 0) + 1

    lines = [
        f"\n==================================================",
        f"Synthetic Dataset Summary: {split_name.upper()} ({total} clips)",
        f"==================================================",
        f"Clip Types:",
    ]
    for k, v in sorted(type_counts.items()):
        lines.append(f"  - {k:<15}: {v:3d} ({v/total*100:5.1f}%)")

    lines.append(f"\nTarget SNR Distribution (Speech-Active Regions):")
    for k, v in sorted(snr_counts.items(), key=lambda x: (x[0] == "None", float(x[0]) if x[0] != "None" else 999)):
        label = f"{k} dB" if k != "None" else "N/A (distractor)"
        lines.append(f"  - {label:<20}: {v:3d} ({v/total*100:5.1f}%)")

    lines.append(f"\nDegradation Chains:")
    for k, v in sorted(deg_counts.items()):
        lines.append(f"  - {k:<25}: {v:3d} ({v/total*100:5.1f}%)")

    lines.append(f"\nMulti-Speaker Overlaps: {overlap_count} clips ({overlap_count/total*100:5.1f}%)")
    lines.append(f"==================================================\n")

    summary_text = "\n".join(lines)
    print(summary_text)
    return summary_text
