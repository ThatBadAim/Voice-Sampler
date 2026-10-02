"""
evaluator.py — Central coordinator for running evaluations, logging, and comparison.
"""

from __future__ import annotations

from datetime import datetime, timezone
import json
import logging
from pathlib import Path
from typing import Any
import subprocess

from eval.harness.metrics import (
    evaluate_file,
    compute_aggregate_metrics,
    compute_bootstrap_ci,
    compute_det_and_eer,
    FileEvalResult,
)
from eval.harness.scanner_interface import BaseScanner
from eval.harness.reporting import export_json, generate_markdown_report, generate_plots
from eval.synthetic.generator import validate_test_directory, get_repo_root

logger = logging.getLogger("eval.harness")


def categorize_snr(snr: float | None) -> str:
    """Categorize SNR in dB into standard reporting buckets."""
    if snr is None:
        return "Distractor (Game Only)"
    if snr >= 20.0:
        return "Clean (>= 20 dB)"
    if snr >= 10.0:
        return "Moderate (10 to 20 dB)"
    if snr >= 0.0:
        return "Low (0 to 10 dB)"
    return "Harsh (-5 to 0 dB)"


def get_git_commit_hash() -> str:
    try:
        res = subprocess.run(["git", "rev-parse", "--short", "HEAD"], capture_output=True, text=True, check=True)
        return res.stdout.strip()
    except Exception:
        return "unknown"


