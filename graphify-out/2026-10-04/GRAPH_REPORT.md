# Graph Report - Voice-Sampler  (2026-10-04)

## Corpus Check
- 127 files · ~212,898 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 1427 nodes · 2759 edges · 98 communities (86 shown, 10 thin omitted)
- Extraction: 89% EXTRACTED · 11% INFERRED · 0% AMBIGUOUS · INFERRED: 298 edges (avg confidence: 0.84)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `975f4df1`
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
- CoreEngineTests
- Model & Dataset Licensing Registry
- vad
- app/README.md
- eval/README.md
- models/README.md
- research/README.md
- synthetic/__init__.py
- .List
- evaluator.py
- WindowItem
- VoiceScan Baseline Experiment & Feasibility Report
- real_speech_eval.py
- EvaluationCoordinator
- VoiceScan.Core
- BulkFileTests
- OnnxEmbeddingModel
- harness/__init__.py
- ReviewViewModel
- FileScanResult
- 2. Step-by-Step Analysis & Empirical Deltas
- VoiceScan.Core.Storage
- MainAppViewModel
- InstallActions
- compute_fbank
- speech_and_noise
- SpooledAudio
- IWaveformService
- WebRtcVad
- FfplayAudioOutput
- .LoadAndRunSample
- IBackgroundScanController
- AudioPlaybackController
- ResultsViewModel
- ScanDashboardViewModel
- EnrollmentWizardViewModel
- 2. Screen Walkthrough & UI Reference
- SyntheticDataGenerator
- AppLayerTests
- VerdictFilter
- .StreamDecodeAsync
- AudioQualityReport
- generator.py
- apply_degradation_chain
- BaseScanner
- MediaFileItem
- IAudioPlaybackController
- DetectedSegment
- EnrollmentWizardView
- .OnPropertyChanged
- VoiceProfileSummary
- .Collect
- VoiceScanLogger
- CohortDocument
- FileVerdictResult
- .StartScanAsync
- VoiceScan Base Version Documentation
- SPEC: Avalonia Desktop App (Windows + Linux)
- publish-linux.sh
- .ScoreAndAggregate
- .generate_split
- .ExtractWindows
- EnrollmentSampleItem
- .EnrollProfileAsync
- VoiceScan
- EnrollmentStep
- VoiceScan.Installer.csproj
- build-installer.sh
- SileroVad
- App
- BackgroundScanController
- .ScanFileAsync
- ScanMetadata
- ResultSortColumn
- VoiceScan.App.Core.ViewModels
- ResultsView
- .Verify
- IDisposable
- .run_evaluation
- .AddSamplesAsync
- .DrawLabel
- ReviewView
- .EstimateSnrDb
- AppPaths
- .SplitPanes
- VerdictConverters
- .StreamDecode_MultiHourAudio_BoundedMemoryUsage
- FakeAudioOutput
- .ExportReportAsync

## God Nodes (most connected - your core abstractions)
1. `EnrollmentWizardViewModel` - 49 edges
2. `VoiceScan.Core` - 44 edges
3. `ResultsViewModel` - 42 edges
4. `ScanDashboardViewModel` - 42 edges
5. `ReviewViewModel` - 30 edges
6. `VoiceScanDatabase` - 29 edges
7. `AudioPlaybackController` - 27 edges
8. `CoreEngineTests` - 26 edges
9. `SileroVad` - 24 edges
10. `VoiceProfile` - 24 edges

## Surprising Connections (you probably didn't know these)
- `__getattr__()` --uses--> `BaselineScanner`  [INFERRED]
  eval/harness/__init__.py → research/baseline_experiment.py
- `FakeAudioOutput` --references--> `Duration`  [EXTRACTED]
  engine/VoiceScan.Tests/AppLayerTests.cs → app/VoiceScan.App.Core/Models/ResultModels.cs
- `__getattr__()` --uses--> `BaselineScanner`  [INFERRED]
  eval/harness/scanner_interface.py → research/baseline_experiment.py
