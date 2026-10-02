# Graph Report - Voice-Sampler  (2026-10-02)

## Corpus Check
- 108 files · ~157,878 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 1240 nodes · 2308 edges · 70 communities (65 shown, 5 thin omitted)
- Extraction: 92% EXTRACTED · 8% INFERRED · 0% AMBIGUOUS · INFERRED: 194 edges (avg confidence: 0.84)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `e9aac47a`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- VoiceScan.Core.csproj
- VoiceScan System Design
- VoiceScan: 10 Agent Prompts (concept → base version)
- 2. Core Functional Requirements
- VoiceScan: Project Plan (working title)
- AGENTS.md — Agent Operating Guidelines for VoiceScan
- baseline_experiment.py
- CohortDocument
- 2. Summary of Commercial Blockers & Strategic Path
- manifest.json
- app/README.md
- eval/README.md
- models/README.md
- research/README.md
- synthetic/__init__.py
- VoiceScanDatabase
- evaluator.py
- ClusterNode
- VoiceScan Baseline Experiment & Feasibility Report
- EvaluationCoordinator
- BaseScanner
- OnnxEmbeddingModel
- FileScanResult
- VoiceScan.Core
- DetectedSegment
- ReviewViewModel
- harness/__init__.py
- 2. Step-by-Step Analysis & Empirical Deltas
- .ScoreAndAggregate
- MainAppViewModel
- ScoreNormalizer
- ScanMetadata
- speech_and_noise
- .ScanFileAsync
- BaselineScanner
- Page
- Page
- Page
- Page
- AudioPlaybackController
- ResultsViewModel
- ScanDashboardViewModel
- EnrollmentWizardViewModel
- 2. Screen Walkthrough & UI Reference
- validate_test_directory
- BackgroundScanController
- .StreamDecodeAsync
- generator.py
- CoreEngineTests
- SyntheticDataGenerator
- apply_degradation_chain
- ScanExecutionState
- .EnrollProfileAsync
- AppLayerTests
- BenchmarkViewModel
- UserControl
- Page
- .GenerateEnvelopeAsync
- SileroVad
- VoiceScanLogger
- FileVerdictResult
- .ExportReportAsync
- .StartScanAsync
- VoiceScan Base Version Documentation
- .run_evaluation
- IBackgroundScanController
- compare_runs
- .generate_split
- mock_speaker_pool
- MicaDarkLightStyles.xaml

## God Nodes (most connected - your core abstractions)
1. `VoiceScan.Core` - 32 edges
2. `EnrollmentWizardViewModel` - 31 edges
3. `ResultsViewModel` - 31 edges
4. `ScanDashboardViewModel` - 31 edges
5. `VoiceScanDatabase` - 29 edges
6. `ReviewViewModel` - 28 edges
7. `VoiceScan.App.Core.Models` - 24 edges
8. `MainAppViewModel` - 23 edges
9. `VoiceProfile` - 22 edges
10. `BackgroundScanController` - 21 edges

## Surprising Connections (you probably didn't know these)
- `__getattr__()` --uses--> `BaselineScanner`  [INFERRED]
  eval/harness/__init__.py → research/baseline_experiment.py
- `__getattr__()` --uses--> `BaselineScanner`  [INFERRED]
  eval/harness/scanner_interface.py → research/baseline_experiment.py
- `AudioQualityAnalyzer` --references--> `SileroVad`  [EXTRACTED]
  app/VoiceScan.App.Core/Services/AudioQualityAnalyzer.cs → engine/VoiceScan.Core/SileroVad.cs
- `PipelineScanOptions` --references--> `ScoreNormalizer`  [EXTRACTED]
  app/VoiceScan.App.Core/Services/BackgroundScanController.cs → engine/VoiceScan.Core/ScoreNormalizer.cs
- `BackgroundScanController` --references--> `PipelineScanner`  [EXTRACTED]
  app/VoiceScan.App.Core/Services/BackgroundScanController.cs → engine/VoiceScan.Core/PipelineScanner.cs

