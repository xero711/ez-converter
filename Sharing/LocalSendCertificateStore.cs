using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EZConverter.Sharing;

/// <summary>Persists the LocalSend HTTPS identity for this Windows user without storing a plaintext private key.</summary>
internal static class LocalSendCertificateStore
{
    private const int UiForbidden = 0x1;
    private static readonly object Gate = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static X509Certificate2 LoadOrCreate(string stateDirectory, Func<X509Certificate2> createCertificate)
    {
        if (!OperatingSystem.IsWindows()) return createCertificate();

        var directory = Path.GetFullPath(stateDirectory);
        var path = Path.Combine(directory, "localsend-identity.dpapi");
        lock (Gate)
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(path) && TryLoad(path) is { } existing) return existing;

            var created = createCertificate();
            try
            {
                Persist(path, created);
                return created;
            }
            catch
            {
                created.Dispose();
                throw;
            }
        }
    }

    private static X509Certificate2? TryLoad(string path)
    {
        byte[]? protectedPfx = null;
        byte[]? pfx = null;
        try
        {
            protectedPfx = File.ReadAllBytes(path);
            pfx = Transform(protectedPfx, protect: false);
            var certificate = new X509Certificate2(pfx, (string?)null,
                X509KeyStorageFlags.Exportable);
            if (!certificate.HasPrivateKey || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow ||
                certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(30))
            {
                certificate.Dispose();
                return null;
            }
            return certificate;
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            if (pfx is not null) CryptographicOperations.ZeroMemory(pfx);
            if (protectedPfx is not null) CryptographicOperations.ZeroMemory(protectedPfx);
        }
    }

    private static void Persist(string path, X509Certificate2 certificate)
    {
        var pfx = certificate.Export(X509ContentType.Pfx);
        byte[]? protectedPfx = null;
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            protectedPfx = Transform(pfx, protect: true);
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(protectedPfx);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
            if (protectedPfx is not null) CryptographicOperations.ZeroMemory(protectedPfx);
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static byte[] Transform(byte[] data, bool protect)
    {
        if (data.Length == 0) throw new CryptographicException("証明書データが空です。");
        var input = new DataBlob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        DataBlob output = default;
        IntPtr description = IntPtr.Zero;
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            var succeeded = protect
                ? CryptProtectData(ref input, "EZ Converter LocalSend identity", IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref input, out description, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, UiForbidden, out output);
            if (!succeeded)
                throw new CryptographicException("LocalSend端末証明書をWindows DPAPIで保護／復号できません。",
                    new Win32Exception(Marshal.GetLastWin32Error()));

            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            ClearAndFree(input.Data, input.Size, useLocalFree: false);
            ClearAndFree(output.Data, output.Size, useLocalFree: true);
            if (description != IntPtr.Zero) LocalFree(description);
        }
    }

    private static void ClearAndFree(IntPtr memory, int size, bool useLocalFree)
    {
        if (memory == IntPtr.Zero) return;
        for (var index = 0; index < size; index++) Marshal.WriteByte(memory, index, 0);
        if (useLocalFree) LocalFree(memory);
        else Marshal.FreeHGlobal(memory);
    }
}
