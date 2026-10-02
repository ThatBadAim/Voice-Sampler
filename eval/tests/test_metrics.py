"""
test_metrics.py — Unit tests for evaluation metric formulas, EER, and bootstrap CIs.
"""

import numpy as np
import pytest

from eval.harness.metrics import (
    check_segment_overlap,
    compute_aggregate_metrics,
    compute_bootstrap_ci,
    compute_det_and_eer,
    FileEvalResult,
)
from eval.harness.evaluator import check_regression


def test_segment_overlap_logic():
    # 1. Clear overlap >= 0.5s
    ov, dur = check_segment_overlap(1.0, 3.0, 2.0, 4.0, min_overlap_seconds=0.5)
    assert ov is True
    assert dur == 1.0

    # 2. Overlap is tiny (< 0.5s)
    ov, dur = check_segment_overlap(1.0, 2.2, 2.0, 4.0, min_overlap_seconds=0.5)
    assert ov is False
    assert abs(dur - 0.2) < 1e-5

    # 3. Disjoint
    ov, dur = check_segment_overlap(1.0, 2.0, 3.0, 4.0, min_overlap_seconds=0.5)
    assert ov is False
    assert dur == 0.0


def test_aggregate_metrics_math():
    results = [
        # 2 TP
        FileEvalResult("c1", "s1", True, 10.0, "none", 10.0, True, 0.9, is_true_positive=True),
        FileEvalResult("c2", "s1", True, 10.0, "none", 10.0, True, 0.85, is_true_positive=True),
        # 1 FN
        FileEvalResult("c3", "s1", True, 10.0, "none", 10.0, False, 0.2, is_false_negative=True),
        # 1 FP (with 2 false alarm segments)
        FileEvalResult("c4", "s1", False, 10.0, "none", 10.0, True, 0.7, is_false_positive=True, false_alarm_segments_count=2),
        # 2 TN
        FileEvalResult("c5", "s1", False, 10.0, "none", 10.0, False, 0.1, is_true_negative=True),
        FileEvalResult("c6", "s1", False, 10.0, "none", 10.0, False, 0.05, is_true_negative=True),
    ]

    agg = compute_aggregate_metrics(results)

    # Total files = 6, total audio = 60s = (60 / 3600) hours = 1/60 hr
    # TP = 2, FN = 1 -> Recall = 2 / 3 = 0.6667
    # TP = 2, FP = 1 -> Precision = 2 / 3 = 0.6667
    # FA count = 2, duration hours = 60/3600 = 1/60 -> FA/hr = 2 / (1/60) = 120.0
    assert abs(agg["recall"] - (2.0 / 3.0)) < 1e-4
    assert abs(agg["precision"] - (2.0 / 3.0)) < 1e-4
    assert abs(agg["fa_per_hour"] - 120.0) < 1e-4


def test_det_and_eer_perfect_separation():
    # Positives all high, negatives all low
    results = [
        FileEvalResult("p1", "s1", True, 10.0, "none", 5.0, True, 0.95),
        FileEvalResult("p2", "s1", True, 10.0, "none", 5.0, True, 0.90),
        FileEvalResult("n1", "s1", False, 10.0, "none", 5.0, False, 0.10),
        FileEvalResult("n2", "s1", False, 10.0, "none", 5.0, False, 0.05),
    ]
    far, frr, eer, opt_thresh = compute_det_and_eer(results)
    assert eer == 0.0
    assert 0.10 < opt_thresh < 0.90


def test_det_and_eer_chance():
    # Interleaved / random scores
    results = []
    for i in range(100):
        # Evenly interspersed scores
        results.append(FileEvalResult(f"p{i}", "s1", True, 10.0, "none", 5.0, True, (i + 0.5) / 100.0))
        results.append(FileEvalResult(f"n{i}", "s1", False, 10.0, "none", 5.0, False, i / 100.0))

    far, frr, eer, _ = compute_det_and_eer(results)
    # Equal distribution means EER is near 0.50
    assert abs(eer - 0.50) < 0.05


def test_bootstrap_ci_coverage():
    results = [
        FileEvalResult(f"c{i}", "s1", (i % 2 == 0), 10.0, "none", 10.0, True, 0.8, is_true_positive=(i % 2 == 0), is_false_positive=(i % 2 != 0))
        for i in range(40)
    ]
    ci = compute_bootstrap_ci(results, metric_keys=["recall", "precision"], num_bootstraps=200)

    assert "recall" in ci
    assert "precision" in ci
    low_rec, high_rec = ci["recall"]
    assert 0.0 <= low_rec <= high_rec <= 1.0


def test_regression_gate_triggers():
    # Baseline has 90% recall and 1.0 FA/hr
    baseline = {"recall": 0.90, "fa_per_hour": 1.0}

    # Candidate has 70% recall (severe drop)
    cand = {"recall": 0.70, "fa_per_hour": 1.0}
    has_reg, reasons = check_regression(cand, baseline_metrics=baseline, tolerance=0.05)
    assert has_reg is True
    assert any("Recall regressed" in r for r in reasons)

    # Candidate meets quality criteria
    cand_good = {"recall": 0.92, "fa_per_hour": 0.8}
    has_reg, reasons = check_regression(cand_good, baseline_metrics=baseline, tolerance=0.05)
    assert has_reg is False
