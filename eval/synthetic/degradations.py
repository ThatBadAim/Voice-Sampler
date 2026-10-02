"""
degradations.py — Degradation chains simulating gameplay voice chat environments.

Includes:
- Opus encode/decode via FFmpeg at configurable bitrates (16k, 24k, 32k, etc.)
- Automatic Gain Control (AGC) dynamic range compression simulation
- Band-limiting filter (telephony / narrow band VoIP)
- Light noise suppression / noise gate simulation
"""

from __future__ import annotations

import subprocess
import logging
import numpy as np
import scipy.signal

logger = logging.getLogger("eval.degradations")


def apply_opus_degradation(
    audio: np.ndarray,
    sample_rate: int = 16000,
    bitrate_kbps: int = 24,
) -> np.ndarray:
    """Encode audio to Ogg Opus and decode back via FFmpeg to simulate VoIP codecs."""
    # Ensure float32 in [-1, 1]
    audio_f32 = audio.astype(np.float32)
    raw_pcm = (audio_f32 * 32767.0).clip(-32768.0, 32767.0).astype(np.int16).tobytes()

    cmd = [
        "ffmpeg",
        "-hide_banner",
        "-loglevel", "error",
        "-f", "s16le",
        "-ar", str(sample_rate),
        "-ac", "1",
        "-i", "pipe:0",
        "-c:a", "libopus",
        "-b:a", f"{bitrate_kbps}k",
        "-f", "opus",
        "pipe:1",
    ]

    decode_cmd = [
        "ffmpeg",
        "-hide_banner",
        "-loglevel", "error",
        "-f", "ogg",
        "-i", "pipe:0",
        "-f", "s16le",
        "-ar", str(sample_rate),
        "-ac", "1",
        "pipe:1",
    ]

    try:
        encode_proc = subprocess.Popen(
            cmd,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        decode_proc = subprocess.Popen(
            decode_cmd,
            stdin=encode_proc.stdout,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )

        encode_proc.stdin.write(raw_pcm)
        encode_proc.stdin.close()

        decoded_pcm, decode_err = decode_proc.communicate()
        encode_proc.wait()

        if decode_proc.returncode != 0:
            logger.warning("FFmpeg opus decode error: %s. Returning original audio.", decode_err.decode())
            return audio_f32

        decoded_i16 = np.frombuffer(decoded_pcm, dtype=np.int16)
        decoded_f32 = (decoded_i16.astype(np.float32) / 32768.0)

        # Match exact input length (accounting for codec padding / delay)
        if len(decoded_f32) < len(audio_f32):
            decoded_f32 = np.pad(decoded_f32, (0, len(audio_f32) - len(decoded_f32)))
        else:
            decoded_f32 = decoded_f32[: len(audio_f32)]

        return decoded_f32.astype(np.float32)

    except Exception as exc:
        logger.warning("Opus degradation failed (%s). Falling back to original audio.", exc)
        return audio_f32


def apply_agc(
    audio: np.ndarray,
    sample_rate: int = 16000,
    target_rms_db: float = -18.0,
    attack_ms: float = 10.0,
    release_ms: float = 150.0,
    max_gain_db: float = 18.0,
) -> np.ndarray:
    """Automatic Gain Control (AGC) dynamic compression simulation."""
    audio = audio.astype(np.float32)
    alpha_attack = np.exp(-1.0 / (sample_rate * attack_ms / 1000.0))
    alpha_release = np.exp(-1.0 / (sample_rate * release_ms / 1000.0))

    envelope = np.zeros(len(audio), dtype=np.float32)
    env = 0.0

    abs_audio = np.abs(audio)
    for i in range(len(audio)):
        val = abs_audio[i]
        if val > env:
            env = alpha_attack * env + (1.0 - alpha_attack) * val
        else:
            env = alpha_release * env + (1.0 - alpha_release) * val
        envelope[i] = env

    target_rms = 10.0 ** (target_rms_db / 20.0)
    max_gain = 10.0 ** (max_gain_db / 20.0)

    # Avoid division by zero
    gain = target_rms / (envelope + 1e-4)
    gain = np.clip(gain, 0.2, max_gain)

    out = audio * gain
    peak = np.max(np.abs(out))
    if peak > 0.95:
        out = out * (0.95 / peak)

    return out.astype(np.float32)


def apply_bandlimit(
    audio: np.ndarray,
    sample_rate: int = 16000,
    low_hz: float = 300.0,
    high_hz: float = 3400.0,
) -> np.ndarray:
    """Butterworth bandpass filter simulating telephone / low-fidelity voice channels."""
    nyquist = sample_rate / 2.0
    low = low_hz / nyquist
    high = high_hz / nyquist
    sos = scipy.signal.butter(4, [low, high], btype="bandpass", output="sos")
    filtered = scipy.signal.sosfilt(sos, audio)
    return filtered.astype(np.float32)


def apply_light_noise_suppression(
    audio: np.ndarray,
    sample_rate: int = 16000,
    frame_ms: float = 20.0,
    threshold_db: float = -30.0,
    suppression_gain_db: float = -12.0,
) -> np.ndarray:
    """Light noise suppression / soft noise gate simulation."""
    frame_len = int(sample_rate * frame_ms / 1000.0)
    num_frames = int(np.ceil(len(audio) / frame_len))

    padded_len = num_frames * frame_len
    padded = np.pad(audio, (0, padded_len - len(audio)))
    frames = padded.reshape((num_frames, frame_len))

    rms = np.sqrt(np.mean(frames**2, axis=1) + 1e-12)
    peak = np.max(rms) + 1e-12
    rms_db = 20.0 * np.log10(rms / peak)

    suppression_factor = 10.0 ** (suppression_gain_db / 20.0)
    gain_per_frame = np.where(rms_db < threshold_db, suppression_factor, 1.0)

    processed_frames = frames * gain_per_frame[:, None]
    result = processed_frames.flatten()[: len(audio)]
    return result.astype(np.float32)


def apply_degradation_chain(
    audio: np.ndarray,
    sample_rate: int,
    degradations: list[str],
) -> np.ndarray:
    """Apply a sequential chain of degradations to audio."""
    current = audio.copy()
    for deg in degradations:
        deg_lower = deg.lower().strip()
        if deg_lower == "none":
            continue
        elif deg_lower == "opus_16k":
            current = apply_opus_degradation(current, sample_rate=sample_rate, bitrate_kbps=16)
        elif deg_lower == "opus_24k":
            current = apply_opus_degradation(current, sample_rate=sample_rate, bitrate_kbps=24)
        elif deg_lower == "opus_32k":
            current = apply_opus_degradation(current, sample_rate=sample_rate, bitrate_kbps=32)
        elif deg_lower == "agc":
            current = apply_agc(current, sample_rate=sample_rate)
        elif deg_lower == "bandlimit":
            current = apply_bandlimit(current, sample_rate=sample_rate)
        elif deg_lower in ("noise_suppression", "light_denoise"):
            current = apply_light_noise_suppression(current, sample_rate=sample_rate)
        else:
            logger.warning("Unrecognized degradation: %s. Skipping.", deg)

    return current
