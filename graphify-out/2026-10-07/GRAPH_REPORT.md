# Graph Report - Voice Sampler  (2026-10-07)

## Corpus Check
- 170 files · ~330,801 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 2842 nodes · 6344 edges · 163 communities (142 shown, 16 thin omitted)
- Extraction: 88% EXTRACTED · 12% INFERRED · 0% AMBIGUOUS · INFERRED: 735 edges (avg confidence: 0.83)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `9b63996c`
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
- VoiceScanDatabase
- evaluator.py
- WindowItem
- VoiceScan Baseline Experiment & Feasibility Report
- real_speech_eval.py
- inference_server.py
- VoiceScan.Core
- IDisposable
- OnnxEmbeddingModel
- VoiceProfile
- ReviewDecisionRecord
- DetectedSegment
- VoiceScan Accuracy & Evolution Log
- Nemo
- AppNavigationPage
- InstallActions
- GpuUtilizationSampler
- speech_and_noise
- SpooledAudio
- MediaFileItem
- Task
- IAudioOutputSession
- .StreamDecodeAsync
- ModerationStore
- AudioPlaybackController
- ResultsViewModel
- ScanDashboardViewModel
- EnrollmentWizardViewModel
- 2. Screen Walkthrough & UI Reference
- SyntheticDataGenerator
- AppLayerTests
- VoiceScan.App.Core.Models
- RegressionTests
- AudioQualityReport
- generator.py
- apply_degradation_chain
- IncidentsViewModel
- CohortDocument
- AppThemeTests
- EnrollmentSampleItem
- ScanDashboardView
- Core
- ScanSpeedTests
- .Collect
- SidecarInferenceTests
- AppTheme
- Program
- test_pipeline_logic.py
- VoiceScan Base Version Documentation
- SPEC: Avalonia Desktop App (Windows + Linux)
- publish-linux.sh
- .ScoreAndAggregate
- .generate_split
- HitSegmentViewModel
- VoiceScan.App.Services
- SpeakersViewModel
- VoiceScan
- .RunAsync
- VoiceScan.Installer.csproj
- build-installer.sh
- .ScanFileAsync
- Any
- BackgroundScanController
- SidecarScanResponse
- MainWindow
- SidecarManager
- VerdictFilter
- VoiceScanLogger
- .List
- FileVerdictResult
- EnrollmentStep
- synthetic_speech_and_game_dirs
- SidecarClient
- ReviewQueueItem
- .Command
- RecordingHandler
- UtteranceItem
- Third-Party Notices
- UserSettingsStore
- MainAppViewModel
- FakeEmbeddingModel
- ClipsViewModel
- AppPaths
- start_sidecar.sh script
- .Clips_SelectingAClipListsItsSpeakersAndTranscript
- EmbeddingModelCatalog
- ModelsViewModel
- WebRtcVad
- SetupWindow
- AppearanceItem
- ReanalysisService
- IReadOnlyList
- .EnrollProfileAsync
- WaveformTimelineControl
- mock_speaker_pool
- RelayCommand
- LabelGroup
- .MissingModels
- FakeSidecar
- ClipItem
- ClipPlayer
- SwappableEmbeddingModel
- SpeakerItem
- .GetUtterancesAsync
- SPEC: Moderation Review (Incidents, Clips, Speakers)
- IncidentSortColumn
- EnvelopeAccumulator
- SidecarModelStatus
- ModerationStoreTests
- .ExtractWindows
- .OpenMainWindowAsync
- SidecarAnalysisRequest
- ReviewView
- ModelManager
- .LoadAndRunSample
- .Verify
- VoiceProfileSummary
- INotifyPropertyChanged
- ScanMetadata
- pipeline_logic.py
- ISpeakerEmbeddingModel
- .MergeAdjacentHits
- WordListEntry
- EmbeddingModelItem
- moderation_from_scores
- SPEC: Swappable Models and Clip Re-analysis
- HitSegmentResult
- AnalysisRunItem
- .RefreshAsync
- AppServiceBootstrap.cs
- .Clock
- .LoadSelectedClipAsync
- Task
- init_pipeline
- .DrawLabel
- CancelRegistry
- .BuildAvaloniaApp
- .SplitPanes
- SPEC: Scan Speed and Analysis Reliability
- VerdictConverters
- merge_speech_chunks
- SegmentViewModel.cs
- .ProfilesNeedingReenrollment

## God Nodes (most connected - your core abstractions)
1. `ModerationStore` - 77 edges
2. `IncidentsViewModel` - 68 edges
3. `ResultsViewModel` - 67 edges
4. `VoiceScan.Core` - 61 edges
5. `ClipsViewModel` - 59 edges
6. `SpeakersViewModel` - 58 edges
7. `ModelsViewModel` - 57 edges
8. `ReviewViewModel` - 53 edges
9. `OnnxEmbeddingModel` - 53 edges
10. `EnrollmentWizardViewModel` - 50 edges

## Surprising Connections (you probably didn't know these)
- `FakeAudioOutput` --references--> `Path`  [EXTRACTED]
  engine/VoiceScan.Tests/AppLayerTests.cs → app/VoiceScan.App.Core/Models/MediaFileItem.cs
