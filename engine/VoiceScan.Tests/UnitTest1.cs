namespace VoiceScan.Tests;

using System;
using System.IO;
using VoiceScan.Core;
using Xunit;

public class GpuModelSampleTests
{
    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "VoiceScan.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root.");
    }

    [Fact]
    public void LoadAndRunSample_LoadsEcapaTdnnSuccessfully()
    {
        var root = FindRepoRoot();
        var modelPath = Path.Combine(root, "models", "ecapa_tdnn.onnx");
        Assert.True(File.Exists(modelPath), $"Model missing at {modelPath}");

        var result = GpuModelSample.LoadAndRunSample(modelPath);

        Assert.Equal("ecapa_tdnn.onnx", result.ModelName);
        Assert.True(result.InputCount >= 1);
        Assert.True(result.OutputCount >= 1);
        Assert.Contains("Speaker embedding forward pass executed", result.OutputSummary);
    }

    [Fact]
    public void LoadAndRunSample_LoadsTitaNetSuccessfully()
    {
        var root = FindRepoRoot();
        var modelPath = Path.Combine(root, "models", "titanet_small.onnx");
        Assert.True(File.Exists(modelPath), $"Model missing at {modelPath}");

        var result = GpuModelSample.LoadAndRunSample(modelPath);

        Assert.Equal("titanet_small.onnx", result.ModelName);
        Assert.True(result.InputCount >= 1);
        Assert.True(result.OutputCount >= 1);
        Assert.Contains("Speaker embedding forward pass executed", result.OutputSummary);
    }

    [Fact]
    public void LoadAndRunSample_ThrowsOnMissingFile()
    {
        Assert.Throws<FileNotFoundException>(() => GpuModelSample.LoadAndRunSample("non_existent_model.onnx"));
    }
}
