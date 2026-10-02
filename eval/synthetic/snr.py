"""
snr.py — Speech-active region SNR computation and signal mixing.

Defines mathematical formulation for SNR calculation strictly over speech-active regions:
P_speech = (1 / |M|) * sum_{t in M} s(t)^2
P_noise  = (1 / |M|) * sum_{t in M} n(t)^2
SNR_dB   = 10 * log10(P_speech / P_noise)
"""

from __future__ import annotations

import numpy as np


def detect_speech_activity(
    audio: np.ndarray,
    sample_rate: int = 16000,
    frame_ms: float = 25.0,
    hop_ms: float = 10.0,
    energy_threshold_db: float = -35.0,
) -> np.ndarray:
    """Compute sample-level boolean mask of speech activity using frame RMS energy."""
    frame_len = int(sample_rate * frame_ms / 1000.0)
    hop_len = int(sample_rate * hop_ms / 1000.0)
    num_samples = len(audio)

    if num_samples < frame_len:
        return np.abs(audio) > 1e-4

    num_frames = 1 + int(np.floor((num_samples - frame_len) / hop_len))
    mask = np.zeros(num_samples, dtype=bool)

    peak = np.max(np.abs(audio))
    if peak < 1e-6:
        return mask

    for i in range(num_frames):
        start = i * hop_len
        end = start + frame_len
        frame = audio[start:end]
        rms = np.sqrt(np.mean(frame**2) + 1e-12)
        rms_db = 20.0 * np.log10(rms / (peak + 1e-12))
        if rms_db >= energy_threshold_db:
            mask[start:end] = True

    if not np.any(mask):
        threshold = np.percentile(np.abs(audio), 50)
        mask = np.abs(audio) > threshold

    return mask


def compute_active_power(signal: np.ndarray, mask: np.ndarray) -> float:
    """Compute average signal power over active regions (where mask is True)."""
    assert len(signal) == len(mask), "Signal and mask length mismatch."
    active_samples = signal[mask]
    if len(active_samples) == 0:
        return float(np.mean(signal**2) + 1e-12)
    return float(np.mean(active_samples**2) + 1e-12)


def align_or_tile_noise(noise: np.ndarray, target_length: int, rng: np.random.Generator | None = None) -> np.ndarray:
    """Tile, slice, or randomly offset noise to match target_length exactly."""
    if len(noise) == target_length:
        return noise.copy()
    if len(noise) > target_length:
        max_start = len(noise) - target_length
        start = rng.integers(0, max_start + 1) if rng is not None else 0
        return noise[start : start + target_length].copy()

    repeats = int(np.ceil(target_length / len(noise)))
    tiled = np.tile(noise, repeats)
    return tiled[:target_length].copy()


def mix_at_snr(
    speech: np.ndarray,
    noise: np.ndarray,
    target_snr_db: float,
    speech_mask: np.ndarray | None = None,
    rng: np.random.Generator | None = None,
) -> tuple[np.ndarray, np.ndarray, np.ndarray, float]:
    """Mix speech and noise at a specified target SNR computed strictly over speech-active regions.

    Returns:
        mixed: Final normalized mixed audio array (speech + noise).
        scaled_speech: The speech component as mixed.
        scaled_noise: The noise component as mixed.
        measured_snr_db: Verification measurement of actual SNR in speech regions.
    """
    speech = speech.astype(np.float32)
    noise = noise.astype(np.float32)
    target_length = len(speech)

    aligned_noise = align_or_tile_noise(noise, target_length, rng=rng)

    if speech_mask is None:
        speech_mask = detect_speech_activity(speech)

    p_speech = compute_active_power(speech, speech_mask)
    p_noise = compute_active_power(aligned_noise, speech_mask)

    # Target: 10 * log10( P_speech / (k^2 * P_noise) ) = target_snr_db
    snr_ratio = 10.0 ** (target_snr_db / 10.0)
    k = np.sqrt(p_speech / (p_noise * snr_ratio))
    scaled_noise = (aligned_noise * k).astype(np.float32)
    scaled_speech = speech.copy()

    # Headroom protection without altering SNR:
    # Scaling both speech and noise by the same factor c preserves P_s / P_n exactly.
    unscaled_mix = scaled_speech + scaled_noise
    peak = np.max(np.abs(unscaled_mix))
    if peak > 0.95:
        headroom_factor = 0.95 / peak
        scaled_speech = (scaled_speech * headroom_factor).astype(np.float32)
        scaled_noise = (scaled_noise * headroom_factor).astype(np.float32)

    mixed = scaled_speech + scaled_noise

    # Measured SNR in speech-active regions
    p_scaled_speech = compute_active_power(scaled_speech, speech_mask)
    p_scaled_noise = compute_active_power(scaled_noise, speech_mask)
    measured_snr_db = float(10.0 * np.log10(p_scaled_speech / p_scaled_noise))

    return mixed, scaled_speech, scaled_noise, measured_snr_db
