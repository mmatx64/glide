using System.Buffers.Binary;

namespace Glide.Core;

public enum MessageKind { Hello = 1, Activate, Move, Button, Key, Release, Ping, Pong }

public readonly record struct Packet(MessageKind Kind, int A = 0, int B = 0, int C = 0, long Stamp = 0)
{
    public const int Size = 32;
    public void Write(Span<byte> bytes)
    {
        bytes.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(bytes, (int)Kind);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], A);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], B);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[12..], C);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[16..], Stamp);
    }
    public static Packet Read(ReadOnlySpan<byte> bytes)
    {
        var kind = (MessageKind)BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (!Enum.IsDefined(kind)) throw new InvalidDataException("Unknown input message.");
        return new(kind, BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]));
    }
}

// Only adjacent motion is replaceable: clicks and key transitions are ordering barriers.
public sealed class Outbox : IDisposable
{
    private readonly Packet[] queue;
    private readonly SemaphoreSlim ready = new(0, 1);
    private int head, count;
    private long coalesced;
    public long Coalesced => Interlocked.Read(ref coalesced);
    public Outbox(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        queue = new Packet[capacity];
    }
    public bool TryAdd(Packet packet)
    {
        lock (queue)
        {
            int tail = (head + count - 1 + queue.Length) % queue.Length;
            if (packet.Kind == MessageKind.Move && count != 0 && queue[tail].Kind == MessageKind.Move)
            { queue[tail] = packet; Interlocked.Increment(ref coalesced); return true; }
            if (count >= queue.Length) return false;
            bool wake = count == 0;
            queue[(head + count) % queue.Length] = packet; count++;
            if (wake) ready.Release();
            return true;
        }
    }
    // Drain only events that are already queued. Never wait to fill a batch:
    // isolated input still wakes the writer immediately, in its original order.
    public async ValueTask<int> TakeBatchAsync(Memory<Packet> destination, CancellationToken cancellation)
    {
        if (destination.IsEmpty) throw new ArgumentException("A batch needs at least one slot.", nameof(destination));
        await ready.WaitAsync(cancellation).ConfigureAwait(false);
        lock (queue)
        {
            int length = Math.Min(destination.Length, count);
            for (int i = 0; i < length; i++) destination.Span[i] = RemoveFirst();
            if (count > 0) ready.Release();
            return length;
        }
    }
    private Packet RemoveFirst()
    {
        var packet = queue[head];
        head = (head + 1) % queue.Length; count--;
        return packet;
    }
    public void Dispose() => ready.Dispose();
}

public static class Coordinates
{
    public static int Normalize(double value, int extent) =>
        (int)Math.Round(Math.Clamp(value / Math.Max(1, extent - 1), 0, 1) * 65535);
    public static double Denormalize(int value, int extent) => Math.Clamp(value, 0, 65535) / 65535.0 * Math.Max(1, extent - 1);
    public static void ValidateDesktop(int width, int height)
    {
        if (width is < 64 or > 65536 || height is < 64 or > 65536)
            throw new InvalidDataException("Invalid desktop dimensions.");
    }
}
