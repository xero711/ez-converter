using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MediaConverter.Services;

/// <summary>
/// Stores one stable invitation route token for this Windows user. It is used only
/// with a configured Named Tunnel; Quick Tunnel hostnames remain temporary.
/// </summary>
public sealed class NamedTunnelInvitationIdentityStore
{
    private const string FilePrefix = "EZNAMEDINV1:";
    private readonly string _mutexName;

    public NamedTunnelInvitationIdentityStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
        var identity = SHA256.HashData(Encoding.UTF8.GetBytes(FilePath.ToUpperInvariant()));
        _mutexName = "Local\\EZConverter.NamedTunnelInvitation." + Convert.ToHexString(identity[..16]);
        CryptographicOperations.ZeroMemory(identity);
    }

    public string FilePath { get; }

    public string LoadOrCreateToken()
    {
        return WithMutex(LoadOrCreateCore);
    }

    /// <summary>Rotates the public route token and invalidates previously registered codes.</summary>
    public string RotateToken() => WithMutex(() =>
    {
        var token = CreateToken();
        Save(token, overwrite: true);
        return token;
    });

    private string WithMutex(Func<string> operation)
    {
        using var mutex = new Mutex(initiallyOwned: false, _mutexName);
        var ownsMutex = false;
        try
        {
            try { ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex)
                throw new IOException("固定ホスト名の招待コードを準備できませんでした。もう一度お試しください。");
            return operation();
        }
        finally
        {
            if (ownsMutex) mutex.ReleaseMutex();
        }
    }

    private string LoadOrCreateCore()
    {
        if (!File.Exists(FilePath))
        {
            var token = CreateToken();
            Save(token, overwrite: false);
            return token;
        }

        try
        {
            var protectedText = File.ReadAllText(FilePath, Encoding.UTF8);
            if (!protectedText.StartsWith(FilePrefix, StringComparison.Ordinal))
                throw new InvalidDataException("固定ホスト名の招待識別子ファイルの形式が正しくありません。元のファイルを保持しています。");

            var token = DpapiStringProtector.Unprotect(protectedText[FilePrefix.Length..]);
            if (!IsToken(token))
                throw new InvalidDataException("固定ホスト名の招待識別子ファイルの内容が正しくありません。元のファイルを保持しています。");

            return token.ToLowerInvariant();
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or
                                          System.ComponentModel.Win32Exception or CryptographicException or ArgumentException)
        {
            throw new InvalidDataException("固定ホスト名の招待識別子を読み取れません。元のファイルを保持し、登録済みコードを無効化しないよう新しい識別子は作成しません。", exception);
        }
    }

    private void Save(string token, bool overwrite)
    {
        var directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException("固定ホスト名の招待識別子の保存先を決定できません。");
        Directory.CreateDirectory(directory);
        var temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(FilePrefix);
                writer.Write(DpapiStringProtector.Protect(token));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, FilePath, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static bool IsToken(string token) => token.Length == 64 && token.All(Uri.IsHexDigit);
}
