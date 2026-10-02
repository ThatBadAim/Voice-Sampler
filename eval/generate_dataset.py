#!/usr/bin/env python3
"""
generate_dataset.py — CLI for synthetic audio dataset generation.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

# Ensure repository root is on sys.path
repo_root = Path(__file__).resolve().parent.parent
if str(repo_root) not in sys.path:
    sys.path.insert(0, str(repo_root))

from eval.synthetic.dataset_config import DatasetConfig
from eval.synthetic.generator import SyntheticDataGenerator, print_summary_table, validate_test_directory


def main() -> int:
    parser = argparse.ArgumentParser(
        description="VoiceScan Synthetic Test Data Generator",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter,
    )
    parser.add_argument("--config", type=str, default=None, help="Path to JSON configuration file")
    parser.add_argument("--speech-dir", type=str, default=None, help="Directory containing speaker subfolders")
    parser.add_argument("--game-dir", type=str, default=None, help="Directory containing game audio files")
    parser.add_argument("--split", choices=["dev", "test", "both"], default="dev", help="Dataset split to generate")
    parser.add_argument("--output-dir", type=str, default="eval/dev_dataset", help="Output directory for dev split")
    parser.add_argument(
        "--test-output-dir",
        type=str,
        default="/tmp/voicescan_test_dataset",
        help="Output directory for test split (must be outside the repository)",
    )
    parser.add_argument("--num-clips", type=int, default=50, help="Number of mixed clips to generate per split")
    parser.add_argument("--seed", type=int, default=42, help="Deterministic random seed")

    args = parser.parse_args()

    if args.config:
        config = DatasetConfig.from_json(args.config)
    else:
        config = DatasetConfig()

    if args.speech_dir:
        config.speech_dir = args.speech_dir
    if args.game_dir:
        config.game_dir = args.game_dir
    if args.output_dir:
        config.output_dir = args.output_dir
    if args.test_output_dir:
        config.test_output_dir = args.test_output_dir
    if args.seed is not None:
        config.seed = args.seed

    generator = SyntheticDataGenerator(config)

    speech_path = Path(config.speech_dir)
    game_path = Path(config.game_dir)

    speaker_clips = generator.discover_speech_data(speech_path)
    game_clips = generator.discover_game_audio(game_path)

    if not speaker_clips:
        print(f"[ERROR] No speaker audio found in '{speech_path}'.")
        print("Please provide speaker clips organized in subfolders: <speech_dir>/<speaker_id>/*.wav")
        return 1

    print(f"[INFO] Discovered {len(speaker_clips)} speakers with audio.")
    print(f"[INFO] Discovered {len(game_clips)} game audio clips.")

    dev_speakers, test_speakers = generator.split_speakers(speaker_clips)
    print(f"[INFO] Split: {len(dev_speakers)} dev speakers, {len(test_speakers)} test speakers.")

    if args.split in ("dev", "both"):
        dev_out = Path(config.output_dir)
        print(f"\n[INFO] Generating DEV split at: {dev_out.resolve()}")
        dev_gt = generator.generate_split("dev", dev_speakers, game_clips, dev_out, num_clips=args.num_clips)
        print_summary_table(dev_gt, "dev")

    if args.split in ("test", "both"):
        test_out = Path(config.test_output_dir)
        # Enforce security check
        validate_test_directory(test_out)
        print(f"\n[INFO] Generating TEST split at: {test_out.resolve()}")
        test_gt = generator.generate_split("test", test_speakers, game_clips, test_out, num_clips=args.num_clips)
        print_summary_table(test_gt, "test")

    return 0


if __name__ == "__main__":
    sys.exit(main())
