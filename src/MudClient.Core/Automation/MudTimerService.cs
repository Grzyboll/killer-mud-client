using System.Collections.Concurrent;

namespace MudClient.Core.Automation;

public sealed class MudTimerService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _timers =
        new(StringComparer.OrdinalIgnoreCase);

    public void StartOnce(
        string name,
        TimeSpan delay,
        Func<CancellationToken, Task> callback)
    {
        Start(name, delay, periodic: false, callback);
    }

    public void StartPeriodic(
        string name,
        TimeSpan interval,
        Func<CancellationToken, Task> callback)
    {
        Start(name, interval, periodic: true, callback);
    }

    public void CancelAll(string? exceptName = null)
    {
        foreach (var timerName in _timers.Keys)
        {
            if (!string.Equals(timerName, exceptName, StringComparison.OrdinalIgnoreCase))
                Cancel(timerName);
        }
    }

    public bool Cancel(string name)
    {
        if (!_timers.TryRemove(name, out var cancellation))
        {
            return false;
        }

        cancellation.Cancel();
        cancellation.Dispose();
        return true;
    }

    private void Start(
        string name,
        TimeSpan interval,
        bool periodic,
        Func<CancellationToken, Task> callback)
    {
        Cancel(name);

        var cancellation = new CancellationTokenSource();
        if (!_timers.TryAdd(name, cancellation))
        {
            cancellation.Dispose();
            throw new InvalidOperationException($"Cannot create timer '{name}'.");
        }

        _ = RunAsync(name, interval, periodic, callback, cancellation);
    }

    private async Task RunAsync(
        string name,
        TimeSpan interval,
        bool periodic,
        Func<CancellationToken, Task> callback,
        CancellationTokenSource cancellation)
    {
        try
        {
            do
            {
                await Task.Delay(interval, cancellation.Token).ConfigureAwait(false);
                await callback(cancellation.Token).ConfigureAwait(false);
            }
            while (periodic && !cancellation.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Expected when a timer is stopped or the application closes.
        }
        finally
        {
            if (_timers.TryGetValue(name, out var current) && ReferenceEquals(current, cancellation))
            {
                _timers.TryRemove(name, out _);
            }

            cancellation.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        CancelAll();
        return ValueTask.CompletedTask;
    }
}
