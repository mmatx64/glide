namespace Glide;

// Completion and emergency stop share a lock: a stale continuation cannot
// start another session after the stop has returned.
internal sealed class SharingOperations : IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource current = new();
    private long generation;
    private bool stopped, disposed;
    internal bool IsStopped { get { lock (gate) return stopped; } }
    internal readonly record struct Operation(long Generation, CancellationToken Token);
    internal Operation Begin(bool resume = true)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (stopped && !resume) throw new OperationCanceledException("Sharing was stopped.");
            current.Cancel(); current.Dispose(); current = new();
            stopped = false;
            return new(++generation, current.Token);
        }
    }
    internal void Update(Action<bool> update) { lock (gate) update(stopped); }
    internal void Commit(Operation operation, Action action)
    {
        lock (gate)
        {
            operation.Token.ThrowIfCancellationRequested();
            if (disposed || stopped || operation.Generation != generation) throw new OperationCanceledException("Sharing was stopped.");
            action();
        }
    }
    internal void Stop(Action stop)
    {
        lock (gate)
        {
            stopped = true; generation++;
            current.Cancel();
            stop();
        }
    }
    internal void Pause(Action stop, Action save) { Stop(stop); save(); }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; stopped = true; current.Cancel(); current.Dispose();
        }
    }
}