class EvaluationCoordinator:
    def __init__(self, log_path: Path | None = None):
        self.repo_root = get_repo_root()
        self.final_log_path = log_path or (self.repo_root / "docs" / "final_eval_runs.log")

    def run_evaluation(
        self,
        dataset_dir: Path,
        scanner: BaseScanner,
        profile_name_or_path: str,
        is_final: bool = False,
        output_dir: Path | None = None,
        min_overlap_sec: float = 0.5,
        match_threshold: float = 0.5,
    ) -> dict[str, Any]:
        """Execute full evaluation run on a dataset."""
        dataset_dir = dataset_dir.resolve()
        if is_final:
            # Enforce security gate: test set must strictly be outside repo
            validate_test_directory(dataset_dir, self.repo_root)

        audio_dir = dataset_dir / "audio"
        ground_truth_path = dataset_dir / "ground_truth.json"

        if not ground_truth_path.exists():
            raise FileNotFoundError(f"ground_truth.json not found in dataset directory: {dataset_dir}")

        with open(ground_truth_path, "r", encoding="utf-8") as f:
            ground_truth_items = json.load(f)

        # Run scanner
        scan_output, elapsed_seconds = scanner.scan(
            audio_dir=audio_dir,
            profile_name_or_path=profile_name_or_path,
            ground_truth=ground_truth_items,
        )

        scanner_files_by_name = {}
        for f_item in scan_output.get("files", []):
            fname = Path(f_item.get("file_path", "")).name or f_item.get("clip_id", "")
            scanner_files_by_name[fname] = f_item

        file_eval_results: list[FileEvalResult] = []
        for gt in ground_truth_items:
            clip_id = gt["clip_id"]
            scanner_item = scanner_files_by_name.get(clip_id)
            eval_res = evaluate_file(
                ground_truth_item=gt,
                scanner_item=scanner_item,
                min_overlap_seconds=min_overlap_sec,
                match_verdict_threshold=match_threshold,
            )
            file_eval_results.append(eval_res)

        # 1. Global Metrics
        global_metrics = compute_aggregate_metrics(file_eval_results)
        far, frr, eer, opt_threshold = compute_det_and_eer(file_eval_results)
        global_metrics["eer"] = float(eer)
        global_metrics["optimal_threshold"] = float(opt_threshold)

        # 2. Bootstrap CIs
        bootstrap_ci = compute_bootstrap_ci(file_eval_results, num_bootstraps=1000)

        # 3. Stratified Breakdown: SNR Buckets
        snr_groups: dict[str, list[FileEvalResult]] = {}
        for r in file_eval_results:
            b = categorize_snr(r.snr_target_db)
            snr_groups.setdefault(b, []).append(r)

        snr_breakdown = {}
        for b, items in snr_groups.items():
            b_metrics = compute_aggregate_metrics(items)
            b_ci = compute_bootstrap_ci(items, num_bootstraps=500) if len(items) >= 5 else {}
            snr_breakdown[b] = {"metrics": b_metrics, "confidence_intervals": b_ci}

        # 4. Stratified Breakdown: Degradation Chains
        deg_groups: dict[str, list[FileEvalResult]] = {}
        for r in file_eval_results:
            deg_groups.setdefault(r.degradation, []).append(r)

        deg_breakdown = {}
        for d, items in deg_groups.items():
            d_metrics = compute_aggregate_metrics(items)
            deg_breakdown[d] = {"metrics": d_metrics}

        total_audio_seconds = sum(r.duration_seconds for r in file_eval_results)
        speed_multiple = (total_audio_seconds / elapsed_seconds) if elapsed_seconds > 0 else 0.0

        metadata = {
            "timestamp": datetime.now(timezone.utc).isoformat(),
            "git_commit": get_git_commit_hash(),
            "dataset_path": str(dataset_dir),
            "split": "test" if is_final else "dev",
            "is_final": is_final,
            "profile": profile_name_or_path,
            "total_audio_duration_seconds": round(total_audio_seconds, 2),
            "total_audio_duration_hours": round(total_audio_seconds / 3600.0, 4),
            "elapsed_seconds": round(elapsed_seconds, 3),
            "speed_realtime_multiple": round(speed_multiple, 2),
        }

        eval_summary = {
            "metadata": metadata,
            "metrics": global_metrics,
            "confidence_intervals": bootstrap_ci,
            "det_curve": {
                "far": far.tolist(),
                "frr": frr.tolist(),
                "eer": eer,
                "optimal_threshold": opt_threshold,
            },
            "snr_breakdown": snr_breakdown,
            "degradation_breakdown": deg_breakdown,
        }

        # If --final, log run with timestamp
        if is_final:
            self._log_final_run(metadata, global_metrics)

        # Output generation if path provided
        if output_dir:
            output_dir = output_dir.resolve()
            output_dir.mkdir(parents=True, exist_ok=True)
            export_json(eval_summary, output_dir / "eval_results.json")
            generate_markdown_report(eval_summary, output_dir / "eval_report.md")
            generate_plots(eval_summary, output_dir / "plots")

        return eval_summary

    def _log_final_run(self, metadata: dict[str, Any], metrics: dict[str, float]) -> None:
        """Log test set execution to persistent log file."""
        self.final_log_path.parent.mkdir(parents=True, exist_ok=True)
        timestamp = metadata["timestamp"]
        commit = metadata["git_commit"]
        path = metadata["dataset_path"]
        rec = metrics.get("recall", 0.0) * 100.0
        prec = metrics.get("precision", 0.0) * 100.0
        fa = metrics.get("fa_per_hour", 0.0)
        eer = metrics.get("eer", 0.0) * 100.0
        speed = metadata.get("speed_realtime_multiple", 0.0)

        log_entry = (
            f"[{timestamp}] FINAL RUN commit={commit} dataset={path} "
            f"recall={rec:.1f}% prec={prec:.1f}% fa_per_hr={fa:.2f} eer={eer:.2f}% speed={speed:.1f}x\n"
        )
        with open(self.final_log_path, "a", encoding="utf-8") as f:
            f.write(log_entry)
        logger.info("Logged --final run to: %s", self.final_log_path)