## Import Cycles
- None detected.

## Communities (70 total, 5 thin omitted)

### Community 0 - "VoiceScan.Core.csproj"
Cohesion: 0.09
Nodes (20): net10.0, Microsoft.Data.Sqlite (10.0.12), Microsoft.NET.Sdk, net10.0, Microsoft.NET.Sdk, net10.0, Microsoft.NET.Sdk, net10.0 (+12 more)

### Community 1 - "VoiceScan System Design"
Cohesion: 0.13
Nodes (14): 1. Product Description, 2. Verdict Model: Match / Possible / No Match, 3. End-to-End Processing Pipeline, 4. Embedding Cache Architecture, 5. Success Metrics & Evaluation Standards, 6. Desktop UI, Benchmark Integration & Evidence Reporting, Core Principles, Design Invariants (+6 more)

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
Cohesion: 0.10
Nodes (29): decode_audio_ffmpeg(), extract_speech_windows(), main(), merge_adjacent_hits(), Any, InferenceSession, ndarray, Path (+21 more)

### Community 7 - "CohortDocument"
Cohesion: 0.22
Nodes (6): CohortDocument, CohortSize, CreatedAt, Embeddings, ModelId, SchemaVersion

### Community 8 - "2. Summary of Commercial Blockers & Strategic Path"
Cohesion: 0.33
Nodes (5): 1. Verified Model & Tool Inventory, 2. Summary of Commercial Blockers & Strategic Path, Blockers:, Model & Dataset Licensing Registry, Safe Commercial Baseline Path:

### Community 9 - "manifest.json"
Cohesion: 0.50
Nodes (3): manifest_version, models, updated_at

### Community 14 - "synthetic/__init__.py"
Cohesion: 0.22
Nodes (16): eval.synthetic module — VoiceScan synthetic data generation and evaluation…, align_or_tile_noise(), compute_active_power(), detect_speech_activity(), mix_at_snr(), ndarray, snr.py — Speech-active region SNR computation and signal mixing. Defines…, Compute sample-level boolean mask of speech activity using frame RMS energy. (+8 more)

### Community 15 - "VoiceScanDatabase"
Cohesion: 0.19
Nodes (10): CachedWindow, CacheStats, StoredProfileInfo, CancellationToken, IReadOnlyList, List, SqliteConnection, Task (+2 more)

### Community 16 - "evaluator.py"
Cohesion: 0.17
Nodes (22): evaluator.py — Central coordinator for running evaluations, logging, and…, check_segment_overlap(), compute_aggregate_metrics(), compute_bootstrap_ci(), compute_det_and_eer(), evaluate_file(), FileEvalResult, Any (+14 more)

### Community 17 - "ClusterNode"
Cohesion: 0.11
Nodes (19): ClusterNode, IReadOnlyList, List, ClusterNode, Count, Id, SumVector, Windows (+11 more)

### Community 18 - "VoiceScan Baseline Experiment & Feasibility Report"
Cohesion: 0.10
Nodes (19): 1. Executive Summary & Pipeline Architecture, 2. Reproducible Dev-Set Benchmark Numbers, 3.1 Breakdown by Signal-to-Noise Ratio (SNR), 3.2 Breakdown by Degradation Chain, 3. Stratified Failure Analysis: Where It Works & Where It Fails, 4. Analysis of False Alarms, 5. Honest Viability Assessment, 6. Ranked List of the Three Most Promising Improvements (+11 more)

### Community 19 - "EvaluationCoordinator"
Cohesion: 0.26
Nodes (12): EvaluationCoordinator, PerfectGroundTruthScanner, Oracle stub scanner: emits perfect detections with 1.0 confidence for target…, fixture, Path, test_stubs_and_harness.py — Acceptance verification for Perfect Stub, Random…, Create a minimal synthetic evaluation dataset on disk with 20 clips., synthetic_eval_dataset() (+4 more)