- `FakeAudioOutput` --references--> `Path`  [EXTRACTED]
  engine/VoiceScan.Tests/AppLayerTests.cs → app/VoiceScan.App.Core/Models/MediaFileItem.cs
- `run()` --calls--> `Path`  [EXTRACTED]
  eval/real_speech_eval.py → app/VoiceScan.App.Core/Models/MediaFileItem.cs

## Import Cycles
- None detected.

## Communities (98 total, 10 thin omitted)

### Community 0 - "VoiceScan.Avalonia.csproj"
Cohesion: 0.08
Nodes (24): net10.0, Microsoft.Data.Sqlite (10.0.12), Microsoft.NET.Sdk, net10.0, Microsoft.NET.Sdk, net10.0, Microsoft.NET.Sdk, net10.0 (+16 more)

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
Cohesion: 0.12
Nodes (24): test_baseline_scanner.py — Acceptance tests for the BaselineScanner feasibility…, test_baseline_scanner_execution_on_dev_clip(), test_baseline_scanner_instantiation(), BaselineScanner, decode_audio_ffmpeg(), extract_speech_windows(), main(), merge_adjacent_hits() (+16 more)

### Community 7 - "CoreEngineTests"
Cohesion: 0.26
Nodes (3): Fact, Task, CoreEngineTests

### Community 8 - "Model & Dataset Licensing Registry"
Cohesion: 0.40
Nodes (4): 1. Verified Model & Tool Inventory, 2. Audio Processing Architecture, Model & Dataset Licensing Registry, Primary Pipeline

### Community 9 - "vad"
Cohesion: 0.20
Nodes (9): manifest_version, models, updated_at, vad, architecture, engine, license_type, notes (+1 more)

### Community 14 - "synthetic/__init__.py"
Cohesion: 0.22
Nodes (16): eval.synthetic module — VoiceScan synthetic data generation and evaluation…, align_or_tile_noise(), compute_active_power(), detect_speech_activity(), mix_at_snr(), ndarray, snr.py — Speech-active region SNR computation and signal mixing. Defines…, Compute sample-level boolean mask of speech activity using frame RMS energy. (+8 more)

### Community 15 - ".List"
Cohesion: 0.09
Nodes (27): CancellationToken, DateTimeOffset, IReadOnlyList, Task, EvidenceReportExporter, ExportResult, PdfRow, ReportExportSettings (+19 more)

### Community 16 - "evaluator.py"
Cohesion: 0.15
Nodes (25): check_regression(), evaluator.py — Central coordinator for running evaluations, logging, and…, Check if metrics violate quality gates or regress beyond tolerance., check_segment_overlap(), compute_aggregate_metrics(), compute_bootstrap_ci(), compute_det_and_eer(), evaluate_file() (+17 more)

### Community 17 - "WindowItem"
Cohesion: 0.10
Nodes (21): ClusterNode, IReadOnlyList, List, ClusterNode, Count, Id, SumVector, Windows (+13 more)

### Community 18 - "VoiceScan Baseline Experiment & Feasibility Report"
Cohesion: 0.10
Nodes (19): 1. Executive Summary & Pipeline Architecture, 2. Reproducible Dev-Set Benchmark Numbers, 3.1 Breakdown by Signal-to-Noise Ratio (SNR), 3.2 Breakdown by Degradation Chain, 3. Stratified Failure Analysis: Where It Works & Where It Fails, 4. Analysis of False Alarms, 5. Honest Viability Assessment, 6. Ranked List of the Three Most Promising Improvements (+11 more)

### Community 19 - "real_speech_eval.py"
Cohesion: 0.33
Nodes (10): build(), fetch(), main(), mix(), opus(), overlap(), ndarray, real_speech_eval.py — end-to-end engine evaluation on real read speech… (+2 more)

