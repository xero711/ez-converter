using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MediaConverter.Services;

/// <summary>
/// Encodes a live HTTPS receive invitation as an EZC1 bearer code.
/// Anyone who obtains the code can attempt to use that invitation.
/// </summary>
public static class InvitationCodeService
{
    private const string CodePrefix = "EZC1-";
    private const int MaximumUrlLength = 2048;
    private const int MaximumCodeLength = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] LocalHostSuffixes =
    [
        "localhost", "local", "localdomain", "lan", "home", "home.arpa", "internal", "intranet",
        "test", "example", "invalid", "onion"
    ];

    /// <summary>
    /// Validates the invitation shape and public DNS destination, then encodes it.
    /// DNS is checked so hostnames resolving to private or special-use addresses are refused.
    /// </summary>
    public static async Task<string> EncodeAsync(string invitationUrl, CancellationToken cancellationToken = default)
    {
        var uri = ParseInvitationUri(invitationUrl);
        await EnsurePublicDnsDestinationAsync(uri.IdnHost, cancellationToken).ConfigureAwait(false);
        return EncodeValidatedUri(uri);
    }

    /// <summary>
    /// Restores and validates a code before it is used as a network destination.
    /// Call this immediately before sending; structural validation alone does not check DNS.
    /// </summary>
    public static async Task<Uri> RestoreAndValidateAsync(string registrationCode, CancellationToken cancellationToken = default)
    {
        var uri = ParseInvitationUri(Restore(registrationCode));
        await EnsurePublicDnsDestinationAsync(uri.IdnHost, cancellationToken).ConfigureAwait(false);
        return uri;
    }

    /// <summary>
    /// Restores the URL after validating the EZC1 encoding and invitation path.
    /// This method does not perform DNS resolution; use RestoreAndValidateAsync before connecting.
    /// </summary>
    public static string Restore(string registrationCode)
    {
        if (!TryRestore(registrationCode, out var invitationUrl))
        {
            throw new FormatException("EZC1登録コードが無効です。");
        }

        return invitationUrl;
    }

    /// <summary>Validates a code's encoding and HTTPS invitation shape without network access.</summary>
    public static bool TryValidate(string? registrationCode) => TryRestore(registrationCode, out _);

    internal static string CreateStructuralCode(string invitationUrl) =>
        EncodeValidatedUri(ParseInvitationUri(invitationUrl));

    /// <summary>
    /// Structurally validates a code and restores its URL without making a network request.
    /// Use RestoreAndValidateAsync before using the result as a transfer destination.
    /// </summary>
    public static bool TryRestore(string? registrationCode, out string invitationUrl)
    {
        invitationUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(registrationCode))
        {
            return false;
        }

        var code = registrationCode.Trim();
        if (code.Length > MaximumCodeLength || !code.StartsWith(CodePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var payload = code[CodePrefix.Length..];
        if (payload.Length == 0 || payload.Length % 4 == 1 ||
            payload.Any(character => !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            var base64 = payload.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            var restored = StrictUtf8.GetString(bytes);
            var uri = ParseInvitationUri(restored);
            var canonicalCode = EncodeValidatedUri(uri);
            if (!string.Equals(canonicalCode, code, StringComparison.Ordinal))
            {
                return false;
            }

            invitationUrl = uri.AbsoluteUri;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string EncodeValidatedUri(Uri uri)
    {
        var bytes = Encoding.UTF8.GetBytes(uri.AbsoluteUri);
        try
        {
            var payload = Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            return CodePrefix + payload;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static Uri ParseInvitationUri(string invitationUrl)
    {
        if (string.IsNullOrWhiteSpace(invitationUrl) || invitationUrl.Length > MaximumUrlLength)
        {
            throw new FormatException("招待URLの形式が正しくありません。");
        }

        var trimmedUrl = invitationUrl.Trim();
        if (trimmedUrl.Contains('@') || trimmedUrl.Contains('?') || trimmedUrl.Contains('#') ||
            !Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var uri))
        {
            throw new FormatException("招待URLにユーザー情報・クエリ・フラグメントを含めないでください。");
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new FormatException("招待URLにはHTTPSを使い、ユーザー情報・クエリ・フラグメントを含めないでください。");
        }

        if (uri.HostNameType != UriHostNameType.Dns || IPAddress.TryParse(uri.Host, out _))
        {
            throw new FormatException("招待URLのホストにはIPアドレスを指定できません。");
        }

        var host = uri.IdnHost.TrimEnd('.');
        if (!IsPublicDnsNameShape(host))
        {
            throw new FormatException("招待URLにはインターネット上の公開ホスト名を指定してください。");
        }

        var path = uri.AbsolutePath;
        if (path.Contains('%') || !IsInvitationPath(path))
        {
            throw new FormatException("招待URLのパスは /i/ に続く64桁の16進トークンだけにしてください。");
        }

        return uri;
    }

    private static bool IsInvitationPath(string path)
    {
        if (path.Length != 67 || !path.StartsWith("/i/", StringComparison.Ordinal))
        {
            return false;
        }

        for (var index = 3; index < path.Length; index++)
        {
            var character = path[index];
            if (!(character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPublicDnsNameShape(string host)
    {
        if (host.Length is < 3 or > 253 || !host.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        if (LocalHostSuffixes.Any(suffix => host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var labels = host.Split('.');
        return labels.All(label => label.Length is > 0 and <= 63 &&
            label[0] != '-' && label[^1] != '-' &&
            label.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-'));
    }

    private static async Task EnsurePublicDnsDestinationAsync(string host, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SocketException exception)
        {
            throw new FormatException("招待URLのホストを公開DNSで確認できませんでした。", exception);
        }

        if (addresses.Length == 0 || addresses.Any(address => !IsGloballyRoutable(address)))
        {
            throw new FormatException("招待URLのホストがプライベートまたは特殊用途のアドレスを指しています。");
        }
    }

    private static bool IsGloballyRoutable(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsGloballyRoutable(address.MapToIPv4());
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var first = bytes[0];
            var second = bytes[1];
            var third = bytes[2];

            return first is not (0 or 10 or 127) && first < 224 &&
                !(first == 100 && second is >= 64 and <= 127) &&
                !(first == 169 && second == 254) &&
                !(first == 172 && second is >= 16 and <= 31) &&
                !(first == 192 && (second is 0 or 168)) &&
                !(first == 192 && second == 88 && third == 99) &&
                !(first == 198 && (second is 18 or 19 || second == 51 && third == 100)) &&
                !(first == 203 && second == 0 && third == 113);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.Equals(IPAddress.IPv6Any) || IPAddress.IsLoopback(address) ||
            address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.IsIPv6Multicast)
        {
            return false;
        }

        // Only global-unicast space is accepted. Reject documentation, Teredo and 6to4 ranges too.
        return (bytes[0] & 0xE0) == 0x20 &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x02);
    }
}
