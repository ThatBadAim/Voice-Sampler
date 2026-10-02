"""
test_generator.py — Integration tests for end-to-end dataset generation.
"""

from pathlib import Path
import json
import numpy as np
import soundfile as sf
import pytest

from eval.synthetic.dataset_config import DatasetConfig
from eval.synthetic.generator import SyntheticDataGenerator, print_summary_table


@pytest.fixture
def synthetic_speech_and_game_dirs(tmp_path: Path) -> tuple[Path, Path]:
    """Create miniature clean speech clips and game audio clips."""
    sr = 16000
    speech_dir = tmp_path / "speech"
    game_dir = tmp_path / "game_audio"
    speech_dir.mkdir()
    game_dir.mkdir()

    # Create 4 speakers, 4 clips each (each 2 seconds)
    for spk_i in range(1, 5):
        spk_dir = speech_dir / f"speaker_{spk_i:02d}"
        spk_dir.mkdir()
        for clip_i in range(1, 5):
            t = np.linspace(0, 2.0, int(2.0 * sr), endpoint=False)
            freq = 150.0 * spk_i + 50.0 * clip_i
            audio = 0.5 * np.sin(2 * np.pi * freq * t)
            # Add silence head/tail
            audio[: int(0.2 * sr)] = 0.0
            audio[-int(0.2 * sr) :] = 0.0
            sf.write(str(spk_dir / f"clip_{clip_i}.wav"), audio.astype(np.float32), sr)

    # Create 2 game audio files (each 12 seconds)
    for g_i in range(1, 3):
        t = np.linspace(0, 12.0, int(12.0 * sr), endpoint=False)
        noise = 0.15 * np.sin(2 * np.pi * 100 * t) + 0.05 * np.random.normal(0, 0.1, len(t))
        sf.write(str(game_dir / f"game_{g_i}.wav"), noise.astype(np.float32), sr)

    return speech_dir, game_dir


def test_end_to_end_generator(synthetic_speech_and_game_dirs, tmp_path: Path):
    speech_dir, game_dir = synthetic_speech_and_game_dirs
    dev_out = tmp_path / "output_dev"

    config = DatasetConfig(
        speech_dir=str(speech_dir),
        game_dir=str(game_dir),
        output_dir=str(dev_out),
        clip_duration_seconds=5.0,
        seed=999,
    )

    generator = SyntheticDataGenerator(config)
    speakers = generator.discover_speech_data(speech_dir)
    game_clips = generator.discover_game_audio(game_dir)

    dev_speakers, _ = generator.split_speakers(speakers)

    # Generate 15 clips
    gt = generator.generate_split("dev", dev_speakers, game_clips, dev_out, num_clips=15)

    assert len(gt) == 15
    manifest_file = dev_out / "ground_truth.json"
    assert manifest_file.exists()

    with open(manifest_file, "r", encoding="utf-8") as f:
        loaded_gt = json.load(f)

    assert len(loaded_gt) == 15
    first = loaded_gt[0]

    # Verify ground-truth schema
    required_keys = [
        "clip_id",
        "split",
        "clip_type",
        "duration_seconds",
        "sample_rate",
        "target_speaker_id",
        "target_present",
        "snr_target_db",
        "snr_measured_db",
        "degradation_chain",
        "has_overlap",
        "speaker_turns",
    ]
    for key in required_keys:
        assert key in first, f"Missing key '{key}' in ground truth entry"

    # Verify generated audio files
    for entry in loaded_gt:
        audio_path = dev_out / "audio" / entry["clip_id"]
        assert audio_path.exists()
        audio, sr = sf.read(str(audio_path))
        assert sr == 16000
        assert len(audio) == int(5.0 * 16000)

    # Verify enrollment audio exists and is non-empty
    enroll_dir = dev_out / "enrollment"
    assert enroll_dir.exists()
    for spk_id in dev_speakers:
        spk_enroll = enroll_dir / spk_id
        assert spk_enroll.exists()
        enroll_files = list(spk_enroll.glob("*.wav"))
        assert len(enroll_files) >= 1

    # Verify summary table prints without exception
    summary = print_summary_table(loaded_gt, "dev")
    assert "Synthetic Dataset Summary" in summary
    assert "Target SNR Distribution" in summary
