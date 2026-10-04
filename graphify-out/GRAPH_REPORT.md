# Graph Report - Voice-Sampler  (2026-10-04)

## Corpus Check
- 136 files · ~295,072 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 1826 nodes · 3710 edges · 114 communities (100 shown, 10 thin omitted)
- Extraction: 88% EXTRACTED · 12% INFERRED · 0% AMBIGUOUS · INFERRED: 437 edges (avg confidence: 0.83)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `959ebd02`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- VoiceScan.Avalonia.csproj
- VoiceScan System Design
- VoiceScan: 10 Agent Prompts (concept → base version)
- 2. Core Functional Requirements
- VoiceScan: Project Plan (working title)
- AGENTS.md — Agent Operating Guidelines for VoiceScan
- CoreEngineTests
- ReviewViewModel
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
- inference_server.py
- VoiceScan.App.Core.Models
- IDisposable
- OnnxEmbeddingModel
- IAudioPlaybackController
- ReviewDecisionRecord
- FileScanResult
- VoiceScan Accuracy & Evolution Log
- Nemo
- MainWindow
- .StreamDecodeAsync
- GpuUtilizationSampler
- speech_and_noise
- SpooledAudio
- WaveformEnvelope
- .OpenMainWindowAsync
- IAudioOutputSession
- .LoadAndRunSample
- IBackgroundScanController
- AudioPlaybackController
- ResultsViewModel
- ScanDashboardViewModel
- EnrollmentWizardViewModel
- 2. Screen Walkthrough & UI Reference
- SyntheticDataGenerator
- AppLayerTests
- ResultSortColumn
- RegressionTests
- AudioQualityReport
- generator.py
- apply_degradation_chain
- ScanMetadata
- CohortDocument
- Core
- DetectedSegment
- ScanDashboardView
- ScanExecutionState
- VoiceProfileSummary
- .Collect
- .PipelineScanner_SidecarSpeech_MapsSegmentsAndScoresProfile
- .EnrollProfileAsync
- ResultsView
- .OnPropertyChanged
- VoiceScan Base Version Documentation
- SPEC: Avalonia Desktop App (Windows + Linux)
- publish-linux.sh
- .ScoreAndAggregate
- .generate_split
- HitSegmentViewModel
- MediaFileItem
- MockEmbeddingModel
- VoiceScan
- Program
- VoiceScan.Installer.csproj
- build-installer.sh
- .ScanFileAsync
- EnrollmentStep
- BackgroundScanController
- WebRtcVad
- CrashHandler
- SidecarManager
- ResultModels.cs
- .Warn
- .AddSamplesAsync
- EvidenceReportExporter
- VoiceProfile
- synthetic_speech_and_game_dirs
- SidecarScanResponse
- ReviewQueueItem
- VoiceScan.Core.Logging
- AppPaths
- EnrollmentSampleItem
- Third-Party Notices
- UserSettingsStore
- MainAppViewModel
- VoiceScan.Core
- .ConfirmSegmentAsync
- SetupWindow
- start_sidecar.sh script
- .ExtractWindows
- Task
- LogLevel
- .BuildAvaloniaApp
- SetupCheck
- ReviewDecision
- .SplitPanes
- VerdictConverters
- SegmentViewModel.cs
- SidecarHealthResponse
- mock_speaker_pool

## God Nodes (most connected - your core abstractions)
1. `ResultsViewModel` - 60 edges
2. `EnrollmentWizardViewModel` - 50 edges
3. `ScanDashboardViewModel` - 49 edges
4. `ReviewViewModel` - 46 edges
5. `VoiceScan.Core` - 46 edges
6. `OnnxEmbeddingModel` - 43 edges
7. `VoiceScanDatabase` - 39 edges
8. `VoiceProfile` - 37 edges
9. `WebRtcVad` - 35 edges
10. `RegressionTests` - 33 edges

## Surprising Connections (you probably didn't know these)
- `FakeAudioOutput` --references--> `Duration`  [EXTRACTED]
  engine/VoiceScan.Tests/AppLayerTests.cs → app/VoiceScan.App.Core/Models/ResultModels.cs
