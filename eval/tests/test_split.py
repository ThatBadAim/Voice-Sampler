"""
test_split.py — Unit tests verifying zero speaker and clip leakage between splits.

Acceptance requirement: Enrollment audio, dev set, and test set must never share speakers or source clips.
"""

from pathlib import Path
import pytest
from eval.synthetic.dataset_config import DatasetConfig
from eval.synthetic.generator import SyntheticDataGenerator, validate_test_directory, get_repo_root


@pytest.fixture
def mock_speaker_pool(tmp_path: Path) -> dict[str, list[Path]]:
    """Create temporary directory structure for 10 speakers with 6 clips each."""
    speakers = {}
    for spk_idx in range(1, 11):
        spk_name = f"spk_{spk_idx:02d}"
        spk_dir = tmp_path / spk_name
        spk_dir.mkdir(parents=True)
        clips = []
        for clip_idx in range(1, 7):
            clip_file = spk_dir / f"clip_{clip_idx:02d}.wav"
            clip_file.write_text(f"dummy audio content for {spk_name} clip {clip_idx}")
            clips.append(clip_file)
        speakers[spk_name] = clips
    return speakers


def test_speaker_split_zero_leakage(mock_speaker_pool):
    config = DatasetConfig(dev_speaker_ratio=0.5, seed=123)
    generator = SyntheticDataGenerator(config)

    dev_speakers, test_speakers = generator.split_speakers(mock_speaker_pool)

    # 1. Non-empty
    assert len(dev_speakers) > 0
    assert len(test_speakers) > 0
    assert len(dev_speakers) + len(test_speakers) == len(mock_speaker_pool)

    # 2. Strict disjointness (zero speaker ID leakage)
    dev_set = set(dev_speakers.keys())
    test_set = set(test_speakers.keys())
    assert dev_set.isdisjoint(test_set), f"Leakage detected between dev and test: {dev_set & test_set}"

    # 3. Source clip disjointness
    dev_clips = {c.name for clips in dev_speakers.values() for c in clips}
    test_clips = {c.name for clips in test_speakers.values() for c in clips}
    # While filenames could be identical across different speakers, full paths must be strictly disjoint:
    dev_clip_paths = {str(c.resolve()) for clips in dev_speakers.values() for c in clips}
    test_clip_paths = {str(c.resolve()) for clips in test_speakers.values() for c in clips}
    assert dev_clip_paths.isdisjoint(test_clip_paths)


def test_split_determinism(mock_speaker_pool):
    config1 = DatasetConfig(seed=42)
    config2 = DatasetConfig(seed=42)

    gen1 = SyntheticDataGenerator(config1)
    gen2 = SyntheticDataGenerator(config2)

    dev1, test1 = gen1.split_speakers(mock_speaker_pool)
    dev2, test2 = gen2.split_speakers(mock_speaker_pool)

    assert set(dev1.keys()) == set(dev2.keys())
    assert set(test1.keys()) == set(test2.keys())


def test_security_violation_when_test_dir_in_repo():
    repo_root = get_repo_root()
    inside_dir = repo_root / "eval" / "test_split_danger"

    with pytest.raises(ValueError, match="SECURITY VIOLATION"):
        validate_test_directory(inside_dir, repo_root)


def test_security_passes_for_outside_dir(tmp_path):
    repo_root = get_repo_root()
    # tmp_path in /tmp is outside repo root
    outside_dir = tmp_path / "voicescan_held_out_test"
    validated = validate_test_directory(outside_dir, repo_root)
    assert validated == outside_dir.resolve()
