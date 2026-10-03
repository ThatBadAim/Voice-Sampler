namespace VoiceScan.App.Core.Models;

public enum EnrollmentStep
{
    AudioSelection = 1,
    ConsentVerification = 2,
    QualityDiagnostics = 3,
    ProfileCreation = 4,
    Complete = 5
}

public enum AudioQualityTier
{
    Excellent,
    Acceptable,
    Warning,
    Rejected
}

public sealed record AudioQualityReport(
    AudioQualityTier Tier,
    double TotalAudioDurationSeconds,
    double SpeechDurationSeconds,
    double SnrEstimateDb,
    double NoiseFloorRms,
    bool MinimumSpeechMet,
    bool SnrThresholdMet,
    IReadOnlyList<string> FeedbackMessages,
    bool IsAcceptableForEnrollment);

public sealed record VoiceProfileSummary(
    string Name,
    string Path,
    int Dimension,
    int WindowCount,
    DateTimeOffset CreatedAtUtc);
