"""
metrics.py — Statistical evaluation metrics for VoiceScan.

Implements:
1. False alarms per hour (FA/hr) of audio (segment-level and file-level).
2. Per-file recall and precision with customizable temporal overlap criteria.
3. Segment-level timing error (start offset, end offset, absolute timing error).
4. Detection Error Tradeoff (DET) curve and Equal Error Rate (EER).
5. 95% Bootstrap confidence intervals across files.
6. Breakdown by SNR bucket and degradation type.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any
import numpy as np


@dataclass
class SegmentMatch:
    pred_start: float
    pred_end: float
    true_start: float
    true_end: float
    start_error: float
    end_error: float
    overlap_duration: float


@dataclass
class FileEvalResult:
    clip_id: str
    target_speaker_id: str
    target_present: bool
    snr_target_db: float | None
    degradation: str
    duration_seconds: float
    scanner_predicted_positive: bool
    max_confidence: float
    is_true_positive: bool = False
    is_false_positive: bool = False
    is_true_negative: bool = False
    is_false_negative: bool = False
    false_alarm_segments_count: int = 0
    segment_matches: list[SegmentMatch] = field(default_factory=list)


def check_segment_overlap(
    pred_start: float,
    pred_end: float,
    true_start: float,
    true_end: float,
    min_overlap_seconds: float = 0.5,
) -> tuple[bool, float]:
    """Check if predicted segment overlaps a true segment by at least min_overlap_seconds."""
    overlap_start = max(pred_start, true_start)
    overlap_end = min(pred_end, true_end)
    overlap_duration = max(0.0, overlap_end - overlap_start)
    is_overlapping = overlap_duration >= min_overlap_seconds
    return is_overlapping, overlap_duration


def evaluate_file(
    ground_truth_item: dict[str, Any],
    scanner_item: dict[str, Any] | None,
    min_overlap_seconds: float = 0.5,
    match_verdict_threshold: float = 0.5,
) -> FileEvalResult:
    """Evaluate scanner results for a single file against ground truth."""
    clip_id = ground_truth_item["clip_id"]
    target_present = ground_truth_item.get("target_present", False)
    duration_seconds = float(ground_truth_item.get("duration_seconds", 10.0))
    snr_target_db = ground_truth_item.get("snr_target_db")
    degradation = " + ".join(ground_truth_item.get("degradation_chain", ["none"]))
    target_speaker = ground_truth_item.get("target_speaker_id", "")

    # Ground truth target segments
    true_turns = [
        t for t in ground_truth_item.get("speaker_turns", [])
        if t.get("is_target", False) or t.get("speaker_id") == target_speaker
    ]

    predicted_segments = []
    max_confidence = 0.0
    file_verdict_match = False

    if scanner_item:
        max_confidence = float(scanner_item.get("max_confidence", 0.0))
        file_verdict = str(scanner_item.get("verdict", "")).lower()
        if file_verdict in ("match", "positive") or max_confidence >= match_verdict_threshold:
            file_verdict_match = True

        for seg in scanner_item.get("segments", []):
            conf = float(seg.get("confidence", 0.0))
            seg_verdict = str(seg.get("verdict", "")).lower()
            if seg_verdict in ("match", "positive") or conf >= match_verdict_threshold:
                predicted_segments.append(
                    {
                        "start": float(seg.get("start_time_seconds", 0.0)),
                        "end": float(seg.get("end_time_seconds", 0.0)),
                        "confidence": conf,
                    }
                )

    # Classification logic
    is_tp = False
    is_fp = False
    is_tn = False
    is_fn = False
    segment_matches: list[SegmentMatch] = []
    false_alarm_count = 0

    if target_present:
        # File has target speaker: must have at least one overlapping predicted segment to count as TP
        matched_any = False
        for p_seg in predicted_segments:
            seg_matched = False
            for t_turn in true_turns:
                t_start = float(t_turn.get("start_time_seconds", 0.0))
                t_end = float(t_turn.get("end_time_seconds", 0.0))
                is_ov, ov_dur = check_segment_overlap(
                    p_seg["start"], p_seg["end"], t_start, t_end, min_overlap_seconds
                )
                if is_ov:
                    seg_matched = True
                    matched_any = True
                    segment_matches.append(
                        SegmentMatch(
                            pred_start=p_seg["start"],
                            pred_end=p_seg["end"],
                            true_start=t_start,
                            true_end=t_end,
                            start_error=abs(p_seg["start"] - t_start),
                            end_error=abs(p_seg["end"] - t_end),
                            overlap_duration=ov_dur,
                        )
                    )
            if not seg_matched:
                false_alarm_count += 1

        if matched_any:
            is_tp = True
        else:
            is_fn = True
    else:
        # File does not have target speaker
        false_alarm_count = len(predicted_segments)
        if file_verdict_match or len(predicted_segments) > 0:
            is_fp = True
        else:
            is_tn = True

    return FileEvalResult(
        clip_id=clip_id,
        target_speaker_id=target_speaker,
        target_present=target_present,
        snr_target_db=snr_target_db,
        degradation=degradation,
        duration_seconds=duration_seconds,
        scanner_predicted_positive=(is_tp or is_fp),
        max_confidence=max_confidence,
        is_true_positive=is_tp,
        is_false_positive=is_fp,
        is_true_negative=is_tn,
        is_false_negative=is_fn,
        false_alarm_segments_count=false_alarm_count,
        segment_matches=segment_matches,
    )


def compute_aggregate_metrics(file_results: list[FileEvalResult]) -> dict[str, float]:
    """Compute aggregate evaluation metrics from a collection of FileEvalResult items."""
    total_files = len(file_results)
    if total_files == 0:
        return {
            "total_files": 0,
            "recall": 0.0,
            "precision": 0.0,
            "fa_per_hour": 0.0,
            "mean_timing_error_sec": 0.0,
            "tp_count": 0,
            "fp_count": 0,
            "tn_count": 0,
            "fn_count": 0,
        }

    tp = sum(1 for r in file_results if r.is_true_positive)
    fp = sum(1 for r in file_results if r.is_false_positive)
    tn = sum(1 for r in file_results if r.is_true_negative)
    fn = sum(1 for r in file_results if r.is_false_negative)

    total_duration_hours = sum(r.duration_seconds for r in file_results) / 3600.0
    total_false_alarms = sum(r.false_alarm_segments_count for r in file_results)

    recall = (tp / (tp + fn)) if (tp + fn) > 0 else 0.0
    precision = (tp / (tp + fp)) if (tp + fp) > 0 else 0.0
    fa_per_hour = (total_false_alarms / total_duration_hours) if total_duration_hours > 0 else 0.0

    # Timing error across all matched segments
    all_timing_errors = []
    for r in file_results:
        for m in r.segment_matches:
            all_timing_errors.append(0.5 * (m.start_error + m.end_error))

    mean_timing_error = float(np.mean(all_timing_errors)) if all_timing_errors else 0.0

    return {
        "total_files": float(total_files),
        "recall": float(recall),
        "precision": float(precision),
        "fa_per_hour": float(fa_per_hour),
        "mean_timing_error_sec": float(mean_timing_error),
        "tp_count": float(tp),
        "fp_count": float(fp),
        "tn_count": float(tn),
        "fn_count": float(fn),
    }


def compute_bootstrap_ci(
    file_results: list[FileEvalResult],
    metric_keys: list[str] | None = None,
    num_bootstraps: int = 1000,
    ci_level: float = 0.95,
    seed: int = 42,
) -> dict[str, tuple[float, float]]:
    """Compute non-parametric bootstrap confidence intervals across files."""
    if metric_keys is None:
        metric_keys = ["recall", "precision", "fa_per_hour", "mean_timing_error_sec"]

    n = len(file_results)
    if n < 2:
        agg = compute_aggregate_metrics(file_results)
        return {k: (agg.get(k, 0.0), agg.get(k, 0.0)) for k in metric_keys}

    rng = np.random.default_rng(seed)
    bootstrap_vals: dict[str, list[float]] = {k: [] for k in metric_keys}

    for _ in range(num_bootstraps):
        resampled_indices = rng.integers(0, n, size=n)
        sample = [file_results[i] for i in resampled_indices]
        metrics = compute_aggregate_metrics(sample)
        for k in metric_keys:
            bootstrap_vals[k].append(metrics.get(k, 0.0))

    lower_pct = 100.0 * (1.0 - ci_level) / 2.0
    upper_pct = 100.0 * (1.0 - (1.0 - ci_level) / 2.0)

    result_ci: dict[str, tuple[float, float]] = {}
    for k in metric_keys:
        low = float(np.percentile(bootstrap_vals[k], lower_pct))
        high = float(np.percentile(bootstrap_vals[k], upper_pct))
        result_ci[k] = (low, high)

    return result_ci


def compute_det_and_eer(
    file_results: list[FileEvalResult],
    num_thresholds: int = 200,
) -> tuple[np.ndarray, np.ndarray, float, float]:
    """Compute DET curve points (FAR, FRR), Equal Error Rate (EER), and optimal threshold."""
    scores = np.array([r.max_confidence for r in file_results], dtype=float)
    labels = np.array([1 if r.target_present else 0 for r in file_results], dtype=int)

    pos_mask = (labels == 1)
    neg_mask = (labels == 0)

    num_pos = np.sum(pos_mask)
    num_neg = np.sum(neg_mask)

    if num_pos == 0 or num_neg == 0:
        return np.array([0.0]), np.array([0.0]), 0.0, 0.5

    thresholds = np.linspace(0.0, 1.0, num_thresholds)
    far_list = []
    frr_list = []

    pos_scores = scores[pos_mask]
    neg_scores = scores[neg_mask]

    for t in thresholds:
        # False Alarm: negative scored >= t
        fa = np.sum(neg_scores >= t)
        far = fa / num_neg
        # False Rejection: positive scored < t
        fr = np.sum(pos_scores < t)
        frr = fr / num_pos
        far_list.append(far)
        frr_list.append(frr)

    far_arr = np.array(far_list, dtype=float)
    frr_arr = np.array(frr_list, dtype=float)

    # Find EER: point where |FAR - FRR| is minimized
    diff = np.abs(far_arr - frr_arr)
    min_idx = int(np.argmin(diff))
    eer = float(0.5 * (far_arr[min_idx] + frr_arr[min_idx]))
    opt_threshold = float(thresholds[min_idx])

    return far_arr, frr_arr, eer, opt_threshold
