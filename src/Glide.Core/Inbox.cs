namespace Glide.Core;

public sealed class Inbox(int capacity = 256)
{
    private readonly Queue<(Connection Peer, Packet Packet)> queue = new(capacity);
    private readonly int capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private bool scheduled;
    public bool TryAdd(Connection peer, Packet packet) => TryAdd(peer, packet, out _);
    public bool TryAdd(Connection peer, Packet packet, out bool wake)
        => TryAddBatch(peer, new ReadOnlySpan<Packet>(in packet), out wake);
    public bool TryAddBatch(Connection peer, ReadOnlySpan<Packet> packets, out bool wake)
    {
        wake = false;
        if (!peer.IsAlive) return false;
        if (packets.IsEmpty) return true;
        lock (queue)
        {
            if (packets.Length <= capacity - queue.Count)
            {
                foreach (var packet in packets) queue.Enqueue((peer, packet));
                wake = !scheduled; scheduled = true;
                return true;
            }
        }
        // Attachment belongs to the input thread; always stop this sender.
        // Stop outside the queue lock to avoid a connection/queue lock inversion.
        peer.Stop(); return false;
    }
    public int TakeBatch(Span<(Connection Peer, Packet Packet)> destination)
    {
        if (destination.IsEmpty) throw new ArgumentException("A batch needs at least one slot.", nameof(destination));
        lock (queue)
        {
            int length = Math.Min(destination.Length, queue.Count);
            for (int i = 0; i < length; i++) destination[i] = queue.Dequeue();
            return length;
        }
    }
    // Keep the wake reservation while the consumer applies a batch. Arrivals
    // during injection either use its continuation or schedule a fresh wake.
    public bool CompleteBatch()
    {
        lock (queue)
        {
            scheduled = queue.Count != 0;
            return scheduled;
        }
    }
}
