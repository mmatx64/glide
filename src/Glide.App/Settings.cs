using System.Runtime.InteropServices;
using System.Text;
using Glide.Core;

namespace Glide;

internal sealed class Settings
{
    private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
    private string? decryptedCode;
    internal string Path { get; }
    internal string Role { get => Get("Role", "Controller"); set => values["Role"] = value; }
    internal string Host { get => Get("PeerAddress", ""); set => values["PeerAddress"] = value; }
    internal bool RemoteOnRight { get => Get("RemoteSide", "Right") == "Right"; set => values["RemoteSide"] = value ? "Right" : "Left"; }
    internal bool AutoConnect { get => Get("AutoConnect", "True").Equals("True", StringComparison.OrdinalIgnoreCase); set => values["AutoConnect"] = value.ToString(); }
    internal bool DiscoveryEnabled { get => Get("DiscoveryEnabled", "True").Equals("True", StringComparison.OrdinalIgnoreCase); set => values["DiscoveryEnabled"] = value.ToString(); }
    internal string PeerName { get => Get("PeerName", ""); set => values["PeerName"] = value; }
    private string Get(string key, string fallback) => values.GetValueOrDefault(key, fallback);
    internal Settings(string? path = null, bool loadFromDisk = true)
    {
        Path = path ?? System.IO.Path.Combine(AppContext.BaseDirectory, "Glide.ini");
        if (loadFromDisk && File.Exists(Path))
            foreach (var line in File.ReadLines(Path))
            {
                var text = line.Trim();
                if (text.Length == 0 || text[0] is ';' or '#' or '[') continue;
                int separator = text.IndexOf('=');
                if (separator > 0) values[text[..separator].Trim()] = text[(separator + 1)..].Trim();
            }
    }
    internal string PairingCode
    {
        get => decryptedCode ??= values.TryGetValue("PeerCredential", out var data) && data.Length > 0 ? Encoding.UTF8.GetString(Protect(Convert.FromBase64String(data), false)) : "";
        set { values["PeerCredential"] = value.Length == 0 ? "" : Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(value), true)); decryptedCode = value; }
    }
    internal PairingIdentity Identity()
    {
        if (values.TryGetValue("ReceiverIdentity", out var data) && data.Length > 0)
        {
            var bytes = Protect(Convert.FromBase64String(data), false);
            try { return new PairingIdentity(bytes[32..], bytes[..32]); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }
        var identity = new PairingIdentity();
        StoreIdentity(identity);
        return identity;
    }
    internal void StoreIdentity(PairingIdentity identity)
    {
        var bytes = identity.Secret.Concat(identity.Export()).ToArray();
        try { values["ReceiverIdentity"] = Convert.ToBase64String(Protect(bytes, true)); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
    internal void Save()
    {
        values["Role"] = Role; values["PeerAddress"] = Host; values["RemoteSide"] = RemoteOnRight ? "Right" : "Left";
        values["AutoConnect"] = AutoConnect.ToString(); values["DiscoveryEnabled"] = DiscoveryEnabled.ToString();
        string text = "; Glide portable settings. Keep this folder writable.\r\n; Credentials use Windows DPAPI and work only for this Windows user on this PC.\r\n; TCP 24819 input, UDP 24820 discovery, TCP 24821 pairing.\r\n[Glide]\r\n" +
            string.Join("\r\n", values.Select(kv => kv.Key + "=" + kv.Value.Replace("\r", "").Replace("\n", ""))) + "\r\n";
        File.WriteAllText(Path + ".tmp", text, new UTF8Encoding(false));
        File.Move(Path + ".tmp", Path, true);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, string? description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    private static byte[] Protect(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            Blob output;
            bool ok = encrypt ? CryptProtectData(ref input, "Glide pairing", 0, 0, 0, 1, out output) : CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output);
            if (!ok) throw new InvalidOperationException(encrypt ? $"Windows could not protect pairing credentials (error {Marshal.GetLastWin32Error()})." : "Cannot unlock Glide.ini credentials. Pair again under this Windows account using a fresh INI.");
            try { var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { unsafe { new Span<byte>((void*)output.Data, output.Length).Clear(); } LocalFree(output.Data); }
        }
        finally { unsafe { new Span<byte>((void*)input.Data, input.Length).Clear(); } Marshal.FreeHGlobal(input.Data); }
    }
}
