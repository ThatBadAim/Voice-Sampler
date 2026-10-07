namespace VoiceScan.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using VoiceScan.App.Core.Services;
using VoiceScan.Core;
using VoiceScan.Core.Inference;
using VoiceScan.Core.Storage;
using Xunit;

/// <summary>Swappable voice-embedding models and sidecar model control (docs/SPEC-model-swap.md).</summary>
public sealed class ModelSwapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vs_modelswap_" + Guid.NewGuid().ToString("N"));

    public ModelSwapTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly EmbeddingModelEntry ModelA = new(
        "model-a", "Model A", "/models/a.onnx", "", FeatureFrontEnd.SpeechBrainFbank, new(0.48, 0.60), IsBuiltIn: true);
    private static readonly EmbeddingModelEntry ModelB = new(
        "model-b", "Model B", "/models/b.onnx", "", FeatureFrontEnd.NemoMelSpectrogram, new(0.52, 0.58), IsBuiltIn: true);
    private static readonly EmbeddingModelEntry Broken = new(
        "broken", "Broken", "/models/broken.onnx", "", FeatureFrontEnd.SpeechBrainFbank, new(0.48, 0.60), IsBuiltIn: true);

    private EmbeddingModelCatalog Catalog() =>
        new(Path.Combine(_dir, "models"), () => [ModelA, ModelB, Broken]);

    private static ISpeakerEmbeddingModel Load(EmbeddingModelEntry entry) =>
        entry.ModelId == Broken.ModelId
            ? throw new InvalidDataException("Model 'broken.onnx' does not match the SHA-256 in models/manifest.json")
            : new FakeEmbeddingModel(entry.ModelId);

    private string FakeOnnx(string name, byte seed)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Enumerable.Range(0, 4096).Select(i => (byte)(i * seed)).ToArray());
        return path;
    }

    [Fact]
    public void Catalog_ImportApprovesACopyThatSurvivesARestartAndCanBeRemoved()
    {
        var catalog = Catalog();
        string source = FakeOnnx("my-voice-v2.onnx", 7);

        var entry = catalog.Import(source, "  My voice v2 ", FeatureFrontEnd.NemoMelSpectrogram);

        Assert.False(entry.IsBuiltIn);
        Assert.False(entry.IsEvaluated);
        Assert.Equal("My voice v2", entry.DisplayName);
        Assert.StartsWith("custom-", entry.ModelId);
        Assert.Equal(ModelIntegrity.HashFile(source), entry.Sha256);
        Assert.Equal(OnnxEmbeddingModel.BuiltInOperatingPoint(FeatureFrontEnd.NemoMelSpectrogram), entry.OperatingPoint);
        Assert.StartsWith(catalog.ImportedDirectory, entry.FilePath);
        Assert.True(File.Exists(entry.FilePath));

        File.Delete(source); // the approved copy is independent of the original
        var reloaded = Catalog().Find(entry.ModelId);
        Assert.NotNull(reloaded);
        Assert.Equal(entry, reloaded);
        Assert.Equal(entry.ModelId, catalog.Import(FakeOnnx("again.onnx", 7), "dup", FeatureFrontEnd.SpeechBrainFbank).ModelId);
        Assert.Equal(4, catalog.List().Count);

        catalog.Remove(entry.ModelId);
        Assert.Null(Catalog().Find(entry.ModelId));
        Assert.False(File.Exists(entry.FilePath));
        Assert.Throws<InvalidOperationException>(() => catalog.Remove(ModelA.ModelId));
    }

    [Fact]
    public void Catalog_RefusesNonOnnxFilesAndAModelChangedAfterApproval()
    {
        var catalog = Catalog();
        string text = Path.Combine(_dir, "weights.bin");
        File.WriteAllText(text, "not a model");
        Assert.Throws<ArgumentException>(() => catalog.Import(text, null, FeatureFrontEnd.SpeechBrainFbank));

        var entry = catalog.Import(FakeOnnx("model.onnx", 3), null, FeatureFrontEnd.SpeechBrainFbank);
        File.AppendAllText(entry.FilePath, "tampered");

        Assert.Throws<InvalidDataException>(() => ModelIntegrity.VerifyHash(entry.FilePath, entry.Sha256));
        // Loading verifies the checksum before creating an ONNX session, so the changed file is never run.
        var ex = Assert.Throws<InvalidDataException>(() => new OnnxEmbeddingModel(entry));
        Assert.Contains("changed since it was approved", ex.Message);
    }

    [Fact]
    public void Swappable_RefusesSwapWhileInUseThenServesTheNewModel()
    {
        var a = new FakeEmbeddingModel("a", vector: [1f, 0f]);
        var b = new FakeEmbeddingModel("b", vector: [0f, 1f]);
        using var model = new SwappableEmbeddingModel(a);
        int swaps = 0;
        model.Swapped += () => swaps++;

        var use = model.BeginUse();
        Assert.Throws<InvalidOperationException>(() => model.Swap(b));
        Assert.Equal("a@v1", model.ModelVersion);

        use.Dispose();
        use.Dispose(); // releasing twice must not unbalance the count
        Assert.Same(a, model.Swap(b));

        Assert.Equal(1, swaps);
        Assert.Equal("b@v1", model.ModelVersion);
        Assert.Equal([0f, 1f], model.ExtractEmbedding(new float[16000]));
        Assert.Same(b, model.Swap(a)); // no use is left behind by the inference call above
    }

    [Fact]
    public async Task ModelManager_SwitchKeepsTheOldModelOnFailureAndSavesTheChoice()
    {
        var initial = new FakeEmbeddingModel(ModelA.ModelId);
        var swappable = new SwappableEmbeddingModel(initial);
        string settingsPath = Path.Combine(_dir, "settings.json");
        var manager = new ModelManager(Catalog(), swappable, Load, settings: new UserSettingsStore(settingsPath));

        await Assert.ThrowsAsync<InvalidDataException>(() => manager.SwitchEmbeddingModelAsync(Broken.ModelId));
        Assert.Same(initial, swappable.Current);
        Assert.Null(new UserSettingsStore(settingsPath).Current.EmbeddingModelId);

        using (swappable.BeginUse())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SwitchEmbeddingModelAsync(ModelB.ModelId));
        }
        Assert.Same(initial, swappable.Current);

        await manager.SwitchEmbeddingModelAsync(ModelB.ModelId);
        Assert.Equal(ModelB.ModelId, swappable.ModelId);
        Assert.True(initial.Disposed);
        Assert.Equal(ModelB.ModelId, new UserSettingsStore(settingsPath).Current.EmbeddingModelId);
        Assert.Throws<InvalidOperationException>(() => manager.RemoveEmbeddingModel(ModelB.ModelId));
    }

    [Fact]
    public void ModelManager_StartupFallsBackToTheDefaultWhenTheChosenModelIsGoneOrBroken()
    {
        var fallback = new FakeEmbeddingModel("default");

        var chosen = ModelManager.LoadStartupModel(Catalog(), ModelB.ModelId, Load, () => fallback);
        Assert.Equal(ModelB.ModelId, chosen.Model.ModelId);
        Assert.Null(chosen.Warning);

        var gone = ModelManager.LoadStartupModel(Catalog(), "custom-deleted", Load, () => fallback);
        Assert.Same(fallback, gone.Model);
        Assert.Contains("no longer available", gone.Warning);

        var broken = ModelManager.LoadStartupModel(Catalog(), Broken.ModelId, Load, () => fallback);
        Assert.Same(fallback, broken.Model);
        Assert.Contains("could not be loaded", broken.Warning);
    }

    [Fact]
    public async Task ModelManager_ImportWithdrawsAModelThatDoesNotLoad()
    {
        var catalog = Catalog();
        var manager = new ModelManager(catalog, new SwappableEmbeddingModel(new FakeEmbeddingModel(ModelA.ModelId)),
            _ => throw new InvalidDataException("input 'feats' has the wrong rank"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.ImportEmbeddingModelAsync(FakeOnnx("wrong-layout.onnx", 5), "Wrong", FeatureFrontEnd.SpeechBrainFbank));

        Assert.Contains("not imported", ex.Message);
        Assert.DoesNotContain(catalog.List(), e => !e.IsBuiltIn);
        Assert.Empty(Directory.GetFiles(catalog.ImportedDirectory));
    }

    [Fact]
    public async Task SidecarClient_ReadsAndSetsModelsAndSendsPresetAndRange()
    {
        var handler = new RecordingHandler("""
            {"models": {"asr": "large-v3", "moderation": "unbiased"}, "loaded": {"asr": true, "moderation": true},
             "errors": {"asr": null, "moderation": null}, "has_speech": true, "segments": []}
            """);
        using var client = new SidecarClient(new HttpClient(handler) { BaseAddress = new Uri(SidecarClient.DefaultBaseUrl) });

        var status = await client.GetModelsAsync();
        Assert.Equal("large-v3", status.Models["asr"]);
        Assert.True(status.Loaded["moderation"]);

        await client.SetModelsAsync(new Dictionary<string, string> { ["asr"] = "large-v3" });
        var response = await client.AnalyseAsync("/rec/a.wav", new SidecarAnalysisRequest(AnalysisPreset.Detailed, 12.5, 30));
        await client.ScanAudioAsync("/rec/b.wav");

        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/models", handler.Requests[0].Path);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Equal("large-v3", JsonDocument.Parse(handler.Requests[1].Body).RootElement.GetProperty("models").GetProperty("asr").GetString());

        var detailed = JsonDocument.Parse(handler.Requests[2].Body).RootElement;
        Assert.Equal("/process", handler.Requests[2].Path);
        Assert.Equal("detailed", detailed.GetProperty("preset").GetString());
        Assert.Equal(12.5, detailed.GetProperty("start_seconds").GetDouble());
        Assert.Equal(30, detailed.GetProperty("end_seconds").GetDouble());
        Assert.Equal("large-v3", response.Models!["asr"]);

        var standard = JsonDocument.Parse(handler.Requests[3].Body).RootElement;
        Assert.Equal("standard", standard.GetProperty("preset").GetString());
        Assert.False(standard.TryGetProperty("start_seconds", out _)); // whole file: no range is sent
    }

    [Fact]
    public async Task ClipAnalyzer_EmbedsTheLinesOfASectionWithoutAVoiceProfile()
    {
        string wav = TestAudio.WriteTone(Path.Combine(_dir, "clip.wav"), 20);
        var sidecar = new FakeSidecar
        {
            Respond = _ => new SidecarScanResponse
            {
                Segments =
                [
                    new DetectedSegment(1, 3, 0.9, speakerLabel: "SPEAKER_00", transcript: "before the section"),
                    new DetectedSegment(11, 13, 0.9, speakerLabel: "SPEAKER_00", transcript: "you are garbage"),
                    new DetectedSegment(15, 15.05, 0.9, speakerLabel: "SPEAKER_01", transcript: "uh"),
                ]
            }
        };
        var model = new FakeEmbeddingModel("model-a", vector: [0f, 0f, 1f, 0f]);

        var result = await new ClipAnalyzer(model, sidecar).AnalyseAsync(wav, new SidecarAnalysisRequest(AnalysisPreset.Detailed, 10, 17));

        var request = Assert.Single(sidecar.Requests);
        Assert.Equal(AnalysisPreset.Detailed, request.Preset);
        Assert.Equal((10.0, 17.0), (request.StartSeconds!.Value, request.EndSeconds!.Value));
        Assert.True(result.AnalyzerUsed);
        Assert.Equal("large-v3-turbo", result.AnalyzerModels!["asr"]);
        Assert.Equal(20.0, result.DurationSeconds, 1);
        Assert.Equal(FastFileHasher.ComputeFastHash(wav), result.FileHash);
        Assert.Equal(["you are garbage", "uh"], result.Segments.Select(s => s.Transcript));
        Assert.Equal([0f, 0f, 1f, 0f], result.Segments[0].Embedding!);
        Assert.Null(result.Segments[1].Embedding); // 50 ms is too short to embed
    }

    [Fact]
    public async Task ClipAnalyzer_FailsVisiblyWhenTheSidecarDoesNotAnswer()
    {
        string wav = TestAudio.WriteTone(Path.Combine(_dir, "clip.wav"), 2);
        var analyzer = new ClipAnalyzer(new FakeEmbeddingModel("model-a"), new FakeSidecar());

        await Assert.ThrowsAsync<HttpRequestException>(() => analyzer.AnalyseAsync(wav, SidecarAnalysisRequest.Standard));
    }
}