- `run()` --calls--> `Path`  [EXTRACTED]
  eval/real_speech_eval.py → app/VoiceScan.App.Core/Models/MediaFileItem.cs
- `get_repo_root()` --calls--> `Path`  [EXTRACTED]
  eval/synthetic/generator.py → app/VoiceScan.App.Core/Models/MediaFileItem.cs
- `validate_test_directory()` --calls--> `Path`  [EXTRACTED]
  eval/synthetic/generator.py → app/VoiceScan.App.Core/Models/MediaFileItem.cs
- `UtteranceItem` --references--> `UtteranceRecord`  [EXTRACTED]
  app/VoiceScan.App.Core/Models/ModerationModels.cs → engine/VoiceScan.Core/Storage/ModerationStore.cs

## Import Cycles
- None detected.

## Communities (163 total, 16 thin omitted)

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
Cohesion: 0.27
Nodes (3): Fact, Task, CoreEngineTests

### Community 7 - "ReviewViewModel"
Cohesion: 0.07
Nodes (31): CancellationToken, ICommand, IEnumerable, IReadOnlyList, List, ObservableCollection, Task, ReviewViewModel (+23 more)

### Community 8 - "Model & Dataset Licensing Registry"
Cohesion: 0.40
Nodes (4): 1. Verified Model & Tool Inventory, 2. Audio Processing Architecture, Model & Dataset Licensing Registry, Primary Pipeline

### Community 9 - "vad"
Cohesion: 0.20
Nodes (9): manifest_version, models, updated_at, vad, architecture, engine, license_type, notes (+1 more)

### Community 14 - "synthetic/__init__.py"
Cohesion: 0.22
Nodes (16): eval.synthetic module — VoiceScan synthetic data generation and evaluation…, align_or_tile_noise(), compute_active_power(), detect_speech_activity(), mix_at_snr(), ndarray, snr.py — Speech-active region SNR computation and signal mixing. Defines…, Compute sample-level boolean mask of speech activity using frame RMS energy. (+8 more)

### Community 15 - "VoiceScanDatabase"
Cohesion: 0.06
Nodes (39): CancellationToken, Func, IAsyncEnumerable, IReadOnlyList, Task, OrderedPrefetch, FastFileHasher, IReadOnlyList (+31 more)

### Community 16 - "evaluator.py"
Cohesion: 0.05
Nodes (69): Path, categorize_snr(), check_regression(), compare_runs(), EvaluationCoordinator, get_git_commit_hash(), Any, evaluator.py — Central coordinator for running evaluations, logging, and… (+61 more)

### Community 17 - "WindowItem"
Cohesion: 0.11
Nodes (19): ClusterNode, IReadOnlyList, List, ClusterNode, Count, SumVector, Windows, SpeakerCluster (+11 more)

### Community 18 - "VoiceScan Baseline Experiment & Feasibility Report"
Cohesion: 0.10
Nodes (19): 1. Executive Summary & Pipeline Architecture, 2. Reproducible Dev-Set Benchmark Numbers, 3.1 Breakdown by Signal-to-Noise Ratio (SNR), 3.2 Breakdown by Degradation Chain, 3. Stratified Failure Analysis: Where It Works & Where It Fails, 4. Analysis of False Alarms, 5. Honest Viability Assessment, 6. Ranked List of the Three Most Promising Improvements (+11 more)

### Community 19 - "real_speech_eval.py"
Cohesion: 0.33
Nodes (10): build(), fetch(), main(), mix(), opus(), overlap(), ndarray, real_speech_eval.py — end-to-end engine evaluation on real read speech… (+2 more)

### Community 20 - "inference_server.py"
Cohesion: 0.12
Nodes (27): BaseModel, Detoxify, get, post, put, analyse(), cancel(), CancelRequest (+19 more)

### Community 21 - "VoiceScan.Core"
Cohesion: 0.17
Nodes (4): VoiceScan.Tests, VoiceScan.Core.Storage, VoiceScan.Core.Inference, VoiceScan.Core

### Community 22 - "IDisposable"
Cohesion: 0.10
Nodes (15): CancellationToken, Fact, IReadOnlyList, ReadOnlySpan, Task, BulkFileTests, FakeAnalyzer, FakeScanController (+7 more)

### Community 23 - "OnnxEmbeddingModel"
Cohesion: 0.12
Nodes (17): EmbeddingModelEntry, IsEvaluated, InferenceSession, IReadOnlyList, Path, OnnxEmbeddingModel, ActiveProvider, EmbeddingDimension (+9 more)

### Community 24 - "VoiceProfile"
Cohesion: 0.15
Nodes (11): List, VoiceProfile, Centroid, ClipCount, CreatedAt, EnrollmentEmbeddings, ModelId, ModelVersion (+3 more)

### Community 25 - "ReviewDecisionRecord"
Cohesion: 0.17
Nodes (15): ReviewDecision, Confirmed, Rejected, Unreviewed, ReviewDecisionRecord, CancellationToken, IReadOnlyList, List (+7 more)

### Community 26 - "DetectedSegment"
Cohesion: 0.06
Nodes (34): Dictionary, IReadOnlyList, List, DetectedSegment, Embedding, EndTimeSeconds, IsOffensive, ModerationScores (+26 more)