- `FakeAudioOutput` --references--> `Path`  [EXTRACTED]
  engine/VoiceScan.Tests/AppLayerTests.cs → app/VoiceScan.App.Core/Models/MediaFileItem.cs
- `run()` --calls--> `Path`  [EXTRACTED]
  eval/real_speech_eval.py → app/VoiceScan.App.Core/Models/MediaFileItem.cs
- `FakeScanController` --references--> `ScanExecutionState`  [EXTRACTED]
  engine/VoiceScan.Tests/BulkFileTests.cs → app/VoiceScan.App.Core/Models/ScanModels.cs
- `FakeScanController` --references--> `OverallScanProgress`  [EXTRACTED]
  engine/VoiceScan.Tests/BulkFileTests.cs → app/VoiceScan.App.Core/Models/ScanModels.cs

## Import Cycles
- None detected.

## Communities (114 total, 10 thin omitted)

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

### Community 6 - "CoreEngineTests"
Cohesion: 0.19
Nodes (4): SpeechInterval, Fact, Task, CoreEngineTests

### Community 7 - "ReviewViewModel"
Cohesion: 0.08
Nodes (24): IReadOnlyList, List, ObservableCollection, ReviewViewModel, AddToProfileOnConfirm, AvailableSpeakers, CanAct, CanPlaySnippet (+16 more)

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
Cohesion: 0.08
Nodes (31): IReadOnlyList, CancellationToken, Func, IAsyncEnumerable, IReadOnlyList, Task, OrderedPrefetch, FastFileHasher (+23 more)

### Community 16 - "evaluator.py"
Cohesion: 0.05
Nodes (70): Path, categorize_snr(), check_regression(), compare_runs(), EvaluationCoordinator, get_git_commit_hash(), Any, evaluator.py — Central coordinator for running evaluations, logging, and… (+62 more)

### Community 17 - "WindowItem"
Cohesion: 0.12
Nodes (19): ClusterNode, IReadOnlyList, List, ClusterNode, Count, SumVector, Windows, SpeakerCluster (+11 more)

### Community 18 - "VoiceScan Baseline Experiment & Feasibility Report"
Cohesion: 0.10
Nodes (19): 1. Executive Summary & Pipeline Architecture, 2. Reproducible Dev-Set Benchmark Numbers, 3.1 Breakdown by Signal-to-Noise Ratio (SNR), 3.2 Breakdown by Degradation Chain, 3. Stratified Failure Analysis: Where It Works & Where It Fails, 4. Analysis of False Alarms, 5. Honest Viability Assessment, 6. Ranked List of the Three Most Promising Improvements (+11 more)

### Community 19 - "real_speech_eval.py"
Cohesion: 0.33
Nodes (10): build(), fetch(), main(), mix(), opus(), overlap(), ndarray, real_speech_eval.py — end-to-end engine evaluation on real read speech… (+2 more)

### Community 20 - "inference_server.py"
Cohesion: 0.10
Nodes (27): BaseModel, FastAPI, get, post, DetectedSegmentModel, get_device_info(), health_check(), HealthResponse (+19 more)

### Community 21 - "VoiceScan.App.Core.Models"
Cohesion: 0.20
Nodes (6): VoiceScan.App.Services, VoiceScan.App, VoiceScan.App.Core.ViewModels, VoiceScan.App.Views, VoiceScan.App.Core.Models, VoiceScan.App.Core.Services

### Community 22 - "IDisposable"
Cohesion: 0.18
Nodes (10): CancellationToken, Fact, IReadOnlyList, ReadOnlySpan, Task, BulkFileTests, FakeAnalyzer, FakeWaveformService (+2 more)

### Community 23 - "OnnxEmbeddingModel"
Cohesion: 0.13
Nodes (12): AppServiceBootstrap, InferenceSession, IReadOnlyList, OnnxEmbeddingModel, ActiveProvider, EmbeddingDimension, IsCudaActive, ModelId (+4 more)

### Community 24 - "IAudioPlaybackController"
Cohesion: 0.15
Nodes (6): IAudioPlaybackController, CurrentFilePath, CurrentPositionSeconds, IsAudioAvailable, IsPlaying, TotalDurationSeconds

