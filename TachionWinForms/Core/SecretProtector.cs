using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Tachion.Core;

public static class SecretProtector
{
    // Windows DPAPI: encrypts data so only the same Windows user account can decrypt it.
    // This avoids adding NuGet packages and keeps the published tachion app tiny.
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var input = ToBlob(bytes);
        var output = new DATA_BLOB();
        try
        {
            if (!CryptProtectData(ref input, "tachion sync token", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, ref output))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var protectedBytes = FromBlob(output);
            return Convert.ToBase64String(protectedBytes);
        }
        finally
        {
            FreeBlob(input);
            FreeBlob(output);
            Array.Clear(bytes, 0, bytes.Length);
        }
    }

    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return string.Empty;
        var bytes = Convert.FromBase64String(protectedBase64);
        var input = ToBlob(bytes);
        var output = new DATA_BLOB();
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, ref output))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var plainBytes = FromBlob(output);
            try { return Encoding.UTF8.GetString(plainBytes); }
            finally { Array.Clear(plainBytes, 0, plainBytes.Length); }
        }
        finally
        {
            FreeBlob(input);
            FreeBlob(output);
            Array.Clear(bytes, 0, bytes.Length);
        }
    }

    private static DATA_BLOB ToBlob(byte[] data)
    {
        var blob = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    private static byte[] FromBlob(DATA_BLOB blob)
    {
        if (blob.cbData <= 0 || blob.pbData == IntPtr.Zero) return Array.Empty<byte>();
        var data = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, data, 0, blob.cbData);
        return data;
    }

    private static void FreeBlob(DATA_BLOB blob)
    {
        if (blob.pbData != IntPtr.Zero)
            LocalFree(blob.pbData);
    }
}