### Community 27 - "VoiceScan Accuracy & Evolution Log"
Cohesion: 0.14
Nodes (13): 1. Executive Summary & Progression, 2. Step-by-Step Analysis & Empirical Deltas, 3. Best-Performing Engine Configuration, 4. Verification Commands, ECAPA / TitaNet operating points (2026-10-04) — supersedes the WeSpeaker numbers below, Headline Progression Table, Real-speech re-evaluation (2026-10-03, WeSpeaker / CAM++, models since replaced) — supersedes the synthetic dev-set numbers below, Step 1: Within-File Speaker Clustering (Agglomerative Hierarchical Clustering) (+5 more)

### Community 28 - "Nemo"
Cohesion: 0.14
Nodes (9): FeatureMatrix, Fft, Size, Nemo, RealSpectrum, SpeechBrain, SpeechFeatures, Fft (+1 more)

### Community 29 - "AppNavigationPage"
Cohesion: 0.18
Nodes (10): AppNavigationPage, Clips, Enrollment, Incidents, Models, Results, Review, Scan (+2 more)

### Community 30 - "InstallActions"
Cohesion: 0.06
Nodes (27): Progress, Action, Exception, ManualResetEventSlim, Window, CrashHandler, LogPath, CheckBox (+19 more)

### Community 31 - "GpuUtilizationSampler"
Cohesion: 0.14
Nodes (10): CancellationToken, CancellationTokenSource, Task, TimeSpan, GpuUtilizationSampler, Latest, Fact, Task (+2 more)

### Community 32 - "speech_and_noise"
Cohesion: 0.50
Nodes (4): fixture, ndarray, Create speech signal with silence gaps and background noise., speech_and_noise()

### Community 33 - "SpooledAudio"
Cohesion: 0.14
Nodes (10): CancellationToken, List, Task, ClipAnalyzer, EmbeddingModel, ConcurrentDictionary, Max, Min (+2 more)

### Community 34 - "MediaFileItem"
Cohesion: 0.13
Nodes (15): MediaFileItem, FileName, IsLoadingWaveform, IsWaveformVisible, Waveform, WaveformError, IReadOnlyList, WaveformEnvelope (+7 more)

### Community 35 - "Task"
Cohesion: 0.27
Nodes (3): Func, List, Task

### Community 36 - "IAudioOutputSession"
Cohesion: 0.06
Nodes (28): IReadOnlyList, Process, FfplayAudioOutput, IsAvailable, FfplaySession, HasExited, IAudioOutput, IsAvailable (+20 more)

### Community 37 - ".StreamDecodeAsync"
Cohesion: 0.14
Nodes (14): CancellationToken, ConcurrentDictionary, IAsyncEnumerable, IReadOnlyList, Process, Task, AudioDecoder, AudioTrackInfo (+6 more)

### Community 38 - "ModerationStore"
Cohesion: 0.32
Nodes (5): CancellationToken, SemaphoreSlim, Task, ModerationStore, SpeakerSummary

### Community 39 - "AudioPlaybackController"
Cohesion: 0.17
Nodes (9): Stopwatch, AudioPlaybackController, CurrentFilePath, CurrentPositionSeconds, IsAudioAvailable, IsPlaying, TotalDurationSeconds, ElapsedEventArgs (+1 more)

### Community 40 - "ResultsViewModel"
Cohesion: 0.05
Nodes (39): ICommand, IReadOnlyList, List, ObservableCollection, ResultsViewModel, AvailableSpeakers, ExportSettings, ExportStatus (+31 more)

### Community 41 - "ScanDashboardViewModel"
Cohesion: 0.06
Nodes (27): IBackgroundScanController, CurrentProgress, CurrentState, ModelId, OperatingPoint, ObservableCollection, ScanDashboardViewModel, CanCancelScan (+19 more)

### Community 42 - "EnrollmentWizardViewModel"
Cohesion: 0.08
Nodes (24): Action, ObservableCollection, PropertyChangedEventArgs, EnrollmentWizardViewModel, AcceptedSampleCount, CanCreateProfile, CanDeleteProfile, CanProceedFromAudioSelection (+16 more)

### Community 43 - "2. Screen Walkthrough & UI Reference"
Cohesion: 0.13
Nodes (14): 1. Executive Summary & Verification Matrix, 2. Screen Walkthrough & UI Reference, 3. 5-Hour Folder Endurance & UI Thread Responsiveness Benchmark, 4. Manual Verification Checklist, A. Voice Profile Enrollment, B. Scan Execution, Benchmark Results, C. Results & Waveform Timeline (+6 more)

### Community 44 - "SyntheticDataGenerator"
Cohesion: 0.15
Nodes (13): get_repo_root(), Locate the git repository root from current file., Validate that test split directory is strictly outside repository root., Discover audio clips per speaker from directory structure:…, Discover game audio noise clips., Deterministically partition speakers into dev and test sets with zero leakage., SyntheticDataGenerator, validate_test_directory() (+5 more)

### Community 45 - "AppLayerTests"
Cohesion: 0.10
Nodes (15): ResultSortColumn, Duration, FileName, HitCount, MaxConfidence, Verdict, Fact, List (+7 more)

