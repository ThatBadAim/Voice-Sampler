# Graph Report - Voice-Sampler  (2026-10-01)

## Corpus Check
- 53 files · ~28,319 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 500 nodes · 885 edges · 25 communities (21 shown, 4 thin omitted)
- Extraction: 95% EXTRACTED · 5% INFERRED · 0% AMBIGUOUS · INFERRED: 41 edges (avg confidence: 0.85)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `e9aac47a`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- VoiceScan.Tests.csproj
- VoiceScan System Design
- VoiceScan: 10 Agent Prompts (concept → base version)
- 2. Core Functional Requirements
- VoiceScan: Project Plan (working title)
- AGENTS.md — Agent Operating Guidelines for VoiceScan
- baseline_experiment.py
- .LoadAndRunSample
- 2. Summary of Commercial Blockers & Strategic Path
- manifest.json
- app/README.md
- eval/README.md
- models/README.md
- research/README.md
- generator.py
- speech_and_noise
- evaluator.py
- EvaluationCoordinator
- VoiceScan Baseline Experiment & Feasibility Report
- apply_degradation_chain
- mock_speaker_pool
- OnnxEmbeddingModel
- FileScanResult
- VoiceScan.Core
- .StreamDecodeAsync

## God Nodes (most connected - your core abstractions)
1. `EvaluationCoordinator` - 18 edges
2. `BaselineScanner` - 18 edges
3. `VoiceScan.Core` - 16 edges
4. `OnnxEmbeddingModel` - 16 edges
5. `VoiceProfile` - 16 edges
6. `SyntheticDataGenerator` - 16 edges
7. `DatasetConfig` - 15 edges
8. `SileroVad` - 14 edges
9. `FileEvalResult` - 13 edges
10. `validate_test_directory()` - 13 edges

## Surprising Connections (you probably didn't know these)
- `__getattr__()` --uses--> `BaselineScanner`  [INFERRED]
  eval/harness/__init__.py → research/baseline_experiment.py
- `__getattr__()` --uses--> `BaselineScanner`  [INFERRED]
  eval/harness/scanner_interface.py → research/baseline_experiment.py
- `run_experiment()` --calls--> `EvaluationCoordinator`  [EXTRACTED]
  research/baseline_experiment.py → eval/harness/evaluator.py
- `BaselineScanner` --inherits--> `BaseScanner`  [EXTRACTED]
  research/baseline_experiment.py → eval/harness/scanner_interface.py
- `main()` --calls--> `BaselineScanner`  [EXTRACTED]
  eval/run_eval.py → research/baseline_experiment.py

## Import Cycles
- None detected.

## Communities (25 total, 4 thin omitted)

### Community 0 - "VoiceScan.Tests.csproj"
Cohesion: 0.14
Nodes (11): net10.0, Microsoft.NET.Sdk, net10.0, Microsoft.NET.Sdk, net10.0, Microsoft.NET.Sdk, coverlet.collector (6.0.4), Microsoft.ML.OnnxRuntime (1.20.1) (+3 more)

### Community 1 - "VoiceScan System Design"
Cohesion: 0.14
Nodes (13): 1. Product Description, 2. Verdict Model: Match / Possible / No Match, 3. End-to-End Processing Pipeline, 4. Embedding Cache Architecture, 5. Success Metrics & Evaluation Standards, Core Principles, Design Invariants, Diagnostic Reason Flags (+5 more)

### Community 2 - "VoiceScan: 10 Agent Prompts (concept → base version)"
Cohesion: 0.14
Nodes (13): After Prompt 10 (you), Prompt 10: Reports, benchmark screen and base-version acceptance, Prompt 1: Foundation, rules and specs, Prompt 2: Models and licensing audit, Prompt 3: Synthetic data generator, Prompt 4: Evaluation harness, Prompt 5: Feasibility spike (Python), Prompt 6: C# engine core (+5 more)

### Community 3 - "2. Core Functional Requirements"
Cohesion: 0.15
Nodes (12): 1. Objective, 2.1 Audio Ingest & Decoding, 2.2 Voice Activity Detection (VAD), 2.3 Windowing, 2.4 GPU Embedding Extraction, 2.5 Profile Enrollment, 2.6 Similarity Scoring & Verdicts, 2.7 CLI Interface (+4 more)

### Community 4 - "VoiceScan: Project Plan (working title)"
Cohesion: 0.12
Nodes (15): 1. What the demo must prove, 2. Core technical approach, 3. Success metrics (defined at output level), 4. Phases and gates, 5. Evaluation harness rules, 6. Risks and mitigations, 7. Features that improve the product (ranked), 8. Agent workflow rules (+7 more)

### Community 5 - "AGENTS.md — Agent Operating Guidelines for VoiceScan"
Cohesion: 0.25
Nodes (7): 1. Project Summary & Stack, 2. Build, Test & Knowledge Graph Commands, 3. Shared Workflow Rules, 4. Anti-AI Slop Directives, 5. Coding Conventions, 6. Where Specs Live, AGENTS.md — Agent Operating Guidelines for VoiceScan

### Community 6 - "baseline_experiment.py"
Cohesion: 0.09
Nodes (35): Path, test_baseline_scanner.py — Acceptance tests for the BaselineScanner feasibility…, test_baseline_scanner_execution_on_dev_clip(), test_baseline_scanner_instantiation(), BaselineScanner, decode_audio_ffmpeg(), extract_speech_windows(), main() (+27 more)

### Community 7 - ".LoadAndRunSample"
Cohesion: 0.27
Nodes (5): InferenceSession, GpuModelSample, ModelVerificationResult, Fact, GpuModelSampleTests

