using VoiceScan.App.Core.Models;
using VoiceScan.Core;

namespace VoiceScan.App.Core.Services;

public interface IAudioQualityAnalyzer
{
    Task<AudioQualityReport> AnalyzeAudioAsync(string audioFilePath, CancellationToken cancellationToken = default);
    AudioQualityReport AnalyzePcm(ReadOnlySpan<float> pcm16k, int sampleRate = 16000);
}

public sealed class AudioQualityAnalyzer : IAudioQualityAnalyzer
{
    private readonly WebRtcVad _vadDetector;

    public AudioQualityAnalyzer(WebRtcVad? vadDetector = null)
    {
        _vadDetector = vadDetector ?? new WebRtcVad();
    }

    /// <summary>
    /// Checks exactly the audio enrollment will use: the first <see cref="ProfileEnrollmentService.MaxSecondsPerClip"/>
    /// seconds of the file.
    /// </summary>
    public async Task<AudioQualityReport> AnalyzeAudioAsync(string audioFilePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(audioFilePath))
        {
            throw new FileNotFoundException("Audio file not found for quality analysis.", audioFilePath);
        }

        float[] samples = await AudioDecoder.DecodeEntireFileAsync(
            audioFilePath, cancellationToken: cancellationToken, maxDurationSeconds: ProfileEnrollmentService.MaxSecondsPerClip);

        if (samples.Length == 0)
        {
            return new AudioQualityReport(
                Tier: AudioQualityTier.Rejected,
                TotalAudioDurationSeconds: 0,
                SpeechDurationSeconds: 0,
                SnrEstimateDb: 0,
                NoiseFloorRms: 0,
                MinimumSpeechMet: false,
                SnrThresholdMet: false,
                FeedbackMessages: ["No audio stream could be decoded from the selected file."],
                IsAcceptableForEnrollment: false);
        }

        return AnalyzePcm(samples, 16000);
    }

    public AudioQualityReport AnalyzePcm(ReadOnlySpan<float> pcm16k, int sampleRate = 16000)
    {
        double totalDuration = (double)pcm16k.Length / sampleRate;

        // Estimate SNR and noise floor using AcousticDiagnostics
        float[] samplesArray = pcm16k.ToArray();
        double snrDb = AcousticDiagnostics.EstimateSnrDb(samplesArray);

        // Calculate noise floor RMS (bottom 15% energy frames)
        int frameSize = sampleRate / 20; // 50ms frames
        int numFrames = pcm16k.Length / frameSize;
        List<double> frameEnergies = new(Math.Max(1, numFrames));

        for (int i = 0; i < numFrames; i++)
        {
            double sumSq = 0.0;
            int offset = i * frameSize;
            for (int j = 0; j < frameSize; j++)
            {
                float s = pcm16k[offset + j];
                sumSq += s * s;
            }
            frameEnergies.Add(Math.Sqrt(sumSq / frameSize));
        }

        double noiseFloorRms = 0.001;
        if (frameEnergies.Count > 0)
        {
            frameEnergies.Sort();
            int bottomCount = Math.Max(1, (int)(frameEnergies.Count * 0.15));
            noiseFloorRms = frameEnergies.Take(bottomCount).Average();
        }

        // Speech duration from the same voice activity detector enrollment uses.
        double speechDuration = _vadDetector.DetectSpeechIntervals(samplesArray).Sum(x => x.EndTimeSeconds - x.StartTimeSeconds);

        bool minSpeechMet = speechDuration >= 4.0;
        bool snrMet = snrDb >= 10.0;

        List<string> feedback = [];
        AudioQualityTier tier;

        if (speechDuration < 3.0)
        {
            tier = AudioQualityTier.Rejected;
            feedback.Add($"Insufficient speech detected ({speechDuration:F1}s). A minimum of 4.0 seconds of clear speech is required.");
        }
        else if (speechDuration < 5.0)
        {
            tier = AudioQualityTier.Warning;
            feedback.Add($"Short speech sample ({speechDuration:F1}s). While acceptable, 5 to 15 seconds produces optimal enrollment accuracy.");
        }
        else
        {
            tier = AudioQualityTier.Acceptable;
            feedback.Add($"Good speech volume ({speechDuration:F1}s of active voice detected).");
        }

        if (snrDb < 6.0)
        {
            tier = AudioQualityTier.Rejected;
            feedback.Add($"Heavy background noise or game audio detected (SNR {snrDb:F1} dB). Please record in a quieter space.");
        }
        else if (snrDb < 12.0)
        {
            if (tier != AudioQualityTier.Rejected) tier = AudioQualityTier.Warning;
            feedback.Add($"Moderate background noise detected (SNR {snrDb:F1} dB). Target voice might be less distinct in noisy scans.");
        }
        else
        {
            if (tier != AudioQualityTier.Rejected && tier != AudioQualityTier.Warning) tier = AudioQualityTier.Excellent;
            feedback.Add($"Pristine acoustic clarity (SNR {snrDb:F1} dB).");
        }

        bool acceptable = tier != AudioQualityTier.Rejected;

        return new AudioQualityReport(
            Tier: tier,
            TotalAudioDurationSeconds: totalDuration,
            SpeechDurationSeconds: speechDuration,
            SnrEstimateDb: snrDb,
            NoiseFloorRms: noiseFloorRms,
            MinimumSpeechMet: minSpeechMet,
            SnrThresholdMet: snrMet,
            FeedbackMessages: feedback,
            IsAcceptableForEnrollment: acceptable);
    }
}
