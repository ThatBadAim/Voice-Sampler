namespace VoiceScan.Core;

using System;
using System.Collections.Generic;

/// <summary>Decision settings measured for one embedding model on the real-speech development set.</summary>
/// <param name="Threshold">Cosine score at or above which a speaker cluster is a Match.</param>
/// <param name="ClusterDistanceThreshold">AHC stopping distance (1 - average cosine) for within-file speaker clustering.</param>
public sealed record ModelOperatingPoint(double Threshold, double ClusterDistanceThreshold);

/// <summary>
/// Swappable interface for speaker embedding models.
/// </summary>
public interface ISpeakerEmbeddingModel : IDisposable
{
    string ModelId { get; }

    /// <summary>
    /// Identifies everything that changes the embeddings (model, exact weights, feature front-end).
    /// Profiles and cached embeddings are only valid for the version that produced them.
    /// </summary>
    string ModelVersion { get; }

    int EmbeddingDimension { get; }
    string ActiveProvider { get; }
    bool IsCudaActive { get; }
    ModelOperatingPoint OperatingPoint { get; }

    /// <summary>
    /// Extract a single unit-normalized embedding vector from a 16 kHz audio window.
    /// </summary>
    float[] ExtractEmbedding(float[] audioWindow);

    /// <summary>
    /// Batched inference across multiple audio windows yielding unit-normalized embedding vectors.
    /// </summary>
    float[][] ExtractEmbeddingsBatch(IReadOnlyList<float[]> audioWindows);
}
