# VoiceScan Evaluation Harness & Synthetic Data

Directory for:
- `/eval/data_config`: Configuration templates and dev data metadata definitions (Prompt 3).
- Synthetic test data generator (Opus/codec degradation, SNR-controlled speech mixing).
- Evaluation harness (Prompt 4): metrics calculation (FAR/hr, recall, precision, DET/EER, realtime multiple) with 95% bootstrap confidence intervals.

*Note: Final held-out test sets are stored outside the repository and accessed only with the `--final` flag by human operators.*