### Community 20 - "BaseScanner"
Cohesion: 0.19
Nodes (8): BaseScanner, __getattr__(), Any, Path, RandomStubScanner, scanner_interface.py — Scanner invocation contracts and verification stubs.…, Stochastic stub scanner: returns random confidence scores ~ U(0, 1)., Execute scan across audio_dir. Returns: result_json: Dictionary adhering to…

### Community 21 - "OnnxEmbeddingModel"
Cohesion: 0.11
Nodes (13): IReadOnlyList, ISpeakerEmbeddingModel, ActiveProvider, EmbeddingDimension, IsCudaActive, ModelId, InferenceSession, IReadOnlyList (+5 more)

### Community 22 - "FileScanResult"
Cohesion: 0.18
Nodes (12): List, FileScanResult, AudioTrackIndex, ClipId, DurationSeconds, FilePath, MaxConfidence, Segments (+4 more)

### Community 23 - "VoiceScan.Core"
Cohesion: 0.07
Nodes (16): AppServiceBootstrap, VoiceScan.App, VoiceScan.App.Core.ViewModels, VoiceScan.Tests, VoiceScan.Core.Storage, VoiceScan.App.Views, VoiceScan.Core, VoiceScan.App.Core.Models (+8 more)

### Community 24 - "DetectedSegment"
Cohesion: 0.27
Nodes (10): End, DetectedSegment, Confidence, EndTimeSeconds, ReasonFlags, StartTimeSeconds, Verdict, IReadOnlyList (+2 more)

### Community 25 - "ReviewViewModel"
Cohesion: 0.09
Nodes (31): DateTimeOffset, IReadOnlyList, ReviewDecision, Confirmed, Rejected, Unreviewed, ReviewDecisionRecord, ReviewQueueItem (+23 more)

### Community 26 - "harness/__init__.py"
Cohesion: 0.24
Nodes (11): __getattr__(), eval.harness module — VoiceScan evaluation harness and metrics calculation., export_json(), generate_markdown_report(), generate_plots(), Any, Path, reporting.py — Generation of JSON results, Markdown audit reports, and PNG… (+3 more)

### Community 27 - "2. Step-by-Step Analysis & Empirical Deltas"
Cohesion: 0.17
Nodes (11): 1. Executive Summary & Progression, 2. Step-by-Step Analysis & Empirical Deltas, 3. Best-Performing Engine Configuration, 4. Verification Commands, Headline Progression Table, Step 1: Within-File Speaker Clustering (Agglomerative Hierarchical Clustering), Step 2: Temporal Smoothing and Segment Aggregation, Step 3: Score Normalization (AS-Norm) and Cohort Tool (+3 more)

### Community 28 - ".ScoreAndAggregate"
Cohesion: 0.29
Nodes (4): List, AcousticDiagnostics, IReadOnlyList, Verdict

### Community 29 - "MainAppViewModel"
Cohesion: 0.05
Nodes (33): Application, App, AppNavigationPage, Benchmark, Enrollment, Results, Review, Scan (+25 more)

### Community 30 - "ScoreNormalizer"
Cohesion: 0.21
Nodes (6): List, ScoreNormalizer, CohortSize, SimilarityScorer, Mean, StdDev

### Community 31 - "ScanMetadata"
Cohesion: 0.29
Nodes (7): ScanMetadata, ElapsedSeconds, EngineVersion, ModelId, ProfileName, Threshold, Timestamp

### Community 32 - "speech_and_noise"
Cohesion: 0.50
Nodes (4): fixture, ndarray, Create speech signal with silence gaps and background noise., speech_and_noise()

### Community 33 - ".ScanFileAsync"
Cohesion: 0.10
Nodes (19): CancellationToken, List, Task, PipelineScanner, EmbeddingModel, Vad, List, VoiceProfile (+11 more)

### Community 34 - "BaselineScanner"
Cohesion: 0.38
Nodes (6): Path, test_baseline_scanner.py — Acceptance tests for the BaselineScanner feasibility…, test_baseline_scanner_execution_on_dev_clip(), test_baseline_scanner_instantiation(), BaselineScanner, Feasibility Baseline Scanner implementing the full end-to-end VoiceScan…

