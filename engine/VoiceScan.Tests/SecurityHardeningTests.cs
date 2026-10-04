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
    public void ModelIntegrity_RejectsFileWhoseHashDiffersFromManifest()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "ecapa_tdnn.onnx"), "tampered");
            File.WriteAllText(Path.Combine(dir, "manifest.json"),
                "{\"models\":[{\"filename\":\"ecapa_tdnn.onnx\",\"sha256\":\"" + new string('0', 64) + "\"}]}");

            Assert.Throws<InvalidDataException>(() => ModelIntegrity.Verify(Path.Combine(dir, "ecapa_tdnn.onnx")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ModelIntegrity_AcceptsFileMatchingManifest()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string model = Path.Combine(dir, "ecapa_tdnn.onnx");
            File.WriteAllText(model, "genuine");
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(model)));
            File.WriteAllText(Path.Combine(dir, "manifest.json"),
                "{\"models\":[{\"filename\":\"ecapa_tdnn.onnx\",\"sha256\":\"" + hash + "\"}]}");

            ModelIntegrity.Verify(model);
        }
        finally { Directory.Delete(dir, true); }
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
