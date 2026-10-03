# Graph Report - Voice-Sampler  (2026-10-04)

## Corpus Check
- 120 files · ~210,273 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 1321 nodes · 2539 edges · 82 communities (70 shown, 9 thin omitted)
- Extraction: 89% EXTRACTED · 11% INFERRED · 0% AMBIGUOUS · INFERRED: 279 edges (avg confidence: 0.84)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `0073d7b0`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- VoiceScan.Avalonia.csproj
- VoiceScan System Design
- VoiceScan: 10 Agent Prompts (concept → base version)
- 2. Core Functional Requirements
- VoiceScan: Project Plan (working title)
- AGENTS.md — Agent Operating Guidelines for VoiceScan
- baseline_experiment.py
- EnrollmentWizardView
- Model & Dataset Licensing Registry
- vad
- app/README.md
- eval/README.md
- models/README.md
- research/README.md
- synthetic/__init__.py
- .ScanFileAsync
- evaluator.py
- WindowItem
- VoiceScan Baseline Experiment & Feasibility Report
- real_speech_eval.py
- EvaluationCoordinator
- VoiceScan.Core
- .MergeAdjacentHits
- ScoreNormalizer
- .run_evaluation
- ReviewViewModel
- FileScanResult
- 2. Step-by-Step Analysis & Empirical Deltas
- CohortDocument
- MainAppViewModel
- InstallActions
- create_session
- speech_and_noise
- SpooledAudio
- BaselineScanner
- IAudioPlaybackController
- FfplayAudioOutput
- .LoadAndRunSample
- BackgroundScanController
- AudioPlaybackController
- ResultsViewModel
- ScanDashboardViewModel
- EnrollmentWizardViewModel
- 2. Screen Walkthrough & UI Reference
- validate_test_directory
- IDisposable
- VerdictFilter
- generator.py
- AudioQualityReport
- SyntheticDataGenerator
- apply_degradation_chain
- BaseScanner
- CoreEngineTests
- .CreateProfileAsync
- .GenerateEnvelopeAsync
- ResultsView
- ScanMetadata
- VoiceProfileSummary
- OnnxEmbeddingModel
- VoiceScanLogger
- FakeAudioOutput
- FileVerdictResult
- .StartScanAsync
- VoiceScan Base Version Documentation
- SPEC: Avalonia Desktop App (Windows + Linux)
- publish-linux.sh
- .ScoreAndAggregate
- .generate_split
- mock_speaker_pool
- .PickFilesAsync
- .EstimateSnrDb
- VoiceScan
- EnrollmentStep
- VoiceScan.Installer.csproj
- build-installer.sh
- ScanDashboardView
- ReviewView
- DetectedSegment
- .ExportReportAsync

## God Nodes (most connected - your core abstractions)
1. `ResultsViewModel` - 42 edges
2. `VoiceScan.Core` - 40 edges
3. `EnrollmentWizardViewModel` - 38 edges
4. `ScanDashboardViewModel` - 36 edges
5. `ReviewViewModel` - 30 edges
6. `VoiceScanDatabase` - 29 edges
7. `AudioPlaybackController` - 27 edges
8. `CoreEngineTests` - 26 edges
9. `VoiceProfile` - 24 edges
10. `MainAppViewModel` - 23 edges

## Surprising Connections (you probably didn't know these)
- `__getattr__()` --uses--> `BaselineScanner`  [INFERRED]
  eval/harness/__init__.py → research/baseline_experiment.py
- `__getattr__()` --uses--> `BaselineScanner`  [INFERRED]
  eval/harness/scanner_interface.py → research/baseline_experiment.py
- `FakeAudioOutput` --references--> `Duration`  [EXTRACTED]
  engine/VoiceScan.Tests/AppLayerTests.cs → app/VoiceScan.App.Core/Models/ResultModels.cs
- `AudioQualityAnalyzer` --references--> `SileroVad`  [EXTRACTED]
  app/VoiceScan.App.Core/Services/AudioQualityAnalyzer.cs → engine/VoiceScan.Core/SileroVad.cs
