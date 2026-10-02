namespace Glide;

internal static class ServiceInstallerTests
{
    internal static void Run(string directory, List<string> lines)
    {
        // Only fixture PowerShell scripts run here. No SCM, enrollment or firewall changes.
        string package = Path.Combine(directory, "installer fixture with spaces " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(package);
        string script = Path.Combine(package, "Install-Service.ps1");
        string marker = Path.Combine(package, "invocation.txt");
        try
        {
            File.WriteAllText(script, """
                param([string]$Action, [switch]$NoStart)
                if ($Action -ne 'Install' -or -not $NoStart) { exit 3 }
                $blocked = $false
                try { $null = Read-Host 'Must not prompt' } catch { $blocked = $true }
                if (-not $blocked) { exit 4 }
                [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'invocation.txt'), 'Install;NoStart;NonInteractive')
                # Exceed pipe capacity on both streams to check bounded concurrent draining.
                [Console]::Out.Write(('O' * 100000))
                [Console]::Error.Write(('E' * 100000))
                exit 0
                """);
            ServiceInstaller.Run(package);
            if (File.ReadAllText(marker) != "Install;NoStart;NonInteractive") throw new Exception("Service installer arguments or script path were incorrect.");
            lines.Add("PASS service installer runs a path with spaces noninteractively with NoStart and drains large output without blocking (fixture only)");
            File.WriteAllText(script, "[Console]::Error.Write('fixture setup failure'); exit 7");
            bool rejected = false;
            try { ServiceInstaller.Run(package); }
            catch (IOException ex) { rejected = ex.Message.Contains("exit 7") && ex.Message.Contains("fixture setup failure"); }
            if (!rejected) throw new Exception("Failed service installer did not propagate its error.");
            lines.Add("PASS service installer reports setup failure for update rollback (fixture only)");
            File.WriteAllText(script, "Start-Sleep -Seconds 30; [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'late.txt'), 'late')");
            rejected = false;
            try { ServiceInstaller.Run(package, TimeSpan.FromSeconds(2)); }
            catch (IOException ex) { rejected = ex.Message.Contains("timed out"); }
            if (!rejected || File.Exists(Path.Combine(package, "late.txt"))) throw new Exception("Service installer deadline failed.");
            lines.Add("PASS service installer timeout terminates the fixture process before rollback");
        }
        finally { Directory.Delete(package, true); }
    }
}
