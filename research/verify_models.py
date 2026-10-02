"""
verify_models.py — Verification of speaker embedding models (Prompt 2).

Loads WeSpeaker ResNet34 and 3D-Speaker CAM++ ONNX models, extracts 80-dim
filterbank features from a test WAV, executes inference with CUDAExecutionProvider
(falling back to CPUExecutionProvider with a warning if CUDA is unavailable),
and asserts fixed-length 1D embedding vectors.
"""

from __future__ import annotations

import sys
import logging
from pathlib import Path
import numpy as np
import soundfile as sf
import onnxruntime as ort

logging.basicConfig(level=logging.INFO, format="%(levelname)s: %(message)s")
logger = logging.getLogger("verify_models")


def compute_fbank(
    audio: np.ndarray,
    sample_rate: int = 16000,
    n_mels: int = 80,
    frame_length_ms: float = 25.0,
    frame_shift_ms: float = 10.0,
) -> np.ndarray:
    """Compute 80-dim log-mel filterbank features with CMVN."""
    # Ensure float32 in [-1.0, 1.0]
    if audio.dtype != np.float32:
        audio = audio.astype(np.float32)
    if np.max(np.abs(audio)) > 1.0:
        audio = audio / np.max(np.abs(audio))

    # Pre-emphasis
    pre_emphasized = np.append(audio[0], audio[1:] - 0.97 * audio[:-1])

    frame_len = int(sample_rate * frame_length_ms / 1000.0)
    frame_step = int(sample_rate * frame_shift_ms / 1000.0)
    num_samples = len(pre_emphasized)

    if num_samples < frame_len:
        pre_emphasized = np.pad(pre_emphasized, (0, frame_len - num_samples))
        num_samples = frame_len

    num_frames = 1 + int(np.floor((num_samples - frame_len) / frame_step))
    indices = (
        np.tile(np.arange(0, frame_len), (num_frames, 1))
        + np.tile(np.arange(0, num_frames * frame_step, frame_step), (frame_len, 1)).T
    )

    frames = pre_emphasized[indices]
    # Hamming window
    window = np.hamming(frame_len)
    frames = frames * window

    # FFT & Power spectrum
    n_fft = 512
    mag_frames = np.absolute(np.fft.rfft(frames, n_fft))
    pow_frames = (1.0 / n_fft) * (mag_frames ** 2)

    # Mel filterbank
    low_freq_mel = 0.0
    high_freq_mel = 2595.0 * np.log10(1.0 + (sample_rate / 2.0) / 700.0)
    mel_points = np.linspace(low_freq_mel, high_freq_mel, n_mels + 2)
    hz_points = 700.0 * (10.0 ** (mel_points / 2595.0) - 1.0)
    bin_points = np.floor((n_fft + 1) * hz_points / sample_rate).astype(int)

    fbank = np.zeros((n_mels, int(np.floor(n_fft / 2 + 1))))
    for m in range(1, n_mels + 1):
        f_m_minus = bin_points[m - 1]
        f_m = bin_points[m]
        f_m_plus = bin_points[m + 1]

        for k in range(f_m_minus, f_m):
            fbank[m - 1, k] = (k - bin_points[m - 1]) / (bin_points[m] - bin_points[m - 1])
        for k in range(f_m, f_m_plus):
            fbank[m - 1, k] = (bin_points[m + 1] - k) / (bin_points[m + 1] - bin_points[m])

    filter_banks = np.dot(pow_frames, fbank.T)
    filter_banks = np.where(filter_banks == 0, np.finfo(float).eps, filter_banks)
    log_filter_banks = np.log(filter_banks)

    # Cepstral Mean Normalization (CMN)
    log_filter_banks -= np.mean(log_filter_banks, axis=0, keepdims=True)
    return log_filter_banks.astype(np.float32)