### Community 35 - "Page"
Cohesion: 0.06
Nodes (38): ResultSortColumn, Duration, FileName, HitCount, MaxConfidence, Verdict, VerdictFilter, All (+30 more)

### Community 36 - "Page"
Cohesion: 0.08
Nodes (30): CancelButton, ClusteringCheckBox, CurrentFileStatusText, EtaText, FilesCompletedText, GpuUtilText, MatchCountText, NoMatchCountText (+22 more)

### Community 37 - "Page"
Cohesion: 0.10
Nodes (26): BrowseAudioButton, ConsentCheckBox, CreateProfileButton, FeedbackBox, FeedbackMessageText, MultiConditionCheckBox, NoiseFloorText, Page (+18 more)

### Community 38 - "Page"
Cohesion: 0.09
Nodes (24): ActionStatusText, AddToProfileCheckBox, ConfidenceText, ConfirmButton, HistoryListView, Page, PlaySnippetButton, QueueListView (+16 more)

### Community 39 - "AudioPlaybackController"
Cohesion: 0.10
Nodes (12): AudioPlaybackController, CurrentFilePath, CurrentPositionSeconds, IsPlaying, TotalDurationSeconds, IAudioPlaybackController, CurrentFilePath, CurrentPositionSeconds (+4 more)

### Community 40 - "ResultsViewModel"
Cohesion: 0.12
Nodes (14): IEnumerable, List, ObservableCollection, ResultsViewModel, FilteredFiles, HasSelectedFile, IsPlaying, PlaybackPosition (+6 more)

### Community 41 - "ScanDashboardViewModel"
Cohesion: 0.11
Nodes (17): ObservableCollection, ScanDashboardViewModel, CanCancelScan, CanPauseScan, CanResumeScan, CanStartScan, ClusterThreshold, CompletedFiles (+9 more)

### Community 42 - "EnrollmentWizardViewModel"
Cohesion: 0.06
Nodes (39): Action, DateTimeOffset, IReadOnlyList, AudioQualityReport, AudioQualityTier, Acceptable, Excellent, Rejected (+31 more)

### Community 43 - "2. Screen Walkthrough & UI Reference"
Cohesion: 0.13
Nodes (14): 1. Executive Summary & Verification Matrix, 2. Screen Walkthrough & UI Reference, 3. 5-Hour Folder Endurance & UI Thread Responsiveness Benchmark, 4. Manual Verification Checklist, A. Voice Profile Enrollment, B. Scan Execution, Benchmark Results, C. Results & Waveform Timeline (+6 more)

### Community 44 - "validate_test_directory"
Cohesion: 0.19
Nodes (11): get_repo_root(), Path, Locate the git repository root from current file., Validate that test split directory is strictly outside repository root., Discover audio clips per speaker from directory structure:…, Discover game audio noise clips., Deterministically partition speakers into dev and test sets with zero leakage., validate_test_directory() (+3 more)

### Community 45 - "BackgroundScanController"
Cohesion: 0.19
Nodes (4): BackgroundScanController, CurrentProgress, CurrentState, ManualResetEventSlim

### Community 46 - ".StreamDecodeAsync"
Cohesion: 0.13
Nodes (14): CancellationToken, IReadOnlyList, Task, AudioDecoder, AudioTrackInfo, DecodedAudioChunk, InferenceSession, GpuModelSample (+6 more)

### Community 47 - "generator.py"
Cohesion: 0.19
Nodes (11): dataset_config.py — Configuration models and validation for synthetic data…, print_summary_table(), Any, generator.py — Synthetic test dataset generator for VoiceScan. Enforces: 1.…, Generate and print formatted summary table of generated synthetic clips., fixture, Path, test_generator.py — Integration tests for end-to-end dataset generation. (+3 more)