### Community 20 - "EvaluationCoordinator"
Cohesion: 0.18
Nodes (17): compare_runs(), EvaluationCoordinator, Compare two evaluation result JSON files and return metric deltas., PerfectGroundTruthScanner, RandomStubScanner, Stochastic stub scanner: returns random confidence scores ~ U(0, 1)., Oracle stub scanner: emits perfect detections with 1.0 confidence for target…, main() (+9 more)

### Community 21 - "VoiceScan.Core"
Cohesion: 0.23
Nodes (4): VoiceScan.Tests, VoiceScan.Core, VoiceScan.App.Core.Models, VoiceScan.App.Core.Services

### Community 22 - "BulkFileTests"
Cohesion: 0.18
Nodes (9): CancellationToken, Fact, IReadOnlyList, ReadOnlySpan, Task, BulkFileTests, FakeAnalyzer, FakeWaveformService (+1 more)

### Community 23 - "OnnxEmbeddingModel"
Cohesion: 0.07
Nodes (22): Func, FbankProfile, CamPlusPlus, WeSpeaker, Filterbank, IReadOnlyList, ISpeakerEmbeddingModel, ActiveProvider (+14 more)

### Community 24 - "harness/__init__.py"
Cohesion: 0.24
Nodes (10): __getattr__(), eval.harness module — VoiceScan evaluation harness and metrics calculation., export_json(), generate_markdown_report(), generate_plots(), Any, reporting.py — Generation of JSON results, Markdown audit reports, and PNG…, Generate high-resolution PNG plots for DET curve and SNR breakdown. (+2 more)

### Community 25 - "ReviewViewModel"
Cohesion: 0.06
Nodes (43): DateTimeOffset, IReadOnlyList, ReviewDecision, Confirmed, Rejected, Unreviewed, ReviewDecisionRecord, ReviewQueueItem (+35 more)

### Community 26 - "FileScanResult"
Cohesion: 0.17
Nodes (13): List, FileScanResult, AudioTrackIndex, ClipId, DurationSeconds, FileHash, FilePath, WaveformMaxPeaks (+5 more)

### Community 27 - "2. Step-by-Step Analysis & Empirical Deltas"
Cohesion: 0.15
Nodes (12): 1. Executive Summary & Progression, 2. Step-by-Step Analysis & Empirical Deltas, 3. Best-Performing Engine Configuration, 4. Verification Commands, Headline Progression Table, Real-speech re-evaluation (2026-10-03) — supersedes the synthetic dev-set numbers below, Step 1: Within-File Speaker Clustering (Agglomerative Hierarchical Clustering), Step 2: Temporal Smoothing and Segment Aggregation (+4 more)

### Community 28 - "VoiceScan.Core.Storage"
Cohesion: 0.25
Nodes (3): VoiceScan.Core.Logging, VoiceScan.Core.Storage, VoiceScan.Cli

### Community 29 - "MainAppViewModel"
Cohesion: 0.06
Nodes (27): AppNavigationPage, Enrollment, Results, Review, Scan, MainAppViewModel, CurrentPage, Enrollment (+19 more)

### Community 30 - "InstallActions"
Cohesion: 0.08
Nodes (22): Progress, CheckBox, VoiceScan.Installer, Form, Task, InstallActions, DataDir, DefaultInstallDir (+14 more)

### Community 31 - "compute_fbank"
Cohesion: 0.24
Nodes (8): compute_fbank(), create_session(), InferenceSession, ndarray, verify_models.py — Verification of speaker embedding models (Prompt 2). Loads…, 80-dim log-mel fbank with mean normalization, matching kaldi-native-fbank. Same…, Initialize ONNX session prioritizing CUDA, falling back to CPU with warning., verify_models()

### Community 32 - "speech_and_noise"
Cohesion: 0.50
Nodes (4): fixture, ndarray, Create speech signal with silence gaps and background noise., speech_and_noise()

### Community 33 - "SpooledAudio"
Cohesion: 0.12
Nodes (12): List, Max, Min, ReadOnlySpan, EnvelopeAccumulator, SampleCount, Max, Min (+4 more)

