using System.ComponentModel;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;

namespace VoiceScan.App.Core.Services;

/// <summary>Plain-language description of a failure: what went wrong and what the user can do about it.</summary>
public sealed record ErrorExplanation(string Cause, string Remedy);

public static class ErrorExplainer
{
    private const string ReportAdvice =
        "Use \"Copy details\" and include the log file when reporting this. Restarting VoiceScan is safe.";

    public static ErrorExplanation Explain(Exception ex)
    {
        ex = Unwrap(ex);
        string message = ex.Message ?? "";
        string typeName = ex.GetType().FullName ?? ex.GetType().Name;

        switch (ex)
        {
            case FileNotFoundException f:
                return new($"A file the app needs was not found: {f.FileName ?? message}.",
                    "Check that it was not moved, renamed or deleted, then try again. If it is a model file, reinstall or re-select it on the Models page.");
            case DirectoryNotFoundException:
                return new($"A folder the app needs was not found. {message}",
                    "Check that the folder (or the drive it is on) is still available, then try again.");
            case UnauthorizedAccessException:
                return new($"VoiceScan is not allowed to access a file or folder. {message}",
                    "Check the file's permissions, make sure it is not open in another program, or choose a location you own.");
            case OutOfMemoryException:
                return new("The computer ran out of memory.",
                    "Close other programs and scan fewer or shorter files at a time.");
            case DllNotFoundException or BadImageFormatException:
                return new($"A required native library could not be loaded. {message}",
                    "For GPU use, install a CUDA/cuDNN version that matches the ONNX Runtime build. Otherwise reinstall VoiceScan so its bundled libraries are restored.");
            case TimeoutException:
                return new("An operation took too long and was stopped.",
                    "Try again. If it keeps happening, the inference service or disk may be overloaded.");
            case OperationCanceledException:
                return new("The operation was cancelled.", "Nothing is wrong; start it again if you still need it.");
            case HttpRequestException or SocketException:
                return new("VoiceScan could not reach its local inference service. VoiceScan never uses the internet.",
                    "Check that the sidecar is running and that no firewall is blocking local connections, then retry.");
            case Win32Exception when Mentions(message, "ffmpeg", "ffprobe"):
                return new("FFmpeg could not be started, so audio cannot be decoded.",
                    "Install FFmpeg or reinstall VoiceScan so the bundled copy is restored.");
            case IOException io when IsDiskFull(io):
                return new("The disk is full.", "Free up space on the drive VoiceScan stores its data on, then retry.");
            case IOException:
                return new($"A file could not be read or written. {message}",
                    "Make sure the file is not open in another program and the drive is still connected, then retry.");
            case InvalidDataException or FormatException when Mentions(message, "audio", "wav", "decode", "format"):
                return new($"The audio file could not be read. {message}",
                    "The file may be corrupt or in an unsupported format. Try converting it to WAV or FLAC.");
        }

        if (Mentions(typeName, "Sqlite"))
            return new($"VoiceScan's local database reported a problem. {message}",
                "Make sure only one copy of VoiceScan is running. If the message says the database is corrupt, move the .db file aside to let VoiceScan recreate it.");
        if (Mentions(typeName, "OnnxRuntime"))
            return new($"The voice model failed to load or run. {message}",
                Mentions(message, "cuda", "cudnn", "gpu")
                    ? "GPU inference failed. Install matching CUDA/cuDNN libraries, or continue on the CPU."
                    : "Re-select or reinstall the model on the Models page; the file may be damaged.");
        if (Mentions(message, "sha-256", "integrity", "checksum"))
            return new($"A model file failed its integrity check. {message}",
                "The file is damaged or not the expected release. Replace it with the original from the model's source.");

        return new($"An unexpected internal error occurred ({ex.GetType().Name}). {message}".Trim(), ReportAdvice);
    }

    /// <summary>The first exception that says something specific about the real cause.</summary>
    public static Exception Unwrap(Exception ex)
    {
        while (ex.InnerException is { } inner &&
               ex is AggregateException or TargetInvocationException or TypeInitializationException)
        {
            ex = inner is AggregateException agg && agg.InnerExceptions.Count > 0 ? agg.InnerExceptions[0] : inner;
        }

        return ex;
    }

    private static bool Mentions(string text, params string[] needles) =>
        needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static bool IsDiskFull(IOException io) =>
        (io.HResult & 0xFFFF) is 0x27 or 0x70 || Mentions(io.Message, "no space left", "not enough space", "disk full");
}
