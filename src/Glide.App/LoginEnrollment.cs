// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Glide contributors

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Glide.Core;

namespace Glide;

// Only the enrolled elevated user exports their existing receiver identity.
// Machine DPAPI permits startup before that user's profile/token is available;
// the surrounding service directory ACL restricts the encrypted copy to admins/SYSTEM.
internal static class LoginEnrollment
{
    internal static string Marker => Path.Combine(ServiceHost.DataDirectory, "LoginControl.enabled");
    internal static string Profile => Path.Combine(ServiceHost.DataDirectory, "LoginReceiver.dat");
    internal static string ProbeData => Path.Combine(ServiceHost.DataDirectory, "LoginDpapi.probe");
    internal static bool Ready(Settings settings) => settings.Role == "Receiver" && settings.AutoConnect
        && settings.StoredIdentity.Length > 0 && settings.HasPeer;
    internal static void SyncIfInstalled(Settings settings)
    {
        if (!string.Equals(Path.GetFullPath(settings.Path), ServiceHost.SettingsPath, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Marker)) return;
        ServiceHost.ValidateInstallLocation();
        ServiceHost.RejectReparsePoints(Marker);
        string owner = File.ReadAllText(Path.Combine(ServiceHost.InstallDirectory, "Owner.sid")).Trim();
        Sync(settings, owner);
    }
    internal static void Sync(Settings settings, string owner)
    {
        foreach (string path in new[] { Profile, Profile + ".tmp" })
            if (File.Exists(path)) ServiceHost.RejectReparsePoints(path);
        if (!Ready(settings)) { File.Delete(Profile); return; }
        using var user = WindowsIdentity.GetCurrent();
        if (user.IsSystem || user.User?.Value != owner || !new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Sign-in control enrollment requires the enrolled elevated Windows account.");
        var encrypted = Encode(settings, owner);
        File.WriteAllBytes(Profile + ".tmp", encrypted);
        File.Move(Profile + ".tmp", Profile, true);
    }
    internal static int Enroll()
    {
        try
        {
            ServiceHost.ValidateInstallLocation();
            SyncIfInstalled(new Settings(ServiceHost.SettingsPath));
            return 0;
        }
        catch (Exception ex) { ServiceHost.Log("Sign-in enrollment failed: " + ex.Message); return 1; }
    }
    internal static int CreateProbe()
    {
        try
        {
            ServiceHost.ValidateInstallLocation();
            using var user = WindowsIdentity.GetCurrent();
            string owner = File.ReadAllText(Path.Combine(ServiceHost.InstallDirectory, "Owner.sid")).Trim();
            if (user.IsSystem || user.User?.Value != owner || !new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Sign-in probe requires the enrolled elevated user.");
            if (File.Exists(ProbeData)) ServiceHost.RejectReparsePoints(ProbeData);
            using var identity = new PairingIdentity();
            byte[] payload = identity.Secret.Concat(identity.Export()).ToArray();
            try { File.WriteAllBytes(ProbeData, Settings.Protect(payload, true, machine: true)); }
            finally { CryptographicOperations.ZeroMemory(payload); }
            return 0;
        }
        catch (Exception ex) { ServiceHost.Log("Sign-in probe setup failed: " + ex.Message); return 1; }
    }
    internal static bool Enabled()
    {
        if (!File.Exists(Marker) || !File.Exists(Profile)) return false;
        ServiceHost.RejectReparsePoints(Marker); ServiceHost.RejectReparsePoints(Profile);
        return Ready(new Settings(ServiceHost.SettingsPath));
    }
    internal static byte[] Encode(Settings settings, string owner)
    {
        if (!Ready(settings)) throw new InvalidOperationException("Pair the receiving service PC and start sharing before enrolling sign-in control.");
        _ = Invitation.Parse(settings.PairingCode);
        using var identity = settings.Identity();
        byte[] pfx = identity.Export();
        byte[] payload = new byte[104 + pfx.Length];
        try
        {
            BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x474c4231);
            SHA256.HashData(Encoding.UTF8.GetBytes(owner)).CopyTo(payload, 4);
            SHA256.HashData(Encoding.UTF8.GetBytes(settings.StoredIdentity)).CopyTo(payload, 36);
            identity.Secret.CopyTo(payload, 68);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(100), pfx.Length);
            pfx.CopyTo(payload, 104);
            return Settings.Protect(payload, true, machine: true);
        }
        finally { CryptographicOperations.ZeroMemory(payload); CryptographicOperations.ZeroMemory(pfx); }
    }
    internal static PairingIdentity Decode(byte[] encrypted, Settings settings, string owner)
    {
        if (!Ready(settings) || encrypted.Length is < 1 or > 65536) throw new InvalidDataException("Sign-in receiver enrollment is unavailable.");
        byte[] payload = Settings.Protect(encrypted, false);
        try
        {
            if (payload.Length < 104 || BinaryPrimitives.ReadUInt32LittleEndian(payload) != 0x474c4231
                || !CryptographicOperations.FixedTimeEquals(payload.AsSpan(4, 32), SHA256.HashData(Encoding.UTF8.GetBytes(owner)))
                || !CryptographicOperations.FixedTimeEquals(payload.AsSpan(36, 32), SHA256.HashData(Encoding.UTF8.GetBytes(settings.StoredIdentity)))
                || BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(100)) != payload.Length - 104)
                throw new InvalidDataException("Sign-in receiver enrollment no longer matches this account/pairing.");
            return new PairingIdentity(payload[104..], payload[68..100]);
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }
}
