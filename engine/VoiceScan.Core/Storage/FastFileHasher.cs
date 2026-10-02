namespace VoiceScan.Core.Storage;

using System;
using System.IO;
using System.Security.Cryptography;

/// <summary>
/// High-throughput file hasher designed for large audio and video files.
/// Strategy:
/// - Small files (&lt;= 8 MB): Complete SHA-256 hash.
/// - Large files (&gt; 8 MB): Hybrid hash combining:
///   1. 8-byte file length header.
///   2. Head block (first 1 MB).
///   3. Tail block (last 1 MB).
///   4. 4 periodic sample blocks (256 KB each) evenly distributed across the file body.
///   All sampled components are streamed into SHA-256 to produce a deterministic signature.
/// - FullHash option: Compute full SHA-256 on demand for strict verification.
/// </summary>
public static class FastFileHasher
{
    private const long SmallFileThresholdBytes = 8 * 1024 * 1024; // 8 MB
    private const int LargeFileHeadTailBlockBytes = 1 * 1024 * 1024; // 1 MB
    private const int PeriodicBlockBytes = 256 * 1024; // 256 KB
    private const int PeriodicSampleCount = 4;

    /// <summary>
    /// Computes deterministic hybrid file hash optimized for rapid scanning of multi-gigabyte media captures.
    /// </summary>
    public static string ComputeFastHash(string filePath, bool forceFullHash = false)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Cannot hash missing file: {filePath}");
        }

        var fileInfo = new FileInfo(filePath);
        long length = fileInfo.Length;

        if (forceFullHash || length <= SmallFileThresholdBytes)
        {
            return ComputeFullSha256(filePath);
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // 1. Incorporate 64-bit file length
        byte[] lengthBytes = BitConverter.GetBytes(length);
        sha256.AppendData(lengthBytes);

        // 2. Head block (1 MB)
        byte[] buffer = new byte[LargeFileHeadTailBlockBytes];
        int headBytesRead = stream.Read(buffer, 0, LargeFileHeadTailBlockBytes);
        sha256.AppendData(buffer, 0, headBytesRead);

        // 3. Periodic sample blocks across the middle body
        long middleAvailable = length - (2L * LargeFileHeadTailBlockBytes);
        if (middleAvailable > PeriodicBlockBytes * PeriodicSampleCount)
        {
            long stride = middleAvailable / (PeriodicSampleCount + 1);
            byte[] sampleBuf = new byte[PeriodicBlockBytes];

            for (int i = 1; i <= PeriodicSampleCount; i++)
            {
                long offset = LargeFileHeadTailBlockBytes + (i * stride);
                stream.Seek(offset, SeekOrigin.Begin);
                int read = stream.Read(sampleBuf, 0, PeriodicBlockBytes);
                if (read > 0)
                {
                    sha256.AppendData(sampleBuf, 0, read);
                }
            }
        }

        // 4. Tail block (1 MB)
        long tailOffset = Math.Max(0, length - LargeFileHeadTailBlockBytes);
        stream.Seek(tailOffset, SeekOrigin.Begin);
        int tailBytesRead = stream.Read(buffer, 0, LargeFileHeadTailBlockBytes);
        sha256.AppendData(buffer, 0, tailBytesRead);

        byte[] hash = sha256.GetHashAndReset();
        return "hybrid-" + Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Computes full cryptographic SHA-256 hash across the entire file stream.
    /// </summary>
    public static string ComputeFullSha256(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        using var sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(stream);
        return "sha256-" + Convert.ToHexStringLower(hash);
    }
}
