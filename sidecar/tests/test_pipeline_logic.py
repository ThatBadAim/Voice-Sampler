import os
import sys
import unittest

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from pipeline_logic import (  # noqa: E402
    DiarizationWindow,
    activity_from_probs,
    asr_language,
    is_hallucination,
    merge_speech_chunks,
    moderation_from_scores,
    regex_moderation,
    speaker_for_span,
    split_line_by_speaker,
    stitch_windows,
    window_starts,
)

FRAME = 0.5


def window(start: float, *speakers: str) -> DiarizationWindow:
    """One string per local speaker, one character per 0.5 s frame: '#' active, '.' silent."""
    active = np.array([[c == "#" for c in s] for s in speakers]).T
    return DiarizationWindow(start=start, frame_seconds=FRAME, active=active)


class MergeSpeechChunksTests(unittest.TestCase):
    def test_drops_short_speech_and_joins_close_speech(self) -> None:
        chunks = merge_speech_chunks([(0.0, 0.1), (1.0, 2.0), (2.3, 3.0), (5.0, 6.0)], 0.35, 0.5, 30.0)
        self.assertEqual([(1.0, 3.0), (5.0, 6.0)], chunks)

    def test_respects_max_chunk_size_and_cuts_long_speech(self) -> None:
        chunks = merge_speech_chunks([(0.0, 20.0), (20.2, 32.0), (40.0, 105.0)], 0.35, 0.5, 30.0)
        self.assertEqual([(0.0, 20.0), (20.2, 32.0), (40.0, 70.0), (70.0, 100.0), (100.0, 105.0)], chunks)

    def test_applies_time_offset(self) -> None:
        self.assertEqual([(101.0, 102.0)], merge_speech_chunks([(1.0, 2.0)], 0.35, 0.5, 30.0, time_offset=100.0))


class DiarizationStitchingTests(unittest.TestCase):
    def test_window_starts_cover_the_audio_with_overlap(self) -> None:
        self.assertEqual([0.0], window_starts(60.0, 90.0, 15.0))
        self.assertEqual([0.0, 75.0, 150.0], window_starts(200.0, 90.0, 15.0))

    def test_argmax_fallback_when_no_class_passes_threshold(self) -> None:
        probs = np.array([[0.4, 0.1], [0.2, 0.3]])
        self.assertEqual([[True, False], [False, True]], activity_from_probs(probs).tolist())
        self.assertFalse(activity_from_probs(np.array([[0.9, 0.1]]))[0, 1])

    def test_keeps_speaker_labels_consistent_when_local_order_flips(self) -> None:
        # Window 2 starts at 3 s; its local speaker 1 is window 1's speaker 0 (both active in the 3-5 s overlap).
        first = window(0.0, "##########", "..........")
        second = window(3.0, "....######", "####......")
        turns = stitch_windows([first, second], total_seconds=8.0)
        self.assertEqual(
            [{"start": 0.0, "end": 5.0, "speaker": "SPEAKER_00"}, {"start": 5.0, "end": 8.0, "speaker": "SPEAKER_01"}],
            turns,
        )

    def test_each_window_owns_frames_up_to_the_middle_of_the_overlap(self) -> None:
        # Overlap 3-5 s: window 1 hears A until 5 s, window 2 hears A until 4.5 s and B from 4.5 s. After the 4 s
        # boundary window 2 decides, so A ends at 4.5 s instead of overlapping B until 5 s.
        first = window(0.0, "##########")
        second = window(3.0, "###.......", "...#######")
        turns = stitch_windows([first, second], total_seconds=8.0)
        self.assertEqual(
            [("SPEAKER_00", 0.0, 4.5), ("SPEAKER_01", 4.5, 8.0)],
            [(t["speaker"], t["start"], t["end"]) for t in turns],
        )

    def test_speaker_silent_in_overlap_gets_a_new_label(self) -> None:
        first = window(0.0, "####......")
        second = window(3.0, "......####")
        turns = stitch_windows([first, second], total_seconds=8.0)
        self.assertEqual(["SPEAKER_00", "SPEAKER_01"], [t["speaker"] for t in turns])

    def test_applies_time_offset_and_drops_tiny_turns(self) -> None:
        win = DiarizationWindow(start=0.0, frame_seconds=0.05, active=np.array([[True], [False], [True], [True], [True]]))
        turns = stitch_windows([win], total_seconds=0.25, time_offset=60.0)
        self.assertEqual([{"start": 60.0, "end": 60.25, "speaker": "SPEAKER_00"}], turns)


