"""
real_speech_eval.py — end-to-end engine evaluation on real read speech (LibriSpeech dev-clean, CC BY 4.0).

The bundled synthetic dev set contains no real speech, so it cannot rank engine configurations.
This harness builds multi-speaker "voice chat" files with exact ground truth from real utterances,
runs the real CLI on them, and scores segments.

  python3 eval/real_speech_eval.py fetch --data-dir D        # download ~8 utterances x 40 speakers (~50 MB)
  python3 eval/real_speech_eval.py build --data-dir D        # enrollment clips + degraded mixtures + truth.json
  python3 eval/real_speech_eval.py run   --data-dir D --label baseline -- --no-clustering --smooth-radius 1

Keep D outside the repository. These are development data: never tune against held-out test data.
"""

from __future__ import annotations

import argparse
import collections
import concurrent.futures as cf
import json
import subprocess
import sys
import tempfile
import urllib.request
from pathlib import Path

import numpy as np
import soundfile as sf

REPO = Path(__file__).resolve().parent.parent
SR = 16000
ROWS_URL = (
    "https://datasets-server.huggingface.co/rows?dataset=openslr/librispeech_asr"
    "&config=clean&split=validation&length=100&offset="
)
CONDITIONS = ["clean", "opus24", "game10", "overlap5", "overlap0", "game0_opus24"]
FILES_PER_TARGET = 12  # half target-present, half target-absent
TARGET_COUNT = 20
CLI_DLL = REPO / "engine/VoiceScan.Cli/bin/Release/net10.0/VoiceScan.Cli.dll"


def fetch(data_dir: Path) -> None:
    out = data_dir / "librispeech"
    out.mkdir(parents=True, exist_ok=True)

    def page(offset: int):
        for _ in range(4):
            try:
                return json.load(urllib.request.urlopen(ROWS_URL + str(offset), timeout=60))["rows"]
            except Exception as exc:  # network retry
                err = exc
        raise err

    with cf.ThreadPoolExecutor(6) as pool:
        rows = [r["row"] for p in pool.map(page, range(0, 2700, 100)) for r in p]
    by_speaker = collections.defaultdict(list)
    for row in rows:
        by_speaker[row["speaker_id"]].append(row)
    chosen = [r for rs in by_speaker.values() for r in rs[:8]]

    def get(row):
        target = out / f"{row['speaker_id']}_{row['id']}.flac"
        if not target.exists():
            urllib.request.urlretrieve(row["audio"][0]["src"], target)

    with cf.ThreadPoolExecutor(8) as pool:
        list(pool.map(get, chosen))
    print(f"fetched {len(chosen)} utterances from {len(by_speaker)} speakers into {out}")


def opus(audio: np.ndarray, kbps: int) -> np.ndarray:
    with tempfile.TemporaryDirectory() as tmp:
        sf.write(f"{tmp}/i.wav", audio, SR)
        subprocess.run(["ffmpeg", "-y", "-v", "error", "-i", f"{tmp}/i.wav", "-c:a", "libopus",
                        "-b:a", f"{kbps}k", "-application", "voip", f"{tmp}/o.ogg"], check=True)
        subprocess.run(["ffmpeg", "-y", "-v", "error", "-i", f"{tmp}/o.ogg", "-ar", str(SR), "-ac", "1",
                        f"{tmp}/r.wav"], check=True)
        return sf.read(f"{tmp}/r.wav", dtype="float32")[0]