### Community 46 - "VoiceScan.App.Core.Models"
Cohesion: 0.18
Nodes (5): VoiceScan.App.Core.ViewModels, VoiceScan.Core.Logging, VoiceScan.App.Core.Models, VoiceScan.Cli, VoiceScan.App.Core.Services

### Community 47 - "RegressionTests"
Cohesion: 0.13
Nodes (10): ArgumentException, Fact, InlineData, InvalidDataException, InvalidOperationException, List, Task, Theory (+2 more)

### Community 48 - "AudioQualityReport"
Cohesion: 0.27
Nodes (6): IReadOnlyList, AudioQualityReport, CancellationToken, ReadOnlySpan, Task, IAudioQualityAnalyzer

### Community 49 - "generator.py"
Cohesion: 0.22
Nodes (10): main(), generate_dataset.py — CLI for synthetic audio dataset generation., DatasetConfig, Any, dataset_config.py — Configuration models and validation for synthetic data…, print_summary_table(), generator.py — Synthetic test dataset generator for VoiceScan. Enforces: 1.…, Generate and print formatted summary table of generated synthetic clips. (+2 more)

### Community 50 - "apply_degradation_chain"
Cohesion: 0.26
Nodes (12): apply_agc(), apply_bandlimit(), apply_degradation_chain(), apply_light_noise_suppression(), apply_opus_degradation(), ndarray, degradations.py — Degradation chains simulating gameplay voice chat…, Automatic Gain Control (AGC) dynamic compression simulation. (+4 more)

### Community 51 - "IncidentsViewModel"
Cohesion: 0.05
Nodes (46): Func, ICommand, IEnumerable, IReadOnlyList, List, ObservableCollection, Task, IncidentsViewModel (+38 more)

### Community 52 - "CohortDocument"
Cohesion: 0.18
Nodes (7): List, CohortDocument, CohortSize, CreatedAt, Embeddings, ModelId, SchemaVersion

### Community 54 - "EnrollmentSampleItem"
Cohesion: 0.19
Nodes (9): EnrollmentSampleItem, AnalysisError, IsAccepted, IsAnalyzing, Report, CancellationToken, IEnumerable, IReadOnlyList (+1 more)

### Community 55 - "ScanDashboardView"
Cohesion: 0.07
Nodes (18): Control, DragEventArgs, IReadOnlyList, Task, FilePickers, DragEventArgs, RoutedEventArgs, EnrollmentWizardView (+10 more)

### Community 56 - "Core"
Cohesion: 0.15
Nodes (5): Core, List, Core, ProbabilityStream, Span

### Community 57 - "ScanSpeedTests"
Cohesion: 0.18
Nodes (10): TestAudio, CancellationToken, Fact, HttpRequestMessage, HttpResponseMessage, Task, GatedSidecar, Request (+2 more)

### Community 58 - ".Collect"
Cohesion: 0.22
Nodes (5): IEnumerable, IReadOnlyList, MediaFileCollector, IEnumerable, StringComparer

### Community 59 - "SidecarInferenceTests"
Cohesion: 0.27
Nodes (9): ArgumentException, Fact, HttpRequestException, HttpRequestMessage, HttpResponseMessage, Task, MockHttpMessageHandler, SidecarInferenceTests (+1 more)

### Community 60 - "AppTheme"
Cohesion: 0.25
Nodes (5): IReadOnlyList, AppTheme, All, AppThemeVariants, ThemeVariant

### Community 61 - "Program"
Cohesion: 0.31
Nodes (6): Dictionary, List, Task, Program, UsageException, Exception

### Community 62 - "test_pipeline_logic.py"
Cohesion: 0.16
Nodes (15): diarize(), Speaker turns (absolute seconds) from overlapping windows; empty when no…, activity_from_probs(), DiarizationWindow, _match_speakers(), ndarray, Speaker activity per frame. When no class passes the threshold anywhere…, Maps cur's local speakers to prev's global ids by frames where both are active… (+7 more)

### Community 63 - "VoiceScan Base Version Documentation"
Cohesion: 0.13
Nodes (14): 1. System Requirements & Prerequisites, 2.1 Build the Solution, 2.2 Run Unit & Integration Tests, 2.3 Run the CLI Engine, 2.4 Run the Desktop Application (Avalonia), 2. How to Build & Run, 3. Known Limitations & Edge Cases, 4. Model & Component Licenses Summary (+6 more)

### Community 68 - ".ScoreAndAggregate"
Cohesion: 0.20
Nodes (8): List, AcousticDiagnostics, IReadOnlyList, ScoreNormalizer, CohortSize, Mean, Segments, StdDev

### Community 69 - ".generate_split"
Cohesion: 0.33
Nodes (4): Any, ndarray, Load audio file converted to 16 kHz mono float32., Generate a complete synthetic split (dev or test) with enrollment and ground…

### Community 70 - "HitSegmentViewModel"
Cohesion: 0.08
Nodes (23): IReadOnlyList, HitSegmentViewModel, Confidence, Decision, DisplaySpeakerLabel, DurationSeconds, End, EndTimeSeconds (+15 more)

