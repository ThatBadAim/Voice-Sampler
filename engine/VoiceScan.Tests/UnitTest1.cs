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
    public void LoadAndRunSample_LoadsSileroVadSuccessfully()
    {
        var root = FindRepoRoot();
        var modelPath = Path.Combine(root, "models", "silero_vad.onnx");
        Assert.True(File.Exists(modelPath), $"Model missing at {modelPath}");

        var result = GpuModelSample.LoadAndRunSample(modelPath);

        Assert.Equal("silero_vad.onnx", result.ModelName);
        Assert.True(result.InputCount >= 3);
        Assert.True(result.OutputCount >= 1);
        Assert.Contains("forward pass executed", result.OutputSummary);
    }

    [Fact]
    public void LoadAndRunSample_LoadsWeSpeakerResNet34Successfully()
    {
        var root = FindRepoRoot();
        var modelPath = Path.Combine(root, "models", "wespeaker_en_voxceleb_resnet34.onnx");
        Assert.True(File.Exists(modelPath), $"Model missing at {modelPath}");

        var result = GpuModelSample.LoadAndRunSample(modelPath);

        Assert.Equal("wespeaker_en_voxceleb_resnet34.onnx", result.ModelName);
        Assert.Equal(1, result.InputCount);
        Assert.Equal(1, result.OutputCount);
        Assert.Contains("[1x256]", result.OutputSummary);
    }

    [Fact]
    public void LoadAndRunSample_ThrowsOnMissingFile()
    {
        Assert.Throws<FileNotFoundException>(() => GpuModelSample.LoadAndRunSample("non_existent_model.onnx"));
    }
}