def mix(audio: np.ndarray, noise: np.ndarray, snr_db: float) -> np.ndarray:
    noise = np.tile(noise, len(audio) // len(noise) + 1)[: len(audio)]
    gain = np.sqrt(np.mean(audio**2) / ((np.mean(noise**2) + 1e-12) * 10 ** (snr_db / 10)))
    return (audio + noise * gain).astype(np.float32)


def build(data_dir: Path, seed: int) -> None:
    rng = np.random.default_rng(seed)
    utts = collections.defaultdict(list)
    for path in sorted((data_dir / "librispeech").glob("*.flac")):
        audio, _ = sf.read(path, dtype="float32")
        utts[int(path.name.split("_")[0])].append(audio)
    speakers = sorted(utts)
    targets = speakers[:TARGET_COUNT]
    game = [sf.read(f, dtype="float32")[0] for f in sorted((REPO / "eval/data_config/game_audio").glob("*.wav"))]
    truth = []
    enroll_dir = data_dir / "enroll"
    enroll_dir.mkdir(exist_ok=True)

    for target in targets:
        gap = np.zeros(int(0.5 * SR), np.float32)
        sf.write(enroll_dir / f"{target}.wav", np.concatenate([x for u in utts[target][:3] for x in (u, gap)]), SR)
        files_dir = data_dir / "files" / str(target)
        files_dir.mkdir(parents=True, exist_ok=True)
        others = [s for s in speakers if s != target]

        for index in range(FILES_PER_TARGET):
            present = index % 2 == 0
            condition = CONDITIONS[(index // 2 + targets.index(target)) % len(CONDITIONS)]
            chunks, intervals, cursor = [], [], 0.0
            partners = list(rng.choice(others, size=3, replace=False))
            interferers = []
            while cursor < 45.0:
                is_target = present and rng.random() < 0.4
                speaker = target if is_target else int(rng.choice(partners))
                pool = utts[speaker][3:] if is_target else utts[speaker]
                utt = pool[rng.integers(len(pool))]
                if is_target:
                    intervals.append([round(cursor, 3), round(cursor + len(utt) / SR, 3)])
                    if condition.startswith("overlap"):
                        other = utts[int(rng.choice(others))][0]
                        utt = mix(utt, other, float(condition[len("overlap"):]))
                silence = np.zeros(int(rng.uniform(0.3, 1.2) * SR), np.float32)
                chunks += [utt, silence]
                cursor += (len(utt) + len(silence)) / SR
            audio = np.concatenate(chunks)
            if present and not intervals:  # guarantee at least one target turn
                utt = utts[target][3]
                intervals.append([0.0, round(len(utt) / SR, 3)])
                audio = np.concatenate([utt, np.zeros(SR // 2, np.float32), audio])
                intervals = [intervals[0]] + [[a + len(utt) / SR + 0.5, b + len(utt) / SR + 0.5] for a, b in intervals[1:]]
            if condition.startswith("game"):
                audio = mix(audio, game[index % len(game)], float(condition[4:].split("_")[0]))
            if condition.endswith("opus24"):
                audio = opus(audio, 24)
            name = f"f{index}_{condition}.wav"
            sf.write(files_dir / name, np.clip(audio, -1, 1), SR)
            truth.append({"target": target, "file": name, "condition": condition,
                          "duration": len(audio) / SR, "target_intervals": intervals if present else []})

    (data_dir / "truth.json").write_text(json.dumps(truth, indent=1))
    hours = sum(t["duration"] for t in truth) / 3600
    print(f"built {len(truth)} files ({hours:.2f} h) for {len(targets)} targets; present={sum(1 for t in truth if t['target_intervals'])}")


def overlap(a, b) -> float:
    return max(0.0, min(a[1], b[1]) - max(a[0], b[0]))


def run(data_dir: Path, label: str, scan_flags: list[str], model: str) -> dict:
    truth = json.loads((data_dir / "truth.json").read_text())
    by_target = collections.defaultdict(list)
    for entry in truth:
        by_target[entry["target"]].append(entry)
    results_dir = data_dir / "results" / label
    results_dir.mkdir(parents=True, exist_ok=True)
    db = data_dir / f"cache_{model}.db"
    stats = collections.defaultdict(lambda: collections.Counter())

    for target, entries in by_target.items():
        profile = data_dir / "profiles" / f"{model}_{target}.json"
        profile.parent.mkdir(exist_ok=True)
        base = ["dotnet", str(CLI_DLL)]
        if not profile.exists():
            subprocess.run(base + ["enroll", "--audio", str(data_dir / "enroll" / f"{target}.wav"), "--name", str(target),
                                   "--output", str(profile), "--model", model, "--db", str(db)],
                           cwd=REPO, check=True, capture_output=True)
        out = results_dir / f"{target}.json"
        subprocess.run(base + ["scan", "--input", str(data_dir / "files" / str(target)), "--profile", str(profile),
                               "--output", str(out), "--model", model, "--db", str(db)] + scan_flags,
                       cwd=REPO, check=True, capture_output=True)
        scanned = {Path(f["file_path"]).name: f for f in json.loads(out.read_text())["files"]}

        for entry in entries:
            file_result = scanned[entry["file"]]
            gt = entry["target_intervals"]
            for level, accepted in (("match", {"Match"}), ("possible", {"Match", "Possible"})):
                segs = [s for s in file_result["segments"] if s["verdict"] in accepted]
                s = stats[(level, entry["condition"])]
                s["hours"] += entry["duration"] / 3600
                s["present_files"] += bool(gt)
                s["absent_files"] += not gt
                s["present_detected"] += bool(gt) and any(any(overlap([g["start_time_seconds"], g["end_time_seconds"]], i) > 0.5 for i in gt) for g in segs)
                s["absent_flagged"] += (not gt) and bool(segs)
                s["gt_seconds"] += sum(b - a for a, b in gt)
                s["gt_covered"] += sum(min(b - a, sum(overlap([g["start_time_seconds"], g["end_time_seconds"]], [a, b]) for g in segs)) for a, b in gt)
                for g in segs:
                    seg = [g["start_time_seconds"], g["end_time_seconds"]]
                    if gt and any(overlap(seg, i) > 0 for i in gt):
                        s["seg_tp"] += 1
                    else:
                        s["seg_fa"] += 1

    return summarize(stats, label)


def summarize(stats, label: str) -> dict:
    print(f"\n=== {label} ===")
    summary = {}
    for level in ("match", "possible"):
        rows = {c: s for (lv, c), s in stats.items() if lv == level}
        total = sum(rows.values(), collections.Counter())
        print(f"-- verdict >= {level}: file recall / absent-file false-flag rate / seg FA per hour / target-time coverage")
        for cond in CONDITIONS + ["ALL"]:
            s = total if cond == "ALL" else rows.get(cond)
            if not s:
                continue
            line = (f"  {cond:14s} recall {100 * s['present_detected'] / max(1, s['present_files']):5.1f}%  "
                    f"absent flagged {100 * s['absent_flagged'] / max(1, s['absent_files']):5.1f}%  "
                    f"FA/hr {s['seg_fa'] / max(1e-9, s['hours']):6.1f}  coverage {100 * s['gt_covered'] / max(1e-9, s['gt_seconds']):5.1f}%")
            print(line)
            if cond == "ALL":
                summary[level] = {"recall": s["present_detected"] / max(1, s["present_files"]),
                                  "absent_flagged": s["absent_flagged"] / max(1, s["absent_files"]),
                                  "fa_per_hour": s["seg_fa"] / max(1e-9, s["hours"]),
                                  "coverage": s["gt_covered"] / max(1e-9, s["gt_seconds"])}
    return summary


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=["fetch", "build", "run"])
    parser.add_argument("--data-dir", type=Path, required=True)
    parser.add_argument("--label", default="run")
    parser.add_argument("--model", default="ecapa")
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("scan_flags", nargs="*", help="extra flags for `VoiceScan.Cli scan`, after --")
    args = parser.parse_args()
    if args.command == "fetch":
        fetch(args.data_dir)
    elif args.command == "build":
        build(args.data_dir, args.seed)
    else:
        summary = run(args.data_dir, args.label, args.scan_flags, args.model)
        (args.data_dir / "results" / args.label / "summary.json").write_text(json.dumps(summary, indent=1))
    return 0


if __name__ == "__main__":
    sys.exit(main())
