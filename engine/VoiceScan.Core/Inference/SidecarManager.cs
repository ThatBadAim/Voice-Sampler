namespace VoiceScan.Core.Inference;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VoiceScan.Core.Logging;

/// <summary>
/// Manages the process lifecycle of the Python sidecar inference service.
/// </summary>
public sealed class SidecarManager : IInferenceClient, IAsyncDisposable, IDisposable
{
    private readonly SidecarClient _client;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly string? _explicitScriptPath;
    private readonly string? _explicitPythonPath;
    private readonly TimeSpan _startupTimeout;
    private Process? _process;
    private bool _disposed;

    public IInferenceClient Client => _client;

    public SidecarManager(
        string? scriptPath = null,
        string? pythonPath = null,
        string baseUrl = SidecarClient.DefaultBaseUrl,
        TimeSpan? startupTimeout = null)
    {
        _client = new SidecarClient(baseUrl: baseUrl);
        _explicitScriptPath = scriptPath;
        _explicitPythonPath = pythonPath;
        _startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(25);
    }

    public Task<bool> CheckHealthAsync(CancellationToken ct = default) => _client.CheckHealthAsync(ct);

    public async Task<SidecarScanResponse?> ProcessAudioAsync(string filePath, CancellationToken ct = default)
    {
        bool isRunning = await EnsureRunningAsync(ct);
        if (!isRunning)
        {
            VoiceScanLogger.Error(nameof(SidecarManager), "Cannot process audio: sidecar service is not running and could not be started.");
            return null;
        }

        return await _client.ProcessAudioAsync(filePath, ct);
    }

    /// <summary>
    /// Ensures the sidecar service is healthy and responding. If down, attempts to spawn it.
    /// </summary>
    public async Task<bool> EnsureRunningAsync(CancellationToken ct = default)
    {
        if (_disposed) return false;

        // 1. Fast check if already responding
        if (await _client.CheckHealthAsync(ct))
        {
            return true;
        }

        await _lifecycleLock.WaitAsync(ct);
        try
        {
            if (_disposed) return false;

            // Double check inside lock
            if (await _client.CheckHealthAsync(ct))
            {
                return true;
            }

            // 2. Check if existing managed process is still launching
            if (_process != null && !_process.HasExited)
            {
                if (await WaitForHealthyAsync(_startupTimeout, ct))
                {
                    return true;
                }
            }

            // 3. Locate script and python runtime
            string? scriptPath = _explicitScriptPath ?? LocateInferenceScript();
            if (string.IsNullOrEmpty(scriptPath) || !File.Exists(scriptPath))
            {
                VoiceScanLogger.Error(
                    nameof(SidecarManager),
                    $"Sidecar script not found. Searched from: {AppContext.BaseDirectory} and {Directory.GetCurrentDirectory()}");
                return false;
            }

            string scriptDir = Path.GetDirectoryName(scriptPath)!;
            string pythonExe = _explicitPythonPath ?? LocatePythonExecutable(scriptDir);

            VoiceScanLogger.Info(
                nameof(SidecarManager),
                $"Starting sidecar process: \"{pythonExe}\" \"{scriptPath}\"");

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{scriptPath}\"",
                WorkingDirectory = scriptDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // Set PYTHONUNBUFFERED=1 for real-time logs
            startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

            var proc = new Process { StartInfo = startInfo };
            proc.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    VoiceScanLogger.Debug("SidecarProcess", e.Data);
                }
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    VoiceScanLogger.Warn("SidecarProcess", e.Data);
                }
            };

            if (!proc.Start())
            {
                VoiceScanLogger.Error(nameof(SidecarManager), "Failed to start sidecar process.");
                proc.Dispose();
                return false;
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _process = proc;

            // 4. Poll until healthy
            bool ready = await WaitForHealthyAsync(_startupTimeout, ct);
            if (ready)
            {
                VoiceScanLogger.Info(nameof(SidecarManager), $"Sidecar service started successfully (PID: {proc.Id}).");
                return true;
            }

            if (proc.HasExited)
            {
                VoiceScanLogger.Error(nameof(SidecarManager), $"Sidecar process exited prematurely with code {proc.ExitCode}.");
            }
            else
            {
                VoiceScanLogger.Error(nameof(SidecarManager), $"Sidecar process failed to become ready within {_startupTimeout.TotalSeconds:F0}s.");
            }

            return false;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task<bool> WaitForHealthyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
        {
            if (_process != null && _process.HasExited)
            {
                return false;
            }

            if (await _client.CheckHealthAsync(ct))
            {
                return true;
            }

            try
            {
                await Task.Delay(500, ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    public static string? LocateInferenceScript()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "sidecar", "inference_server.py"),
            Path.Combine(Directory.GetCurrentDirectory(), "sidecar", "inference_server.py")
        };

        // Search parent directories up towards solution root
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            candidates.Add(Path.Combine(dir.FullName, "sidecar", "inference_server.py"));
            dir = dir.Parent;
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public static string LocatePythonExecutable(string scriptDirectory)
    {
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string venvBin = isWindows ? "Scripts" : "bin";
        string pythonName = isWindows ? "python.exe" : "python";

        // Check virtual environments in script dir or repo root
        var candidatePaths = new List<string>
        {
            Path.Combine(scriptDirectory, ".venv", venvBin, pythonName),
            Path.Combine(scriptDirectory, "venv", venvBin, pythonName),
            Path.Combine(scriptDirectory, "..", ".venv", venvBin, pythonName),
            Path.Combine(scriptDirectory, "..", "venv", venvBin, pythonName)
        };

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }

        // System fallback
        return isWindows ? "python" : "python3";
    }

    public void Stop()
    {
        if (_process != null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    VoiceScanLogger.Info(nameof(SidecarManager), $"Stopping sidecar process (PID: {_process.Id})...");
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                VoiceScanLogger.Warn(nameof(SidecarManager), $"Error while killing sidecar process: {ex.Message}");
            }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _client.Dispose();
        _lifecycleLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await Task.Run(() => Stop());
        _client.Dispose();
        _lifecycleLock.Dispose();
    }
}
