// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Glide contributors

using System.Diagnostics;
using System.Text;

namespace Glide;

internal static class ServiceInstaller
{
    internal static void Run(string packageDirectory, TimeSpan? timeout = null)
        => RunAsync(packageDirectory, timeout ?? TimeSpan.FromSeconds(90)).GetAwaiter().GetResult();

    private static async Task RunAsync(string packageDirectory, TimeSpan timeout)
    {
        string script = Path.Combine(packageDirectory, "Install-Service.ps1");
        if (!File.Exists(script)) throw new IOException("The verified release is missing its service installer.");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = packageDirectory, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Action", "Install", "-NoStart" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the service installer.");
        using var deadline = new CancellationTokenSource(timeout);
        // Drain both pipes concurrently so setup cannot block on a full output pipe.
        // Retain only bounded diagnostics; the downloaded script runs locally.
        var stdout = ReadOutput(process.StandardOutput, deadline.Token);
        var stderr = ReadOutput(process.StandardError, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException($"Service setup failed (exit {process.ExitCode}).\n{error}\n{output}".Trim());
        }
        catch (OperationCanceledException)
        {
            throw new IOException("Service setup timed out. The update will restore the previous files and service state.");
        }
        finally
        {
            deadline.Cancel();
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> ReadOutput(StreamReader reader, CancellationToken cancellation)
    {
        const int limit = 8192;
        var result = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
        {
            int count = Math.Min(read, limit - result.Length);
            if (count > 0) result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
