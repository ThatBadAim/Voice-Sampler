#!/usr/bin/env python3
"""
run_eval.py — CLI for the VoiceScan Evaluation Harness.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

# Ensure repo root is on sys.path
repo_root = Path(__file__).resolve().parent.parent
if str(repo_root) not in sys.path:
    sys.path.insert(0, str(repo_root))

from eval.harness import (
    EvaluationCoordinator,
    PerfectGroundTruthScanner,
    RandomStubScanner,
    SubprocessCliScanner,
    compare_runs,
    check_regression,
)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="VoiceScan Evaluation Harness",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter,
    )
    parser.add_argument("--dataset-dir", type=str, default="eval/dev_dataset", help="Dataset directory to evaluate")
    parser.add_argument("--profile", type=str, default="speaker_01", help="Target speaker profile name/path")
    parser.add_argument(
        "--scanner",
        choices=["perfect", "random", "cli", "wespeaker", "campplus"],
        default="perfect",
        help="Scanner implementation to evaluate",
    )
    parser.add_argument("--threshold", type=float, default=None, help="Custom similarity threshold for baseline scanner")
    parser.add_argument(
        "--scanner-cmd",
        type=str,
        default=None,
        help="Command for CLI scanner (used when --scanner=cli, e.g. 'dotnet run --project engine/VoiceScan.Cli -- scan')",
    )
    parser.add_argument(
        "--output-dir",
        type=str,
        default="eval/reports/latest",
        help="Directory to save JSON, Markdown report, and PNG plots",
    )
    parser.add_argument(
        "--final",
        action="store_true",
        help="Flag indicating evaluation on the held-out test set (dataset must be outside the repo)",
    )
    parser.add_argument(
        "--compare",
        nargs=2,
        metavar=("RUN_A", "RUN_B"),
        help="Compare two evaluation result JSON files and display deltas",
    )
    parser.add_argument(
        "--baseline",
        type=str,
        default=None,
        help="Path to baseline evaluation JSON to check for regressions",
    )
    parser.add_argument("--min-recall", type=float, default=None, help="Minimum recall threshold required")
    parser.add_argument("--max-fa-hr", type=float, default=None, help="Maximum false alarms/hr allowed")
    parser.add_argument("--regression-tolerance", type=float, default=0.05, help="Tolerance margin before regression exit")

    args = parser.parse_args()

    # 1. Comparison mode
    if args.compare:
        run_a, run_b = Path(args.compare[0]), Path(args.compare[1])
        deltas = compare_runs(run_a, run_b)
        print(deltas["markdown_summary"])
        return 0

    # 2. Select scanner
    if args.scanner == "perfect":
        scanner = PerfectGroundTruthScanner()
    elif args.scanner == "random":
        scanner = RandomStubScanner(seed=42)
    elif args.scanner in ("wespeaker", "campplus"):
        from research.baseline_experiment import BaselineScanner
        scanner = BaselineScanner(model_name=args.scanner, threshold=args.threshold)
    elif args.scanner == "cli":
        if not args.scanner_cmd:
            print("[ERROR] --scanner-cmd is required when --scanner=cli", file=sys.stderr)
            return 2
        import shlex
        scanner = SubprocessCliScanner(command_prefix=shlex.split(args.scanner_cmd))
    else:
        raise ValueError(f"Unknown scanner: {args.scanner}")

    # 3. Run evaluation
    coordinator = EvaluationCoordinator()
    dataset_dir = Path(args.dataset_dir)
    output_dir = Path(args.output_dir)

    print(f"[INFO] Running evaluation on '{dataset_dir}' with scanner '{args.scanner}' (is_final={args.final})...")
    try:
        eval_summary = coordinator.run_evaluation(
            dataset_dir=dataset_dir,
            scanner=scanner,
            profile_name_or_path=args.profile,
            is_final=args.final,
            output_dir=output_dir,
        )
    except Exception as exc:
        print(f"[ERROR] Evaluation failed: {exc}", file=sys.stderr)
        return 1

    # Print summary Markdown report to terminal
    report_path = output_dir / "eval_report.md"
    if report_path.exists():
        print("\n" + report_path.read_text(encoding="utf-8"))

    # 4. Check quality gates and regression exit codes
    baseline_metrics = None
    if args.baseline:
        with open(args.baseline, "r", encoding="utf-8") as f:
            baseline_data = json.load(f)
            baseline_metrics = baseline_data.get("metrics")

    has_reg, reg_reasons = check_regression(
        metrics=eval_summary["metrics"],
        baseline_metrics=baseline_metrics,
        min_recall=args.min_recall,
        max_fa_per_hour=args.max_fa_hr,
        tolerance=args.regression_tolerance,
    )

    if has_reg:
        print("\n[REGRESSION FAILURE] Quality gates violated:", file=sys.stderr)
        for r in reg_reasons:
            print(f"  - {r}", file=sys.stderr)
        return 1

    print("[SUCCESS] Evaluation completed successfully. Quality gates passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