### Community 8 - "2. Summary of Commercial Blockers & Strategic Path"
Cohesion: 0.33
Nodes (5): 1. Verified Model & Tool Inventory, 2. Summary of Commercial Blockers & Strategic Path, Blockers:, Model & Dataset Licensing Registry, Safe Commercial Baseline Path:

### Community 9 - "manifest.json"
Cohesion: 0.50
Nodes (3): manifest_version, models, updated_at

### Community 14 - "generator.py"
Cohesion: 0.07
Nodes (49): main(), generate_dataset.py — CLI for synthetic audio dataset generation., DatasetConfig, Any, Path, dataset_config.py — Configuration models and validation for synthetic data…, get_repo_root(), print_summary_table() (+41 more)

### Community 15 - "speech_and_noise"
Cohesion: 0.50
Nodes (4): fixture, ndarray, Create speech signal with silence gaps and background noise., speech_and_noise()

### Community 16 - "evaluator.py"
Cohesion: 0.10
Nodes (40): categorize_snr(), check_regression(), get_git_commit_hash(), evaluator.py — Central coordinator for running evaluations, logging, and…, Check if metrics violate quality gates or regress beyond tolerance., Categorize SNR in dB into standard reporting buckets., Execute full evaluation run on a dataset., __getattr__() (+32 more)

### Community 17 - "EvaluationCoordinator"
Cohesion: 0.10
Nodes (29): compare_runs(), EvaluationCoordinator, Any, Path, Log test set execution to persistent log file., Compare two evaluation result JSON files and return metric deltas., BaseScanner, __getattr__() (+21 more)

### Community 18 - "VoiceScan Baseline Experiment & Feasibility Report"
Cohesion: 0.10
Nodes (19): 1. Executive Summary & Pipeline Architecture, 2. Reproducible Dev-Set Benchmark Numbers, 3.1 Breakdown by Signal-to-Noise Ratio (SNR), 3.2 Breakdown by Degradation Chain, 3. Stratified Failure Analysis: Where It Works & Where It Fails, 4. Analysis of False Alarms, 5. Honest Viability Assessment, 6. Ranked List of the Three Most Promising Improvements (+11 more)

### Community 19 - "apply_degradation_chain"
Cohesion: 0.26
Nodes (12): apply_agc(), apply_bandlimit(), apply_degradation_chain(), apply_light_noise_suppression(), apply_opus_degradation(), ndarray, degradations.py — Degradation chains simulating gameplay voice chat…, Automatic Gain Control (AGC) dynamic compression simulation. (+4 more)

### Community 20 - "mock_speaker_pool"
Cohesion: 0.50
Nodes (4): mock_speaker_pool(), fixture, Path, Create temporary directory structure for 10 speakers with 6 clips each.

### Community 21 - "OnnxEmbeddingModel"
Cohesion: 0.05
Nodes (45): Task, Program, IReadOnlyList, ISpeakerEmbeddingModel, ActiveProvider, EmbeddingDimension, IsCudaActive, ModelId (+37 more)

### Community 22 - "FileScanResult"
Cohesion: 0.07
Nodes (30): End, List, DetectedSegment, Confidence, EndTimeSeconds, ReasonFlags, StartTimeSeconds, Verdict (+22 more)

### Community 23 - "VoiceScan.Core"
Cohesion: 0.09
Nodes (8): VoiceScan.Tests, VoiceScan.Core, VoiceScan.Cli, Filterbank, SimilarityScorer, Fact, Task, StreamingMemoryTests

### Community 24 - ".StreamDecodeAsync"
Cohesion: 0.27
Nodes (8): CancellationToken, IReadOnlyList, Task, AudioDecoder, AudioTrackInfo, DecodedAudioChunk, IAsyncEnumerable, Stream

## Knowledge Gaps
- **117 isolated node(s):** `VoiceScan.Cli`, `net10.0`, `Microsoft.NET.Sdk`, `ModelId`, `EmbeddingDimension` (+112 more)
  These have ≤1 connection - possible missing edges or undocumented components. (Counts symbols only; 241 node(s) total have ≤1 connection when file, concept and rationale nodes are included.)
- **4 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `VoiceScan.Core` connect `VoiceScan.Core` to `.StreamDecodeAsync`, `OnnxEmbeddingModel`, `FileScanResult`, `.LoadAndRunSample`?**
  _High betweenness centrality (0.027) - this node is a cross-community bridge._
- **Why does `EvaluationCoordinator` connect `EvaluationCoordinator` to `evaluator.py`, `baseline_experiment.py`?**
  _High betweenness centrality (0.022) - this node is a cross-community bridge._
- **Why does `detect_speech_activity()` connect `generator.py` to `baseline_experiment.py`?**
  _High betweenness centrality (0.022) - this node is a cross-community bridge._
- **Are the 2 inferred relationships involving `EvaluationCoordinator` (e.g. with `FileEvalResult` and `BaseScanner`) actually correct?**
  _`EvaluationCoordinator` has 2 INFERRED edges - model-reasoned connections that need verification._
- **Are the 2 inferred relationships involving `BaselineScanner` (e.g. with `__getattr__()` and `__getattr__()`) actually correct?**
  _`BaselineScanner` has 2 INFERRED edges - model-reasoned connections that need verification._
- **Are the 5 inferred relationships involving `OnnxEmbeddingModel` (e.g. with `.HandleEnrollCommandAsync()` and `.HandleScanCommandAsync()`) actually correct?**
  _`OnnxEmbeddingModel` has 5 INFERRED edges - model-reasoned connections that need verification._
- **What connects `VoiceScan.Cli`, `net10.0`, `Microsoft.NET.Sdk` to the rest of the system?**
  _117 weakly-connected nodes found - possible documentation gaps or missing edges._