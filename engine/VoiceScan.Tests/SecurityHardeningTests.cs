namespace VoiceScan.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VoiceScan.Core;
using Xunit;

public class SecurityHardeningTests
{
    [Fact]
    public void ModelIntegrity_RejectsTamperedModelEvenWithItsOwnManifest()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string model = Path.Combine(dir, "ecapa_tdnn.onnx");
            File.WriteAllText(model, "tampered");
            // A manifest planted next to the model is ignored: only the checksums compiled into the engine count.
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(model)));
            File.WriteAllText(Path.Combine(dir, "manifest.json"),
                "{\"models\":[{\"filename\":\"ecapa_tdnn.onnx\",\"sha256\":\"" + hash + "\"}]}");

            Assert.Throws<InvalidDataException>(() => ModelIntegrity.Verify(model));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ModelIntegrity_RejectsUnlistedModelFile()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string model = Path.Combine(dir, "my_model.onnx");
            File.WriteAllText(model, "anything");
            Assert.Throws<InvalidDataException>(() => ModelIntegrity.Verify(model));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ModelIntegrity_AcceptsReleasedModelAndReturnsItsHash()
    {
        string model = AppPaths.FindModel("ecapa_tdnn.onnx")!;
        Assert.NotNull(model);

        string hash = ModelIntegrity.Verify(model);

        Assert.Equal(ModelIntegrity.ExpectedHash("ecapa_tdnn.onnx")!, hash, ignoreCase: true);
    }

    [Fact]
    public async Task ProbeAudioTracks_TreatsQuoteInFileNameAsLiteral()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string name = Path.Combine(dir, "a\" -f null \"b.wav");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "jfk_speech.wav"), name);

            var tracks = await AudioDecoder.ProbeAudioTracksAsync(name);

            Assert.Single(tracks);
        }
        finally { Directory.Delete(dir, true); }
    }
}