- `PipelineScanOptions` --references--> `ScoreNormalizer`  [EXTRACTED]
  app/VoiceScan.App.Core/Services/BackgroundScanController.cs → engine/VoiceScan.Core/ScoreNormalizer.cs

## Import Cycles
- None detected.

## Communities (82 total, 9 thin omitted)

### Community 0 - "VoiceScan.Avalonia.csproj"
Cohesion: 0.08
Nodes (23): net10.0, Microsoft.Data.Sqlite (10.0.12), Microsoft.NET.Sdk, net10.0, Microsoft.NET.Sdk, net10.0, Microsoft.NET.Sdk, net10.0 (+15 more)

### Community 1 - "VoiceScan System Design"
Cohesion: 0.13
Nodes (14): 1. Product Description, 2. Verdict Model: Match / Possible / No Match, 3. End-to-End Processing Pipeline, 4. Embedding Cache Architecture, 5. Success Metrics & Evaluation Standards, 6. Desktop UI & Evidence Reporting, Core Principles, Design Invariants (+6 more)

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
Cohesion: 0.15
Nodes (20): decode_audio_ffmpeg(), extract_speech_windows(), main(), merge_adjacent_hits(), Any, InferenceSession, ndarray, Path (+12 more)

### Community 7 - "EnrollmentWizardView"
Cohesion: 0.32
Nodes (4): RoutedEventArgs, EnrollmentWizardView, ViewModel, KeyEventArgs

### Community 8 - "Model & Dataset Licensing Registry"
Cohesion: 0.40
Nodes (4): 1. Verified Model & Tool Inventory, 2. Audio Processing Architecture, Model & Dataset Licensing Registry, Primary Pipeline

### Community 9 - "vad"
Cohesion: 0.20
Nodes (9): manifest_version, models, updated_at, vad, architecture, engine, license_type, notes (+1 more)

### Community 14 - "synthetic/__init__.py"
Cohesion: 0.22
Nodes (16): eval.synthetic module — VoiceScan synthetic data generation and evaluation…, align_or_tile_noise(), compute_active_power(), detect_speech_activity(), mix_at_snr(), ndarray, snr.py — Speech-active region SNR computation and signal mixing. Defines…, Compute sample-level boolean mask of speech activity using frame RMS energy. (+8 more)

### Community 15 - ".ScanFileAsync"
Cohesion: 0.06
Nodes (45): CancellationToken, DateTimeOffset, IReadOnlyList, Task, EvidenceReportExporter, ExportResult, PdfRow, ReportExportSettings (+37 more)

### Community 16 - "evaluator.py"
Cohesion: 0.17
Nodes (23): evaluator.py — Central coordinator for running evaluations, logging, and…, eval.harness module — VoiceScan evaluation harness and metrics calculation., check_segment_overlap(), compute_aggregate_metrics(), compute_bootstrap_ci(), compute_det_and_eer(), evaluate_file(), FileEvalResult (+15 more)

### Community 17 - "WindowItem"
Cohesion: 0.10
Nodes (21): ClusterNode, IReadOnlyList, List, ClusterNode, Count, Id, SumVector, Windows (+13 more)

### Community 18 - "VoiceScan Baseline Experiment & Feasibility Report"
Cohesion: 0.10
Nodes (19): 1. Executive Summary & Pipeline Architecture, 2. Reproducible Dev-Set Benchmark Numbers, 3.1 Breakdown by Signal-to-Noise Ratio (SNR), 3.2 Breakdown by Degradation Chain, 3. Stratified Failure Analysis: Where It Works & Where It Fails, 4. Analysis of False Alarms, 5. Honest Viability Assessment, 6. Ranked List of the Three Most Promising Improvements (+11 more)

### Community 19 - "real_speech_eval.py"
Cohesion: 0.32
Nodes (11): build(), fetch(), main(), mix(), opus(), overlap(), ndarray, Path (+3 more)

