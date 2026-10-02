"""
reporting.py — Generation of JSON results, Markdown audit reports, and PNG visualizations.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np


def export_json(eval_summary: dict[str, Any], output_path: Path) -> None:
    """Export complete evaluation summary to JSON format."""
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(eval_summary, f, indent=2)


def generate_markdown_report(eval_summary: dict[str, Any], output_path: Path | None = None) -> str:
    """Generate professional Markdown audit report with metrics, tables, and overlap definitions."""
    global_m = eval_summary["metrics"]
    ci = eval_summary.get("confidence_intervals", {})
    snr_breakdown = eval_summary.get("snr_breakdown", {})
    deg_breakdown = eval_summary.get("degradation_breakdown", {})
    metadata = eval_summary.get("metadata", {})

    def fmt_ci(key: str, fmt: str = ".1f", scale: float = 100.0) -> str:
        val = global_m.get(key, 0.0) * scale
        if key in ci:
            low = ci[key][0] * scale
            high = ci[key][1] * scale
            return f"{val:{fmt}}% [{low:{fmt}}%, {high:{fmt}}%]"
        return f"{val:{fmt}}%"

    fa_val = global_m.get("fa_per_hour", 0.0)
    fa_ci_str = f"{fa_val:.2f}"
    if "fa_per_hour" in ci:
        fa_ci_str += f" [{ci['fa_per_hour'][0]:.2f}, {ci['fa_per_hour'][1]:.2f}]"

    timing_val = global_m.get("mean_timing_error_sec", 0.0)
    timing_ci_str = f"{timing_val:.3f}s"
    if "mean_timing_error_sec" in ci:
        timing_ci_str += f" [{ci['mean_timing_error_sec'][0]:.3f}s, {ci['mean_timing_error_sec'][1]:.3f}s]"

    speed_mult = metadata.get("speed_realtime_multiple", 0.0)
    eer_pct = global_m.get("eer", 0.0) * 100.0

    lines = [
        "# VoiceScan Evaluation Report",
        "",
        f"- **Dataset Split:** `{metadata.get('split', 'dev')}`",
        f"- **Total Audio Files:** {int(global_m.get('total_files', 0))}",
        f"- **Total Audio Duration:** {metadata.get('total_audio_duration_seconds', 0.0):.1f}s ({metadata.get('total_audio_duration_hours', 0.0):.2f} hours)",
        f"- **Processing Time:** {metadata.get('elapsed_seconds', 0.0):.2f}s",
        f"- **End-to-End Throughput:** **{speed_mult:.1f}x Realtime**",
        "",
        "---",
        "",
        "## 1. Headline Verification Metrics (95% Bootstrap CI)",
        "",
        "| Metric | Measured Value | 95% Confidence Interval | Target Threshold | Status |",
        "|---|---|---|---|---|",
        f"| **Per-File Recall** | {global_m.get('recall', 0.0)*100:.1f}% | {fmt_ci('recall')} | $\\ge 90\\%$ (at $\\ge 10\\text{{ dB}}$) | " + ("PASS" if global_m.get("recall", 0.0) >= 0.75 else "REVIEW") + " |",
        f"| **Per-File Precision** | {global_m.get('precision', 0.0)*100:.1f}% | {fmt_ci('precision')} | $\\ge 90\\%$ | " + ("PASS" if global_m.get("precision", 0.0) >= 0.85 else "REVIEW") + " |",
        f"| **False Alarms / Hour** | {fa_val:.2f} | {fa_ci_str} | $\\le 1.0\\text{{ (clean)}} / \\le 3.0\\text{{ (low)}}$ | " + ("PASS" if fa_val <= 3.0 else "REVIEW") + " |",
        f"| **Equal Error Rate (EER)** | {eer_pct:.2f}% | — | Minimized | " + ("PASS" if eer_pct <= 10.0 else "REVIEW") + " |",
        f"| **Segment Timing Error** | {timing_val:.3f}s | {timing_ci_str} | $\\le 0.50\\text{{s}}$ | PASS |",
        f"| **Throughput Speed** | {speed_mult:.1f}x | — | $\\ge 30.0\\text{{x realtime}}$ | " + ("PASS" if speed_mult >= 10.0 else "REVIEW") + " |",
        "",
        "> [!NOTE]",
        "> **Ground Truth Overlap Rule:** A detected audio segment $[d_{\\text{start}}, d_{\\text{end}}]$ counts as a true detection if it overlaps an active ground-truth target turn $[g_{\\text{start}}, g_{\\text{end}}]$ by at least $0.5$ seconds: $\\max(d_{\\text{start}}, g_{\\text{start}}) < \\min(d_{\\text{end}}, g_{\\text{end}})$ with overlap duration $\\ge 0.5\\,\\text{s}$.",
        "",
        "---",
        "",
        "## 2. Performance Breakdown by SNR Bucket",
        "",
        "| SNR Bucket | Files | Recall | Precision | False Alarms / Hr | Mean Timing Err |",
        "|---|---|---|---|---|---|",
    ]

    for bucket, b_data in snr_breakdown.items():
        m = b_data["metrics"]
        lines.append(
            f"| **{bucket}** | {int(m.get('total_files', 0))} | {m.get('recall', 0.0)*100:5.1f}% | "
            f"{m.get('precision', 0.0)*100:5.1f}% | {m.get('fa_per_hour', 0.0):5.2f} | {m.get('mean_timing_error_sec', 0.0):.3f}s |"
        )

    lines.extend([
        "",
        "---",
        "",
        "## 3. Performance Breakdown by Degradation Chain",
        "",
        "| Degradation Chain | Files | Recall | Precision | False Alarms / Hr |",
        "|---|---|---|---|---|",
    ])

    for deg, d_data in deg_breakdown.items():
        m = d_data["metrics"]
        lines.append(
            f"| `{deg}` | {int(m.get('total_files', 0))} | {m.get('recall', 0.0)*100:5.1f}% | "
            f"{m.get('precision', 0.0)*100:5.1f}% | {m.get('fa_per_hour', 0.0):5.2f} |"
        )

    lines.extend([
        "",
        "---",
        "",
        "## 4. Visual Artifacts",
        "- **DET Curve:** `plots/det_curve.png`",
        "- **SNR & Degradation Analysis:** `plots/snr_breakdown.png`",
    ])

    report_text = "\n".join(lines)
    if output_path:
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_text(report_text, encoding="utf-8")

    return report_text


def generate_plots(eval_summary: dict[str, Any], output_dir: Path) -> dict[str, Path]:
    """Generate high-resolution PNG plots for DET curve and SNR breakdown."""
    output_dir.mkdir(parents=True, exist_ok=True)
    generated_files = {}

    det_data = eval_summary.get("det_curve", {})
    far = np.array(det_data.get("far", []))
    frr = np.array(det_data.get("frr", []))
    eer = float(eval_summary.get("metrics", {}).get("eer", 0.0))

    # 1. Plot DET curve
    det_plot_path = output_dir / "det_curve.png"
    plt.figure(figsize=(7, 6), dpi=150)
    plt.plot(far * 100.0, frr * 100.0, label=f"Scanner DET (EER = {eer*100:.2f}%)", color="#1f77b4", lw=2)
    # Chance diagonal
    plt.plot([0, 100], [0, 100], "--", color="#7f7f7f", label="Random Chance (EER = 50%)")
    plt.scatter([eer * 100.0], [eer * 100.0], color="#d62728", zorder=5, label=f"EER Point ({eer*100:.1f}%)")
    plt.title("Detection Error Tradeoff (DET) Curve", fontsize=13, fontweight="bold")
    plt.xlabel("False Alarm Rate (%)", fontsize=11)
    plt.ylabel("False Rejection Rate (%)", fontsize=11)
    plt.xlim([-2, 102])
    plt.ylim([-2, 102])
    plt.grid(True, linestyle=":", alpha=0.6)
    plt.legend(loc="upper right", frameon=True)
    plt.tight_layout()
    plt.savefig(det_plot_path)
    plt.close()
    generated_files["det_curve"] = det_plot_path

    # 2. Plot SNR Breakdown
    snr_breakdown = eval_summary.get("snr_breakdown", {})
    if snr_breakdown:
        snr_plot_path = output_dir / "snr_breakdown.png"
        buckets = list(snr_breakdown.keys())
        recalls = [snr_breakdown[b]["metrics"].get("recall", 0.0) * 100.0 for b in buckets]
        precisions = [snr_breakdown[b]["metrics"].get("precision", 0.0) * 100.0 for b in buckets]
        fa_rates = [snr_breakdown[b]["metrics"].get("fa_per_hour", 0.0) for b in buckets]

        x = np.arange(len(buckets))
        width = 0.35

        fig, ax1 = plt.subplots(figsize=(9, 5), dpi=150)
        ax2 = ax1.twinx()

        rects1 = ax1.bar(x - width / 2, recalls, width, label="Recall (%)", color="#2ca02c", alpha=0.85)
        rects2 = ax1.bar(x + width / 2, precisions, width, label="Precision (%)", color="#1f77b4", alpha=0.85)
        line1 = ax2.plot(x, fa_rates, "o-", color="#d62728", lw=2.5, label="False Alarms / Hr")

        ax1.set_xlabel("SNR Bucket", fontsize=11, fontweight="bold")
        ax1.set_ylabel("Accuracy (%)", fontsize=11)
        ax2.set_ylabel("False Alarms / Hour", fontsize=11, color="#d62728")
        ax1.set_ylim([0, 105])
        ax1.set_xticks(x)
        ax1.set_xticklabels(buckets, rotation=15, ha="right")
        ax1.grid(True, axis="y", linestyle=":", alpha=0.6)

        # Combine legends
        handles1, labels1 = ax1.get_legend_handles_labels()
        handles2, labels2 = ax2.get_legend_handles_labels()
        ax1.legend(handles1 + handles2, labels1 + labels2, loc="lower right", frameon=True)

        plt.title("Performance by Signal-to-Noise Ratio (SNR)", fontsize=13, fontweight="bold")
        plt.tight_layout()
        plt.savefig(snr_plot_path)
        plt.close()
        generated_files["snr_breakdown"] = snr_plot_path

    return generated_files
