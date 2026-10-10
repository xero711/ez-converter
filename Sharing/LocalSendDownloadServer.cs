using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EZConverter.Sharing;

/// <summary>
/// A short-lived LocalSend v2 reverse-download endpoint. The sender's PC is the
/// HTTP server; files are streamed from their original paths and are never relayed
/// through an application-owned service.
/// </summary>
public sealed class LocalSendDownloadServer : IAsyncDisposable
{
    private const string ApiPrefix = "/api/localsend/v2";
    private readonly IReadOnlyDictionary<string, (LocalFile Source, LocalSendFile Metadata)> _files;
    private readonly byte[]? _pinHash;
    private readonly byte[]? _pinSalt;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Start, int Count)> _pinAttempts = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly FileExtensionContentTypeProvider _contentTypes = new();
    private WebApplication? _app;
    private Task? _expiryTask;
    private int _stopped;
    private int _disposed;

    public string Id { get; } = TransferFiles.NewToken();
    public string Alias { get; }
    public DateTimeOffset ExpiresAt { get; }
    public bool PinRequired => _pinHash is not null;
    public int Port { get; private set; }
    public long TotalBytes { get; }
    public int FileCount => _files.Count;
    public event Action<TransferProgress>? Progress;

    private LocalSendDownloadServer(string alias, IReadOnlyList<LocalFile> files, string? pin, TimeSpan lifetime)
    {
        if (string.IsNullOrWhiteSpace(alias) || alias.Length > 64) throw new ArgumentException("表示名は1～64文字で指定してください。", nameof(alias));
        if (pin is not null && (pin.Length != 6 || pin.Any(character => character is < '0' or > '9')))
            throw new ArgumentException("LocalSend互換のPINは数字6桁で指定してください。", nameof(pin));
        if (lifetime < TimeSpan.Zero || lifetime > TimeSpan.FromDays(7)) throw new ArgumentOutOfRangeException(nameof(lifetime));
        TransferFiles.ValidateManifest(files.Select(item => item.File).ToList(), long.MaxValue);

        Alias = alias;
        ExpiresAt = lifetime == TimeSpan.Zero ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow + lifetime;
        TotalBytes = files.Sum(item => item.File.Length);
        _files = BuildMetadata(files);
        if (pin is not null)
        {
            _pinSalt = RandomNumberGenerator.GetBytes(16);
            _pinHash = Rfc2898DeriveBytes.Pbkdf2(pin, _pinSalt, 100_000, HashAlgorithmName.SHA256, 32);
        }
    }

    public static async Task<LocalSendDownloadServer> StartAsync(
        string alias, IReadOnlyList<LocalFile> files, string? pin, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        var server = new LocalSendDownloadServer(alias, files, pin, lifetime);
        try
        {
            await server.StartCoreAsync(cancellationToken);
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    public string LocalLink(string? address = null) => $"http://{TransferServer.FormatUriHost(address ?? TransferServer.LocalAddresses().FirstOrDefault() ?? "127.0.0.1")}:{Port}/";

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(LocalSendDownloadServer).Assembly.FullName });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxConcurrentConnections = 64;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            options.ListenAnyIP(0);
        });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!TransferServer.IsLocalSendConnection(context.Connection.LocalIpAddress, context.Connection.RemoteIpAddress))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            await next(context);
        });

        app.MapGet("/", () => Results.Content(ReadPage("localsend.html"), "text/html; charset=utf-8"));
        app.MapGet("/health", () => IsLive() ? Results.NoContent() : Results.StatusCode(StatusCodes.Status410Gone));
        app.MapGet("/localsend.js", () => Results.Text(ReadPage("localsend.js"), "text/javascript; charset=utf-8"));
        app.MapGet(ApiPrefix + "/info", () => IsLive() ? Results.Json(CreateDeviceInfo()) : Results.StatusCode(StatusCodes.Status410Gone));
        app.MapPost(ApiPrefix + "/prepare-download", PrepareDownloadAsync);
        app.MapGet(ApiPrefix + "/download", DownloadAsync);
        app.MapGet(ApiPrefix + "/download-all", DownloadAllAsync);

        try
        {
            await app.StartAsync(cancellationToken);
            var addresses = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses;
            Port = new Uri(addresses.Single(address => address.StartsWith("http:", StringComparison.Ordinal))).Port;
            _app = app;
            if (ExpiresAt != DateTimeOffset.MaxValue)
                _expiryTask = StopAtExpiryAsync(ExpiresAt - DateTimeOffset.UtcNow);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private IResult PrepareDownloadAsync(HttpContext context)
    {
        if (!IsLive()) return Results.StatusCode(StatusCodes.Status410Gone);
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            return Results.BadRequest(new { error = "このAPIのリクエスト本文は空である必要があります。" });

        var requestedSession = context.Request.Query["sessionId"].ToString();
        if (!string.IsNullOrEmpty(requestedSession))
        {
            if (requestedSession.Length != 64 || !requestedSession.All(Uri.IsHexDigit) || !_sessions.TryGetValue(requestedSession, out var sessionExpiry) || sessionExpiry <= DateTimeOffset.UtcNow)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            _sessions[requestedSession] = SessionExpiry();
            return CreatePrepareResponse(requestedSession);
        }

        if (PinRequired && !ValidatePin(context.Request.Query["pin"].ToString(), context.Connection.RemoteIpAddress?.ToString() ?? "unknown"))
            return Results.StatusCode(_pinAttempts.TryGetValue(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", out var attempt) && attempt.Count > 8
                ? StatusCodes.Status429TooManyRequests : StatusCodes.Status401Unauthorized);

        PruneSessions();
        if (_sessions.Count >= 256) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        var sessionId = TransferFiles.NewToken();
        _sessions[sessionId] = SessionExpiry();
        return CreatePrepareResponse(sessionId);
    }

    private IResult CreatePrepareResponse(string sessionId) => Results.Json(new
    {
        info = CreateDeviceInfo(),
        sessionId,
        files = _files.ToDictionary(pair => pair.Key, pair => pair.Value.Metadata, StringComparer.Ordinal)
    });

    private async Task DownloadAsync(HttpContext context)
    {
        if (!IsLive()) { context.Response.StatusCode = StatusCodes.Status410Gone; return; }
        var sessionId = context.Request.Query["sessionId"].ToString();
        var fileId = context.Request.Query["fileId"].ToString();
        if (!_sessions.TryGetValue(sessionId, out var sessionExpiry) || sessionExpiry <= DateTimeOffset.UtcNow)
        { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        if (!_files.TryGetValue(fileId, out var item)) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }

        var file = item.Source;
        var stream = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        if (stream.Length != file.File.Length || file.LastWriteTicks != 0 && File.GetLastWriteTimeUtc(file.SourcePath).Ticks != file.LastWriteTicks)
        {
            await stream.DisposeAsync();
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        var transferId = Guid.NewGuid().ToString("N");
        var tracked = new TrackedReadStream(stream, _lifetime.Token, context.RequestAborted, bytes =>
            Emit(new(transferId, "送信", item.Metadata.FileName, bytes, file.File.Length, "送信中")));
        if (!_contentTypes.TryGetContentType(item.Metadata.FileName, out var contentType)) contentType = "application/octet-stream";
        try
        {
            await Results.File(tracked, contentType, item.Metadata.FileName, enableRangeProcessing: true).ExecuteAsync(context);
            if (!context.RequestAborted.IsCancellationRequested)
                Emit(new(transferId, "送信", item.Metadata.FileName, tracked.LastPosition, file.File.Length, "完了"));
        }
        finally { await tracked.DisposeAsync(); }
    }

    private async Task DownloadAllAsync(HttpContext context)
    {
        if (!IsLive()) { context.Response.StatusCode = StatusCodes.Status410Gone; return; }
        var sessionId = context.Request.Query["sessionId"].ToString();
        if (!_sessions.TryGetValue(sessionId, out var sessionExpiry) || sessionExpiry <= DateTimeOffset.UtcNow)
        { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }

        var bodyControl = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
        if (bodyControl is not null) bodyControl.AllowSynchronousIO = true;
        context.Response.ContentType = "application/zip";
        context.Response.Headers.ContentDisposition = "attachment; filename=EZ-Converter-Share.zip";
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _lifetime.Token);
        var transferId = Guid.NewGuid().ToString("N");
        long completed = 0;
        try
        {
            using (var archive = new System.IO.Compression.ZipArchive(context.Response.Body, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var item in _files.Values)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    var baseline = completed;
                    var entry = archive.CreateEntry(item.Source.File.RelativePath, System.IO.Compression.CompressionLevel.NoCompression);
                    await using var source = OpenSource(item.Source, linked.Token, bytes =>
                        Emit(new(transferId, "送信", "まとめてダウンロード", baseline + bytes, TotalBytes, "送信中")));
                    await using var destination = entry.Open();
                    await source.CopyToAsync(destination, 128 * 1024, linked.Token);
                    completed += item.Source.File.Length;
                }
            }
            if (!context.RequestAborted.IsCancellationRequested)
                Emit(new(transferId, "送信", "まとめてダウンロード", TotalBytes, TotalBytes, "完了"));
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
    }

    private TrackedReadStream OpenSource(LocalFile file, CancellationToken requestToken, Action<long> progress)
    {
        var stream = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        if (stream.Length != file.File.Length || file.LastWriteTicks != 0 && File.GetLastWriteTimeUtc(file.SourcePath).Ticks != file.LastWriteTicks)
        {
            stream.Dispose();
            throw new IOException("共有開始後にファイルが変更されました。");
        }
        return new TrackedReadStream(stream, _lifetime.Token, requestToken, progress);
    }

    private bool ValidatePin(string pin, string remote)
    {
        if (_pinAttempts.Count >= 4096)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var entry in _pinAttempts.Where(entry => now - entry.Value.Start >= TimeSpan.FromMinutes(1)))
                _pinAttempts.TryRemove(entry.Key, out _);
            if (_pinAttempts.Count >= 4096) return false;
        }
        var attempt = _pinAttempts.AddOrUpdate(remote, (DateTimeOffset.UtcNow, 1), (_, current) =>
            DateTimeOffset.UtcNow - current.Start >= TimeSpan.FromMinutes(1) ? (DateTimeOffset.UtcNow, 1) : (current.Start, current.Count + 1));
        if (attempt.Count > 8 || pin.Length != 6 || pin.Any(character => character is < '0' or > '9')) return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(pin, _pinSalt!, 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actual, _pinHash!);
    }

    private object CreateDeviceInfo() => new
    {
        alias = Alias,
        version = "2.0",
        deviceModel = "Windows",
        deviceType = "desktop",
        fingerprint = Id,
        download = true
    };

    private DateTimeOffset SessionExpiry() => ExpiresAt == DateTimeOffset.MaxValue ? DateTimeOffset.UtcNow.AddHours(12) : ExpiresAt;
    private bool IsLive() => Volatile.Read(ref _stopped) == 0 && ExpiresAt > DateTimeOffset.UtcNow;

    private void PruneSessions()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _sessions.Where(entry => entry.Value <= now)) _sessions.TryRemove(entry.Key, out _);
        foreach (var entry in _pinAttempts.Where(entry => now - entry.Value.Start >= TimeSpan.FromMinutes(1))) _pinAttempts.TryRemove(entry.Key, out _);
    }

    private IReadOnlyDictionary<string, (LocalFile Source, LocalSendFile Metadata)> BuildMetadata(IReadOnlyList<LocalFile> files)
    {
        var result = new Dictionary<string, (LocalFile, LocalSendFile)>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var relativePath = file.File.RelativePath.Replace('\\', '/');
            TransferFiles.ValidateRelativePath(relativePath);
            var leafName = relativePath[(relativePath.LastIndexOf('/') + 1)..];
            var mime = _contentTypes.TryGetContentType(leafName, out var detected) ? detected : "application/octet-stream";
            result.Add(file.File.Id, (file, new LocalSendFile(file.File.Id, relativePath, file.File.Length, mime, file.File.Sha256,
                LocalSendFileMetadataTimes.FromTicks(file.LastWriteTicks, file.LastAccessTicks))));
        }
        return result;
    }

    private static string ReadPage(string name)
    {
        using var stream = typeof(LocalSendDownloadServer).Assembly.GetManifestResourceStream("EZConverter.Sharing.Web." + name)
            ?? throw new InvalidOperationException($"埋め込みWebリソースがありません: {name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private void Emit(TransferProgress progress) { try { Progress?.Invoke(progress); } catch { } }

    private async Task StopAtExpiryAsync(TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, _lifetime.Token);
            await StopCoreAsync();
        }
        catch (OperationCanceledException) { }
    }

    private async Task StopCoreAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        if (!_lifetime.IsCancellationRequested) _lifetime.Cancel();
        if (_app is { } app)
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
            _app = null;
        }
    }

    public async Task StopAsync()
    {
        if (!_lifetime.IsCancellationRequested) _lifetime.Cancel();
        if (_expiryTask is not null) await _expiryTask;
        await StopCoreAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync();
        _lifetime.Dispose();
    }

    private sealed record LocalSendFile(string Id, string FileName, long Size, string FileType, string Sha256, LocalSendFileMetadataTimes? Metadata);
}