### Community 20 - "EvaluationCoordinator"
Cohesion: 0.14
Nodes (22): check_regression(), compare_runs(), EvaluationCoordinator, Path, Compare two evaluation result JSON files and return metric deltas., Check if metrics violate quality gates or regress beyond tolerance., PerfectGroundTruthScanner, RandomStubScanner (+14 more)

### Community 21 - "VoiceScan.Core"
Cohesion: 0.06
Nodes (23): App, AppServiceBootstrap, STAThread, Program, VerdictConverters, AppBuilder, Application, Color (+15 more)

### Community 22 - ".MergeAdjacentHits"
Cohesion: 0.31
Nodes (6): End, Confidence, IReadOnlyList, List, Start, SimilarityScorer

### Community 23 - "ScoreNormalizer"
Cohesion: 0.40
Nodes (4): ScoreNormalizer, CohortSize, Mean, StdDev

### Community 24 - ".run_evaluation"
Cohesion: 0.16
Nodes (15): categorize_snr(), get_git_commit_hash(), Any, Log test set execution to persistent log file., Categorize SNR in dB into standard reporting buckets., Execute full evaluation run on a dataset., export_json(), generate_markdown_report() (+7 more)

### Community 25 - "ReviewViewModel"
Cohesion: 0.06
Nodes (38): DateTimeOffset, IReadOnlyList, ReviewDecision, Confirmed, Rejected, Unreviewed, ReviewDecisionRecord, ReviewQueueItem (+30 more)

### Community 26 - "FileScanResult"
Cohesion: 0.16
Nodes (13): List, FileScanResult, AudioTrackIndex, ClipId, DurationSeconds, FileHash, FilePath, WaveformMaxPeaks (+5 more)

### Community 27 - "2. Step-by-Step Analysis & Empirical Deltas"
Cohesion: 0.15
Nodes (12): 1. Executive Summary & Progression, 2. Step-by-Step Analysis & Empirical Deltas, 3. Best-Performing Engine Configuration, 4. Verification Commands, Headline Progression Table, Real-speech re-evaluation (2026-10-03) — supersedes the synthetic dev-set numbers below, Step 1: Within-File Speaker Clustering (Agglomerative Hierarchical Clustering), Step 2: Temporal Smoothing and Segment Aggregation (+4 more)

### Community 28 - "CohortDocument"
Cohesion: 0.18
Nodes (7): List, CohortDocument, CohortSize, CreatedAt, Embeddings, ModelId, SchemaVersion

### Community 29 - "MainAppViewModel"
Cohesion: 0.06
Nodes (27): AppNavigationPage, Enrollment, Results, Review, Scan, MainAppViewModel, CurrentPage, Enrollment (+19 more)

### Community 30 - "InstallActions"
Cohesion: 0.07
Nodes (22): Progress, CheckBox, VoiceScan.Installer, Form, Task, InstallActions, DataDir, DefaultInstallDir (+14 more)

### Community 31 - "create_session"
Cohesion: 0.24
Nodes (9): compute_fbank(), create_session(), InferenceSession, ndarray, Path, verify_models.py — Verification of speaker embedding models (Prompt 2). Loads…, 80-dim log-mel fbank with mean normalization, matching kaldi-native-fbank. Same…, Initialize ONNX session prioritizing CUDA, falling back to CPU with warning. (+1 more)

### Community 32 - "speech_and_noise"
Cohesion: 0.50
Nodes (4): fixture, ndarray, Create speech signal with silence gaps and background noise., speech_and_noise()

### Community 33 - "SpooledAudio"
Cohesion: 0.13
Nodes (12): List, Max, Min, ReadOnlySpan, EnvelopeAccumulator, SampleCount, Max, Min (+4 more)

### Community 34 - "BaselineScanner"
Cohesion: 0.32
Nodes (7): __getattr__(), Path, test_baseline_scanner.py — Acceptance tests for the BaselineScanner feasibility…, test_baseline_scanner_execution_on_dev_clip(), test_baseline_scanner_instantiation(), BaselineScanner, Feasibility Baseline Scanner implementing the full end-to-end VoiceScan…

