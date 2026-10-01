using Glide.Core;

internal static class TestSupport
{
    internal static async Task<Packet> TakeOne(Outbox queue, CancellationToken ct)
    {
        var batch = new Packet[1];
        if (await queue.TakeBatchAsync(batch, ct) != 1) throw new Exception("Expected one queued packet.");
        return batch[0];
    }
    internal static async Task Ignore(Task task) { try { await task; } catch (Exception) { } }
}
