namespace Glide;

internal static class LifecycleTests
{
    internal static async Task Run(string directory)
    {
        using var operations = new SharingOperations();
        var pending = operations.Begin();
        bool running = true;
        operations.Stop(() => running = false);
        try { operations.Commit(pending, () => running = true); throw new Exception("Stopped pairing restarted sharing."); }
        catch (OperationCanceledException) { }
        if (running || !pending.Token.IsCancellationRequested) throw new Exception("Emergency stop did not invalidate pending work.");
        try { operations.Begin(resume: false); throw new Exception("Automatic restart escaped a stop."); }
        catch (OperationCanceledException) { }
        var resumed = operations.Begin(); operations.Commit(resumed, () => running = true);
        if (!running) throw new Exception("Explicit resume failed.");

        var settings = new Settings(Path.Combine(directory, "missing-" + Guid.NewGuid().ToString("N"), "Glide.ini"), false);
        try { operations.Pause(() => running = false, settings.Save); throw new Exception("Expected an unwritable settings path."); }
        catch (DirectoryNotFoundException) { }
        if (running || !operations.IsStopped) throw new Exception("Settings failure prevented Pause.");

        for (int i = 0; i < 250; i++)
        {
            var operation = operations.Begin(); running = false;
            var start = Task.Run(() =>
            {
                try { operations.Commit(operation, () => running = true); }
                catch (OperationCanceledException) { }
            });
            var stop = Task.Run(() => operations.Stop(() => running = false));
            await Task.WhenAll(start, stop);
            if (running) throw new Exception("Concurrent completion survived emergency stop.");
        }
    }
}