### Community 71 - "VoiceScan.App.Services"
Cohesion: 0.18
Nodes (8): ClipsView, IncidentsView, RoutedEventArgs, ModelsView, SpeakersView, VoiceScan.App.Services, VoiceScan.App.Views, UserControl

### Community 72 - "SpeakersViewModel"
Cohesion: 0.06
Nodes (34): SpeakerChoice, Func, ICommand, List, ObservableCollection, Task, SpeakersViewModel, Appearances (+26 more)

### Community 73 - "VoiceScan"
Cohesion: 0.29
Nodes (6): Building from source, GPU, Install, Use, VoiceScan, Where things are stored

### Community 74 - ".RunAsync"
Cohesion: 0.33
Nodes (5): CancellationToken, IReadOnlyList, Task, PipelineScanOptions, CancellationToken

### Community 77 - ".ScanFileAsync"
Cohesion: 0.16
Nodes (14): Action, CancellationToken, Exception, Func, IReadOnlyDictionary, List, Task, PipelineScanner (+6 more)

### Community 78 - "Any"
Cohesion: 0.14
Nodes (19): align_and_assign(), align_words(), _clamp_to_line(), configure_vad(), decode_audio(), detect_speech(), load_alignment(), load_asr() (+11 more)

### Community 79 - "BackgroundScanController"
Cohesion: 0.11
Nodes (17): TimeSpan, OverallScanProgress, ScanExecutionState, Cancelled, Completed, Failed, Idle, Paused (+9 more)

### Community 80 - "SidecarScanResponse"
Cohesion: 0.11
Nodes (16): DetectedSegment, SidecarScanResponse, HasSpeech, Models, Segments, CancellationToken, Func, IReadOnlyList (+8 more)

### Community 81 - "MainWindow"
Cohesion: 0.20
Nodes (4): Button, Dictionary, MainWindow, Window

### Community 82 - "SidecarManager"
Cohesion: 0.19
Nodes (9): CancellationToken, Process, SemaphoreSlim, Task, TimeSpan, SidecarManager, Client, IAsyncDisposable (+1 more)

### Community 83 - "VerdictFilter"
Cohesion: 0.33
Nodes (6): VerdictFilter, All, Error, Match, NoMatch, Possible

### Community 84 - "VoiceScanLogger"
Cohesion: 0.19
Nodes (9): Exception, LogLevel, Debug, Error, Fatal, Info, Warn, VoiceScanLogger (+1 more)

### Community 86 - "FileVerdictResult"
Cohesion: 0.12
Nodes (13): FileVerdictResult, IsError, CancellationToken, DateTimeOffset, IReadOnlyList, Task, EvidenceReportExporter, ExportResult (+5 more)

### Community 87 - "EnrollmentStep"
Cohesion: 0.29
Nodes (6): EnrollmentStep, AudioSelection, Complete, ConsentVerification, ProfileCreation, QualityDiagnostics

### Community 88 - "synthetic_speech_and_game_dirs"
Cohesion: 0.67
Nodes (3): fixture, Create miniature clean speech clips and game audio clips., synthetic_speech_and_game_dirs()

### Community 89 - "SidecarClient"
Cohesion: 0.14
Nodes (14): CancellationToken, CancellationTokenSource, HttpResponseMessage, IReadOnlyDictionary, JsonSerializerOptions, List, Task, TimeSpan (+6 more)

### Community 90 - "ReviewQueueItem"
Cohesion: 0.17
Nodes (12): DateTimeOffset, IReadOnlyList, ReviewQueueItem, DisplaySpeakerLabel, End, IsFlagged, IsOffensive, ModerationViolations (+4 more)

### Community 91 - ".Command"
Cohesion: 0.11
Nodes (25): Appearances, ClipAppearance, ClipLine, ClipRow, Status, DetectedSegment, Dictionary, FileScanResult (+17 more)

### Community 92 - "RecordingHandler"
Cohesion: 0.15
Nodes (13): Body, List, Path, RecordingHandler, Requests, Body, List, Path (+5 more)

### Community 93 - "UtteranceItem"
Cohesion: 0.09
Nodes (20): UtteranceItem, Categories, CategoriesText, ClipName, Id, IsIncident, Note, RangeText (+12 more)

### Community 94 - "Third-Party Notices"
Cohesion: 0.50
Nodes (3): Feature front-ends, Third-Party Notices, WebRTC voice activity detector

### Community 95 - "UserSettingsStore"
Cohesion: 0.17
Nodes (12): JsonSerializerOptions, UserSettings, ClusterThreshold, EmbeddingModelId, LastBrowseFolder, LastProfilePath, ModerationSensitivity, ThemeId (+4 more)

### Community 96 - "MainAppViewModel"
Cohesion: 0.10
Nodes (17): IReadOnlyList, MainAppViewModel, Clips, CurrentPage, Enrollment, GpuStatusMessage, Incidents, IsDarkTheme (+9 more)

### Community 97 - "FakeEmbeddingModel"
Cohesion: 0.12
Nodes (17): IReadOnlyList, FakeEmbeddingModel, ActiveProvider, Disposed, EmbeddingDimension, IsCudaActive, ModelId, ModelVersion (+9 more)

