"""
eval.harness module — VoiceScan evaluation harness and metrics calculation.
"""

from eval.harness.metrics import (
    evaluate_file,
    compute_aggregate_metrics,
    compute_bootstrap_ci,
    compute_det_and_eer,
    check_segment_overlap,
    FileEvalResult,
)
from eval.harness.scanner_interface import (
    BaseScanner,
    SubprocessCliScanner,
    PerfectGroundTruthScanner,
    RandomStubScanner,
)
from eval.harness.reporting import export_json, generate_markdown_report, generate_plots
from eval.harness.evaluator import EvaluationCoordinator, compare_runs, check_regression

__all__ = [
    "evaluate_file",
    "compute_aggregate_metrics",
    "compute_bootstrap_ci",
    "compute_det_and_eer",
    "check_segment_overlap",
    "FileEvalResult",
    "BaseScanner",
    "BaselineScanner",
    "SubprocessCliScanner",
    "PerfectGroundTruthScanner",
    "RandomStubScanner",
    "export_json",
    "generate_markdown_report",
    "generate_plots",
    "EvaluationCoordinator",
    "compare_runs",
    "check_regression",
]


def __getattr__(name: str):
    if name == "BaselineScanner":
        from research.baseline_experiment import BaselineScanner

        return BaselineScanner
    raise AttributeError(f"module '{__name__}' has no attribute '{name}'")

