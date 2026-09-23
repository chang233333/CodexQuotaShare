using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;

namespace CodexQuotaShare.Windows;

/// Stores the relay device secret with Windows DPAPI CurrentUser protection.
/// The JSON envelope contains only the device id and encrypted bytes.
internal static class DeviceSecretStore
{
    private sealed record Envelope(int Version, string DeviceId, string ProtectedSecret);

    public static void Save(string path, string deviceId, string deviceSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceSecret);
        var protectedBytes = Protect(Encoding.UTF8.GetBytes(deviceSecret));
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A directory is required.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Envelope(1, deviceId, Convert.ToBase64String(protectedBytes))));
        File.Move(temporary, path, true);
    }

    public static (string DeviceId, string DeviceSecret) Load(string path)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path))
            ?? throw new InvalidDataException("DEVICE_IDENTITY_INVALID");
        if (envelope.Version != 1 || string.IsNullOrWhiteSpace(envelope.DeviceId) || string.IsNullOrWhiteSpace(envelope.ProtectedSecret))
            throw new InvalidDataException("DEVICE_IDENTITY_INVALID");
        var clear = Unprotect(Convert.FromBase64String(envelope.ProtectedSecret));
        return (envelope.DeviceId, Encoding.UTF8.GetString(clear));
    }

    private static byte[] Protect(byte[] clear)
    {
        return Transform(clear, CryptProtectData);
    }

    private static byte[] Unprotect(byte[] encrypted)
    {
        return Transform(encrypted, CryptUnprotectData);
    }

    private delegate bool CryptTransform(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

    private static byte[] Transform(byte[] input, CryptTransform transform)
    {
        var inputHandle = Marshal.AllocHGlobal(input.Length);
        try
        {
            Marshal.Copy(input, 0, inputHandle, input.Length);
            var inputBlob = new DataBlob { cbData = input.Length, pbData = inputHandle };
            var outputBlob = new DataBlob();
            if (!transform(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref outputBlob))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DPAPI_FAILED");
            try
            {
                var result = new byte[outputBlob.cbData];
                Marshal.Copy(outputBlob.pbData, result, 0, result.Length);
                return result;
            }
            finally { LocalFree(outputBlob.pbData); }
        }
        finally { Marshal.FreeHGlobal(inputHandle); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [SuppressUnmanagedCodeSecurity]
    private static extern bool CryptProtectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [SuppressUnmanagedCodeSecurity]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);
}