### Community 34 - "IWaveformService"
Cohesion: 0.29
Nodes (6): CancellationToken, ReadOnlySpan, Task, IWaveformService, WaveformPreview, WaveformService

### Community 35 - "WebRtcVad"
Cohesion: 0.16
Nodes (8): IReadOnlyList, List, ProbabilityStream, ReadOnlySpan, ProbabilityStream, WebRtcVad, Mode, SettingsFingerprint

### Community 36 - "FfplayAudioOutput"
Cohesion: 0.18
Nodes (9): IReadOnlyList, FfplayAudioOutput, IsAvailable, FfplaySession, HasExited, IAudioOutput, IsAvailable, Lazy (+1 more)

### Community 37 - ".LoadAndRunSample"
Cohesion: 0.28
Nodes (5): InferenceSession, GpuModelSample, ModelVerificationResult, Fact, GpuModelSampleTests

### Community 38 - "IBackgroundScanController"
Cohesion: 0.12
Nodes (6): IBackgroundScanController, CurrentProgress, CurrentState, FakeScanController, CurrentProgress, CurrentState

### Community 39 - "AudioPlaybackController"
Cohesion: 0.14
Nodes (9): Stopwatch, AudioPlaybackController, CurrentFilePath, CurrentPositionSeconds, IsAudioAvailable, IsPlaying, TotalDurationSeconds, ElapsedEventArgs (+1 more)

### Community 40 - "ResultsViewModel"
Cohesion: 0.10
Nodes (19): Func, IEnumerable, List, ObservableCollection, ResultsViewModel, ExportStatus, FilteredFiles, HasResults (+11 more)

### Community 41 - "ScanDashboardViewModel"
Cohesion: 0.09
Nodes (19): ObservableCollection, ScanDashboardViewModel, CanCancelScan, CanEditFiles, CanPauseScan, CanResumeScan, CanStartScan, ClusterThreshold (+11 more)

### Community 42 - "EnrollmentWizardViewModel"
Cohesion: 0.08
Nodes (22): ObservableCollection, EnrollmentWizardViewModel, AcceptedSampleCount, CanCreateProfile, CanDeleteProfile, CanProceedFromAudioSelection, CanProceedFromConsent, CanProceedFromQuality (+14 more)

### Community 43 - "2. Screen Walkthrough & UI Reference"
Cohesion: 0.13
Nodes (14): 1. Executive Summary & Verification Matrix, 2. Screen Walkthrough & UI Reference, 3. 5-Hour Folder Endurance & UI Thread Responsiveness Benchmark, 4. Manual Verification Checklist, A. Voice Profile Enrollment, B. Scan Execution, Benchmark Results, C. Results & Waveform Timeline (+6 more)

### Community 44 - "SyntheticDataGenerator"
Cohesion: 0.12
Nodes (17): Path, get_repo_root(), Locate the git repository root from current file., Validate that test split directory is strictly outside repository root., Discover audio clips per speaker from directory structure:…, Discover game audio noise clips., Deterministically partition speakers into dev and test sets with zero leakage., SyntheticDataGenerator (+9 more)

### Community 45 - "AppLayerTests"
Cohesion: 0.22
Nodes (5): AudioQualityAnalyzer, Fact, List, Task, AppLayerTests

### Community 46 - "VerdictFilter"
Cohesion: 0.40
Nodes (5): VerdictFilter, All, Match, NoMatch, Possible

### Community 47 - ".StreamDecodeAsync"
Cohesion: 0.27
Nodes (8): CancellationToken, IReadOnlyList, Task, AudioDecoder, AudioTrackInfo, DecodedAudioChunk, IAsyncEnumerable, Stream

### Community 48 - "AudioQualityReport"
Cohesion: 0.31
Nodes (6): IReadOnlyList, AudioQualityReport, CancellationToken, ReadOnlySpan, Task, IAudioQualityAnalyzer