def compare_runs(run_a_path: Path, run_b_path: Path) -> dict[str, Any]:
    """Compare two evaluation result JSON files and return metric deltas."""
    with open(run_a_path, "r", encoding="utf-8") as f:
        run_a = json.load(f)
    with open(run_b_path, "r", encoding="utf-8") as f:
        run_b = json.load(f)

    mA = run_a["metrics"]
    mB = run_b["metrics"]

    deltas = {
        "delta_recall": mB.get("recall", 0.0) - mA.get("recall", 0.0),
        "delta_precision": mB.get("precision", 0.0) - mA.get("precision", 0.0),
        "delta_fa_per_hour": mB.get("fa_per_hour", 0.0) - mA.get("fa_per_hour", 0.0),
        "delta_eer": mB.get("eer", 0.0) - mA.get("eer", 0.0),
        "delta_timing_error_sec": mB.get("mean_timing_error_sec", 0.0) - mA.get("mean_timing_error_sec", 0.0),
        "run_a_file": str(run_a_path),
        "run_b_file": str(run_b_path),
    }

    report_lines = [
        "# Evaluation Metric Comparison",
        f"- Run A (Baseline): `{run_a_path.name}`",
        f"- Run B (Candidate): `{run_b_path.name}`",
        "",
        "| Metric | Run A | Run B | Delta (B - A) | Verdict |",
        "|---|---|---|---|---|",
        f"| **Recall** | {mA.get('recall', 0.0)*100:.2f}% | {mB.get('recall', 0.0)*100:.2f}% | {deltas['delta_recall']*100:+.2f}% | {'IMPROVED' if deltas['delta_recall'] > 0 else 'REGRESSED' if deltas['delta_recall'] < 0 else 'UNCHANGED'} |",
        f"| **Precision** | {mA.get('precision', 0.0)*100:.2f}% | {mB.get('precision', 0.0)*100:.2f}% | {deltas['delta_precision']*100:+.2f}% | {'IMPROVED' if deltas['delta_precision'] > 0 else 'REGRESSED' if deltas['delta_precision'] < 0 else 'UNCHANGED'} |",
        f"| **False Alarms / Hr** | {mA.get('fa_per_hour', 0.0):.2f} | {mB.get('fa_per_hour', 0.0):.2f} | {deltas['delta_fa_per_hour']:+.2f} | {'IMPROVED' if deltas['delta_fa_per_hour'] < 0 else 'REGRESSED' if deltas['delta_fa_per_hour'] > 0 else 'UNCHANGED'} |",
        f"| **Equal Error Rate** | {mA.get('eer', 0.0)*100:.2f}% | {mB.get('eer', 0.0)*100:.2f}% | {deltas['delta_eer']*100:+.2f}% | {'IMPROVED' if deltas['delta_eer'] < 0 else 'REGRESSED' if deltas['delta_eer'] > 0 else 'UNCHANGED'} |",
    ]
    deltas["markdown_summary"] = "\n".join(report_lines)
    return deltas


def check_regression(
    metrics: dict[str, float],
    baseline_metrics: dict[str, float] | None = None,
    min_recall: float | None = None,
    max_fa_per_hour: float | None = None,
    tolerance: float = 0.05,
) -> tuple[bool, list[str]]:
    """Check if metrics violate quality gates or regress beyond tolerance."""
    reasons = []

    recall = metrics.get("recall", 0.0)
    fa_hr = metrics.get("fa_per_hour", 0.0)

    if min_recall is not None and recall < (min_recall - tolerance):
        reasons.append(f"Recall {recall*100:.1f}% below minimum threshold {min_recall*100:.1f}% (tol={tolerance*100:.1f}%)")

    if max_fa_per_hour is not None and fa_hr > (max_fa_per_hour + tolerance):
        reasons.append(f"False Alarms/Hr {fa_hr:.2f} exceeds maximum threshold {max_fa_per_hour:.2f}")

    if baseline_metrics:
        b_recall = baseline_metrics.get("recall", 0.0)
        b_fa_hr = baseline_metrics.get("fa_per_hour", 0.0)

        if recall < (b_recall - tolerance):
            reasons.append(f"Recall regressed from {b_recall*100:.1f}% to {recall*100:.1f}% (delta={recall - b_recall:+.2f})")
        if fa_hr > (b_fa_hr + tolerance):
            reasons.append(f"False alarms/hr regressed from {b_fa_hr:.2f} to {fa_hr:.2f} (delta={fa_hr - b_fa_hr:+.2f})")

    has_regression = len(reasons) > 0
    return has_regression, reasons