### Community 25 - "ReviewDecisionRecord"
Cohesion: 0.25
Nodes (9): ReviewDecisionRecord, CancellationToken, IReadOnlyList, List, SemaphoreSlim, SqliteDataReader, Task, IReviewRepository (+1 more)

### Community 26 - "FileScanResult"
Cohesion: 0.12
Nodes (18): List, FileScanResult, AudioTrackIndex, ClipId, DurationSeconds, Error, FileHash, FilePath (+10 more)

### Community 27 - "VoiceScan Accuracy & Evolution Log"
Cohesion: 0.14
Nodes (13): 1. Executive Summary & Progression, 2. Step-by-Step Analysis & Empirical Deltas, 3. Best-Performing Engine Configuration, 4. Verification Commands, ECAPA / TitaNet operating points (2026-10-04) — supersedes the WeSpeaker numbers below, Headline Progression Table, Real-speech re-evaluation (2026-10-03, WeSpeaker / CAM++, models since replaced) — supersedes the synthetic dev-set numbers below, Step 1: Within-File Speaker Clustering (Agglomerative Hierarchical Clustering) (+5 more)

### Community 28 - "Nemo"
Cohesion: 0.12
Nodes (12): FeatureFrontEnd, NemoMelSpectrogram, SpeechBrainFbank, FeatureMatrix, Fft, Size, Nemo, RealSpectrum (+4 more)

### Community 29 - "MainWindow"
Cohesion: 0.14
Nodes (10): AppNavigationPage, Enrollment, Results, Review, Scan, Button, Dictionary, RoutedEventArgs (+2 more)

### Community 30 - ".StreamDecodeAsync"
Cohesion: 0.05
Nodes (38): Progress, CheckBox, VoiceScan.Installer, CancellationToken, IAsyncEnumerable, IReadOnlyList, Process, Task (+30 more)

### Community 31 - "GpuUtilizationSampler"
Cohesion: 0.21
Nodes (6): CancellationToken, CancellationTokenSource, Task, TimeSpan, GpuUtilizationSampler, Latest

### Community 32 - "speech_and_noise"
Cohesion: 0.50
Nodes (4): fixture, ndarray, Create speech signal with silence gaps and background noise., speech_and_noise()

### Community 33 - "SpooledAudio"
Cohesion: 0.09
Nodes (15): ConcurrentDictionary, List, Max, Min, ReadOnlySpan, EnvelopeAccumulator, SampleCount, Max (+7 more)

### Community 34 - "WaveformEnvelope"
Cohesion: 0.29
Nodes (7): WaveformEnvelope, CancellationToken, ReadOnlySpan, Task, IWaveformService, WaveformPreview, WaveformService

### Community 35 - ".OpenMainWindowAsync"
Cohesion: 0.24
Nodes (6): IClassicDesktopStyleApplicationLifetime, Task, Window, App, Application, ProgressBar

### Community 36 - "IAudioOutputSession"
Cohesion: 0.08
Nodes (20): IReadOnlyList, Process, FfplayAudioOutput, IsAvailable, FfplaySession, HasExited, IAudioOutput, IsAvailable (+12 more)

### Community 37 - ".LoadAndRunSample"
Cohesion: 0.28
Nodes (5): InferenceSession, GpuModelSample, ModelVerificationResult, Fact, GpuModelSampleTests

### Community 38 - "IBackgroundScanController"
Cohesion: 0.09
Nodes (12): IBackgroundScanController, CurrentProgress, CurrentState, ModelId, OperatingPoint, ModelOperatingPoint, ModelSpec, FakeScanController (+4 more)

### Community 39 - "AudioPlaybackController"
Cohesion: 0.14
Nodes (9): Stopwatch, AudioPlaybackController, CurrentFilePath, CurrentPositionSeconds, IsAudioAvailable, IsPlaying, TotalDurationSeconds, ElapsedEventArgs (+1 more)

### Community 40 - "ResultsViewModel"
Cohesion: 0.05
Nodes (40): IReadOnlyList, FileVerdictResult, IsError, HitSegmentResult, DisplaySpeakerLabel, End, IsFlagged, ModerationViolations (+32 more)

### Community 41 - "ScanDashboardViewModel"
Cohesion: 0.08
Nodes (23): IEnumerable, ObservableCollection, ScanDashboardViewModel, CanCancelScan, CanEditFiles, CanPauseScan, CanResumeScan, CanStartScan (+15 more)