### Community 49 - "generator.py"
Cohesion: 0.17
Nodes (13): main(), generate_dataset.py — CLI for synthetic audio dataset generation., DatasetConfig, Any, dataset_config.py — Configuration models and validation for synthetic data…, print_summary_table(), generator.py — Synthetic test dataset generator for VoiceScan. Enforces: 1.…, Generate and print formatted summary table of generated synthetic clips. (+5 more)

### Community 50 - "apply_degradation_chain"
Cohesion: 0.26
Nodes (12): apply_agc(), apply_bandlimit(), apply_degradation_chain(), apply_light_noise_suppression(), apply_opus_degradation(), ndarray, degradations.py — Degradation chains simulating gameplay voice chat…, Automatic Gain Control (AGC) dynamic compression simulation. (+4 more)

### Community 51 - "BaseScanner"
Cohesion: 0.19
Nodes (7): BaseScanner, __getattr__(), Any, scanner_interface.py — Scanner invocation contracts and verification stubs.…, Execute scan across audio_dir. Returns: result_json: Dictionary adhering to…, Executes a command-line scanner binary or script conforming to the contract:…, SubprocessCliScanner

### Community 52 - "MediaFileItem"
Cohesion: 0.18
Nodes (8): MediaFileItem, FileName, IsLoadingWaveform, IsWaveformVisible, Waveform, WaveformError, INotifyPropertyChanged, PropertyChangedEventArgs

### Community 53 - "IAudioPlaybackController"
Cohesion: 0.14
Nodes (6): IAudioPlaybackController, CurrentFilePath, CurrentPositionSeconds, IsAudioAvailable, IsPlaying, TotalDurationSeconds

### Community 54 - "DetectedSegment"
Cohesion: 0.20
Nodes (12): End, DetectedSegment, Confidence, Embedding, EndTimeSeconds, ReasonFlags, StartTimeSeconds, Verdict (+4 more)

### Community 55 - "EnrollmentWizardView"
Cohesion: 0.09
Nodes (14): Control, DragEventArgs, IReadOnlyList, Task, FilePickers, DragEventArgs, RoutedEventArgs, EnrollmentWizardView (+6 more)

### Community 57 - "VoiceProfileSummary"
Cohesion: 0.25
Nodes (7): DateTimeOffset, AudioQualityTier, Acceptable, Excellent, Rejected, Warning, VoiceProfileSummary

### Community 58 - ".Collect"
Cohesion: 0.25
Nodes (5): IEnumerable, IReadOnlyList, MediaFileCollector, IEnumerable, StringComparer

### Community 59 - "VoiceScanLogger"
Cohesion: 0.11
Nodes (16): Action, Exception, ManualResetEventSlim, Window, CrashHandler, LogPath, Exception, LogLevel (+8 more)

### Community 60 - "CohortDocument"
Cohesion: 0.18
Nodes (7): List, CohortDocument, CohortSize, CreatedAt, Embeddings, ModelId, SchemaVersion

### Community 61 - "FileVerdictResult"
Cohesion: 0.20
Nodes (11): IReadOnlyList, FileVerdictResult, HitSegmentResult, WaveformEnvelope, IReadOnlyList, WaveformTimelineControl, Envelope, Control (+3 more)

### Community 62 - ".StartScanAsync"
Cohesion: 0.29
Nodes (6): CancellationToken, IReadOnlyList, Task, PipelineScanOptions, CancellationToken, Task

### Community 63 - "VoiceScan Base Version Documentation"
Cohesion: 0.13
Nodes (14): 1. System Requirements & Prerequisites, 2.1 Build the Solution, 2.2 Run Unit & Integration Tests, 2.3 Run the CLI Engine, 2.4 Run the Desktop Application (Avalonia), 2. How to Build & Run, 3. Known Limitations & Edge Cases, 4. Model & Component Licenses Summary (+6 more)

