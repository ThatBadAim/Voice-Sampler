namespace VoiceScan.Core;

using System;
using System.Collections.Generic;

/// <summary>
/// Swappable interface for speaker embedding models (e.g. WeSpeaker ResNet34, 3D-Speaker CAM++).
/// </summary>
public interface ISpeakerEmbeddingModel : IDisposable
{
    string ModelId { get; }
    int EmbeddingDimension { get; }
    string ActiveProvider { get; }
    bool IsCudaActive { get; }

    /// <summary>
    /// Extract a single unit-normalized embedding vector from a 16 kHz audio window.
    /// </summary>
    float[] ExtractEmbedding(float[] audioWindow);

    /// <summary>
    /// Batched inference across multiple audio windows yielding unit-normalized embedding vectors.
    /// </summary>
    float[][] ExtractEmbeddingsBatch(IReadOnlyList<float[]> audioWindows);
}