### Community 98 - "ClipsViewModel"
Cohesion: 0.06
Nodes (31): ICommand, IReadOnlyList, List, ObservableCollection, ClipsViewModel, CancelReanalysisCommand, CheckLineCommand, Clips (+23 more)

### Community 99 - "AppPaths"
Cohesion: 0.15
Nodes (9): AppPaths, DatabasePath, DataRoot, LogFilePath, ModelsDirectory, ProfilesDirectory, SpoolDirectory, TestModelFixture (+1 more)

### Community 102 - ".Clips_SelectingAClipListsItsSpeakersAndTranscript"
Cohesion: 0.27
Nodes (12): ModerationSettings, Sensitivity, Fact, List, Start, Task, ModerationViewModelTests, RecordingOutput (+4 more)

### Community 103 - "EmbeddingModelCatalog"
Cohesion: 0.11
Nodes (16): IReadOnlyList, FrontEndChoice, All, ApprovedModel, DateTimeOffset, Func, IReadOnlyList, JsonSerializerOptions (+8 more)

### Community 104 - "ModelsViewModel"
Cohesion: 0.07
Nodes (29): ICommand, IReadOnlyList, ObservableCollection, ModelsViewModel, ActiveModelText, ApplySidecarModelsCommand, CancelReanalysisCommand, EmbeddingModels (+21 more)

### Community 105 - "WebRtcVad"
Cohesion: 0.33
Nodes (6): AudioQualityAnalyzer, ProfileEnrollmentService, EmbeddingModel, WebRtcVad, Mode, SettingsFingerprint

### Community 106 - "SetupWindow"
Cohesion: 0.35
Nodes (3): IClassicDesktopStyleApplicationLifetime, RoutedEventArgs, SetupWindow

### Community 107 - "AppearanceItem"
Cohesion: 0.14
Nodes (14): AppearanceItem, Appearance, ClipName, HasIncidents, Id, IncidentCount, LocalLabel, MatchText (+6 more)

### Community 108 - "ReanalysisService"
Cohesion: 0.14
Nodes (14): Action, CancellationToken, CancellationTokenSource, IEnumerable, SynchronizationContext, Task, ReanalysisService, Completed (+6 more)

### Community 109 - "IReadOnlyList"
Cohesion: 0.12
Nodes (20): AnalysisPreset, Detailed, Standard, DateTimeOffset, IReadOnlyDictionary, IReadOnlyList, AnalysisRun, ClipAnalysisStatus (+12 more)

### Community 110 - ".EnrollProfileAsync"
Cohesion: 0.25
Nodes (4): AudioAugmenter, IReadOnlyList, CancellationToken, Task

### Community 111 - "WaveformTimelineControl"
Cohesion: 0.18
Nodes (8): IReadOnlyList, WaveformTimelineControl, Envelope, Control, VoiceScan.App.Controls, PointerEventArgs, PointerPressedEventArgs, StyledProperty

### Community 112 - "mock_speaker_pool"
Cohesion: 0.67
Nodes (3): mock_speaker_pool(), fixture, Create temporary directory structure for 10 speakers with 6 clips each.

### Community 113 - "RelayCommand"
Cohesion: 0.22
Nodes (4): Action, Func, RelayCommand, ICommand

### Community 114 - "LabelGroup"
Cohesion: 0.20
Nodes (11): End, Start, LabelGroup, Intervals, Label, NormalizedVector, Segments, TalkTime (+3 more)

### Community 115 - ".MissingModels"
Cohesion: 0.29
Nodes (3): IReadOnlyList, SetupCheck, FfmpegInstallHint

### Community 116 - "FakeSidecar"
Cohesion: 0.17
Nodes (14): CancellationToken, Dictionary, Func, HttpRequestMessage, HttpResponseMessage, IReadOnlyDictionary, Task, FakeSidecar (+6 more)

### Community 117 - "ClipItem"
Cohesion: 0.15
Nodes (13): ClipItem, DurationText, FileName, FilePath, HasIncidents, Id, IncidentCount, IsAnalysed (+5 more)

### Community 119 - "ClipPlayer"
Cohesion: 0.08
Nodes (12): IAudioPlaybackController, CurrentFilePath, CurrentPositionSeconds, IsAudioAvailable, IsPlaying, TotalDurationSeconds, Action, SynchronizationContext (+4 more)

### Community 120 - "SwappableEmbeddingModel"
Cohesion: 0.14
Nodes (11): IDisposable, IReadOnlyList, SwappableEmbeddingModel, ActiveProvider, Current, EmbeddingDimension, IsCudaActive, ModelId (+3 more)

### Community 121 - "SpeakerItem"
Cohesion: 0.20
Nodes (10): SpeakerItem, ClipCount, DisplayName, HasIncidents, Id, IncidentCount, LastSeenText, ProfileText (+2 more)

### Community 122 - ".GetUtterancesAsync"
Cohesion: 0.40
Nodes (6): AnalysisRunInfo, IsSection, Fact, InvalidOperationException, Task, ReanalysisStoreTests

