"""
test_snr.py — Unit tests verifying SNR accuracy in speech-active regions.

Acceptance requirement: Measured SNR must be within 0.5 dB of target SNR (-5, 0, 5, 10, 20 dB).
"""

import numpy as np
import pytest
from eval.synthetic.snr import (
    mix_at_snr,
    detect_speech_activity,
    compute_active_power,
)


@pytest.fixture
def speech_and_noise() -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """Create speech signal with silence gaps and background noise."""
    sr = 16000
    duration_s = 5.0
    total_samples = int(sr * duration_s)
    t = np.linspace(0, duration_s, total_samples, endpoint=False)

    # 1. Speech signal: bursts of multi-tone harmonic voice separated by silence
    speech = np.zeros(total_samples, dtype=np.float32)
    # Burst 1: 0.5s to 2.0s
    idx1 = (t >= 0.5) & (t < 2.0)
    speech[idx1] = 0.4 * np.sin(2 * np.pi * 220 * t[idx1]) + 0.2 * np.sin(2 * np.pi * 440 * t[idx1])
    # Burst 2: 3.0s to 4.5s
    idx2 = (t >= 3.0) & (t < 4.5)
    speech[idx2] = 0.5 * np.sin(2 * np.pi * 300 * t[idx2]) + 0.25 * np.sin(2 * np.pi * 600 * t[idx2])

    # 2. Continuous game-like noise: pink/brown noise plus rumble
    rng = np.random.default_rng(12345)
    noise = rng.normal(0, 0.2, total_samples).astype(np.float32)
    noise += 0.15 * np.sin(2 * np.pi * 60 * t).astype(np.float32)

    # 3. Known ground-truth speech mask
    true_mask = idx1 | idx2
    return speech, noise, true_mask


@pytest.mark.parametrize("target_snr_db", [-5.0, 0.0, 5.0, 10.0, 20.0])
def test_snr_within_tolerance_with_true_mask(speech_and_noise, target_snr_db):
    speech, noise, true_mask = speech_and_noise

    mixed, scaled_speech, scaled_noise, measured_snr = mix_at_snr(
        speech=speech,
        noise=noise,
        target_snr_db=target_snr_db,
        speech_mask=true_mask,
    )

    # Re-verify independently on actual components mixed
    p_speech = compute_active_power(scaled_speech, true_mask)
    p_noise = compute_active_power(scaled_noise, true_mask)
    independent_snr = 10.0 * np.log10(p_speech / p_noise)

    delta = abs(independent_snr - target_snr_db)
    assert delta <= 0.5, f"Target {target_snr_db} dB, measured {independent_snr:.4f} dB, error {delta:.4f} > 0.5 dB"
    assert abs(measured_snr - target_snr_db) <= 0.5


@pytest.mark.parametrize("target_snr_db", [-5.0, 0.0, 5.0, 10.0, 20.0])
def test_snr_within_tolerance_with_detected_mask(speech_and_noise, target_snr_db):
    speech, noise, _ = speech_and_noise

    detected_mask = detect_speech_activity(speech, sample_rate=16000)
    assert np.any(detected_mask), "VAD mask should detect active speech"

    mixed, scaled_speech, scaled_noise, measured_snr = mix_at_snr(
        speech=speech,
        noise=noise,
        target_snr_db=target_snr_db,
        speech_mask=detected_mask,
    )

    p_speech = compute_active_power(scaled_speech, detected_mask)
    p_noise = compute_active_power(scaled_noise, detected_mask)
    independent_snr = 10.0 * np.log10(p_speech / p_noise)

    delta = abs(independent_snr - target_snr_db)
    assert delta <= 0.5, f"Target {target_snr_db} dB, measured {independent_snr:.4f} dB, error {delta:.4f} > 0.5 dB"


def test_no_clipping_occurs(speech_and_noise):
    speech, noise, true_mask = speech_and_noise
    mixed, _, _, _ = mix_at_snr(speech=speech, noise=noise, target_snr_db=-10.0, speech_mask=true_mask)
    assert np.max(np.abs(mixed)) <= 0.96, "Peak must not exceed headroom limit of 0.96"
