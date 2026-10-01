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
    private readonly LinkedList<Packet> queue = new();
    private readonly SemaphoreSlim ready = new(0, 1);
    private readonly int capacity;
    public long Coalesced { get; private set; }
    public Outbox(int capacity = 256) => this.capacity = capacity;
    public bool TryAdd(Packet packet)
    {
        lock (queue)
        {
            if (packet.Kind == MessageKind.Move && queue.Last?.Value.Kind == MessageKind.Move)
            { queue.Last.Value = packet; Coalesced++; return true; }
            if (queue.Count >= capacity) return false;
            bool wake = queue.Count == 0;
            queue.AddLast(packet);
            if (wake) ready.Release();
            return true;
        }
    }
    public async ValueTask<Packet> TakeAsync(CancellationToken cancellation)
    {
        await ready.WaitAsync(cancellation).ConfigureAwait(false);
        lock (queue)
        {
            var packet = queue.First!.Value;
            queue.RemoveFirst();
            if (queue.Count > 0) ready.Release();
            return packet;
        }
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