### Community 68 - ".ScoreAndAggregate"
Cohesion: 0.24
Nodes (8): IReadOnlyList, MaxConfidence, Segments, Verdict, ScoreNormalizer, CohortSize, Mean, StdDev

### Community 69 - ".generate_split"
Cohesion: 0.33
Nodes (4): Any, ndarray, Load audio file converted to 16 kHz mono float32., Generate a complete synthetic split (dev or test) with enrollment and ground…

### Community 70 - ".ExtractWindows"
Cohesion: 0.27
Nodes (6): IReadOnlyList, SpeechAudioWindow, SpeechWindowExtractor, WindowPlan, SpeechInterval, WindowPlan

### Community 71 - "EnrollmentSampleItem"
Cohesion: 0.29
Nodes (5): EnrollmentSampleItem, AnalysisError, IsAccepted, IsAnalyzing, Report

### Community 72 - ".EnrollProfileAsync"
Cohesion: 0.29
Nodes (4): AudioAugmenter, CancellationToken, IReadOnlyList, Task

### Community 73 - "VoiceScan"
Cohesion: 0.29
Nodes (6): Building from source, GPU, Install, Use, VoiceScan, Where things are stored

### Community 74 - "EnrollmentStep"
Cohesion: 0.29
Nodes (6): EnrollmentStep, AudioSelection, Complete, ConsentVerification, ProfileCreation, QualityDiagnostics

### Community 77 - "SileroVad"
Cohesion: 0.23
Nodes (5): ProfileEnrollmentService, IReadOnlyList, ProbabilityStream, SileroVad, SettingsFingerprint

### Community 78 - "App"
Cohesion: 0.18
Nodes (6): App, STAThread, Program, AppBuilder, Application, VoiceScan.App

### Community 79 - "BackgroundScanController"
Cohesion: 0.12
Nodes (15): OverallScanProgress, ScanExecutionState, Cancelled, Completed, Failed, Idle, Paused, Scanning (+7 more)

### Community 80 - ".ScanFileAsync"
Cohesion: 0.31
Nodes (8): CancellationToken, List, Task, PipelineScanner, EmbeddingModel, Vad, FileNotFoundException, IReadOnlyDictionary

### Community 81 - "ScanMetadata"
Cohesion: 0.25
Nodes (7): ScanMetadata, ElapsedSeconds, EngineVersion, ModelId, ProfileName, Threshold, Timestamp

### Community 82 - "ResultSortColumn"
Cohesion: 0.33
Nodes (6): ResultSortColumn, Duration, FileName, HitCount, MaxConfidence, Verdict

### Community 83 - "VoiceScan.App.Core.ViewModels"
Cohesion: 0.42
Nodes (3): VoiceScan.App.Services, VoiceScan.App.Core.ViewModels, VoiceScan.App.Views

### Community 84 - "ResultsView"
Cohesion: 0.28
Nodes (4): RoutedEventArgs, ResultsView, ViewModel, SelectionChangedEventArgs

### Community 85 - ".Verify"
Cohesion: 0.39
Nodes (4): Fact, Task, SecurityHardeningTests, InvalidDataException

### Community 86 - "IDisposable"
Cohesion: 0.29
Nodes (6): IAudioOutputSession, HasExited, FakeSession, Disposed, HasExited, IDisposable

### Community 87 - ".run_evaluation"
Cohesion: 0.29
Nodes (6): categorize_snr(), get_git_commit_hash(), Any, Log test set execution to persistent log file., Categorize SNR in dB into standard reporting buckets., Execute full evaluation run on a dataset.

### Community 88 - ".AddSamplesAsync"
Cohesion: 0.43
Nodes (4): CancellationToken, IEnumerable, IReadOnlyList, Task

### Community 89 - ".DrawLabel"
Cohesion: 0.38
Nodes (4): DrawingContext, FontWeight, IBrush, Point

### Community 90 - "ReviewView"
Cohesion: 0.38
Nodes (4): RoutedEventArgs, ReviewView, ViewModel, UserControl