### Community 48 - "CoreEngineTests"
Cohesion: 0.30
Nodes (4): ProfileEnrollmentService, Fact, Task, CoreEngineTests

### Community 49 - "SyntheticDataGenerator"
Cohesion: 0.27
Nodes (8): main(), generate_dataset.py — CLI for synthetic audio dataset generation., DatasetConfig, Any, Path, SyntheticDataGenerator, test_speaker_split_zero_leakage(), test_split_determinism()

### Community 50 - "apply_degradation_chain"
Cohesion: 0.26
Nodes (12): apply_agc(), apply_bandlimit(), apply_degradation_chain(), apply_light_noise_suppression(), apply_opus_degradation(), ndarray, degradations.py — Degradation chains simulating gameplay voice chat…, Automatic Gain Control (AGC) dynamic compression simulation. (+4 more)

### Community 51 - "ScanExecutionState"
Cohesion: 0.18
Nodes (10): FileScanTaskItem, OverallScanProgress, ScanExecutionState, Cancelled, Completed, Failed, Idle, Paused (+2 more)

### Community 52 - ".EnrollProfileAsync"
Cohesion: 0.14
Nodes (9): AudioAugmenter, CancellationToken, IReadOnlyList, Task, IReadOnlyList, SpeechInterval, IReadOnlyList, SpeechAudioWindow (+1 more)

### Community 53 - "AppLayerTests"
Cohesion: 0.23
Nodes (5): CancellationTokenSource, Fact, Task, AppLayerTests, Stopwatch

### Community 54 - "BenchmarkViewModel"
Cohesion: 0.11
Nodes (20): DateTimeOffset, IReadOnlyList, BenchmarkReportSummary, SnrTierBenchmarkItem, CancellationToken, IReadOnlyList, Task, BenchmarkService (+12 more)

### Community 55 - "UserControl"
Cohesion: 0.24
Nodes (9): CurrentTimeText, PlaybackCursorLine, StartTimeText, TotalTimeText, UserControl, WaveformCanvas, TextBlock, Canvas (+1 more)

### Community 56 - "Page"
Cohesion: 0.12
Nodes (21): FaCiText, FaValueText, HardwareText, MetadataSummaryText, Page, PrecisionCiText, PrecisionValueText, RecallCiText (+13 more)

### Community 57 - ".GenerateEnvelopeAsync"
Cohesion: 0.39
Nodes (5): CancellationToken, ReadOnlySpan, Task, IWaveformService, WaveformService

### Community 58 - "SileroVad"
Cohesion: 0.31
Nodes (4): Task, Program, InferenceSession, SileroVad

### Community 59 - "VoiceScanLogger"
Cohesion: 0.16
Nodes (10): VoiceScan.Core.Logging, LogLevel, Debug, Error, Fatal, Info, Warn, VoiceScanLogger (+2 more)

### Community 60 - "FileVerdictResult"
Cohesion: 0.25
Nodes (8): IReadOnlyList, WaveformTimelineControl, IReadOnlyList, FileVerdictResult, HitSegmentResult, WaveformEnvelope, VoiceScan.App.Controls, PointerRoutedEventArgs

### Community 61 - ".ExportReportAsync"
Cohesion: 0.31
Nodes (8): CancellationToken, DateTimeOffset, IReadOnlyList, Task, EvidenceReportExporter, ExportResult, IEvidenceReportExporter, ReportExportSettings

### Community 62 - ".StartScanAsync"
Cohesion: 0.36
Nodes (6): CancellationToken, IReadOnlyList, Task, PipelineScanOptions, CancellationToken, Task

### Community 63 - "VoiceScan Base Version Documentation"
Cohesion: 0.12
Nodes (15): 1. System Requirements & Prerequisites, 2.1 Build the Solution, 2.2 Run Unit & Integration Tests, 2.3 Run the CLI Engine, 2.4 Run the Desktop Application (WinUI 3), 2. How to Build & Run, 3. Known Limitations & Edge Cases, 4. Model & Component Licenses Summary (+7 more)

