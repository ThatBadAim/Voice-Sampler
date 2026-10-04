namespace VoiceScan.Tests;

using System;
using System.IO;
using System.Linq;
using VoiceScan.Core;
using Xunit;

public class StreamingTests
{
    [Fact]
    public void WebRtcVad_ChunkedFeed_MatchesWholeBufferDetection()
    {
        var vad = new WebRtcVad();
        float[] speech = AudioDecoder.DecodeEntireFileAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "jfk_speech.wav")).GetAwaiter().GetResult();

        var whole = vad.DetectSpeechIntervals(speech);

        // Chunk sizes that never align with the 480-sample frame.
        var stream = vad.StartProbabilityStream();
        for (int offset = 0, step = 777; offset < speech.Length; offset += step)
        {
            int count = Math.Min(step, speech.Length - offset);
            stream.Feed(speech.Skip(offset).Take(count).ToArray(), count);
        }
        var chunked = WebRtcVad.ProbabilitiesToIntervals(stream.Finish(), (double)speech.Length / 16000);

        Assert.NotEmpty(whole);
        Assert.Equal(whole, chunked);
    }

    [Fact]
    public void SpoolAndPlan_ReproduceInMemoryWindows()
    {
        var rng = new Random(7);
        float[] audio = Enumerable.Range(0, 16000 * 9).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray();
        var intervals = new[] { new SpeechInterval(0.5, 1.4), new SpeechInterval(2.0, 8.3) };

        var expected = SpeechWindowExtractor.ExtractWindows(audio, intervals);

        using var spool = new SpooledAudio();
        for (int offset = 0; offset < audio.Length; offset += 32000)
        {
            spool.Append(audio.Skip(offset).Take(32000).ToArray());
        }
        Assert.Equal(audio.Length, spool.SampleCount);

        var plans = SpeechWindowExtractor.PlanWindows(spool.SampleCount, intervals);
        var actual = plans.Select(p => SpeechWindowExtractor.Materialize(p, spool.Read(p.SourceStart, p.SourceLength))).ToList();

        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].StartTimeSeconds, actual[i].StartTimeSeconds);
            Assert.Equal(expected[i].EndTimeSeconds, actual[i].EndTimeSeconds);
            Assert.Equal(expected[i].AudioSamples, actual[i].AudioSamples);
        }
    }

    [Fact]
    public void SpooledAudio_EnvelopeCapturesPeaks()
    {
        float[] audio = new float[256 * 1000];
        audio[256 * 10 + 3] = 0.9f;
        audio[256 * 990 + 5] = -0.7f;

        var spool = new SpooledAudio();
        spool.Append(audio);
        var (min, max) = spool.Envelope(100);
        spool.Dispose();

        Assert.Equal(100, min.Length);
        Assert.Equal(0.9f, max[1]);
        Assert.Equal(-0.7f, min[99]);
        Assert.Equal(0.9f, max.Max());
    }
}
