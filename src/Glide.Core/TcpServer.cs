// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Glide contributors

using System.Net;
using System.Net.Sockets;

namespace Glide.Core;

// Slow handshakes occupy bounded slots rather than the entire accept loop.
public sealed class TcpServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task task;
    private int stopped, disposed;
    public IPEndPoint LocalEndpoint => (IPEndPoint)listener.LocalEndpoint;
    public Task Completion => task;
    public TcpServer(IPEndPoint endpoint, Func<TcpClient, CancellationToken, Task> handle,
        Action<Exception>? failed = null, int capacity = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        listener = new(endpoint); listener.Start(capacity);
        task = Task.Run(async () =>
        {
            var workers = new List<Task>(capacity);
            var ct = lifetime.Token;
            async Task Serve(TcpClient client)
            {
                using (client)
                {
                    try { await handle(client, ct).ConfigureAwait(false); }
                    catch (Exception) when (ct.IsCancellationRequested) { }
                    catch (Exception ex) { failed?.Invoke(ex); }
                }
            }
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    workers.RemoveAll(t => t.IsCompleted);
                    if (workers.Count >= capacity) { client.Dispose(); continue; }
                    workers.Add(Task.Run(() => Serve(client)));
                }
            }
            catch (Exception) when (ct.IsCancellationRequested) { }
            finally
            {
                Stop();
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
        });
    }
    public void Stop()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        lifetime.Cancel(); listener.Stop();
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Stop();
        try { task.GetAwaiter().GetResult(); }
        finally { lifetime.Dispose(); }
    }
}
