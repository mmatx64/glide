// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Glide contributors

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using Glide.Core;
using static Glide.Native;

namespace Glide;

internal static class LoginTests
{
    internal static async Task Run(string directory, List<string> lines)
    {
        using var currentUser = WindowsIdentity.GetCurrent();
        string owner = currentUser.User!.Value;
        var settings = new Settings(Path.Combine(directory, "login-test.ini"), false) { Role = "Receiver" };
        using var identity = new PairingIdentity();
        settings.StoreIdentity(identity); settings.PairingCode = identity.Invitation;
        byte[] encrypted = LoginEnrollment.Encode(settings, owner);
        using (var loaded = LoginEnrollment.Decode(encrypted, settings, owner))
            if (!loaded.Fingerprint.SequenceEqual(identity.Fingerprint) || !loaded.Secret.SequenceEqual(identity.Secret))
                throw new Exception("Pre-login enrollment replaced the pairing identity.");
        void Reject(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or CryptographicException) { return; }
            throw new Exception("Invalid sign-in enrollment was accepted.");
        }
        Reject(() => LoginEnrollment.Decode(encrypted, settings, owner + "-1").Dispose());
        settings.AutoConnect = false;
        Reject(() => LoginEnrollment.Decode(encrypted, settings, owner).Dispose());
        settings.AutoConnect = true; settings.Role = "Controller";
        Reject(() => LoginEnrollment.Decode(encrypted, settings, owner).Dispose());
        settings.Role = "Receiver"; settings.PairingCode = "";
        Reject(() => LoginEnrollment.Decode(encrypted, settings, owner).Dispose());
        settings.PairingCode = identity.Invitation;
        using (var rotated = new PairingIdentity())
        {
            settings.StoreIdentity(rotated);
            Reject(() => LoginEnrollment.Decode(encrypted, settings, owner).Dispose());
        }
        settings.StoreIdentity(identity);
        byte[] corrupt = encrypted.ToArray(); corrupt[^1] ^= 0x55;
        Reject(() => LoginEnrollment.Decode(corrupt, settings, owner).Dispose());
        Reject(() => LoginEnrollment.Decode(new byte[65537], settings, owner).Dispose());
        // A stale settings write must not silently revive the previous identity.
        encrypted = LoginEnrollment.Encode(settings, owner);
        settings.Save();
        using (var loaded = LoginEnrollment.Decode(encrypted, new Settings(settings.Path), owner))
            if (!loaded.Fingerprint.SequenceEqual(identity.Fingerprint)) throw new Exception("Saved service identity binding failed.");
        File.Delete(settings.Path);
        lines.Add("PASS machine-DPAPI sign-in enrollment preserves the existing pairing; owner mismatch, Pause, Controller role, missing pairing, identity rotation, corruption and oversize are rejected");
        _ = ServiceHost.IsLocked((uint)System.Diagnostics.Process.GetCurrentProcess().SessionId);
        lines.Add("PASS actual WTSINFOEX console lock-state ABI/query (no session lock or reboot performed)");
        await DesktopTransition(identity);
        lines.Add("PASS sign-in desktop guard blocks new input after desktop change, stops the peer and releases tracked keys through the cleanup guard (mock input)");
    }

    private static async Task DesktopTransition(PairingIdentity identity)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var connecting = Connection.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
                Invitation.Parse(identity.Invitation), 1920, 1080, timeout.Token);
            var accepting = Connection.AcceptAsync(await listener.AcceptTcpClientAsync(timeout.Token), identity, 1920, 1080, timeout.Token);
            await using var client = await connecting; await using var server = await accepting;
            using var pressed = new ManualResetEventSlim();
            using var failed = new ManualResetEventSlim();
            using var released = new ManualResetEventSlim();
            bool allowed = true;
            int mouseDowns = 0;
            using var worker = new InputWorker(inputs =>
            {
                foreach (var input in inputs)
                {
                    if (input.Type == 1 && (input.Data.Keyboard.Flags & 2) == 0) pressed.Set();
                    if (input.Type == 1 && (input.Data.Keyboard.Flags & 2) != 0) released.Set();
                    if (input.Type == 0 && input.Data.Mouse.Flags == 2) mouseDowns++;
                }
                return (uint)inputs.Length;
            }, inputAllowed: () => Volatile.Read(ref allowed), releaseAllowed: () => true);
            worker.Notice += _ => failed.Set();
            worker.Attach(server, false, true);
            worker.ReceiveBatch(server, new Packet[] { new(MessageKind.Activate, 1000, 1000), new(MessageKind.Key, 65, 30) });
            if (!pressed.Wait(TimeSpan.FromSeconds(3))) throw new Exception("Sign-in transition test did not hold its key.");
            Volatile.Write(ref allowed, false);
            worker.Receive(server, new(MessageKind.Button, 2));
            if (!failed.Wait(TimeSpan.FromSeconds(3)) || !released.Wait(TimeSpan.FromSeconds(3)) || server.IsAlive || mouseDowns != 0)
                throw new Exception("Desktop transition admitted input or left a tracked key held.");
        }
        finally { listener.Stop(); }
    }
}