### Community 35 - "IAudioPlaybackController"
Cohesion: 0.14
Nodes (6): IAudioPlaybackController, CurrentFilePath, CurrentPositionSeconds, IsAudioAvailable, IsPlaying, TotalDurationSeconds

### Community 36 - "FfplayAudioOutput"
Cohesion: 0.18
Nodes (9): IReadOnlyList, FfplayAudioOutput, IsAvailable, FfplaySession, HasExited, IAudioOutput, IsAvailable, Lazy (+1 more)

### Community 37 - ".LoadAndRunSample"
Cohesion: 0.28
Nodes (5): InferenceSession, GpuModelSample, ModelVerificationResult, Fact, GpuModelSampleTests

### Community 38 - "BackgroundScanController"
Cohesion: 0.09
Nodes (18): OverallScanProgress, ScanExecutionState, Cancelled, Completed, Failed, Idle, Paused, Scanning (+10 more)

### Community 39 - "AudioPlaybackController"
Cohesion: 0.20
Nodes (9): Stopwatch, AudioPlaybackController, CurrentFilePath, CurrentPositionSeconds, IsAudioAvailable, IsPlaying, TotalDurationSeconds, ElapsedEventArgs (+1 more)

### Community 40 - "ResultsViewModel"
Cohesion: 0.10
Nodes (19): Func, IEnumerable, List, ObservableCollection, ResultsViewModel, ExportStatus, FilteredFiles, HasResults (+11 more)

### Community 41 - "ScanDashboardViewModel"
Cohesion: 0.10
Nodes (19): ObservableCollection, ScanDashboardViewModel, CanCancelScan, CanPauseScan, CanResumeScan, CanStartScan, ClusterThreshold, CompletedFiles (+11 more)

### Community 42 - "EnrollmentWizardViewModel"
Cohesion: 0.10
Nodes (19): ObservableCollection, EnrollmentWizardViewModel, CanCreateProfile, CanDeleteProfile, CanProceedFromAudioSelection, CanProceedFromConsent, CanProceedFromQuality, CreatedProfile (+11 more)

### Community 43 - "2. Screen Walkthrough & UI Reference"
Cohesion: 0.13
Nodes (14): 1. Executive Summary & Verification Matrix, 2. Screen Walkthrough & UI Reference, 3. 5-Hour Folder Endurance & UI Thread Responsiveness Benchmark, 4. Manual Verification Checklist, A. Voice Profile Enrollment, B. Scan Execution, Benchmark Results, C. Results & Waveform Timeline (+6 more)

### Community 44 - "validate_test_directory"
Cohesion: 0.19
Nodes (11): get_repo_root(), Path, Locate the git repository root from current file., Validate that test split directory is strictly outside repository root., Discover audio clips per speaker from directory structure:…, Discover game audio noise clips., Deterministically partition speakers into dev and test sets with zero leakage., validate_test_directory() (+3 more)

### Community 45 - "IDisposable"
Cohesion: 0.29
Nodes (6): IAudioOutputSession, HasExited, FakeSession, Disposed, HasExited, IDisposable

### Community 46 - "VerdictFilter"
Cohesion: 0.40
Nodes (5): VerdictFilter, All, Match, NoMatch, Possible

### Community 47 - "generator.py"
Cohesion: 0.19
Nodes (11): dataset_config.py — Configuration models and validation for synthetic data…, print_summary_table(), Any, generator.py — Synthetic test dataset generator for VoiceScan. Enforces: 1.…, Generate and print formatted summary table of generated synthetic clips., fixture, Path, test_generator.py — Integration tests for end-to-end dataset generation. (+3 more)

### Community 48 - "AudioQualityReport"
Cohesion: 0.36
Nodes (6): IReadOnlyList, AudioQualityReport, CancellationToken, ReadOnlySpan, Task, IAudioQualityAnalyzer

### Community 49 - "SyntheticDataGenerator"
Cohesion: 0.27
Nodes (8): main(), generate_dataset.py — CLI for synthetic audio dataset generation., DatasetConfig, Any, Path, SyntheticDataGenerator, test_speaker_split_zero_leakage(), test_split_determinism()