### Community 42 - "EnrollmentWizardViewModel"
Cohesion: 0.09
Nodes (21): ObservableCollection, EnrollmentWizardViewModel, AcceptedSampleCount, CanCreateProfile, CanDeleteProfile, CanProceedFromAudioSelection, CanProceedFromConsent, CanProceedFromQuality (+13 more)

### Community 43 - "2. Screen Walkthrough & UI Reference"
Cohesion: 0.13
Nodes (14): 1. Executive Summary & Verification Matrix, 2. Screen Walkthrough & UI Reference, 3. 5-Hour Folder Endurance & UI Thread Responsiveness Benchmark, 4. Manual Verification Checklist, A. Voice Profile Enrollment, B. Scan Execution, Benchmark Results, C. Results & Waveform Timeline (+6 more)

### Community 44 - "SyntheticDataGenerator"
Cohesion: 0.16
Nodes (14): Path, get_repo_root(), Locate the git repository root from current file., Validate that test split directory is strictly outside repository root., Discover audio clips per speaker from directory structure:…, Discover game audio noise clips., Deterministically partition speakers into dev and test sets with zero leakage., SyntheticDataGenerator (+6 more)

### Community 45 - "AppLayerTests"
Cohesion: 0.16
Nodes (10): AudioQualityAnalyzer, Fact, List, Start, AppLayerTests, FakeAudioOutput, IsAvailable, Sessions (+2 more)

### Community 46 - "ResultSortColumn"
Cohesion: 0.33
Nodes (6): ResultSortColumn, Duration, FileName, HitCount, MaxConfidence, Verdict

### Community 47 - "RegressionTests"
Cohesion: 0.15
Nodes (8): ArgumentException, Fact, InvalidDataException, List, Task, RegressionTests, SilentOutput, IsAvailable

### Community 48 - "AudioQualityReport"
Cohesion: 0.31
Nodes (6): IReadOnlyList, AudioQualityReport, CancellationToken, ReadOnlySpan, Task, IAudioQualityAnalyzer

### Community 49 - "generator.py"
Cohesion: 0.22
Nodes (10): main(), generate_dataset.py — CLI for synthetic audio dataset generation., DatasetConfig, Any, dataset_config.py — Configuration models and validation for synthetic data…, print_summary_table(), generator.py — Synthetic test dataset generator for VoiceScan. Enforces: 1.…, Generate and print formatted summary table of generated synthetic clips. (+2 more)

### Community 50 - "apply_degradation_chain"
Cohesion: 0.26
Nodes (12): apply_agc(), apply_bandlimit(), apply_degradation_chain(), apply_light_noise_suppression(), apply_opus_degradation(), ndarray, degradations.py — Degradation chains simulating gameplay voice chat…, Automatic Gain Control (AGC) dynamic compression simulation. (+4 more)

### Community 51 - "ScanMetadata"
Cohesion: 0.18
Nodes (11): ScanMetadata, ClusteringEnabled, ClusterThreshold, ElapsedSeconds, EngineVersion, ModelId, ModelVersion, ProfileName (+3 more)

### Community 52 - "CohortDocument"
Cohesion: 0.20
Nodes (7): List, CohortDocument, CohortSize, CreatedAt, Embeddings, ModelId, SchemaVersion

### Community 53 - "Core"
Cohesion: 0.15
Nodes (5): Core, List, Core, ProbabilityStream, Span

### Community 54 - "DetectedSegment"
Cohesion: 0.14
Nodes (17): End, IReadOnlyList, DetectedSegment, Confidence, Embedding, EndTimeSeconds, IsOffensive, ModerationViolations (+9 more)

### Community 55 - "ScanDashboardView"
Cohesion: 0.06
Nodes (23): Control, DragEventArgs, IReadOnlyList, Task, FilePickers, DragEventArgs, RoutedEventArgs, EnrollmentWizardView (+15 more)

### Community 56 - "ScanExecutionState"
Cohesion: 0.20
Nodes (9): TimeSpan, OverallScanProgress, ScanExecutionState, Cancelled, Completed, Failed, Idle, Paused (+1 more)

