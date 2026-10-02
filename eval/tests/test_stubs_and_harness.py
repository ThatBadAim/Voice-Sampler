"""
test_stubs_and_harness.py — Acceptance verification for Perfect Stub, Random Stub, and Harness.

Prompt 4 Acceptance:
- Harness runs against stub scanner returning random scores (random ≈ chance).
- Harness runs against stub returning perfect ground truth (perfect ≈ 100%).
- Reports sensible numbers for both.
- Verification of test split security boundary and final run logging.
"""

from pathlib import Path
import json
import numpy as np
import soundfile as sf
import pytest

from eval.harness import (
    EvaluationCoordinator,
    PerfectGroundTruthScanner,
    RandomStubScanner,
    compare_runs,
)


@pytest.fixture
def synthetic_eval_dataset(tmp_path: Path) -> Path:
    """Create a minimal synthetic evaluation dataset on disk with 20 clips."""
    sr = 16000
    dataset_dir = tmp_path / "test_eval_dataset"
    audio_dir = dataset_dir / "audio"
    audio_dir.mkdir(parents=True)

    ground_truth = []
    # 10 positive clips (target present)
    for i in range(1, 11):
        clip_id = f"pos_clip_{i:02d}.wav"
        t = np.linspace(0, 5.0, 5 * sr, endpoint=False)
        audio = 0.5 * np.sin(2 * np.pi * 200 * t).astype(np.float32)
        sf.write(str(audio_dir / clip_id), audio, sr)

        ground_truth.append(
            {
                "clip_id": clip_id,
                "target_speaker_id": "target_spk",
                "target_present": True,
                "duration_seconds": 5.0,
                "snr_target_db": 10.0 if i <= 5 else 0.0,
                "degradation_chain": ["none"] if i <= 5 else ["opus_24k"],
                "speaker_turns": [
                    {
                        "speaker_id": "target_spk",
                        "start_time_seconds": 1.0,
                        "end_time_seconds": 3.5,
                        "is_target": True,
                    }
                ],
            }
        )

    # 10 negative clips (target absent)
    for i in range(1, 11):
        clip_id = f"neg_clip_{i:02d}.wav"
        t = np.linspace(0, 5.0, 5 * sr, endpoint=False)
        audio = 0.3 * np.sin(2 * np.pi * 120 * t).astype(np.float32)
        sf.write(str(audio_dir / clip_id), audio, sr)

        ground_truth.append(
            {
                "clip_id": clip_id,
                "target_speaker_id": "target_spk",
                "target_present": False,
                "duration_seconds": 5.0,
                "snr_target_db": 5.0,
                "degradation_chain": ["none"],
                "speaker_turns": [
                    {
                        "speaker_id": "other_spk",
                        "start_time_seconds": 0.5,
                        "end_time_seconds": 2.5,
                        "is_target": False,
                    }
                ],
            }
        )

    manifest_path = dataset_dir / "ground_truth.json"
    manifest_path.write_text(json.dumps(ground_truth, indent=2), encoding="utf-8")
    return dataset_dir


def test_perfect_ground_truth_stub_achieves_100_percent(synthetic_eval_dataset, tmp_path: Path):
    coordinator = EvaluationCoordinator()
    out_dir = tmp_path / "perfect_report"
    scanner = PerfectGroundTruthScanner()

    summary = coordinator.run_evaluation(
        dataset_dir=synthetic_eval_dataset,
        scanner=scanner,
        profile_name_or_path="target_spk",
        is_final=False,
        output_dir=out_dir,
    )

    metrics = summary["metrics"]
    # Perfect stub must achieve 100% recall, 100% precision, 0 FA/hr, 0 timing error
    assert metrics["recall"] == 1.0
    assert metrics["precision"] == 1.0
    assert metrics["fa_per_hour"] == 0.0
    assert metrics["mean_timing_error_sec"] == 0.0
    assert metrics["eer"] == 0.0

    # Verify generated output files
    assert (out_dir / "eval_results.json").exists()
    assert (out_dir / "eval_report.md").exists()
    assert (out_dir / "plots" / "det_curve.png").exists()
    assert (out_dir / "plots" / "snr_breakdown.png").exists()


def test_random_stub_behaves_as_chance(synthetic_eval_dataset, tmp_path: Path):
    coordinator = EvaluationCoordinator()
    out_dir = tmp_path / "random_report"
    scanner = RandomStubScanner(seed=42)

    summary = coordinator.run_evaluation(
        dataset_dir=synthetic_eval_dataset,
        scanner=scanner,
        profile_name_or_path="target_spk",
        is_final=False,
        output_dir=out_dir,
    )

    metrics = summary["metrics"]
    eer = metrics["eer"]

    # Random scanner: EER should be roughly chance (between 0.30 and 0.70)
    assert 0.30 <= eer <= 0.70, f"Random scanner EER should be around chance (0.5), got {eer}"
    # False alarms should be non-zero
    assert metrics["fa_per_hour"] > 0.0


def test_final_evaluation_safety_and_logging(synthetic_eval_dataset, tmp_path: Path):
    log_file = tmp_path / "final_runs.log"
    coordinator = EvaluationCoordinator(log_path=log_file)
    scanner = PerfectGroundTruthScanner()

    # synthetic_eval_dataset is in tmp_path, which is outside the git repo
    summary = coordinator.run_evaluation(
        dataset_dir=synthetic_eval_dataset,
        scanner=scanner,
        profile_name_or_path="target_spk",
        is_final=True,
    )

    assert log_file.exists(), "Final evaluation must log entry to log file"
    log_content = log_file.read_text(encoding="utf-8")
    assert "FINAL RUN" in log_content
    assert "recall=100.0%" in log_content


def test_run_comparison_deltas(synthetic_eval_dataset, tmp_path: Path):
    coordinator = EvaluationCoordinator()

    out_perfect = tmp_path / "perfect"
    out_random = tmp_path / "random"

    coordinator.run_evaluation(
        dataset_dir=synthetic_eval_dataset,
        scanner=PerfectGroundTruthScanner(),
        profile_name_or_path="target_spk",
        output_dir=out_perfect,
    )
    coordinator.run_evaluation(
        dataset_dir=synthetic_eval_dataset,
        scanner=RandomStubScanner(seed=42),
        profile_name_or_path="target_spk",
        output_dir=out_random,
    )

    deltas = compare_runs(out_perfect / "eval_results.json", out_random / "eval_results.json")

    # Comparing Random to Perfect:
    # delta_fa_per_hour should be positive (random has more false alarms)
    # delta_eer should be positive (random has higher EER)
    assert deltas["delta_fa_per_hour"] > 0
    assert deltas["delta_eer"] > 0
    assert "Evaluation Metric Comparison" in deltas["markdown_summary"]