def create_session(model_path: Path) -> tuple[ort.InferenceSession, str]:
    """Initialize ONNX session prioritizing CUDA, falling back to CPU with warning."""
    available_providers = ort.get_available_providers()
    providers_to_try = []

    if "CUDAExecutionProvider" in available_providers:
        providers_to_try.append("CUDAExecutionProvider")

    providers_to_try.append("CPUExecutionProvider")

    try:
        session = ort.InferenceSession(str(model_path), providers=providers_to_try)
        active_provider = session.get_providers()[0]
        if "CUDA" not in active_provider:
            logger.warning(
                "CUDAExecutionProvider not active on this host for %s. Active: %s",
                model_path.name,
                active_provider,
            )
        else:
            logger.info("CUDAExecutionProvider initialized successfully on GPU for %s", model_path.name)
        return session, active_provider
    except Exception as exc:
        logger.warning(
            "Failed initializing with providers %s (%s). Falling back strictly to CPUExecutionProvider.",
            providers_to_try,
            exc,
        )
        session = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
        return session, "CPUExecutionProvider"


def verify_models() -> bool:
    repo_root = Path(__file__).resolve().parent.parent
    wav_path = repo_root / "research" / "test.wav"
    wespeaker_path = repo_root / "models" / "wespeaker_en_voxceleb_resnet34.onnx"
    campplus_path = repo_root / "models" / "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx"

    if not wav_path.exists():
        logger.info("Test WAV not found at %s. Synthesizing test audio...", wav_path)
        sr = 16000
        t = np.linspace(0, 5.0, 5 * sr, endpoint=False)
        audio = (0.5 * np.sin(2 * np.pi * 220 * t) + 0.25 * np.sin(2 * np.pi * 440 * t)).astype(np.float32)
        sf.write(str(wav_path), audio, sr)
    else:
        logger.info("Reading test audio: %s", wav_path)
        audio, sr = sf.read(str(wav_path))
        if audio.ndim > 1:
            audio = audio[:, 0]
    if sr != 16000:
        logger.warning("Resampling test audio from %d Hz to 16000 Hz", sr)
        import scipy.signal
        samples = int(len(audio) * 16000 / sr)
        audio = scipy.signal.resample(audio, samples)
        sr = 16000

    duration = len(audio) / sr
    logger.info("Audio loaded: %d samples (%.2fs) at %d Hz", len(audio), duration, sr)

    feats = compute_fbank(audio, sample_rate=sr, n_mels=80)
    logger.info("Extracted 80-dim log-mel fbank features: shape=%s", feats.shape)

    # 1. Test WeSpeaker ResNet34
    logger.info("--- Testing WeSpeaker ResNet34 ---")
    session_we, prov_we = create_session(wespeaker_path)
    input_name_we = session_we.get_inputs()[0].name
    # Input shape: [Batch, Time, 80]
    feats_we = np.expand_dims(feats, axis=0)  # [1, T, 80]
    output_we = session_we.run(None, {input_name_we: feats_we})[0]
    emb_we = output_we.flatten()
    assert emb_we.ndim == 1, f"Expected 1D vector, got {emb_we.shape}"
    assert len(emb_we) == 256, f"Expected 256-dim embedding, got {len(emb_we)}"
    norm_we = np.linalg.norm(emb_we)
    logger.info(
        "WeSpeaker Embedding: Dim=%d, L2Norm=%.4f, First5=%s, Provider=%s",
        len(emb_we),
        norm_we,
        np.round(emb_we[:5], 4),
        prov_we,
    )

    # 2. Test 3D-Speaker CAM++
    logger.info("--- Testing 3D-Speaker CAM++ ---")
    session_cam, prov_cam = create_session(campplus_path)
    input_name_cam = session_cam.get_inputs()[0].name
    # Input shape: [Batch, Time, 80]
    feats_cam = np.expand_dims(feats, axis=0)  # [1, T, 80]
    output_cam = session_cam.run(None, {input_name_cam: feats_cam})[0]
    emb_cam = output_cam.flatten()
    assert emb_cam.ndim == 1, f"Expected 1D vector, got {emb_cam.shape}"
    assert len(emb_cam) == 192, f"Expected 192-dim embedding, got {len(emb_cam)}"
    norm_cam = np.linalg.norm(emb_cam)
    logger.info(
        "CAM++ Embedding: Dim=%d, L2Norm=%.4f, First5=%s, Provider=%s",
        len(emb_cam),
        norm_cam,
        np.round(emb_cam[:5], 4),
        prov_cam,
    )

    logger.info("SUCCESS: Both embedding models successfully loaded and verified.")
    return True


if __name__ == "__main__":
    success = verify_models()
    sys.exit(0 if success else 1)