### Community 57 - "VoiceProfileSummary"
Cohesion: 0.25
Nodes (7): DateTimeOffset, AudioQualityTier, Acceptable, Excellent, Rejected, Warning, VoiceProfileSummary

### Community 58 - ".Collect"
Cohesion: 0.29
Nodes (4): IEnumerable, IReadOnlyList, MediaFileCollector, StringComparer

### Community 59 - ".PipelineScanner_SidecarSpeech_MapsSegmentsAndScoresProfile"
Cohesion: 0.21
Nodes (12): ArgumentException, CancellationToken, Fact, Func, Task, MockHttpMessageHandler, MockInferenceClient, SidecarInferenceTests (+4 more)

### Community 60 - ".EnrollProfileAsync"
Cohesion: 0.11
Nodes (14): AudioAugmenter, IReadOnlyList, ISpeakerEmbeddingModel, ActiveProvider, EmbeddingDimension, IsCudaActive, ModelId, ModelVersion (+6 more)

### Community 61 - "ResultsView"
Cohesion: 0.08
Nodes (18): TimeFormat, IReadOnlyList, WaveformTimelineControl, Envelope, PropertyChangedEventArgs, RoutedEventArgs, SelectionChangedEventArgs, VisualTreeAttachmentEventArgs (+10 more)

### Community 62 - ".OnPropertyChanged"
Cohesion: 0.18
Nodes (4): ProfileLibrary, Action, PropertyChangedEventArgs, NotifyCollectionChangedEventArgs

### Community 63 - "VoiceScan Base Version Documentation"
Cohesion: 0.13
Nodes (14): 1. System Requirements & Prerequisites, 2.1 Build the Solution, 2.2 Run Unit & Integration Tests, 2.3 Run the CLI Engine, 2.4 Run the Desktop Application (Avalonia), 2. How to Build & Run, 3. Known Limitations & Edge Cases, 4. Model & Component Licenses Summary (+6 more)

### Community 68 - ".ScoreAndAggregate"
Cohesion: 0.22
Nodes (8): List, AcousticDiagnostics, IReadOnlyList, ScoreNormalizer, CohortSize, Mean, Segments, StdDev

### Community 69 - ".generate_split"
Cohesion: 0.33
Nodes (4): Any, ndarray, Load audio file converted to 16 kHz mono float32., Generate a complete synthetic split (dev or test) with enrollment and ground…

### Community 70 - "HitSegmentViewModel"
Cohesion: 0.08
Nodes (23): IReadOnlyList, HitSegmentViewModel, Confidence, Decision, DisplaySpeakerLabel, DurationSeconds, End, EndTimeSeconds (+15 more)

### Community 71 - "MediaFileItem"
Cohesion: 0.18
Nodes (8): MediaFileItem, FileName, IsLoadingWaveform, IsWaveformVisible, Waveform, WaveformError, Task, INotifyPropertyChanged

### Community 72 - "MockEmbeddingModel"
Cohesion: 0.18
Nodes (8): IReadOnlyList, MockEmbeddingModel, ActiveProvider, EmbeddingDimension, IsCudaActive, ModelId, ModelVersion, OperatingPoint

### Community 73 - "VoiceScan"
Cohesion: 0.29
Nodes (6): Building from source, GPU, Install, Use, VoiceScan, Where things are stored

### Community 74 - "Program"
Cohesion: 0.31
Nodes (6): Dictionary, List, Task, Program, UsageException, Exception

### Community 77 - ".ScanFileAsync"
Cohesion: 0.16
Nodes (13): Action, CancellationToken, Exception, Func, List, Task, PipelineScanner, EmbeddingModel (+5 more)

### Community 78 - "EnrollmentStep"
Cohesion: 0.29
Nodes (6): EnrollmentStep, AudioSelection, Complete, ConsentVerification, ProfileCreation, QualityDiagnostics

### Community 79 - "BackgroundScanController"
Cohesion: 0.14
Nodes (13): CancellationToken, CancellationTokenSource, IReadOnlyList, ManualResetEventSlim, Stopwatch, Task, BackgroundScanController, CurrentProgress (+5 more)

### Community 80 - "WebRtcVad"
Cohesion: 0.24
Nodes (6): WebRtcVad, Mode, SettingsFingerprint, CliArgumentTests, InlineData, Theory

