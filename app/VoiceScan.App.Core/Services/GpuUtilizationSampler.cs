using System.Diagnostics;
using System.Globalization;

namespace VoiceScan.App.Core.Services;

/// <summary>
/// Samples NVIDIA GPU utilisation through the local nvidia-smi tool. Reports null when the tool is
/// missing or fails, so the UI shows "n/a" instead of a made-up number.
/// </summary>
public sealed class GpuUtilizationSampler : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private CancellationTokenSource? _cts;
    private double? _latest;

    public double? Latest => _latest;

    public void Start(bool gpuInUse)
    {
        if (!gpuInUse || _cts != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                _latest = await QueryAsync(token);
                if (_latest is null) return; // tool unavailable: stop polling
                try { await Task.Delay(Interval, token); }
                catch (OperationCanceledException) { return; }
            }
        }, token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _latest = null;
    }

    private static async Task<double?> QueryAsync(CancellationToken token)
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--query-gpu=utilization.gpu");
            psi.ArgumentList.Add("--format=csv,noheader,nounits");

            using var process = Process.Start(psi);
            if (process is null) return null;
            string output = await process.StandardOutput.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            string firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
            return process.ExitCode == 0 && double.TryParse(firstLine, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)
                ? pct
                : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public void Dispose() => Stop();
}
