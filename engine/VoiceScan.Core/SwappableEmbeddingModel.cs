namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.Threading;

/// <summary>
/// The active embedding model, replaceable while the app runs. Scans and re-analyses hold a use for their whole run
/// (<see cref="BeginUse"/>), and every inference call holds one for its duration, so a model is never replaced mid-run
/// and never disposed while it is computing.
/// </summary>
public sealed class SwappableEmbeddingModel : ISpeakerEmbeddingModel
{
    private readonly object _gate = new();
    private ISpeakerEmbeddingModel _current;
    private int _users;

    public SwappableEmbeddingModel(ISpeakerEmbeddingModel initial)
    {
        _current = initial ?? throw new ArgumentNullException(nameof(initial));
    }

    /// <summary>Raised after a successful <see cref="Swap"/>, on the thread that swapped.</summary>
    public event Action? Swapped;

    public ISpeakerEmbeddingModel Current
    {
        get { lock (_gate) return _current; }
    }

    public string ModelId => Current.ModelId;
    public string ModelVersion => Current.ModelVersion;
    public int EmbeddingDimension => Current.EmbeddingDimension;
    public string ActiveProvider => Current.ActiveProvider;
    public bool IsCudaActive => Current.IsCudaActive;
    public ModelOperatingPoint OperatingPoint => Current.OperatingPoint;

    /// <summary>Marks the model as in use until the returned handle is disposed; <see cref="Swap"/> is refused meanwhile.</summary>
    public IDisposable BeginUse()
    {
        Acquire();
        return new Use(this);
    }

    /// <summary>Makes <paramref name="next"/> the active model and returns the previous one, which the caller disposes.</summary>
    /// <exception cref="InvalidOperationException">A scan, re-analysis or inference call is using the model.</exception>
    public ISpeakerEmbeddingModel Swap(ISpeakerEmbeddingModel next)
    {
        ArgumentNullException.ThrowIfNull(next);
        ISpeakerEmbeddingModel previous;
        lock (_gate)
        {
            if (_users > 0)
            {
                throw new InvalidOperationException("The voice model is in use by a scan or re-analysis. Switch models when it has finished.");
            }
            previous = _current;
            _current = next;
        }
        Swapped?.Invoke();
        return previous;
    }

    public float[] ExtractEmbedding(float[] audioWindow)
    {
        var model = Acquire();
        try
        {
            return model.ExtractEmbedding(audioWindow);
        }
        finally
        {
            Release();
        }
    }

    public float[][] ExtractEmbeddingsBatch(IReadOnlyList<float[]> audioWindows)
    {
        var model = Acquire();
        try
        {
            return model.ExtractEmbeddingsBatch(audioWindows);
        }
        finally
        {
            Release();
        }
    }

    private ISpeakerEmbeddingModel Acquire()
    {
        lock (_gate)
        {
            _users++;
            return _current;
        }
    }

    private void Release()
    {
        lock (_gate) _users--;
    }

    public void Dispose() => Current.Dispose();

    private sealed class Use(SwappableEmbeddingModel owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release();
        }
    }
}
