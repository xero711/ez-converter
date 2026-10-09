using System.ComponentModel;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;

namespace MediaConverter.Services;

internal static class DpapiStringProtector
{
    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        out IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob output);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static string Protect(string value) => Convert.ToBase64String(Transform(value, protect: true));

    public static string Unprotect(string value)
    {
        var bytes = Transform(Convert.FromBase64String(value), protect: false);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] Transform(string value, bool protect) => Transform(Encoding.UTF8.GetBytes(value), protect);

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new DataBlob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Marshal.Copy(bytes, 0, input.Data, bytes.Length);
        DataBlob output = default;
        IntPtr description = IntPtr.Zero;
        try
        {
            var succeeded = protect
                ? CryptProtectData(ref input, "EZ Converter protected setting", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref input, out description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
            if (!succeeded) throw new Win32Exception(Marshal.GetLastWin32Error(), "設定値をWindows DPAPIで保護できませんでした。");
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero)
            {
                for (var index = 0; index < input.Size; index++) Marshal.WriteByte(input.Data, index, 0);
            }
            Marshal.FreeHGlobal(input.Data);
            CryptographicOperations.ZeroMemory(bytes);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            if (description != IntPtr.Zero) LocalFree(description);
        }
    }
}