### Community 92 - "AppPaths"
Cohesion: 0.18
Nodes (6): AppPaths, DatabasePath, DataRoot, ModelsDirectory, ProfilesDirectory, ModelIntegrity

### Community 93 - ".SplitPanes"
Cohesion: 0.40
Nodes (3): Control, ResponsiveLayout, Grid

### Community 94 - "VerdictConverters"
Cohesion: 0.50
Nodes (3): VerdictConverters, Color, IValueConverter

### Community 95 - ".StreamDecode_MultiHourAudio_BoundedMemoryUsage"
Cohesion: 0.50
Nodes (3): Fact, Task, StreamingMemoryTests

### Community 96 - "FakeAudioOutput"
Cohesion: 0.33
Nodes (6): Start, FakeAudioOutput, IsAvailable, Sessions, Starts, FakeSession

### Community 97 - ".ExportReportAsync"
Cohesion: 0.50
Nodes (3): CancellationToken, Task, ExportSettingsProvider

## Knowledge Gaps
- **363 isolated node(s):** `AudioSelection`, `ConsentVerification`, `QualityDiagnostics`, `ProfileCreation`, `Complete` (+358 more)
  These have ≤1 connection - possible missing edges or undocumented components. (Counts symbols only; 581 node(s) total have ≤1 connection when file, concept and rationale nodes are included.)
- **10 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Path` connect `SyntheticDataGenerator` to `FakeAudioOutput`, `baseline_experiment.py`, `real_speech_eval.py`, `BaseScanner`, `MediaFileItem`, `.run_evaluation`, `compute_fbank`?**
  _High betweenness centrality (0.204) - this node is a cross-community bridge._
- **Why does `MediaFileItem` connect `MediaFileItem` to `IWaveformService`, `EnrollmentSampleItem`, `ScanDashboardViewModel`, `SyntheticDataGenerator`, `BulkFileTests`, `.Collect`, `FileVerdictResult`, `.StartScanAsync`?**
  _High betweenness centrality (0.147) - this node is a cross-community bridge._
- **Why does `VoiceScan.Core` connect `VoiceScan.Core` to `WindowItem`, `OnnxEmbeddingModel`, `ReviewViewModel`, `VoiceScan.Core.Storage`, `MainAppViewModel`, `SpooledAudio`, `IWaveformService`, `WebRtcVad`, `.LoadAndRunSample`, `.StreamDecodeAsync`, `DetectedSegment`, `CohortDocument`, `FileVerdictResult`, `.ExtractWindows`, `.EnrollProfileAsync`, `SileroVad`, `App`, `ScanMetadata`, `VoiceScan.App.Core.ViewModels`, `.EstimateSnrDb`, `AppPaths`?**
  _High betweenness centrality (0.120) - this node is a cross-community bridge._
- **Are the 4 inferred relationships involving `EnrollmentWizardViewModel` (e.g. with `.CreateMainViewModel()` and `.EnrollmentWizard_DeleteSelectedProfile_RemovesFileAndRaisesEvent()`) actually correct?**
  _`EnrollmentWizardViewModel` has 4 INFERRED edges - model-reasoned connections that need verification._
- **Are the 3 inferred relationships involving `ResultsViewModel` (e.g. with `.CreateMainViewModel()` and `.ResultsViewModel_ExportReport_WritesCsvAndPdf()`) actually correct?**
  _`ResultsViewModel` has 3 INFERRED edges - model-reasoned connections that need verification._
- **Are the 2 inferred relationships involving `ScanDashboardViewModel` (e.g. with `.CreateMainViewModel()` and `.ScanDashboard_AddPaths_AccumulatesWithoutDuplicates_AndRemoves()`) actually correct?**
  _`ScanDashboardViewModel` has 2 INFERRED edges - model-reasoned connections that need verification._
- **What connects `AudioSelection`, `ConsentVerification`, `QualityDiagnostics` to the rest of the system?**
  _363 weakly-connected nodes found - possible documentation gaps or missing edges._