### Community 50 - "apply_degradation_chain"
Cohesion: 0.26
Nodes (12): apply_agc(), apply_bandlimit(), apply_degradation_chain(), apply_light_noise_suppression(), apply_opus_degradation(), ndarray, degradations.py — Degradation chains simulating gameplay voice chat…, Automatic Gain Control (AGC) dynamic compression simulation. (+4 more)

### Community 51 - "BaseScanner"
Cohesion: 0.22
Nodes (8): BaseScanner, __getattr__(), Any, Path, scanner_interface.py — Scanner invocation contracts and verification stubs.…, Execute scan across audio_dir. Returns: result_json: Dictionary adhering to…, Executes a command-line scanner binary or script conforming to the contract:…, SubprocessCliScanner

### Community 52 - "CoreEngineTests"
Cohesion: 0.05
Nodes (34): AudioAugmenter, CancellationToken, IReadOnlyList, Task, AudioDecoder, AudioTrackInfo, DecodedAudioChunk, CancellationToken (+26 more)

### Community 53 - ".CreateProfileAsync"
Cohesion: 0.28
Nodes (3): Action, CancellationToken, Task

### Community 54 - ".GenerateEnvelopeAsync"
Cohesion: 0.33
Nodes (5): CancellationToken, ReadOnlySpan, Task, IWaveformService, WaveformService

### Community 55 - "ResultsView"
Cohesion: 0.24
Nodes (5): RoutedEventArgs, ResultsView, ViewModel, SelectionChangedEventArgs, UserControl

### Community 56 - "ScanMetadata"
Cohesion: 0.29
Nodes (7): ScanMetadata, ElapsedSeconds, EngineVersion, ModelId, ProfileName, Threshold, Timestamp

### Community 57 - "VoiceProfileSummary"
Cohesion: 0.25
Nodes (7): DateTimeOffset, AudioQualityTier, Acceptable, Excellent, Rejected, Warning, VoiceProfileSummary

### Community 58 - "OnnxEmbeddingModel"
Cohesion: 0.05
Nodes (27): AppPaths, DatabasePath, DataRoot, ModelsDirectory, ProfilesDirectory, Func, FbankProfile, CamPlusPlus (+19 more)

### Community 59 - "VoiceScanLogger"
Cohesion: 0.15
Nodes (10): ProfileLibrary, LogLevel, Debug, Error, Fatal, Info, Warn, VoiceScanLogger (+2 more)

### Community 60 - "FakeAudioOutput"
Cohesion: 0.15
Nodes (13): ResultSortColumn, Duration, FileName, HitCount, MaxConfidence, Verdict, Path, Start (+5 more)

### Community 61 - "FileVerdictResult"
Cohesion: 0.16
Nodes (13): IReadOnlyList, FileVerdictResult, HitSegmentResult, WaveformEnvelope, IReadOnlyList, WaveformTimelineControl, Control, VoiceScan.App.Controls (+5 more)

### Community 62 - ".StartScanAsync"
Cohesion: 0.36
Nodes (6): CancellationToken, IReadOnlyList, Task, PipelineScanOptions, CancellationToken, Task

### Community 63 - "VoiceScan Base Version Documentation"
Cohesion: 0.13
Nodes (14): 1. System Requirements & Prerequisites, 2.1 Build the Solution, 2.2 Run Unit & Integration Tests, 2.3 Run the CLI Engine, 2.4 Run the Desktop Application (Avalonia), 2. How to Build & Run, 3. Known Limitations & Edge Cases, 4. Model & Component Licenses Summary (+6 more)

### Community 68 - ".ScoreAndAggregate"
Cohesion: 0.40
Nodes (4): IReadOnlyList, MaxConfidence, Segments, Verdict

### Community 69 - ".generate_split"
Cohesion: 0.40
Nodes (3): ndarray, Load audio file converted to 16 kHz mono float32., Generate a complete synthetic split (dev or test) with enrollment and ground…

