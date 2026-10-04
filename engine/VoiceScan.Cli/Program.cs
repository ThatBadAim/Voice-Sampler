namespace VoiceScan.Cli;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;
using VoiceScan.Core;
using VoiceScan.Core.Logging;
using VoiceScan.Core.Storage;

public static class Program
{
    /// <summary>Exit code when the scan finished but at least one file could not be scanned.</summary>
    public const int ExitSomeFilesFailed = 3;

    private const int ExitUsage = 2;

    /// <summary>A command-line mistake: reported as an error message and exit code 2, without a stack trace.</summary>
    private sealed class UsageException(string message) : Exception(message);

    public static async Task<int> Main(string[] args)
    {
        AppPaths.UseBundledTools();
        VoiceScanLogger.Initialize(AppPaths.LogFilePath);

        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return 0;
        }

        string command = args[0].ToLowerInvariant();
        string[] cmdArgs = args.Length > 1 ? args[1..] : Array.Empty<string>();

        try
        {
            return command switch
            {
                "enroll" => await HandleEnrollCommandAsync(cmdArgs),
                "scan" => await HandleScanCommandAsync(cmdArgs),
                "profiles" => await HandleProfilesCommandAsync(cmdArgs),
                "cache" => await HandleCacheCommandAsync(cmdArgs),
                "cohort" => await HandleCohortCommandAsync(cmdArgs),
                "report" => await HandleReportCommandAsync(cmdArgs),
                "list-tracks" or "tracks" => await HandleListTracksCommandAsync(cmdArgs),
                "verify-model" => HandleVerifyModelCommand(cmdArgs),
                _ when command.StartsWith("--input", StringComparison.Ordinal) => await HandleScanCommandAsync(args),
                _ => throw new UsageException($"Unknown command '{args[0]}'.")
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"[ERROR] {ex.Message}");
            Console.Error.WriteLine("Run 'VoiceScan.Cli --help' for usage.");
            return ExitUsage;
        }
        catch (Exception ex)
        {
            VoiceScanLogger.Fatal("Cli", $"Command '{command}' failed", ex);
            Console.Error.WriteLine($"[FATAL] Command '{command}' failed: {ex.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        string models = string.Join("|", OnnxEmbeddingModel.SupportedModelNames);
        Console.WriteLine("VoiceScan CLI — Local Offline Voice Enrollment, Media Scanner & Embedding Cache");
        Console.WriteLine("Usage:");
        Console.WriteLine($"  VoiceScan.Cli enroll --audio <files...> --name <profile_name> [--output <profile.json>] [--model <{models}>] [--multi-condition] [--db <path>]");
        Console.WriteLine("  VoiceScan.Cli scan --input <folder_or_file> --profile <profile.json|audio|folder|db_name> --output <results.json>");
        Console.WriteLine("                     [--export <report_dir>] [--cohort <cohort.json>] [--threshold <val>] [--cluster-threshold <val>]");
        Console.WriteLine("                     [--no-clustering] [--no-temporal-smoothing] [--peak-delta <val>] [--smooth-radius <n>]");
        Console.WriteLine("                     [--model <id>] [--track <index>] [--db <path>] [--no-cache] [--multi-condition] [--eval-dataset]");
        Console.WriteLine("      --eval-dataset  evaluation datasets only: map clips to target speakers from ground_truth.json next to the");
        Console.WriteLine("                      input and enroll profiles named on the command line from <input>/../enrollment/<name>.");
        Console.WriteLine($"      Exit code {ExitSomeFilesFailed}: the scan finished but some files could not be scanned (listed on stderr).");
        Console.WriteLine("  VoiceScan.Cli report export --results <results.json> --output <dir> [--profile <name>]");
        Console.WriteLine("  VoiceScan.Cli cohort build --audio <files_or_folders...> --output <cohort.json> [--model <id>]");
        Console.WriteLine("  VoiceScan.Cli profiles list [--db <path>]");
        Console.WriteLine("  VoiceScan.Cli profiles delete --name <profile_name> [--db <path>]");
        Console.WriteLine("  VoiceScan.Cli cache stats [--db <path>]");
        Console.WriteLine("  VoiceScan.Cli cache clear [--model <id>] [--file <hash>] [--db <path>]");
        Console.WriteLine("  VoiceScan.Cli list-tracks --input <media_file>");
        Console.WriteLine("  VoiceScan.Cli verify-model [--model <id>]");
        Console.WriteLine("Numbers use '.' as the decimal separator regardless of system language.");
    }

    private static string RequireValue(string[] args, ref int i)
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new UsageException($"{args[i]} needs a value.");
        }
        return args[++i];
    }

    private static double RequireDouble(string[] args, ref int i)
    {
        string option = args[i];
        string value = RequireValue(args, ref i);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
            ? result
            : throw new UsageException($"{option} expects a number such as 0.5, got '{value}'.");
    }

    private static int RequireInt(string[] args, ref int i)
    {
        string option = args[i];
        string value = RequireValue(args, ref i);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : throw new UsageException($"{option} expects a whole number, got '{value}'.");
    }

    private static List<string> ReadValues(string[] args, ref int i)
    {
        var values = new List<string>();
        while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            values.Add(args[++i]);
        }
        return values;
    }

    private static async Task<int> HandleEnrollCommandAsync(string[] args)
    {
        var audioPaths = new List<string>();
        string profileName = string.Empty;
        string? outputPath = null;
        string modelName = "ecapa";
        string? dbPath = null;
        bool multiCondition = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--audio": audioPaths.AddRange(ReadValues(args, ref i)); break;
                case "--name": profileName = RequireValue(args, ref i); break;
                case "--output": outputPath = RequireValue(args, ref i); break;
                case "--model": modelName = RequireValue(args, ref i); break;
                case "--db": dbPath = RequireValue(args, ref i); break;
                case "--multi-condition": multiCondition = true; break;
                default: throw new UsageException($"Unknown option for enroll: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(profileName)) throw new UsageException("--name <profile_name> is required for enrollment.");
        if (audioPaths.Count == 0) throw new UsageException("--audio <paths...> requires at least one audio file.");

        outputPath ??= $"{profileName}.profile.json";

        Console.WriteLine($"[INFO] Enrolling profile '{profileName}' using model '{modelName}' (multi-condition={multiCondition}) from {audioPaths.Count} clip(s)...");

        using var embeddingModel = new OnnxEmbeddingModel(modelName);
        var enrollmentService = new ProfileEnrollmentService(embeddingModel, new WebRtcVad());
        var profile = await enrollmentService.EnrollProfileAsync(audioPaths, profileName, multiCondition: multiCondition);

        profile.SaveToFile(outputPath);
        Console.WriteLine($"[SUCCESS] Voice profile successfully enrolled and saved to: {Path.GetFullPath(outputPath)}");

        try
        {
            using var db = new VoiceScanDatabase(dbPath);
            await db.InitializeAsync();
            await db.SaveProfileAsync(profile);
            Console.WriteLine($"[INFO] Profile '{profileName}' persisted to database.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.Error.WriteLine($"[WARNING] Could not persist profile to database: {ex.Message}");
        }

        Console.WriteLine($"  - Model: {profile.ModelVersion}");
        Console.WriteLine($"  - Total Speech: {profile.TotalSpeechDurationSeconds:F2}s across {profile.EnrollmentEmbeddings.Count} window(s)");
        Console.WriteLine($"  - Centroid Dimension: {profile.Centroid.Length}");

        return 0;
    }

    private static async Task<int> HandleScanCommandAsync(string[] args)
    {
        string? inputPath = null;
        string? profileArg = null;
        string? outputPath = null;
        string? exportDir = null;
        double? thresholdArg = null;
        double? clusterThresholdArg = null;
        string modelName = "ecapa";
        int trackIndex = 0;
        string? dbPath = null;
        bool noCache = false;
        bool enableClustering = true;
        bool enableTemporalSmoothing = true;
        double peakDelta = 0.04;
        int scoreSmoothingRadius = PipelineScanner.DefaultScoreSmoothingRadius;
        string? cohortPath = null;
        bool multiCondition = false;
        bool evalDataset = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--input": inputPath = RequireValue(args, ref i); break;
                case "--profile": profileArg = RequireValue(args, ref i); break;
                case "--output": outputPath = RequireValue(args, ref i); break;
                case "--export": exportDir = RequireValue(args, ref i); break;
                case "--threshold": thresholdArg = RequireDouble(args, ref i); break;
                case "--cluster-threshold": clusterThresholdArg = RequireDouble(args, ref i); break;
                case "--smooth-radius": scoreSmoothingRadius = RequireInt(args, ref i); break;
                case "--no-clustering": enableClustering = false; break;
                case "--no-temporal-smoothing": enableTemporalSmoothing = false; break;
                case "--peak-delta": peakDelta = RequireDouble(args, ref i); break;
                case "--cohort": cohortPath = RequireValue(args, ref i); break;
                case "--multi-condition": multiCondition = true; break;
                case "--model": modelName = RequireValue(args, ref i); break;
                case "--track": trackIndex = RequireInt(args, ref i); break;
                case "--db": dbPath = RequireValue(args, ref i); break;
                case "--no-cache": noCache = true; break;
                case "--eval-dataset": evalDataset = true; break;
                default: throw new UsageException($"Unknown option for scan: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(inputPath)) throw new UsageException("--input <folder_or_file> is required.");
        if (string.IsNullOrWhiteSpace(profileArg)) throw new UsageException("--profile <profile> is required.");
        if (string.IsNullOrWhiteSpace(outputPath)) throw new UsageException("--output <results.json> is required.");
        if (trackIndex < 0) throw new UsageException("--track must be 0 or greater.");
        if (cohortPath != null && !File.Exists(cohortPath)) throw new UsageException($"Cohort file not found: {cohortPath}");

        using var embeddingModel = new OnnxEmbeddingModel(modelName);
        var vad = new WebRtcVad();

        using var database = noCache ? null : new VoiceScanDatabase(dbPath);
        if (database != null)
        {
            await database.InitializeAsync();
        }

        VoiceProfile profile = await ResolveOrEnrollProfileAsync(profileArg, inputPath, embeddingModel, vad, database, multiCondition, evalDataset);

        // Evaluation datasets name a target speaker per clip; only honoured when asked for, so a stray
        // ground_truth.json near real recordings can never change which voice is searched for.
        var clipProfileMap = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
        if (evalDataset)
        {
            await LoadEvalDatasetProfilesAsync(clipProfileMap, profile, inputPath, embeddingModel, vad, database, multiCondition);
        }

        double threshold = thresholdArg ?? embeddingModel.OperatingPoint.Threshold;
        double clusterThreshold = clusterThresholdArg ?? embeddingModel.OperatingPoint.ClusterDistanceThreshold;
        ScoreNormalizer? normalizer = cohortPath != null ? ScoreNormalizer.FromFile(cohortPath, embeddingModel) : null;

        Console.WriteLine($"[INFO] Pipelined Scan starting:");
        Console.WriteLine($"  - Target Profile: {profile.ProfileName} ({profile.ModelVersion})");
        Console.WriteLine($"  - Input: {inputPath}");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  - Threshold: {threshold:F4}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  - Clustering: {(enableClustering ? $"AHC (threshold={clusterThreshold:F2})" : "Disabled")}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  - Temporal Smoothing: {(enableTemporalSmoothing ? $"Active (peakDelta={peakDelta:F3})" : "Disabled")}"));
        Console.WriteLine($"  - Impostor Cohort (AS-Norm): {(normalizer != null ? $"Active ({normalizer.CohortSize} embeddings from {cohortPath})" : "None")}");
        Console.WriteLine($"  - Cache Active: {!noCache}");
        Console.WriteLine($"  - Output: {outputPath}");

        var scanner = new PipelineScanner(embeddingModel, vad, database);
        var document = await scanner.ScanDirectoryAsync(
            inputPath,
            profile,
            threshold,
            audioTrackIndex: trackIndex,
            clusterDistanceThreshold: clusterThreshold,
            enableClustering: enableClustering,
            enableTemporalSmoothing: enableTemporalSmoothing,
            peakDelta: peakDelta,
            normalizer: normalizer,
            clipProfileMap: clipProfileMap.Count > 0 ? clipProfileMap : null,
            scoreSmoothingRadius: scoreSmoothingRadius);

        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        var outDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        File.WriteAllText(outputPath, json);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[SUCCESS] Scan complete. Processed {document.Files.Count} file(s) in {document.ScanMetadata.ElapsedSeconds:F2}s. Results written to: {outputPath}"));

        var failed = document.Files.Where(f => f.Error is not null).ToList();
        foreach (var f in failed)
        {
            Console.Error.WriteLine($"[ERROR] Could not scan {f.FilePath}: {f.Error}");
        }

        if (!string.IsNullOrEmpty(exportDir))
        {
            await ExportReportAsync(document, profile.ProfileName, exportDir);
        }

        return failed.Count > 0 ? ExitSomeFilesFailed : 0;
    }

    private static async Task<VoiceProfile> ResolveOrEnrollProfileAsync(
        string profileArg,
        string inputPath,
        ISpeakerEmbeddingModel embeddingModel,
        WebRtcVad vad,
        VoiceScanDatabase? database,
        bool multiCondition,
        bool evalDataset)
    {
        var enrollmentService = new ProfileEnrollmentService(embeddingModel, vad);

        async Task<VoiceProfile> EnrollAsync(IReadOnlyList<string> clips, string name)
        {
            var prof = await enrollmentService.EnrollProfileAsync(clips, name, multiCondition: multiCondition);
            if (database != null) await database.SaveProfileAsync(prof);
            return prof;
        }

        // An existing path always means that path; a database name is only used when no such file or folder exists.
        if (File.Exists(profileArg) && profileArg.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return VoiceProfile.LoadFromFile(profileArg);
        }

        if (File.Exists(profileArg))
        {
            return await EnrollAsync([profileArg], Path.GetFileNameWithoutExtension(profileArg));
        }

        if (Directory.Exists(profileArg))
        {
            var clips = MediaFileCollector.Collect([profileArg]);
            if (clips.Count == 0) throw new UsageException($"No supported audio files in profile folder: {profileArg}");
            return await EnrollAsync(clips, Path.GetFileName(Path.TrimEndingDirectorySeparator(profileArg)));
        }

        if (database != null && await database.GetProfileAsync(profileArg) is { } stored)
        {
            return stored;
        }

        if (evalDataset)
        {
            string? inputDir = Directory.Exists(inputPath) ? inputPath : Path.GetDirectoryName(inputPath);
            if (!string.IsNullOrEmpty(inputDir))
            {
                foreach (var cand in new[] { Path.Combine(inputDir, "enrollment", profileArg), Path.Combine(inputDir, "..", "enrollment", profileArg) })
                {
                    var clips = Directory.Exists(cand) ? MediaFileCollector.Collect([cand]) : [];
                    if (clips.Count > 0)
                    {
                        return await EnrollAsync(clips, profileArg);
                    }
                }
            }
        }

        throw new UsageException($"Could not find a voice profile file, audio, folder or stored profile named: {profileArg}");
    }

    private static async Task LoadEvalDatasetProfilesAsync(
        Dictionary<string, VoiceProfile> clipProfileMap,
        VoiceProfile defaultProfile,
        string inputPath,
        ISpeakerEmbeddingModel embeddingModel,
        WebRtcVad vad,
        VoiceScanDatabase? database,
        bool multiCondition)
    {
        string? datasetDir = Directory.Exists(inputPath) ? inputPath : Path.GetDirectoryName(inputPath);
        if (string.IsNullOrEmpty(datasetDir)) return;

        var gtFile = new[] { Path.Combine(datasetDir, "ground_truth.json"), Path.Combine(datasetDir, "..", "ground_truth.json") }
            .FirstOrDefault(File.Exists)
            ?? throw new UsageException($"--eval-dataset was given but no ground_truth.json exists in or above {datasetDir}.");

        var enrolled = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase)
        {
            [defaultProfile.ProfileName] = defaultProfile
        };

        using var gtDoc = JsonDocument.Parse(File.ReadAllText(gtFile));
        foreach (var el in gtDoc.RootElement.EnumerateArray())
        {
            if (el.TryGetProperty("clip_id", out var cId) && el.TryGetProperty("target_speaker_id", out var spkId)
                && cId.GetString() is { Length: > 0 } clipName && spkId.GetString() is { Length: > 0 } targetSpk)
            {
                if (!enrolled.TryGetValue(targetSpk, out var spkProf))
                {
                    spkProf = await ResolveOrEnrollProfileAsync(targetSpk, inputPath, embeddingModel, vad, database, multiCondition, evalDataset: true);
                    enrolled[targetSpk] = spkProf;
                }
                clipProfileMap[clipName] = spkProf;
            }
        }
        Console.WriteLine($"[INFO] Loaded target speaker mappings for {clipProfileMap.Count} clip(s) from {gtFile}");
    }

    private static async Task<int> HandleProfilesCommandAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("Profiles Management:");
            Console.WriteLine("  VoiceScan.Cli profiles list [--db <path>]");
            Console.WriteLine("  VoiceScan.Cli profiles delete --name <name> [--db <path>]");
            return 0;
        }

        string sub = args[0].ToLowerInvariant();
        string? dbPath = null;
        string? name = null;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db": dbPath = RequireValue(args, ref i); break;
                case "--name": name = RequireValue(args, ref i); break;
                default: throw new UsageException($"Unknown option for profiles: {args[i]}");
            }
        }

        using var db = new VoiceScanDatabase(dbPath);
        await db.InitializeAsync();

        if (sub == "list")
        {
            var profiles = await db.ListProfilesAsync();
            Console.WriteLine($"Enrolled Profiles in Database ({profiles.Count} total):");
            if (profiles.Count == 0)
            {
                Console.WriteLine("  (No profiles currently stored)");
            }
            foreach (var p in profiles)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  - '{p.Name}': Model={p.ModelVersion}, Created={p.CreatedAt}, Clips={p.ClipCount}, Speech={p.TotalSpeechDurationSeconds:F2}s"));
            }
            return 0;
        }

        if (sub == "delete")
        {
            if (string.IsNullOrWhiteSpace(name)) throw new UsageException("--name <profile_name> is required to delete.");

            bool deleted = await db.DeleteProfileAsync(name);
            Console.WriteLine(deleted
                ? $"[SUCCESS] Profile '{name}' deleted from database."
                : $"[INFO] Profile '{name}' was not found in database.");
            return 0;
        }

        throw new UsageException($"Unknown profiles subcommand: {sub}");
    }

    private static async Task<int> HandleCacheCommandAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("Embedding Cache Management:");
            Console.WriteLine("  VoiceScan.Cli cache stats [--db <path>]");
            Console.WriteLine("  VoiceScan.Cli cache clear [--model <id>] [--file <hash>] [--db <path>]");
            return 0;
        }

        string sub = args[0].ToLowerInvariant();
        string? dbPath = null;
        string? model = null;
        string? fileHash = null;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db": dbPath = RequireValue(args, ref i); break;
                case "--model": model = RequireValue(args, ref i); break;
                case "--file": fileHash = RequireValue(args, ref i); break;
                default: throw new UsageException($"Unknown option for cache: {args[i]}");
            }
        }

        using var db = new VoiceScanDatabase(dbPath);
        await db.InitializeAsync();

        if (sub == "stats")
        {
            var stats = await db.GetCacheStatsAsync();
            Console.WriteLine("=== VoiceScan SQLite Storage & Cache Stats ===");
            Console.WriteLine($"  Cached Files:       {stats.TotalCachedFiles}");
            Console.WriteLine($"  Cached Windows:     {stats.TotalCachedWindows}");
            Console.WriteLine($"  Enrolled Profiles:  {stats.TotalProfiles}");
            Console.WriteLine($"  Stored Scan Runs:   {stats.TotalScanResults}");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Database File Size: {stats.DatabaseSizeBytes / 1024.0 / 1024.0:F2} MB"));
            return 0;
        }

        if (sub == "clear")
        {
            int deleted = await db.InvalidateCacheAsync(model, fileHash);
            Console.WriteLine($"[SUCCESS] Invalidated {deleted} cache entries from database.");
            return 0;
        }

        throw new UsageException($"Unknown cache subcommand: {sub}");
    }

    private static async Task<int> HandleListTracksCommandAsync(string[] args)
    {
        string? inputPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--input": inputPath = RequireValue(args, ref i); break;
                default: throw new UsageException($"Unknown option for list-tracks: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            throw new UsageException("--input <valid_media_file> is required.");
        }

        var tracks = await AudioDecoder.ProbeAudioTracksAsync(inputPath);
        Console.WriteLine($"Audio tracks found in: {inputPath}");
        foreach (var t in tracks)
        {
            Console.WriteLine($"  Track [{t.Index}]: Codec={t.Codec}, Channels={t.Channels}, SampleRate={t.SampleRate}Hz, Title={t.Title ?? "none"}, Language={t.Language ?? "none"}");
        }
        return 0;
    }

    private static async Task<int> HandleCohortCommandAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("Cohort Management:");
            Console.WriteLine("  VoiceScan.Cli cohort build --audio <paths...> --output <cohort.json> [--model <id>]");
            return 0;
        }

        string sub = args[0].ToLowerInvariant();
        if (sub != "build" && sub != "create")
        {
            throw new UsageException($"Unknown cohort subcommand: {sub}");
        }

        var audioPaths = new List<string>();
        string outputPath = "impostor_cohort.json";
        string modelName = "ecapa";

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--audio": audioPaths.AddRange(ReadValues(args, ref i)); break;
                case "--output": outputPath = RequireValue(args, ref i); break;
                case "--model": modelName = RequireValue(args, ref i); break;
                default: throw new UsageException($"Unknown option for cohort build: {args[i]}");
            }
        }

        if (audioPaths.Count == 0) throw new UsageException("--audio <paths...> requires at least one audio file or directory.");

        var allFiles = MediaFileCollector.Collect(audioPaths);
        if (allFiles.Count == 0) throw new UsageException("No audio files found from specified paths.");

        Console.WriteLine($"[INFO] Building impostor cohort from {allFiles.Count} clip(s) using model '{modelName}'...");

        using var embeddingModel = new OnnxEmbeddingModel(modelName);
        var enrollmentService = new ProfileEnrollmentService(embeddingModel, new WebRtcVad());
        var cohortDoc = new CohortDocument { ModelId = embeddingModel.ModelVersion };

        // Each file is decoded up to the enrollment cap and embedded in batches, so long files stay bounded in memory.
        foreach (var file in allFiles)
        {
            try
            {
                var perFile = await enrollmentService.EnrollProfileAsync([file], "cohort");
                cohortDoc.Embeddings.AddRange(perFile.EnrollmentEmbeddings);
            }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine($"[WARNING] Skipping {file}: {ex.Message}");
            }
        }

        cohortDoc.SaveToFile(outputPath);
        Console.WriteLine($"[SUCCESS] Cohort built with {cohortDoc.CohortSize} impostor embeddings saved to: {Path.GetFullPath(outputPath)}");
        return 0;
    }

    private static int HandleVerifyModelCommand(string[] args)
    {
        string modelName = "ecapa";
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model": modelName = RequireValue(args, ref i); break;
                default: throw new UsageException($"Unknown option for verify-model: {args[i]}");
            }
        }

        using var model = new OnnxEmbeddingModel(modelName);
        var embedding = model.ExtractEmbedding(new float[2 * AudioDecoder.DefaultSampleRate]);
        Console.WriteLine($"[SUCCESS] {model.ModelVersion} loaded on {model.ActiveProvider}; checksum verified; {embedding.Length}-dim embedding produced.");
        return 0;
    }

    private static async Task<int> HandleReportCommandAsync(string[] args)
    {
        if (args.Length == 0 || !args[0].Equals("export", StringComparison.OrdinalIgnoreCase))
        {
            throw new UsageException("Usage: VoiceScan.Cli report export --results <results.json> --output <dir> [--profile <name>]");
        }

        string? resultsPath = null;
        string outputDir = "reports/evidence";
        string? profileName = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--results": resultsPath = RequireValue(args, ref i); break;
                case "--output": outputDir = RequireValue(args, ref i); break;
                case "--profile": profileName = RequireValue(args, ref i); break;
                default: throw new UsageException($"Unknown option for report export: {args[i]}");
            }
        }

        if (string.IsNullOrEmpty(resultsPath) || !File.Exists(resultsPath))
        {
            throw new UsageException("--results <results.json> must name an existing scan results file.");
        }

        var doc = JsonSerializer.Deserialize<ScanOutputDocument>(await File.ReadAllTextAsync(resultsPath))
            ?? throw new UsageException($"Could not parse results JSON from: {resultsPath}");

        await ExportReportAsync(doc, profileName, outputDir);
        return 0;
    }

    /// <summary>Writes the evidence report with the settings recorded in the scan results, never assumed defaults.</summary>
    private static async Task ExportReportAsync(ScanOutputDocument document, string? profileOverride, string outputDir)
    {
        var meta = document.ScanMetadata;
        if (meta.ClusteringEnabled is not { } clustering || meta.ClusterThreshold is not { } clusterThreshold
            || meta.TemporalSmoothing is not { } smoothing)
        {
            throw new UsageException(
                "This results file does not record the clustering and smoothing settings it was scanned with " +
                "(it was written by an older VoiceScan). Re-run the scan to produce an evidence report.");
        }

        string profileName = !string.IsNullOrWhiteSpace(profileOverride) ? profileOverride
            : !string.IsNullOrWhiteSpace(meta.ProfileName) ? meta.ProfileName
            : "Unknown profile";
        var scanDate = DateTimeOffset.TryParse(meta.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

        var exportSettings = new ReportExportSettings(
            ProfileName: profileName,
            ModelId: string.IsNullOrEmpty(meta.ModelVersion) ? meta.ModelId : meta.ModelVersion,
            EngineVersion: meta.EngineVersion,
            Threshold: meta.Threshold,
            ClusterThreshold: clusterThreshold,
            TemporalSmoothing: smoothing,
            ScanDateUtc: scanDate,
            ClusteringEnabled: clustering);

        var fileResults = document.Files.Select(f => new FileVerdictResult(
            FilePath: f.FilePath,
            FileName: Path.GetFileName(f.FilePath),
            FileHash: f.FileHash,
            DurationSeconds: f.DurationSeconds,
            OverallVerdict: f.Verdict,
            MaxConfidence: f.MaxConfidence,
            ErrorMessage: f.Error,
            AudioTrackIndex: f.AudioTrackIndex,
            Segments: f.Segments.Select((s, index) => new HitSegmentResult(
                SegmentId: HitSegmentResult.CreateId(f.FilePath, f.FileHash, index, s.StartTimeSeconds),
                FilePath: f.FilePath,
                StartTimeSeconds: s.StartTimeSeconds,
                EndTimeSeconds: s.EndTimeSeconds,
                DurationSeconds: s.EndTimeSeconds - s.StartTimeSeconds,
                Verdict: s.Verdict,
                Confidence: s.Confidence,
                ReasonFlags: s.ReasonFlags,
                SegmentEmbedding: s.Embedding,
                FileHash: f.FileHash)).ToList())).ToList();

        var exportResult = await new EvidenceReportExporter().ExportReportAsync(fileResults, exportSettings, outputDir);
        Console.WriteLine($"[EXPORT] Evidence report generated in: {outputDir}");
        Console.WriteLine($"  - CSV: {exportResult.CsvPath}");
        Console.WriteLine($"  - PDF: {exportResult.PdfPath}");
        Console.WriteLine($"  - Audio Hits Extracted: {exportResult.ExtractedAudioClipPaths.Count} clip(s)");
    }
}
