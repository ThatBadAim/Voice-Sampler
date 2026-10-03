namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Represents a single speech window item ready for clustering.
/// </summary>
public sealed class WindowItem
{
    public int Index { get; }
    public double StartTimeSeconds { get; }
    public double EndTimeSeconds { get; }
    public float[] Embedding { get; }
    public double SnrDb { get; }
    public bool SuspectedOverlap { get; }

    public WindowItem(
        int index,
        double startTimeSeconds,
        double endTimeSeconds,
        float[] embedding,
        double snrDb = 20.0,
        bool suspectedOverlap = false)
    {
        SnrDb = snrDb;
        SuspectedOverlap = suspectedOverlap;
        Index = index;
        StartTimeSeconds = startTimeSeconds;
        EndTimeSeconds = endTimeSeconds;
        Embedding = embedding ?? throw new ArgumentNullException(nameof(embedding));
    }
}

/// <summary>
/// A cluster of speech windows corresponding to an inferred speaker identity within a file.
/// </summary>
public sealed class SpeakerCluster
{
    public int ClusterId { get; set; }
    public List<WindowItem> Windows { get; } = new();
    public float[] Centroid { get; set; } = Array.Empty<float>();
    public float Score { get; set; }
}

/// <summary>
/// Performs within-file Agglomerative Hierarchical Clustering (AHC) using average linkage
/// on unit-normalized speaker window embeddings.
/// </summary>
public static class SpeakerClusterer
{
    /// <summary>
    /// Clusters speech window embeddings using agglomerative clustering with average linkage.
    /// Distance is cosine distance: d(u, v) = 1.0 - cosine_similarity(u, v).
    /// Merging stops when the minimum inter-cluster distance exceeds distanceThreshold.
    /// </summary>
    /// <param name="windows">List of speech windows extracted from the file.</param>
    /// <param name="distanceThreshold">Stopping distance threshold (default 0.40, i.e. min average similarity 0.60).</param>
    /// <returns>List of speaker clusters with computed unit-normalized centroids.</returns>
    public static List<SpeakerCluster> ClusterWindows(
        IReadOnlyList<WindowItem> windows,
        double distanceThreshold = 0.40)
    {
        if (windows == null || windows.Count == 0)
        {
            return new List<SpeakerCluster>();
        }

        int n = windows.Count;
        int dim = windows[0].Embedding.Length;

        if (n == 1)
        {
            var single = new SpeakerCluster { ClusterId = 0 };
            single.Windows.Add(windows[0]);
            single.Centroid = (float[])windows[0].Embedding.Clone();
            return new List<SpeakerCluster> { single };
        }

        // Initialize clusters: one per window
        var clusters = new List<ClusterNode>(n);
        for (int i = 0; i < n; i++)
        {
            var node = new ClusterNode(i, dim);
            node.Windows.Add(windows[i]);
            node.AddEmbedding(windows[i].Embedding);
            clusters.Add(node);
        }

        // Active cluster indices
        var active = new HashSet<int>(Enumerable.Range(0, n));

        // Pairwise distance matrix: dist[i, j] for active clusters
        var dist = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                double d = ComputeClusterDistance(clusters[i], clusters[j]);
                dist[i, j] = d;
                dist[j, i] = d;
            }
        }

        // Nearest neighbor tracking for O(N^2) total clustering time
        int[] nearest = new int[n];
        double[] minDist = new double[n];

        void UpdateNearest(int i)
        {
            double best = double.MaxValue;
            int bestNeighbor = -1;
            foreach (int j in active)
            {
                if (i != j && dist[i, j] < best)
                {
                    best = dist[i, j];
                    bestNeighbor = j;
                }
            }
            minDist[i] = best;
            nearest[i] = bestNeighbor;
        }

        foreach (int i in active)
        {
            UpdateNearest(i);
        }

        // Agglomerative merging loop
        while (active.Count > 1)
        {
            double bestDist = double.MaxValue;
            int bestI = -1;
            int bestJ = -1;

            foreach (int i in active)
            {
                if (minDist[i] < bestDist)
                {
                    bestDist = minDist[i];
                    bestI = i;
                    bestJ = nearest[i];
                }
            }

            if (bestDist > distanceThreshold || bestI == -1 || bestJ == -1)
            {
                break; // Stopping threshold reached
            }

            // Ensure canonical ordering (bestI < bestJ)
            if (bestI > bestJ)
            {
                (bestI, bestJ) = (bestJ, bestI);
            }

            // Merge bestJ into bestI
            var ci = clusters[bestI];
            var cj = clusters[bestJ];

            ci.MergeFrom(cj);
            active.Remove(bestJ);

            // Update distances between new bestI and remaining active clusters
            foreach (int k in active)
            {
                if (k != bestI)
                {
                    double d = ComputeClusterDistance(ci, clusters[k]);
                    dist[bestI, k] = d;
                    dist[k, bestI] = d;
                }
            }

            // Update nearest neighbors
            foreach (int k in active)
            {
                if (k == bestI)
                {
                    UpdateNearest(bestI);
                }
                else if (nearest[k] == bestI || nearest[k] == bestJ)
                {
                    UpdateNearest(k);
                }
                else if (dist[k, bestI] < minDist[k])
                {
                    minDist[k] = dist[k, bestI];
                    nearest[k] = bestI;
                }
            }
        }

        // Finalize resulting clusters
        var result = new List<SpeakerCluster>();
        int clusterId = 0;
        foreach (int idx in active)
        {
            var node = clusters[idx];
            var cluster = new SpeakerCluster
            {
                ClusterId = clusterId++,
                Centroid = node.ComputeNormalizedCentroid()
            };
            cluster.Windows.AddRange(node.Windows.OrderBy(w => w.StartTimeSeconds));
            result.Add(cluster);
        }

        return result;
    }

    private static double ComputeClusterDistance(ClusterNode a, ClusterNode b)
    {
        // By algebraic identity for unit vectors:
        // Average pairwise cosine similarity = (SumVector_A . SumVector_B) / (|A| * |B|)
        double dot = 0.0;
        for (int d = 0; d < a.SumVector.Length; d++)
        {
            dot += a.SumVector[d] * b.SumVector[d];
        }

        double avgSim = dot / ((double)a.Count * b.Count);
        // Clamp to [-1.0, 1.0] for floating point stability
        if (avgSim > 1.0) avgSim = 1.0;
        if (avgSim < -1.0) avgSim = -1.0;

        return 1.0 - avgSim;
    }

    private sealed class ClusterNode
    {
        public int Id { get; }
        public List<WindowItem> Windows { get; } = new();
        public double[] SumVector { get; }
        public int Count { get; private set; }

        public ClusterNode(int id, int dimension)
        {
            Id = id;
            SumVector = new double[dimension];
            Count = 0;
        }

        public void AddEmbedding(float[] emb)
        {
            for (int i = 0; i < emb.Length; i++)
            {
                SumVector[i] += emb[i];
            }
            Count++;
        }

        public void MergeFrom(ClusterNode other)
        {
            Windows.AddRange(other.Windows);
            for (int i = 0; i < SumVector.Length; i++)
            {
                SumVector[i] += other.SumVector[i];
            }
            Count += other.Count;
        }

        public float[] ComputeNormalizedCentroid()
        {
            int dim = SumVector.Length;
            float[] centroid = new float[dim];
            double sumSq = 0.0;

            for (int i = 0; i < dim; i++)
            {
                double val = SumVector[i] / Math.Max(1, Count);
                centroid[i] = (float)val;
                sumSq += val * val;
            }

            double norm = Math.Sqrt(sumSq);
            if (norm > 1e-12)
            {
                float invNorm = (float)(1.0 / norm);
                for (int i = 0; i < dim; i++)
                {
                    centroid[i] *= invNorm;
                }
            }

            return centroid;
        }
    }
}
