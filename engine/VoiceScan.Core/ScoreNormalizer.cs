namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Serializable document representing an impostor cohort for score normalization.
/// </summary>
public sealed class CohortDocument
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "1.0.0";

    [JsonPropertyName("model_id")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonPropertyName("embeddings")]
    public List<float[]> Embeddings { get; set; } = new();

    [JsonPropertyName("cohort_size")]
    public int CohortSize => Embeddings.Count;

    public static CohortDocument LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Cohort file not found: {path}");
        }

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<CohortDocument>(json)
            ?? throw new InvalidDataException($"Failed to deserialize cohort document from: {path}");
    }

    public void SaveToFile(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}

/// <summary>
/// Implements Adaptive Symmetric Score Normalization (AS-Norm).
/// Calibrates raw cosine similarity scores against a pre-indexed cohort of non-target impostor embeddings.
/// </summary>
public sealed class ScoreNormalizer
{
    private readonly List<float[]> _cohort;
    private readonly int _topK;

    public int CohortSize => _cohort.Count;

    public ScoreNormalizer(IReadOnlyList<float[]> cohortEmbeddings, int topK = 50)
    {
        _cohort = cohortEmbeddings?.Where(e => e != null && e.Length > 0).ToList()
            ?? new List<float[]>();
        _topK = Math.Max(1, topK);
    }

    public static ScoreNormalizer FromFile(string cohortFilePath, int topK = 50)
    {
        var doc = CohortDocument.LoadFromFile(cohortFilePath);
        return new ScoreNormalizer(doc.Embeddings, topK);
    }

    /// <summary>
    /// Computes cohort statistics (mean and standard deviation) for the top-K highest similarity cohort entries.
    /// </summary>
    public (double Mean, double StdDev) ComputeCohortStats(float[] embedding)
    {
        if (_cohort.Count == 0 || embedding.Length == 0)
        {
            return (0.0, 1.0);
        }

        int k = Math.Min(_topK, _cohort.Count);
        var scores = new float[_cohort.Count];

        for (int i = 0; i < _cohort.Count; i++)
        {
            scores[i] = SimilarityScorer.CosineSimilarity(embedding, _cohort[i]);
        }

        // Sort descending to get top-K adaptive cohort
        Array.Sort(scores);
        Array.Reverse(scores);

        double sum = 0.0;
        for (int i = 0; i < k; i++)
        {
            sum += scores[i];
        }
        double mean = sum / k;

        double sumSqDiff = 0.0;
        for (int i = 0; i < k; i++)
        {
            double diff = scores[i] - mean;
            sumSqDiff += diff * diff;
        }
        double variance = k > 1 ? sumSqDiff / (k - 1) : 0.0;
        double stdDev = Math.Max(1e-4, Math.Sqrt(variance));

        return (mean, stdDev);
    }

    /// <summary>
    /// Computes the AS-Norm z-score:
    /// s_as = 0.5 * ((s - mu_target) / sigma_target + (s - mu_test) / sigma_test)
    /// </summary>
    public double NormalizeScore(float rawCosineScore, float[] targetCentroid, float[] testEmbedding)
    {
        if (_cohort.Count < 2)
        {
            return rawCosineScore;
        }

        var (muTarget, sigmaTarget) = ComputeCohortStats(targetCentroid);
        var (muTest, sigmaTest) = ComputeCohortStats(testEmbedding);

        double zTarget = (rawCosineScore - muTarget) / sigmaTarget;
        double zTest = (rawCosineScore - muTest) / sigmaTest;

        return 0.5 * (zTarget + zTest);
    }

    /// <summary>
    /// Computes the AS-Norm z-score using precomputed target cohort statistics.
    /// Avoids re-sorting and scanning the cohort repeatedly when scoring many windows against the same target.
    /// </summary>
    public double NormalizeScoreWithTargetStats(float rawCosineScore, (double Mean, double StdDev) targetStats, float[] testEmbedding)
    {
        if (_cohort.Count < 2)
        {
            return rawCosineScore;
        }

        var (muTest, sigmaTest) = ComputeCohortStats(testEmbedding);

        double zTarget = (rawCosineScore - targetStats.Mean) / targetStats.StdDev;
        double zTest = (rawCosineScore - muTest) / sigmaTest;

        return 0.5 * (zTarget + zTest);
    }

    /// <summary>
    /// Calibrates an AS-Norm z-score back into a standardized [0.0, 1.0] probability-like confidence scale.
    /// Using standard logistic sigmoid: 1 / (1 + exp(-0.8 * (z - z_center)))
    /// where z_center = 2.0 (representing a standard ~2-sigma significance boundary).
    /// </summary>
    public static double CalibrateZScoreToConfidence(double zScore, double zCenter = 2.0, double scale = 0.8)
    {
        double x = scale * (zScore - zCenter);
        // Numerical clamp
        if (x > 30.0) return 1.0;
        if (x < -30.0) return 0.0;
        return 1.0 / (1.0 + Math.Exp(-x));
    }
}
