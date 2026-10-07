namespace VoiceScan.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VoiceScan.Core;
using VoiceScan.Core.Inference;

/// <summary>Embedding model returning one fixed vector, so tests control which "voice" every line has.</summary>
internal sealed class FakeEmbeddingModel(string modelId, string? version = null, float[]? vector = null) : ISpeakerEmbeddingModel
{
    public string ModelId { get; } = modelId;
    public string ModelVersion { get; } = version ?? modelId + "@v1";
    public int EmbeddingDimension => Vector.Length;
    public string ActiveProvider => "CPUExecutionProvider";
    public bool IsCudaActive => false;
    public ModelOperatingPoint OperatingPoint { get; } = new(0.48, 0.60);
    public float[] Vector { get; } = vector ?? [1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f];
    public bool Disposed { get; private set; }

    public float[] ExtractEmbedding(float[] audioWindow) => (float[])Vector.Clone();
    public float[][] ExtractEmbeddingsBatch(IReadOnlyList<float[]> audioWindows) => audioWindows.Select(_ => (float[])Vector.Clone()).ToArray();
    public void Dispose() => Disposed = true;
}

/// <summary>Sidecar double: answers analyses with <see cref="Respond"/> and keeps an in-memory model per stage.</summary>
internal sealed class FakeSidecar : IInferenceClient, ISidecarModelControl
{
    public Func<SidecarAnalysisRequest, SidecarScanResponse>? Respond { get; set; }
    public List<SidecarAnalysisRequest> Requests { get; } = [];
    public Dictionary<string, string> Models { get; } = new()
    {
        ["vad"] = "pyannote/segmentation-3.0",
        ["diarization"] = "nvidia/Nemotron-3-Diarization",
        ["asr"] = "large-v3-turbo",
        ["alignment"] = "en",
        ["moderation"] = "unbiased",
    };
    public List<Dictionary<string, string>> SetCalls { get; } = [];
    /// <summary>Model names that fail to load.</summary>
    public HashSet<string> Broken { get; } = [];
    private readonly Dictionary<string, string?> _errors = new();

    public Task<bool> CheckHealthAsync(CancellationToken ct = default) => Task.FromResult(true);

    public async Task<SidecarScanResponse?> ProcessAudioAsync(string filePath, CancellationToken ct = default) =>
        await AnalyseAsync(filePath, SidecarAnalysisRequest.Standard, ct);

    public Task<SidecarScanResponse> AnalyseAsync(string audioFilePath, SidecarAnalysisRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        if (Respond == null) throw new HttpRequestException("Connection refused (localhost:54321)");
        var response = Respond(request);
        response.Models = new Dictionary<string, string>(Models);
        return Task.FromResult(response);
    }

    public Task<SidecarModelStatus> GetModelsAsync(CancellationToken ct = default) => Task.FromResult(Status());

    public Task<SidecarModelStatus> SetModelsAsync(IReadOnlyDictionary<string, string> models, CancellationToken ct = default)
    {
        SetCalls.Add(models.ToDictionary(kv => kv.Key, kv => kv.Value));
        foreach (var (stage, name) in models)
        {
            if (Broken.Contains(name)) _errors[stage] = $"{name}: not found";
            else
            {
                Models[stage] = name;
                _errors[stage] = null;
            }
        }
        return Task.FromResult(Status());
    }

    /// <summary>False reports the sidecar as still loading its models.</summary>
    public bool Ready { get; set; } = true;

    private SidecarModelStatus Status() => new()
    {
        Models = new Dictionary<string, string>(Models),
        Loaded = Models.Keys.ToDictionary(k => k, _ => true),
        Errors = Models.Keys.ToDictionary(k => k, k => _errors.GetValueOrDefault(k)),
        Ready = Ready,
        PipelineVersion = "test-1"
    };
}

/// <summary>HTTP handler that records requests (with bodies) and answers with a fixed JSON body.</summary>
internal sealed class RecordingHandler(string responseJson) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : string.Empty;
        Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) };
    }
}

internal static class TestAudio
{
    /// <summary>Writes a 16 kHz mono 16-bit WAV of a quiet 220 Hz tone.</summary>
    public static string WriteTone(string path, double seconds)
    {
        int count = (int)(seconds * 16000);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8);
        w.Write(36 + count * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(16000);
        w.Write(32000);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(count * 2);
        for (int i = 0; i < count; i++) w.Write((short)(Math.Sin(2 * Math.PI * 220 * i / 16000.0) * 6000));
        return path;
    }
}