class SpeakerAssignmentTests(unittest.TestCase):
    TURNS = [
        {"start": 0.0, "end": 4.0, "speaker": "SPEAKER_00"},
        {"start": 4.0, "end": 9.0, "speaker": "SPEAKER_01"},
    ]

    def test_largest_overlap_wins_not_the_midpoint(self) -> None:
        # Midpoint 4.5 is in SPEAKER_01's turn, but SPEAKER_00 covers 3 of the 4 seconds.
        self.assertEqual("SPEAKER_00", speaker_for_span(1.0, 5.0, self.TURNS[:1] + [{"start": 4.0, "end": 4.6, "speaker": "SPEAKER_01"}]))
        self.assertEqual("SPEAKER_01", speaker_for_span(3.5, 8.0, self.TURNS))

    def test_nearest_turn_when_nothing_overlaps_and_default_without_turns(self) -> None:
        self.assertEqual("SPEAKER_01", speaker_for_span(9.5, 10.0, self.TURNS))
        self.assertEqual("SPEAKER_00", speaker_for_span(1.0, 2.0, []))

    def test_splits_line_where_speaker_changes(self) -> None:
        words = [
            {"word": "watch", "start": 2.0, "end": 2.4},
            {"word": "left", "start": 2.5, "end": 3.0},
            {"word": "I", "start": 4.2, "end": 4.5},
            {"word": "see", "start": 4.6, "end": 5.0},
            {"word": "him", "start": 5.1, "end": 5.5},
        ]
        runs = split_line_by_speaker(words, self.TURNS)
        self.assertEqual(
            [("SPEAKER_00", 2.0, 3.0, "watch left"), ("SPEAKER_01", 4.2, 5.5, "I see him")],
            [(r["speaker"], r["start"], r["end"], r["text"]) for r in runs],
        )

    def test_short_run_joins_its_neighbour_and_untimed_words_follow(self) -> None:
        words = [
            {"word": "one", "start": 1.0, "end": 1.6},
            {"word": "two", "start": 1.7, "end": 2.3},
            {"word": "uh", "start": 4.0, "end": 4.1},
            {"word": "42"},
            {"word": "three", "start": 2.5, "end": 3.0},
        ]
        runs = split_line_by_speaker(words, self.TURNS)
        self.assertEqual(1, len(runs))
        self.assertEqual(("SPEAKER_00", "one two uh 42 three"), (runs[0]["speaker"], runs[0]["text"]))

    def test_no_timed_words_gives_no_runs(self) -> None:
        self.assertEqual([], split_line_by_speaker([{"word": "hi"}], self.TURNS))


class FilterAndModerationTests(unittest.TestCase):
    def test_hallucination_rules(self) -> None:
        self.assertTrue(is_hallucination("[Music]", 1.0, 0.0, -0.1, 0.6, -1.0))
        self.assertTrue(is_hallucination("go go go go go go go go", 3.1, 0.0, -0.1, 0.6, -1.0))
        self.assertTrue(is_hallucination("thanks for watching", 1.2, 0.8, -1.4, 0.6, -1.0))
        self.assertFalse(is_hallucination("thanks for watching", 1.2, 0.8, -0.5, 0.6, -1.0))
        self.assertFalse(is_hallucination("push b now", 1.1, 0.1, -0.3, 0.6, -1.0))

    def test_language_comes_from_an_alignment_language_code_only(self) -> None:
        self.assertEqual("en", asr_language("en"))
        self.assertEqual("de", asr_language(" DE "))
        self.assertIsNone(asr_language("jonatasgrosman/wav2vec2-large-xlsr-53-english"))
        self.assertIsNone(asr_language("/models/align.bin"))

    def test_moderation_scores_and_regex_safety_net(self) -> None:
        result = moderation_from_scores("you idiot", {"toxicity": 0.91, "threat": 0.02}, 0.5)
        self.assertEqual((True, ["toxicity"]), (result["is_offensive"], result["violations"]))
        net = moderation_from_scores("kill you", {"toxicity": 0.1}, 0.5)
        self.assertEqual(["toxic_content"], net["violations"])
        self.assertEqual(["threat"], regex_moderation("i will kill you")["violations"])
        self.assertFalse(regex_moderation("nice shot")["is_offensive"])


if __name__ == "__main__":
    unittest.main()
