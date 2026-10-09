using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EZConverter.Sharing;

internal sealed record TurnRelayCredentials(string Url, string SharedSecret)
{
    private static readonly Regex TurnUrl = new(
        @"^(turn|turns):(?:[A-Za-z0-9.-]+|\[[0-9A-Fa-f:]+\])(?::(?<port>[0-9]{1,5}))?(?:\?transport=(?:udp|tcp|tls))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static TurnRelayCredentials Validate(string url, string sharedSecret)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(sharedSecret);
        url = url.Trim();
        var match = TurnUrl.Match(url);
        if (url.Length > 512 || !match.Success || match.Groups["port"].Success &&
            (!int.TryParse(match.Groups["port"].Value, out var port) || port is < 1 or > 65535))
            throw new ArgumentException("TURN URLは turn:host:3478 または turns:host:5349 の形式で指定してください。");
        if (string.IsNullOrWhiteSpace(sharedSecret) || sharedSecret.Length is < 16 or > 4096)
            throw new ArgumentException("TURN共有シークレットは16～4,096文字で入力してください。");
        return new(url, sharedSecret);
    }

    public static (string Username, string Credential, long ExpiresAtUnixSeconds) CreateTemporaryCredential(
        string sharedSecret,
        DateTimeOffset linkExpiresAt,
        DateTimeOffset? now = null)
    {
        var issuedAt = now ?? DateTimeOffset.UtcNow;
        if (linkExpiresAt <= issuedAt) throw new ArgumentException("リンクの有効期限が切れているためTURN資格情報を発行できません。", nameof(linkExpiresAt));
        // Coturn credentials for a manually-stopped link stay short-lived. Reloading
        // the still-live page issues a fresh credential without making the link expire.
        var expiresAt = linkExpiresAt == DateTimeOffset.MaxValue ? issuedAt.AddDays(7) : linkExpiresAt;
        var expiresUnix = expiresAt.ToUnixTimeSeconds();
        var username = $"{expiresUnix}:ezconverter";
        var digest = HMACSHA1.HashData(Encoding.UTF8.GetBytes(sharedSecret), Encoding.UTF8.GetBytes(username));
        return (username, Convert.ToBase64String(digest), expiresUnix);
    }
}