### Community 81 - "CrashHandler"
Cohesion: 0.31
Nodes (7): Action, Exception, ManualResetEventSlim, Window, CrashHandler, LogPath, TextBox

### Community 82 - "SidecarManager"
Cohesion: 0.24
Nodes (8): CancellationToken, Process, SemaphoreSlim, Task, TimeSpan, SidecarManager, Client, IAsyncDisposable

### Community 83 - "ResultModels.cs"
Cohesion: 0.29
Nodes (6): VerdictFilter, All, Error, Match, NoMatch, Possible

### Community 84 - ".Warn"
Cohesion: 0.22
Nodes (4): Exception, VoiceScanLogger, LogFilePath, ValueTask

### Community 85 - ".AddSamplesAsync"
Cohesion: 0.36
Nodes (4): CancellationToken, IEnumerable, IReadOnlyList, Task

### Community 86 - "EvidenceReportExporter"
Cohesion: 0.16
Nodes (10): CancellationToken, DateTimeOffset, IReadOnlyList, Task, EvidenceReportExporter, ExportResult, PdfRow, ReportExportSettings (+2 more)

### Community 87 - "VoiceProfile"
Cohesion: 0.13
Nodes (12): List, VoiceProfile, Centroid, ClipCount, CreatedAt, EnrollmentEmbeddings, ModelId, ModelVersion (+4 more)

### Community 88 - "synthetic_speech_and_game_dirs"
Cohesion: 0.67
Nodes (3): fixture, Create miniature clean speech clips and game audio clips., synthetic_speech_and_game_dirs()

### Community 89 - "SidecarScanResponse"
Cohesion: 0.21
Nodes (10): CancellationToken, DetectedSegment, List, Task, TimeSpan, IInferenceClient, SidecarClient, SidecarScanResponse (+2 more)

### Community 90 - "ReviewQueueItem"
Cohesion: 0.17
Nodes (12): DateTimeOffset, IReadOnlyList, ReviewQueueItem, DisplaySpeakerLabel, End, IsFlagged, IsOffensive, ModerationViolations (+4 more)

### Community 91 - "VoiceScan.Core.Logging"
Cohesion: 0.18
Nodes (4): VoiceScan.Core.Logging, VoiceScan.Core.Storage, VoiceScan.Core.Inference, VoiceScan.Cli

### Community 92 - "AppPaths"
Cohesion: 0.11
Nodes (14): AppPaths, DatabasePath, DataRoot, LogFilePath, ModelsDirectory, ProfilesDirectory, SpoolDirectory, ModelIntegrity (+6 more)

### Community 93 - "EnrollmentSampleItem"
Cohesion: 0.29
Nodes (5): EnrollmentSampleItem, AnalysisError, IsAccepted, IsAnalyzing, Report

### Community 94 - "Third-Party Notices"
Cohesion: 0.50
Nodes (3): Feature front-ends, Third-Party Notices, WebRTC voice activity detector

### Community 95 - "UserSettingsStore"
Cohesion: 0.21
Nodes (8): UserSettings, ClusterThreshold, LastBrowseFolder, LastProfilePath, UseClustering, UseTemporalSmoothing, UserSettingsStore, Current

### Community 96 - "MainAppViewModel"
Cohesion: 0.18
Nodes (10): MainAppViewModel, CurrentPage, Enrollment, GpuStatusMessage, IsDarkTheme, IsMicaBackdropEnabled, Results, Review (+2 more)

### Community 98 - ".ConfirmSegmentAsync"
Cohesion: 0.35
Nodes (3): CancellationToken, IEnumerable, Task

### Community 99 - "SetupWindow"
Cohesion: 0.36
Nodes (3): IClassicDesktopStyleApplicationLifetime, RoutedEventArgs, SetupWindow

### Community 102 - ".ExtractWindows"
Cohesion: 0.40
Nodes (5): IReadOnlyList, SpeechAudioWindow, SpeechWindowExtractor, WindowPlan, WindowPlan

### Community 103 - "Task"
Cohesion: 0.38
Nodes (4): CancellationToken, IReadOnlyList, Task, FakeReviewRepo