### Community 123 - "SPEC: Moderation Review (Incidents, Clips, Speakers)"
Cohesion: 0.25
Nodes (7): Acceptance criteria, Data flow, Offence detection, Out of scope (follow-ups), Screens, Speaker linking, SPEC: Moderation Review (Incidents, Clips, Speakers)

### Community 124 - "IncidentSortColumn"
Cohesion: 0.29
Nodes (7): IncidentSortColumn, Category, Clip, Score, Speaker, Status, Time

### Community 125 - "EnvelopeAccumulator"
Cohesion: 0.24
Nodes (6): List, Max, Min, ReadOnlySpan, EnvelopeAccumulator, SampleCount

### Community 126 - "SidecarModelStatus"
Cohesion: 0.11
Nodes (17): Dictionary, IReadOnlyList, SidecarHealthResponse, CudaAvailable, DeviceName, IsLoading, IsReady, VramFreeGb (+9 more)

### Community 127 - "ModerationStoreTests"
Cohesion: 0.43
Nodes (5): ArgumentException, Dictionary, Fact, Task, ModerationStoreTests

### Community 128 - ".ExtractWindows"
Cohesion: 0.16
Nodes (10): IReadOnlyList, SpeechAudioWindow, SpeechWindowExtractor, WindowPlan, IReadOnlyList, SpeechInterval, Fact, StreamingTests (+2 more)

### Community 129 - ".OpenMainWindowAsync"
Cohesion: 0.25
Nodes (6): IClassicDesktopStyleApplicationLifetime, Task, Window, App, Application, ProgressBar

### Community 130 - "SidecarAnalysisRequest"
Cohesion: 0.25
Nodes (10): ReanalysisJob, SidecarAnalysisRequest, IsSection, Standard, Fact, IReadOnlyDictionary, Task, ReanalysisViewModelTests (+2 more)

### Community 131 - "ReviewView"
Cohesion: 0.31
Nodes (4): RoutedEventArgs, SelectionChangedEventArgs, ReviewView, ViewModel

### Community 132 - "ModelManager"
Cohesion: 0.22
Nodes (8): CancellationToken, IReadOnlyDictionary, Task, ModelManager, Catalog, EmbeddingModel, Sidecar, SidecarModels

### Community 133 - ".LoadAndRunSample"
Cohesion: 0.28
Nodes (5): InferenceSession, GpuModelSample, ModelVerificationResult, Fact, GpuModelSampleTests

### Community 134 - ".Verify"
Cohesion: 0.25
Nodes (5): ModelIntegrity, Fact, InvalidDataException, Task, SecurityHardeningTests

### Community 135 - "VoiceProfileSummary"
Cohesion: 0.25
Nodes (7): DateTimeOffset, AudioQualityTier, Acceptable, Excellent, Rejected, Warning, VoiceProfileSummary

### Community 136 - "INotifyPropertyChanged"
Cohesion: 0.18
Nodes (8): SidecarStageItem, HasError, Hint, Label, Stage, StateText, Value, INotifyPropertyChanged

### Community 137 - "ScanMetadata"
Cohesion: 0.18
Nodes (11): ScanMetadata, ClusteringEnabled, ClusterThreshold, ElapsedSeconds, EngineVersion, ModelId, ModelVersion, ProfileName (+3 more)