### Community 70 - "mock_speaker_pool"
Cohesion: 0.50
Nodes (4): mock_speaker_pool(), fixture, Path, Create temporary directory structure for 10 speakers with 6 clips each.

### Community 71 - ".PickFilesAsync"
Cohesion: 0.43
Nodes (4): Control, IReadOnlyList, Task, FilePickers

### Community 73 - "VoiceScan"
Cohesion: 0.29
Nodes (6): Building from source, GPU, Install, Use, VoiceScan, Where things are stored

### Community 74 - "EnrollmentStep"
Cohesion: 0.29
Nodes (6): EnrollmentStep, AudioSelection, Complete, ConsentVerification, ProfileCreation, QualityDiagnostics

### Community 78 - "ScanDashboardView"
Cohesion: 0.43
Nodes (3): RoutedEventArgs, ScanDashboardView, ViewModel

### Community 79 - "ReviewView"
Cohesion: 0.47
Nodes (3): RoutedEventArgs, ReviewView, ViewModel

### Community 80 - "DetectedSegment"
Cohesion: 0.33
Nodes (6): DetectedSegment, Embedding, EndTimeSeconds, ReasonFlags, StartTimeSeconds, Verdict

### Community 81 - ".ExportReportAsync"
Cohesion: 0.50
Nodes (3): CancellationToken, Task, ExportSettingsProvider

## Knowledge Gaps
- **346 isolated node(s):** `AudioSelection`, `ConsentVerification`, `QualityDiagnostics`, `ProfileCreation`, `Complete` (+341 more)
  These have ≤1 connection - possible missing edges or undocumented components. (Counts symbols only; 550 node(s) total have ≤1 connection when file, concept and rationale nodes are included.)
- **9 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `VoiceScan.Core` connect `VoiceScan.Core` to `SpooledAudio`, `FileScanResult`, `.LoadAndRunSample`, `.EstimateSnrDb`, `.ScanFileAsync`, `WindowItem`, `FileVerdictResult`, `CoreEngineTests`, `.GenerateEnvelopeAsync`, `.MergeAdjacentHits`, `OnnxEmbeddingModel`, `CohortDocument`, `MainAppViewModel`?**
  _High betweenness centrality (0.103) - this node is a cross-community bridge._
- **Why does `ScanDashboardViewModel` connect `ScanDashboardViewModel` to `BackgroundScanController`, `ScanDashboardView`, `MainAppViewModel`, `VoiceScan.Core`, `.StartScanAsync`, `VoiceProfileSummary`, `FileVerdictResult`, `InstallActions`?**
  _High betweenness centrality (0.089) - this node is a cross-community bridge._
- **Why does `EnrollmentWizardViewModel` connect `EnrollmentWizardViewModel` to `EnrollmentWizardView`, `EnrollmentStep`, `AudioQualityReport`, `CoreEngineTests`, `.CreateProfileAsync`, `VoiceScan.Core`, `VoiceProfileSummary`, `MainAppViewModel`, `ReviewViewModel`?**
  _High betweenness centrality (0.052) - this node is a cross-community bridge._
- **Are the 3 inferred relationships involving `ResultsViewModel` (e.g. with `.CreateMainViewModel()` and `.ResultsViewModel_ExportReport_WritesCsvAndPdf()`) actually correct?**
  _`ResultsViewModel` has 3 INFERRED edges - model-reasoned connections that need verification._
- **Are the 3 inferred relationships involving `EnrollmentWizardViewModel` (e.g. with `.CreateMainViewModel()` and `.EnrollmentWizard_DeleteSelectedProfile_RemovesFileAndRaisesEvent()`) actually correct?**
  _`EnrollmentWizardViewModel` has 3 INFERRED edges - model-reasoned connections that need verification._
- **What connects `AudioSelection`, `ConsentVerification`, `QualityDiagnostics` to the rest of the system?**
  _346 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `VoiceScan.Avalonia.csproj` be split into smaller, more focused modules?**
  _Cohesion score 0.07956989247311828 - nodes in this community are weakly interconnected._