### Community 64 - ".run_evaluation"
Cohesion: 0.22
Nodes (7): categorize_snr(), get_git_commit_hash(), Any, Path, Log test set execution to persistent log file., Categorize SNR in dB into standard reporting buckets., Execute full evaluation run on a dataset.

### Community 65 - "IBackgroundScanController"
Cohesion: 0.29
Nodes (4): IBackgroundScanController, CurrentProgress, CurrentState, IDisposable

### Community 66 - "compare_runs"
Cohesion: 0.27
Nodes (9): check_regression(), compare_runs(), Compare two evaluation result JSON files and return metric deltas., Check if metrics violate quality gates or regress beyond tolerance., Executes a command-line scanner binary or script conforming to the contract:…, SubprocessCliScanner, main(), run_eval.py — CLI for the VoiceScan Evaluation Harness. (+1 more)

### Community 69 - ".generate_split"
Cohesion: 0.40
Nodes (3): ndarray, Load audio file converted to 16 kHz mono float32., Generate a complete synthetic split (dev or test) with enrollment and ground…

### Community 70 - "mock_speaker_pool"
Cohesion: 0.50
Nodes (4): mock_speaker_pool(), fixture, Path, Create temporary directory structure for 10 speakers with 6 clips each.

## Knowledge Gaps
- **316 isolated node(s):** `AudioSelection`, `ConsentVerification`, `QualityDiagnostics`, `ProfileCreation`, `Complete` (+311 more)
  These have ≤1 connection - possible missing edges or undocumented components. (Counts symbols only; 502 node(s) total have ≤1 connection when file, concept and rationale nodes are included.)
- **5 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `ResultsViewModel` connect `ResultsViewModel` to `.ScanFileAsync`, `Page`, `AudioPlaybackController`, `AppLayerTests`, `BenchmarkViewModel`, `VoiceScan.Core`, `FileVerdictResult`, `MainAppViewModel`?**
  _High betweenness centrality (0.067) - this node is a cross-community bridge._
- **Why does `VoiceScan.Core` connect `VoiceScan.Core` to `.ScanFileAsync`, `.StreamDecodeAsync`, `FileVerdictResult`, `ClusterNode`, `.EnrollProfileAsync`, `OnnxEmbeddingModel`, `FileScanResult`, `.ScoreAndAggregate`, `.ExportReportAsync`, `ScoreNormalizer`?**
  _High betweenness centrality (0.064) - this node is a cross-community bridge._
- **Why does `EnrollmentWizardViewModel` connect `EnrollmentWizardViewModel` to `.ScanFileAsync`, `Page`, `CoreEngineTests`, `AppLayerTests`, `BenchmarkViewModel`, `VoiceScan.Core`, `MainAppViewModel`?**
  _High betweenness centrality (0.064) - this node is a cross-community bridge._
- **Are the 2 inferred relationships involving `EnrollmentWizardViewModel` (e.g. with `.CreateMainViewModel()` and `.EnrollmentWizard_StrictMandatoryConsent_GateEnforced()`) actually correct?**
  _`EnrollmentWizardViewModel` has 2 INFERRED edges - model-reasoned connections that need verification._
- **Are the 2 inferred relationships involving `ResultsViewModel` (e.g. with `.CreateMainViewModel()` and `.ResultsViewModel_FilteringAndSorting_OperatesCorrectly()`) actually correct?**
  _`ResultsViewModel` has 2 INFERRED edges - model-reasoned connections that need verification._
- **Are the 4 inferred relationships involving `VoiceScanDatabase` (e.g. with `.CreateMainViewModel()` and `.Benchmark_Scanning50Files_ProfileB_RunsInFractionOfTime()`) actually correct?**
  _`VoiceScanDatabase` has 4 INFERRED edges - model-reasoned connections that need verification._
- **What connects `AudioSelection`, `ConsentVerification`, `QualityDiagnostics` to the rest of the system?**
  _316 weakly-connected nodes found - possible documentation gaps or missing edges._