### Community 138 - "pipeline_logic.py"
Cohesion: 0.23
Nodes (7): Model-free analysis logic for the sidecar (numpy only), kept apart from…, The speaker whose turns overlap [start, end] most; the nearest turn's speaker…, Splits a line's aligned words (absolute "start"/"end"; words without timings…, speaker_for_span(), split_line_by_speaker(), SpeakerAssignmentTests, Turn

### Community 139 - "ISpeakerEmbeddingModel"
Cohesion: 0.13
Nodes (13): Func, StartupModel, AppServiceBootstrap, SettingsPath, ISpeakerEmbeddingModel, ActiveProvider, EmbeddingDimension, IsCudaActive (+5 more)

### Community 140 - ".MergeAdjacentHits"
Cohesion: 0.36
Nodes (6): Confidence, End, IReadOnlyList, List, Start, SimilarityScorer

### Community 141 - "WordListEntry"
Cohesion: 0.31
Nodes (5): IReadOnlyList, WordListEntry, WordListMatcher, InlineData, Theory

### Community 142 - "EmbeddingModelItem"
Cohesion: 0.22
Nodes (9): EmbeddingModelItem, DisplayName, Entry, EvaluationText, FilePath, FrontEndText, IsActive, IsImported (+1 more)

### Community 143 - "moderation_from_scores"
Cohesion: 0.20
Nodes (9): moderate(), Detoxify in batches (regex rules alongside); regex only when no classifier is…, asr_language(), moderation_from_scores(), Any, The transcription language when the alignment stage is set to a language code;…, Result for one line from classifier scores; the regex flags a line the…, regex_moderation() (+1 more)

### Community 144 - "SPEC: Swappable Models and Clip Re-analysis"
Cohesion: 0.22
Nodes (8): Acceptance criteria, Models covered, Out of scope, Re-analysis, Sidecar models, SPEC: Swappable Models and Clip Re-analysis, Storing re-analysed results (replace, keep history), Voice-embedding models

### Community 145 - "HitSegmentResult"
Cohesion: 0.25
Nodes (8): HitSegmentResult, DisplaySpeakerLabel, End, IsFlagged, ModerationViolations, Start, Transcript, ViolationsSummary

### Community 146 - "AnalysisRunItem"
Cohesion: 0.25
Nodes (8): AnalysisRunItem, EmbeddingModelName, Failed, ModelsText, ResultText, Run, TitleText, WhenText

### Community 152 - "Task"
Cohesion: 0.38
Nodes (4): CancellationToken, IReadOnlyList, Task, FakeReviewRepo

### Community 153 - "init_pipeline"
Cohesion: 0.22
Nodes (9): FastAPI, init_pipeline(), install_stage(), lifespan(), load_model_config(), load_stage(), Loads `name` for `stage`; on success it replaces the stage's model, on failure…, Loads the configured model for every stage. A stage that fails to load is… (+1 more)

### Community 154 - ".DrawLabel"
Cohesion: 0.47
Nodes (4): DrawingContext, FontWeight, IBrush, Point

### Community 155 - "CancelRegistry"
Cohesion: 0.25
Nodes (4): CancelRegistry, JobCancelled, Exception, Request ids the engine has given up on. Thread-safe: /cancel arrives while…

### Community 156 - ".BuildAvaloniaApp"
Cohesion: 0.40
Nodes (3): STAThread, Program, AppBuilder

### Community 157 - ".SplitPanes"
Cohesion: 0.40
Nodes (3): Control, ResponsiveLayout, Grid

### Community 158 - "SPEC: Scan Speed and Analysis Reliability"
Cohesion: 0.33
Nodes (5): Acceptance criteria, Engine (C#), Out of scope (needs `/eval` first), Sidecar (Python), SPEC: Scan Speed and Analysis Reliability

### Community 159 - "VerdictConverters"
Cohesion: 0.50
Nodes (3): VerdictConverters, Color, IValueConverter

### Community 160 - "merge_speech_chunks"
Cohesion: 0.47
Nodes (3): merge_speech_chunks(), Drops speech shorter than min_duration_on, joins speech separated by at most…, MergeSpeechChunksTests

### Community 161 - "SegmentViewModel.cs"
Cohesion: 0.50
Nodes (3): SegmentFilterOption, AllSegments, FlaggedOnly

## Knowledge Gaps
- **798 isolated node(s):** `All`, `AudioSelection`, `ConsentVerification`, `QualityDiagnostics`, `ProfileCreation` (+793 more)
  These have ≤1 connection - possible missing edges or undocumented components. (Counts symbols only; 1198 node(s) total have ≤1 connection when file, concept and rationale nodes are included.)
- **16 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Path` connect `evaluator.py` to `MediaFileItem`, `SidecarAnalysisRequest`, `SyntheticDataGenerator`, `AppLayerTests`, `real_speech_eval.py`?**
  _High betweenness centrality (0.101) - this node is a cross-community bridge._
- **Why does `ScanDashboardViewModel` connect `ScanDashboardViewModel` to `MainAppViewModel`, `MediaFileItem`, `VoiceProfileSummary`, `INotifyPropertyChanged`, `WebRtcVad`, `.RunAsync`, `VoiceScan.App.Core.Models`, `BackgroundScanController`, `ScanDashboardView`, `FileVerdictResult`, `IDisposable`, `.Collect`, `InstallActions`, `UserSettingsStore`?**
  _High betweenness centrality (0.071) - this node is a cross-community bridge._
- **Why does `SpeakersViewModel` connect `SpeakersViewModel` to `MainAppViewModel`, `.Clips_SelectingAClipListsItsSpeakersAndTranscript`, `ModerationStore`, `INotifyPropertyChanged`, `WebRtcVad`, `AppearanceItem`, `VoiceScan.App.Core.Models`, `RelayCommand`, `ClipPlayer`, `SpeakerItem`, `UtteranceItem`?**
  _High betweenness centrality (0.060) - this node is a cross-community bridge._
- **Are the 2 inferred relationships involving `ModerationStore` (e.g. with `.CreateMainViewModel()` and `.ScanController_RecordsSidecarAnalysisInModerationStore()`) actually correct?**
  _`ModerationStore` has 2 INFERRED edges - model-reasoned connections that need verification._
- **Are the 4 inferred relationships involving `IncidentsViewModel` (e.g. with `.CreateMainViewModel()` and `.Incidents_PlayUsesThePagesOwnPlayerAndOpensTheSpeaker()`) actually correct?**
  _`IncidentsViewModel` has 4 INFERRED edges - model-reasoned connections that need verification._
- **Are the 5 inferred relationships involving `ResultsViewModel` (e.g. with `.CreateMainViewModel()` and `.ResultsViewModel_ExportReport_WritesCsvAndPdf()`) actually correct?**
  _`ResultsViewModel` has 5 INFERRED edges - model-reasoned connections that need verification._
- **Are the 4 inferred relationships involving `ClipsViewModel` (e.g. with `.CreateMainViewModel()` and `.Clips_SelectingAClipListsItsSpeakersAndTranscript()`) actually correct?**
  _`ClipsViewModel` has 4 INFERRED edges - model-reasoned connections that need verification._