using Glide.Core;

internal static class OutboxBatchTests
{
    internal static async Task Run(Action<bool, string> check, CancellationToken ct)
    {
        using var queue = new Outbox(4);
        var batch = new Packet[3];
        queue.TryAdd(new Packet(MessageKind.Move, 1));
        queue.TryAdd(new Packet(MessageKind.Move, 2));
        queue.TryAdd(new Packet(MessageKind.Key, 65));
        queue.TryAdd(new Packet(MessageKind.Move, 3));
        queue.TryAdd(new Packet(MessageKind.Key, 65, C: 2));
        check(await queue.TakeBatchAsync(batch, ct) == 3 && batch[0].A == 2
            && batch[1].Kind == MessageKind.Key && batch[2].A == 3,
            "batch coalesces only adjacent motion and preserves key barriers");
        // Force the ring to wrap while an earlier key-up is still queued.
        queue.TryAdd(new Packet(MessageKind.Button, 2));
        queue.TryAdd(new Packet(MessageKind.Move, 4));
        queue.TryAdd(new Packet(MessageKind.Move, 5));
        queue.TryAdd(new Packet(MessageKind.Release));
        check(!queue.TryAdd(new Packet(MessageKind.Key, 66)), "wrapped batch queue still rejects reliable overflow");
        check(await queue.TakeBatchAsync(batch, ct) == 3 && batch[0].C == 2
            && batch[1].Kind == MessageKind.Button && batch[2].A == 5,
            "partial drain and ring wrap preserve key-up/button/motion order");
        check((await TestSupport.TakeOne(queue, ct)).Kind == MessageKind.Release, "partial batch keeps its remainder signaled");
        var pending = queue.TakeBatchAsync(batch, ct).AsTask();
        check(!pending.IsCompleted, "empty batch reader waits without spinning");
        queue.TryAdd(new Packet(MessageKind.Key, 67));
        check(await pending == 1 && batch[0].A == 67, "one event wakes batch reader without waiting for a full batch");
        using (var cancel = new CancellationTokenSource())
        {
            var canceled = queue.TakeBatchAsync(batch, cancel.Token).AsTask(); cancel.Cancel();
            try { await canceled; throw new Exception("Empty queue ignored cancellation."); }
            catch (OperationCanceledException) { }
        }
        queue.TryAdd(new Packet(MessageKind.Key, 68));
        check(await queue.TakeBatchAsync(batch, ct) == 1 && batch[0].A == 68, "canceled wait does not consume the next wakeup");
        using var concurrent = new Outbox(64);
        var producer = Task.Run(async () =>
        {
            for (int i = 0; i < 10000; i++)
                while (!concurrent.TryAdd(new Packet(MessageKind.Key, i)))
                { ct.ThrowIfCancellationRequested(); await Task.Yield(); }
        }, ct);
        int next = 0;
        while (next < 10000)
        {
            int count = await concurrent.TakeBatchAsync(batch, ct);
            for (int i = 0; i < count; i++)
                if (batch[i].A != next++) throw new Exception("Concurrent batch delivery lost or reordered an event.");
        }
        await producer;
        check(true, "10000 concurrent reliable events survive repeated batch drains and ring wraps");
    }
}
