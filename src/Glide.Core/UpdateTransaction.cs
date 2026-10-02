// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Glide contributors

namespace Glide.Core;

// The caller owns path protection, process shutdown and restart. Keep rollback
// available until the replacement service has successfully started.
public sealed class UpdateTransaction : IDisposable
{
    private sealed record Replacement(string Target, string Temporary, string Backup, bool Existed);
    private readonly List<Replacement> replacements = [];
    private readonly List<Replacement> applied = [];
    private bool committed;

    public UpdateTransaction(string packageDirectory, IEnumerable<string> destinations)
    {
        try
        {
            foreach (string directory in destinations.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (string name in ReleaseUpdate.PackageFiles.Where(n => n != "Glide.ini"))
            {
                string target = Path.Combine(directory, name);
                string suffix = ".glide-update-" + Guid.NewGuid().ToString("N");
                string temporary = target + suffix + ".tmp", backup = target + suffix + ".bak";
                var item = new Replacement(target, temporary, backup, File.Exists(target));
                replacements.Add(item);
                File.Copy(Path.Combine(packageDirectory, name), temporary, false);
            }
        }
        catch { Dispose(); throw; }
    }

    public void Apply()
    {
        foreach (var item in replacements)
        {
            if (item.Existed) File.Replace(item.Temporary, item.Target, item.Backup);
            else File.Move(item.Temporary, item.Target);
            applied.Add(item);
        }
    }

    public void Rollback()
    {
        List<Exception> failures = [];
        foreach (var item in applied.AsEnumerable().Reverse().ToArray())
        {
            try
            {
                if (item.Existed) File.Replace(item.Backup, item.Target, null);
                else File.Delete(item.Target);
                applied.Remove(item);
            }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (failures.Count != 0) throw new AggregateException("Update rollback failed. Backup files were retained beside the affected files.", failures);
    }

    public void Commit() => committed = true;
    public void Dispose()
    {
        // Preserve backups if rollback cannot complete; never hide its error.
        if (!committed) Rollback();
        foreach (var item in replacements)
        {
            if (File.Exists(item.Temporary)) File.Delete(item.Temporary);
            if (File.Exists(item.Backup)) File.Delete(item.Backup);
        }
    }
}
