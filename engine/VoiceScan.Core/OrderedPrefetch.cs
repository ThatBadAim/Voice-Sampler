namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Runs work for a list of items with a bounded number in flight and yields results in input order,
/// so decoding the next file overlaps with embedding the current one.
/// </summary>
public static class OrderedPrefetch
{
    public static async IAsyncEnumerable<TResult> RunAsync<TItem, TResult>(
        IReadOnlyList<TItem> items,
        int depth,
        Func<int, TItem, CancellationToken, Task<TResult>> work,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;
        var inFlight = new Queue<Task<TResult>>();
        int next = 0;

        try
        {
            while (next < items.Count || inFlight.Count > 0)
            {
                while (next < items.Count && inFlight.Count < Math.Max(1, depth))
                {
                    int index = next++;
                    var item = items[index];
                    inFlight.Enqueue(Task.Run(() => work(index, item, token), token));
                }

                yield return await inFlight.Dequeue();
            }
        }
        finally
        {
            // Consumer stopped early or a task faulted: stop prefetched work and wait for it to unwind.
            linked.Cancel();
            foreach (var task in inFlight)
            {
                try { await task; }
                catch { /* already surfaced or intentionally abandoned */ }
            }
        }
    }
}
