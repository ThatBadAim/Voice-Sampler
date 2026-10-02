"""
test_baseline_scanner.py — Acceptance tests for the BaselineScanner feasibility pipeline.
"""

from pathlib import Path
import pytest

from eval.harness import EvaluationCoordinator, BaselineScanner


def test_baseline_scanner_instantiation():
    we_scanner = BaselineScanner(model_name="wespeaker")
    assert we_scanner.model_id == "wespeaker-resnet34"
    assert we_scanner.threshold == 0.48

    cam_scanner = BaselineScanner(model_name="campplus")
    assert cam_scanner.model_id == "3dspeaker-campplus"
    assert cam_scanner.threshold == 0.38


def test_baseline_scanner_execution_on_dev_clip(tmp_path: Path):
    repo_root = Path(__file__).resolve().parent.parent.parent
    dev_dir = repo_root / "eval" / "dev_dataset"

    if not (dev_dir / "audio").exists():
        pytest.skip("Dev dataset audio not found.")

    coordinator = EvaluationCoordinator()
    scanner = BaselineScanner(model_name="campplus", threshold=0.38)
    out_dir = tmp_path / "baseline_test_run"

    summary = coordinator.run_evaluation(
        dataset_dir=dev_dir,
        scanner=scanner,
        profile_name_or_path="auto",
        is_final=False,
        output_dir=out_dir,
    )

    metrics = summary["metrics"]
    assert "recall" in metrics
    assert "precision" in metrics
    assert "fa_per_hour" in metrics
    assert "eer" in metrics

    assert (out_dir / "eval_results.json").exists()
    assert (out_dir / "eval_report.md").exists()
    assert (out_dir / "plots" / "det_curve.png").exists()
    assert (out_dir / "plots" / "snr_breakdown.png").exists()
