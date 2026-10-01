using System.Collections.Concurrent;

namespace Glide.Core;

public sealed class Inbox(int capacity = 256)
{
    private readonly ConcurrentQueue<(Connection Peer, Packet Packet)> queue = new();
    private readonly int capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private int count;
    public bool TryAdd(Connection peer, Packet packet)
    {
        if (!peer.IsAlive) return false;
        if (Interlocked.Increment(ref count) > capacity)
        {
            Interlocked.Decrement(ref count);
            // Attachment belongs to the input thread; always stop this sender.
            peer.Stop(); return false;
        }
        queue.Enqueue((peer, packet)); return true;
    }
    public bool TryTake(out (Connection Peer, Packet Packet) item)
    {
        if (!queue.TryDequeue(out item)) return false;
        Interlocked.Decrement(ref count); return true;
    }
}
