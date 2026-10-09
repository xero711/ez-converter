using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EZConverter.Sharing;

public sealed partial class TransferServer : IAsyncDisposable
{
    public const int LocalSendDefaultPort = 53317;
    private readonly TransferServerOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, ShareEntry> _shares = new();
    private readonly ConcurrentDictionary<string, LinkInfo> _invitations = new();
    private readonly ConcurrentDictionary<string, ReceiveEntry> _receives = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset WindowStart, int Count)> _localSendPinAttempts = new(StringComparer.Ordinal);
    private readonly X509Certificate2 _certificate;
    private readonly byte[]? _localSendReceivePinSalt;
    private readonly byte[]? _localSendReceivePinHash;
    private readonly string LocalSendHttpFingerprint = TransferFiles.NewToken();
    private TurnRelayCredentials? _turnRelay;
    private WebApplication? _app;
    private Task? _maintenance;
    private int _disposed;
    public event Action<IncomingTransfer>? Incoming;
    public event Action<TransferProgress>? Progress;
    public event Action<Exception>? Faulted;
    public int HttpPort { get; private set; }
    public int HttpsPort { get; private set; }
    public bool UsesLocalSendDefaultPort { get; private set; }
    public string Fingerprint { get; }
    public string DeviceName => _options.DeviceName;
    public string ReceiveDirectory => _options.ReceiveDirectory;
    public X509Certificate2 LocalSendClientCertificate => _certificate;
    public string LoopbackOrigin => $"http://127.0.0.1:{HttpPort}";
    public DeviceInfo Info => new("ez-share/1", DeviceName, Fingerprint, HttpsPort);
    public LocalSendDeviceInfo LocalSendInfo => new(DeviceName, Fingerprint: Fingerprint, Port: HttpsPort, Protocol: "https", Download: false);

    public TransferServer(TransferServerOptions options)
    {
        _options = options;
        if (!string.IsNullOrEmpty(options.LocalSendReceivePin))
        {
            if (options.LocalSendReceivePin.Length != 6 || options.LocalSendReceivePin.Any(character => character is < '0' or > '9'))
                throw new ArgumentException("LocalSend受信PINは数字6桁で指定してください。", nameof(options));
            _localSendReceivePinSalt = RandomNumberGenerator.GetBytes(16);
            _localSendReceivePinHash = Rfc2898DeriveBytes.Pbkdf2(options.LocalSendReceivePin, _localSendReceivePinSalt, 100_000, HashAlgorithmName.SHA256, 32);
        }
        if (options.TerminalReceiveRetention < TimeSpan.Zero || options.TerminalReceiveRetention > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(options), "終端状態の受信セッション保持時間は0～24時間で指定してください。");
        _turnRelay = options.EnableTurnRelay
            ? TurnRelayCredentials.Validate(options.TurnServerUrl, options.TurnSharedSecret)
            : null;
        _certificate = LocalSendCertificateStore.LoadOrCreate(options.StateDirectory, CreateLocalSendCertificate);
        Fingerprint = Convert.ToHexString(SHA256.HashData(_certificate.RawData));
    }

    private static X509Certificate2 CreateLocalSendCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=EZ Converter", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1"), // Server Authentication
            new("1.3.6.1.5.5.7.3.2")  // Client Authentication for LocalSend peer identity
        }, false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        return new X509Certificate2(generated.Export(X509ContentType.Pfx), (string?)null,
            X509KeyStorageFlags.Exportable);
    }

    public void ConfigureTurnRelay(bool enabled, string url, string sharedSecret)
    {
        Volatile.Write(ref _turnRelay, enabled ? TurnRelayCredentials.Validate(url, sharedSecret) : null);
    }

    public static void ValidateTurnRelaySettings(string url, string sharedSecret) =>
        _ = TurnRelayCredentials.Validate(url, sharedSecret);

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_app is not null) return;
        Directory.CreateDirectory(_options.StateDirectory);
        Directory.CreateDirectory(_options.ReceiveDirectory);
        CleanupOrphanedReceiveStages();
        try
        {
            await StartCoreAsync(_options.HttpPort, _options.HttpsPort, ct);
        }
        catch (Exception error) when (_options.HttpsPort == LocalSendDefaultPort && IsDefaultPortUnavailable(error))
        {
            // LocalSend's legacy discovery probes TCP 53317. Prefer that port,
            // but keep EZ Converter usable if another app already owns it.
            await StartCoreAsync(_options.HttpPort, 0, ct);
        }
    }

    private async Task StartCoreAsync(int httpPort, int httpsPort, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(TransferServer).Assembly.FullName });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = TransferServerOptions.ChunkSize;
            k.Limits.MaxConcurrentConnections = 64;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            if (_options.BindAddress.Equals(IPAddress.Any))
            {
                // ListenAnyIP uses Kestrel's dual-mode IPv6 socket where available,
                // while keeping IPv4 LocalSend clients working as before.
                k.ListenAnyIP(httpPort);
                k.ListenAnyIP(httpsPort, o => o.UseHttps(_certificate, https =>
                {
                    https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                    https.ClientCertificateValidation = (certificate, _, _) => IsValidLocalSendClientCertificate(certificate);
                }));
            }
            else
            {
                k.Listen(_options.BindAddress, httpPort);
                k.Listen(_options.BindAddress, httpsPort, o => o.UseHttps(_certificate, https =>
                {
                    // LocalSend peers authenticate with self-signed identity certificates.
                    // Keep certificates optional so browser/WebRTC endpoints still work.
                    https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                    https.ClientCertificateValidation = (certificate, _, _) => IsValidLocalSendClientCertificate(certificate);
                }));
            }
        });
        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            // This listener only serves LocalSend and same-LAN URLs. Internet
            // invitations use the separate signaling listener and never expose
            // these routes through the public tunnel.
            if (!IsLocalSendConnection(ctx.Connection.LocalIpAddress, ctx.Connection.RemoteIpAddress))
            { ctx.Response.StatusCode = 404; return; }
            var isLocalSendApi = ctx.Request.Path.StartsWithSegments("/api/localsend/v2");
            if (ctx.Request.Path.StartsWithSegments("/api") && !isLocalSendApi && !ctx.Request.IsHttps)
            { ctx.Response.StatusCode = 404; return; }
            // Public forwarding headers never grant access to native LAN endpoints.
            if (ctx.Request.Path.StartsWithSegments("/i"))
            {
                var parts = ctx.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !_invitations.TryGetValue(parts[1], out var invitation) || invitation.ExpiresAt <= DateTimeOffset.UtcNow)
                { ctx.Response.StatusCode = 410; return; }
            }
            try { await next(ctx); }
            catch (OperationCanceledException) { if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 410; else ctx.Abort(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException or BadHttpRequestException)
            {
                if (ctx.Response.HasStarted) ctx.Abort();
                else
                {
                    try { Faulted?.Invoke(e); } catch { }
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsJsonAsync(new { error = "転送データを処理できません。ファイル・容量・接続を確認してください。" });
                }
            }
            catch (Exception e)
            {
                try { Faulted?.Invoke(e); } catch { }
                if (ctx.Response.HasStarted) ctx.Abort();
                else ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        });
        MapShareRoutes(app);
        MapLocalSendRoutes(app);
        MapReceiveRoutes(app.MapGroup("/api/ez/v1"));
        MapReceiveRoutes(app.MapGroup("/i/{invitation}/api"));
        app.MapGet("/i/{invitation}", (string invitation) => IsLiveInvitation(invitation)
            ? Results.Content(RenderBrowserPage("invite.html", invitation), "text/html; charset=utf-8")
            : Results.Content("この招待は終了しました。", "text/plain; charset=utf-8", statusCode: 410));
        app.MapGet("/", () => Results.Text("EZ Converter — 有効な共有リンクを開いてください。", "text/plain; charset=utf-8"));
        try
        {
            await app.StartAsync(ct);
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            HttpPort = new Uri(addresses.Single(x => x.StartsWith("http:"))).Port;
            HttpsPort = new Uri(addresses.Single(x => x.StartsWith("https:"))).Port;
            UsesLocalSendDefaultPort = HttpsPort == LocalSendDefaultPort;
            await StartSignalingAsync(ct);
            _app = app;
            _maintenance = MaintainAsync();
        }
        catch { await StopSignalingAsync(); await app.DisposeAsync(); throw; }
    }

    private static bool IsDefaultPortUnavailable(Exception error) =>
        error is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied } ||
        error.InnerException is not null && IsDefaultPortUnavailable(error.InnerException);

    public static IReadOnlyList<string> LocalAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(IsLocalSendInterface)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address).Where(IsShareableLanAddress)
        .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
        .Select(a => a.ToString()).Distinct().ToArray();

    internal static bool IsLocalSendInterfaceType(NetworkInterfaceType type) => type is
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit or
        NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or
        NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211;

    internal static bool IsLocalSendInterface(NetworkInterface network) =>
        network.OperationalStatus == OperationalStatus.Up && IsLocalSendInterfaceType(network.NetworkInterfaceType);

    internal static IReadOnlyList<string> LocalIpv4Addresses() => LocalAddresses()
        .Where(address => IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        .ToArray();

    private static bool IsShareableLanAddress(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && IsLocalSendAddress(address) ||
        IsUniqueLocalIpv6(address) || address.IsIPv6LinkLocal && address.ScopeId > 0;

    private static bool IsUniqueLocalIpv6(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var bytes = address.GetAddressBytes();
        return (bytes[0] & 0xFE) == 0xFC;
    }

    public static string FormatUriHost(string address)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
            return address;
        var literal = ip.ScopeId == 0
            ? ip.ToString()
            : ip.ToString().Replace($"%{ip.ScopeId}", $"%25{ip.ScopeId}", StringComparison.Ordinal);
        return $"[{literal}]";
    }

    internal static bool IsLocalSendAddress(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal) return true;
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork &&
            (bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
             bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254) ||
             address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xFE) == 0xFC;
    }

    internal static bool IsLocalSendPeerAddress(IPAddress? address)
    {
        return IsLocalSendPeerAddress(address, EnumerateNonLanInterfaceAddresses());
    }

    internal static bool IsLocalSendPeerAddress(IPAddress? address, IEnumerable<IPAddress> nonLanInterfaceAddresses)
    {
        if (address is null) return false;
        ArgumentNullException.ThrowIfNull(nonLanInterfaceAddresses);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (!IsLocalSendAddress(address)) return false;

        // A multicast packet can be observed on another local socket with a
        // source address from a VPN adapter. Do not publish that local overlay
        // address as a LocalSend peer, even when it is in a private range.
        // Disconnected adapters are ignored so stale VPN addresses do not
        // hide an otherwise valid LAN peer using the same address.
        return !nonLanInterfaceAddresses.Any(interfaceAddress =>
        {
            if (interfaceAddress.IsIPv4MappedToIPv6) interfaceAddress = interfaceAddress.MapToIPv4();
            return interfaceAddress.Equals(address);
        });
    }

    private static IEnumerable<IPAddress> EnumerateNonLanInterfaceAddresses()
    {
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up || IsLocalSendInterface(network)) continue;
            IPInterfaceProperties properties;
            try { properties = network.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }
            foreach (var unicast in properties.UnicastAddresses)
                yield return unicast.Address;
        }
    }

    internal static bool IsLocalSendConnection(IPAddress? localAddress, IPAddress? remoteAddress)
    {
        if (localAddress is null || remoteAddress is null) return false;
        if (localAddress.IsIPv4MappedToIPv6) localAddress = localAddress.MapToIPv4();
        if (remoteAddress.IsIPv4MappedToIPv6) remoteAddress = remoteAddress.MapToIPv4();
        if (!IsLocalSendPeerAddress(remoteAddress)) return false;
        if (IPAddress.IsLoopback(localAddress)) return IPAddress.IsLoopback(remoteAddress);
        if (!IsShareableLanAddress(localAddress)) return false;

        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!IsLocalSendInterface(network)) continue;
            IPInterfaceProperties properties;
            try { properties = network.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }
            if (properties.UnicastAddresses.Any(unicast => unicast.Address.Equals(localAddress))) return true;
        }
        return false;
    }

    private static bool IsValidLocalSendClientCertificate(X509Certificate2? certificate)
    {
        if (certificate is null) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(certificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        // LocalSend's self-signed device certificates do not necessarily carry
        // an EKU extension. The TLS handshake requests an optional client
        // certificate; validate its self-signature and validity, not an EKU
        // that the protocol does not require.
        return chain.Build(certificate);
    }

    public string LocalLink(LinkInfo link, string? address = null) => $"http://{FormatUriHost(address ?? LocalAddresses().FirstOrDefault() ?? "127.0.0.1")}:{HttpPort}/{(link.IsInvitation ? "i" : "s")}/{link.Token}";
    public LinkInfo CreateInvitation(TimeSpan lifetime) => CreateInvitation(lifetime, TransferFiles.NewToken());

    /// <summary>Creates an invitation using a caller-persisted 256-bit route token.</summary>
    public LinkInfo CreateInvitation(TimeSpan lifetime, string invitationToken)
    {
        ValidateLifetime(lifetime);
        if (string.IsNullOrWhiteSpace(invitationToken) || invitationToken.Length != 64 || !invitationToken.All(Uri.IsHexDigit))
            throw new ArgumentException("招待トークンには64桁の16進文字列を指定してください。", nameof(invitationToken));
        if (_invitations.Count >= 20) throw new InvalidOperationException("先に古い招待を停止してください。");
        var token = invitationToken.ToLowerInvariant();
        if (_shares.ContainsKey(token)) throw new InvalidOperationException("この招待トークンは別の共有で使用中です。");
        var info = new LinkInfo(token, ExpiresAt(lifetime), DeviceName, 0, 0, false, true);
        if (!_invitations.TryAdd(info.Token, info))
            throw new InvalidOperationException("この招待URLはすでに有効です。");
        if (!_invitationHostSecrets.TryAdd(info.Token, TransferFiles.NewToken()))
        {
            _invitations.TryRemove(info.Token, out _);
            throw new InvalidOperationException("この招待URLの受信セッションを準備できませんでした。");
        }
        return info;
    }
    public void RevokeLink(string token)
    {
        if (_shares.TryRemove(token, out var share)) share.Cancellation.Cancel();
        if (_invitations.TryRemove(token, out _))
            foreach (var entry in _receives.Values.Where(e => e.Invitation == token)) CancelReceive(entry);
        _invitationHostSecrets.TryRemove(token, out _);
        if (_signalRooms.TryRemove(token, out var room)) room.Close();
    }
    public async Task CancelTransferAsync(string id)
    {
        if (_receives.TryGetValue(id, out var entry))
        {
            CancelReceive(entry);
            await DeleteStagingAsync(entry);
        }
        if (_localSendUploads.TryGetValue(id, out var localSend) && localSend.State is not ("completed" or "cancelled"))
        {
            localSend.State = "cancelled";
            localSend.Offer.Reject();
            Emit(new(localSend.SessionId, "受信", localSend.Offer.Sender, 0, localSend.Offer.TotalBytes, "キャンセル"));
            await RemoveLocalSendUploadAsync(localSend);
        }
        if (_shares.TryGetValue(id, out var share)) share.Cancellation.Cancel();
    }
    private static void ValidateLifetime(TimeSpan lifetime)
    {
        if (lifetime != TimeSpan.Zero && (lifetime < TimeSpan.FromSeconds(1) || lifetime > TimeSpan.FromDays(7)))
            throw new ArgumentException("有効期限は停止まで、または1秒～7日の範囲で指定してください。");
    }

    private static DateTimeOffset ExpiresAt(TimeSpan lifetime) =>
        lifetime == TimeSpan.Zero ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow + lifetime;
    private static string ReadPage(string name)
    {
        using var stream = typeof(TransferServer).Assembly.GetManifestResourceStream("EZConverter.Sharing.Web." + name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private string RenderBrowserPage(string name, string token)
    {
        var html = ReadPage(name);
        var relay = Volatile.Read(ref _turnRelay);
        if (relay is null) return html;
        var expiresAt = GetLinkExpiry(token);
        if (expiresAt is null) return html;
        var credential = TurnRelayCredentials.CreateTemporaryCredential(relay.SharedSecret, expiresAt.Value);
        var configuration = System.Text.Json.JsonSerializer.Serialize(new
        {
            server = relay.Url,
            username = credential.Username,
            credential = credential.Credential,
            expiresAt = credential.ExpiresAtUnixSeconds
        });
        const string iceScript = "<script src=\"/ice.js\"></script>";
        var initialized = "<script>window.EZTurnConfiguration=" + configuration + ";</script>" + iceScript;
        return html.Replace(iceScript, initialized, StringComparison.Ordinal);
    }

    private DateTimeOffset? GetLinkExpiry(string token)
    {
        if (_shares.TryGetValue(token, out var share) && share.Info.ExpiresAt > DateTimeOffset.UtcNow && !share.Cancellation.IsCancellationRequested)
            return share.Info.ExpiresAt;
        if (_invitations.TryGetValue(token, out var invitation) && invitation.ExpiresAt > DateTimeOffset.UtcNow)
            return invitation.ExpiresAt;
        return null;
    }
    private void Emit(TransferProgress progress) { try { Progress?.Invoke(progress); } catch { /* UI listeners cannot interrupt a transfer. */ } }
    private async Task MaintainAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                foreach (var entry in _shares.Values.Where(s => s.Info.ExpiresAt <= DateTimeOffset.UtcNow)) RevokeLink(entry.Info.Token);
                foreach (var entry in _invitations.Values.Where(s => s.ExpiresAt <= DateTimeOffset.UtcNow)) RevokeLink(entry.Token);
                await PruneFinishedReceivesAsync(DateTimeOffset.UtcNow);
                await PruneLocalSendUploadsAsync(DateTimeOffset.UtcNow);
            }
        }
        catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        foreach (var entry in _shares.Values) entry.Cancellation.Cancel();
        foreach (var entry in _receives.Values) CancelReceive(entry);
        foreach (var entry in _localSendUploads.Values)
        {
            entry.Cancellation.Cancel();
            entry.Offer.Reject();
        }
        if (_maintenance is not null) await _maintenance;
        await StopSignalingAsync();
        if (_app is not null) { await _app.StopAsync(CancellationToken.None); await _app.DisposeAsync(); }
        foreach (var entry in _receives.Values) await DeleteStagingAsync(entry);
        foreach (var entry in _localSendUploads.Values)
        {
            _localSendUploads.TryRemove(entry.SessionId, out _);
            await DeleteLocalSendStageAsync(entry);
            entry.Cancellation.Dispose();
        }
        _certificate.Dispose();
        _lifetime.Dispose();
    }
}
