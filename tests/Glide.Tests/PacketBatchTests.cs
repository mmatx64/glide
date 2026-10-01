using Glide.Core;

internal static class PacketBatchTests
{
    internal static async Task Run(Action<bool, string> check, CancellationToken ct)
    {
        var expected = Enumerable.Range(0, 97).Select(i => (i % 4) switch
        {
            0 => new Packet(MessageKind.Button, 2048, i % 2 == 0 ? 1 : -120),
            1 => new Packet(MessageKind.Move, i, 65535 - i),
            2 => new Packet(MessageKind.Key, 65, 30, i % 3),
            _ => new Packet(MessageKind.Release)
        }).ToArray();
        var bytes = new byte[expected.Length * Packet.Size];
        for (int i = 0; i < expected.Length; i++) expected[i].Write(bytes.AsSpan(i * Packet.Size, Packet.Size));
        foreach (int chunk in new[] { 1, 31, 33, 77, 1024, 4096 })
        {
            using var stream = new FragmentedStream(bytes, chunk);
            var reader = new PacketBatchReader();
            var actual = new List<Packet>();
            while (actual.Count < expected.Length)
            {
                var batch = await reader.ReadAsync(stream, ct);
                if (batch.Length is < 1 or > 32) throw new Exception("Packet reader exceeded its batch bound.");
                actual.AddRange(batch.ToArray());
            }
            check(actual.SequenceEqual(expected), $"packet batches preserve 97 mixed events with {chunk}-byte stream fragments");
        }
        using (var single = new FragmentedStream(bytes[..Packet.Size], 4096))
        {
            var reader = new PacketBatchReader();
            check((await reader.ReadAsync(single, ct)).Length == 1 && single.Reads == 1,
                "one complete packet returns after one read without waiting for a batch");
            bool ended = false;
            try { await reader.ReadAsync(single, ct); } catch (EndOfStreamException) { ended = true; }
            check(ended, "packet reader detects a clean peer disconnect");
        }
        using (var partial = new FragmentedStream(bytes[..(Packet.Size - 1)], 7))
        {
            bool rejected = false;
            try { await new PacketBatchReader().ReadAsync(partial, ct); } catch (EndOfStreamException) { rejected = true; }
            check(rejected, "packet reader rejects a truncated frame");
        }
        using (var invalid = new FragmentedStream(new byte[Packet.Size], 4096))
        {
            bool rejected = false;
            try { await new PacketBatchReader().ReadAsync(invalid, ct); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "batch reader still rejects invalid packet kinds");
        }
        using (var canceled = new FragmentedStream(bytes, 1))
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); bool stopped = false;
            try { await new PacketBatchReader().ReadAsync(canceled, cancellation.Token); } catch (OperationCanceledException) { stopped = true; }
            check(stopped, "fragmented packet reads honor cancellation");
        }
    }

    private sealed class FragmentedStream(byte[] bytes, int chunk) : MemoryStream(bytes)
    {
        internal int Reads { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads++;
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
        }
    }
}
