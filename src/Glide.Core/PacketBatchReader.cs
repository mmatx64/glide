namespace Glide.Core;

// Read whatever plaintext is available; wait only for an incomplete packet,
// never to fill a batch. A returned view is valid until the next ReadAsync.
public sealed class PacketBatchReader
{
    private readonly byte[] bytes = new byte[Packet.Size * 32];
    private readonly Packet[] packets = new Packet[32];
    private int buffered;

    public async ValueTask<ReadOnlyMemory<Packet>> ReadAsync(Stream stream, CancellationToken ct)
    {
        while (buffered < Packet.Size)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(buffered), ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException(buffered == 0 ? "Peer disconnected." : "Peer sent an incomplete input packet.");
            buffered += read;
        }
        int count = buffered / Packet.Size;
        for (int i = 0; i < count; i++) packets[i] = Packet.Read(bytes.AsSpan(i * Packet.Size, Packet.Size));
        int remaining = buffered - count * Packet.Size;
        if (remaining != 0) bytes.AsSpan(count * Packet.Size, remaining).CopyTo(bytes);
        buffered = remaining;
        return packets.AsMemory(0, count);
    }
}
