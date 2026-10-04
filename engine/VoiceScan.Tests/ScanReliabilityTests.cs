using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;
using VoiceScan.Core;
using VoiceScan.Core.Storage;

namespace VoiceScan.Tests;

public sealed class ScanReliabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"reliab_{Guid.NewGuid():N}");

    public ScanReliabilityTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task OrderedPrefetch_YieldsInInputOrderWithinDepth()
    {
        int running = 0, peak = 0;
        var items = Enumerable.Range(0, 8).ToList();

        var results = new List<int>();
        await foreach (var r in OrderedPrefetch.RunAsync(items, depth: 2, async (i, item, ct) =>
        {
            int now = Interlocked.Increment(ref running);
            int seen;
            while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
            await Task.Delay(item % 2 == 0 ? 40 : 5, ct); // later items finish first
            Interlocked.Decrement(ref running);
            return item;
        }))
        {
            results.Add(r);
        }

        Assert.Equal(items, results);
        Assert.InRange(peak, 2, 2);
    }

    [Fact]
    public async Task OrderedPrefetch_StoppingEarlyCancelsPrefetchedWork()
    {
        var started = new List<int>();
        var cancelled = new TaskCompletionSource();

        await foreach (var _ in OrderedPrefetch.RunAsync([0, 1, 2, 3], depth: 2, async (i, item, ct) =>
        {
            lock (started) started.Add(i);
            if (i == 1)
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            }
            return item;
        }))
        {
            break;
        }

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(3, started);
    }

    [Fact]
    public async Task Database_CacheKeepsDurationAndWaveformAndRescanReplacesResult()
    {
        string dbPath = Path.Combine(_root, "cache.db");
        using var db = new VoiceScanDatabase(dbPath);
        await db.InitializeAsync();

        string key = VoiceScanDatabase.ComputeCacheKey("h", "m", "v", "w");
        var windows = new List<CachedWindow> { new(0.0, 2.0, [0.5f, 0.5f]) };
        float[] min = [-0.5f, -0.25f], max = [0.5f, 0.25f];
        await db.SaveCachedWindowsAsync(key, "h", "m", "v", "w", windows, fileInfo: new CachedFileInfo(12.5, min, max));

        var hit = await db.GetCachedScanAsync(key);
        Assert.NotNull(hit);
        Assert.Equal(12.5, hit.Info.DurationSeconds);
        Assert.Equal(min, hit.Info.WaveformMinPeaks);
        Assert.Equal(max, hit.Info.WaveformMaxPeaks);

        await db.SaveScanResultAsync("/a.wav", "h", "p", "m", 0.48, "Match", 0.9, "[]");
        await db.SaveScanResultAsync("/a.wav", "h", "p", "m", 0.48, "Possible", 0.6, "[]");
        await db.SaveScanResultAsync("/a.wav", "h", "p", "m", 0.60, "No match", 0.6, "[]");
        Assert.Equal(2, (await db.GetCacheStatsAsync()).TotalScanResults);
    }

    [Fact]
    public void EmbeddingBatch_MatchesSingleWindowEmbeddings()
    {
        using var model = new OnnxEmbeddingModel("ecapa");
        var rng = new Random(7);
        float[] Noise(int n) => Enumerable.Range(0, n).Select(_ => (float)(rng.NextDouble() - 0.5) * 0.4f).ToArray();
        // The scanner always materializes fixed-length windows, so batches never mix lengths.
        var windows = Enumerable.Range(0, 5).Select(_ => Noise(32000)).ToArray();

        var batch = model.ExtractEmbeddingsBatch(windows);

        for (int i = 0; i < windows.Length; i++)
        {
            var single = model.ExtractEmbedding(windows[i]);
            for (int d = 0; d < single.Length; d++)
            {
                Assert.Equal(single[d], batch[i][d], precision: 3);
            }
        }
    }

    [Fact]
    public async Task Controller_ReportsUnreadableFileAsErrorNotNoMatch()
    {
        string bad = Path.Combine(_root, "broken.wav");
        File.WriteAllText(bad, "this is not audio");

        string profilePath = Path.Combine(_root, "p.json");
        var model = new OnnxEmbeddingModel("ecapa");
        new VoiceProfile { ProfileName = "p", ModelId = model.ModelId, ModelVersion = model.ModelVersion, Centroid = new float[model.EmbeddingDimension] }
            .SaveToFile(profilePath);

        using var controller = new BackgroundScanController(new PipelineScanner(model, new WebRtcVad()));
        var completed = new List<FileVerdictResult>();
        controller.FileCompleted += (_, f) => completed.Add(f);

        var results = await controller.StartScanAsync([bad], profilePath);

        var only = Assert.Single(results);
        Assert.Equal(PipelineScanner.ErrorVerdict, only.OverallVerdict);
        Assert.True(only.IsError);
        Assert.False(string.IsNullOrWhiteSpace(only.ErrorMessage));
        Assert.Equal(1, controller.CurrentProgress.TotalErrors);
        Assert.Equal(0, controller.CurrentProgress.TotalNoMatchFound);
        Assert.Single(completed);
    }

    [Fact]
    public void UserSettingsStore_RoundTripsAndToleratesCorruptFile()
    {
        string path = Path.Combine(_root, "settings.json");

        var store = new UserSettingsStore(path);
        store.Current.LastBrowseFolder = "/media/recordings";
        store.Current.ClusterThreshold = 0.31;
        store.Save();

        var reloaded = new UserSettingsStore(path);
        Assert.Equal("/media/recordings", reloaded.Current.LastBrowseFolder);
        Assert.Equal(0.31, reloaded.Current.ClusterThreshold);

        File.WriteAllText(path, "{ not json");
        Assert.Null(new UserSettingsStore(path).Current.ClusterThreshold);
    }
}