### Community 104 - "LogLevel"
Cohesion: 0.29
Nodes (6): LogLevel, Debug, Error, Fatal, Info, Warn

### Community 105 - ".BuildAvaloniaApp"
Cohesion: 0.40
Nodes (3): STAThread, Program, AppBuilder

### Community 106 - "SetupCheck"
Cohesion: 0.40
Nodes (3): IReadOnlyList, SetupCheck, FfmpegInstallHint

### Community 107 - "ReviewDecision"
Cohesion: 0.40
Nodes (4): ReviewDecision, Confirmed, Rejected, Unreviewed

### Community 108 - ".SplitPanes"
Cohesion: 0.40
Nodes (3): Control, ResponsiveLayout, Grid

### Community 109 - "VerdictConverters"
Cohesion: 0.50
Nodes (3): VerdictConverters, Color, IValueConverter

### Community 110 - "SegmentViewModel.cs"
Cohesion: 0.50
Nodes (3): SegmentFilterOption, AllSegments, FlaggedOnly

### Community 111 - "SidecarHealthResponse"
Cohesion: 0.50
Nodes (4): SidecarHealthResponse, CudaAvailable, DeviceName, Status

### Community 112 - "mock_speaker_pool"
Cohesion: 0.67
Nodes (3): mock_speaker_pool(), fixture, Create temporary directory structure for 10 speakers with 6 clips each.

## Knowledge Gaps
- **479 isolated node(s):** `AudioSelection`, `ConsentVerification`, `QualityDiagnostics`, `ProfileCreation`, `Complete` (+474 more)
  These have ≤1 connection - possible missing edges or undocumented components. (Counts symbols only; 746 node(s) total have ≤1 connection when file, concept and rationale nodes are included.)
- **10 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Path` connect `SyntheticDataGenerator` to `evaluator.py`, `real_speech_eval.py`, `AppLayerTests`, `MediaFileItem`?**
  _High betweenness centrality (0.142) - this node is a cross-community bridge._
- **Why does `ScanDashboardViewModel` connect `ScanDashboardViewModel` to `MainAppViewModel`, `WaveformEnvelope`, `IBackgroundScanController`, `MediaFileItem`, `ResultsViewModel`, `BackgroundScanController`, `VoiceScan.App.Core.Models`, `EvidenceReportExporter`, `OnnxEmbeddingModel`, `ScanExecutionState`, `VoiceProfileSummary`, `ScanDashboardView`, `.StreamDecodeAsync`, `UserSettingsStore`?**
  _High betweenness centrality (0.120) - this node is a cross-community bridge._
- **Why does `MediaFileItem` connect `MediaFileItem` to `WaveformEnvelope`, `ScanDashboardViewModel`, `SyntheticDataGenerator`, `.AddSamplesAsync`, `IDisposable`, `EnrollmentSampleItem`?**
  _High betweenness centrality (0.094) - this node is a cross-community bridge._
- **Are the 5 inferred relationships involving `ResultsViewModel` (e.g. with `.CreateMainViewModel()` and `.ResultsViewModel_ExportReport_WritesCsvAndPdf()`) actually correct?**
  _`ResultsViewModel` has 5 INFERRED edges - model-reasoned connections that need verification._
- **Are the 5 inferred relationships involving `EnrollmentWizardViewModel` (e.g. with `.CreateMainViewModel()` and `.EnrollmentWizard_DeleteSelectedProfile_RemovesFileAndRaisesEvent()`) actually correct?**
  _`EnrollmentWizardViewModel` has 5 INFERRED edges - model-reasoned connections that need verification._
- **Are the 2 inferred relationships involving `ScanDashboardViewModel` (e.g. with `.CreateMainViewModel()` and `.ScanDashboard_AddPaths_AccumulatesWithoutDuplicates_AndRemoves()`) actually correct?**
  _`ScanDashboardViewModel` has 2 INFERRED edges - model-reasoned connections that need verification._
- **Are the 3 inferred relationships involving `ReviewViewModel` (e.g. with `.CreateMainViewModel()` and `.ReviewViewModel_SegmentFilteringAndSpeakerProperties_OperatesCorrectly()`) actually correct?**
  _`ReviewViewModel` has 3 INFERRED edges - model-reasoned connections that need verification._