"""
eval.synthetic module — VoiceScan synthetic data generation and evaluation tooling.
"""

from eval.synthetic.dataset_config import DatasetConfig
from eval.synthetic.snr import mix_at_snr, detect_speech_activity, compute_active_power
from eval.synthetic.degradations import apply_degradation_chain
from eval.synthetic.generator import SyntheticDataGenerator, print_summary_table, validate_test_directory

__all__ = [
    "DatasetConfig",
    "SyntheticDataGenerator",
    "mix_at_snr",
    "detect_speech_activity",
    "compute_active_power",
    "apply_degradation_chain",
    "print_summary_table",
    "validate_test_directory",
]
