using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace EZConverter.Sharing;

public sealed partial class TransferServer
{
    private const long LocalSendChunkedBodyOverheadLimit = 1024 * 1024;

    private sealed class LocalSendUploadFile(LocalSendFileMetadata metadata, string safeName, string token, TransferFile displayFile)
    {
        public LocalSendFileMetadata Metadata { get; } = metadata;
        public string SafeName { get; } = safeName;
        public string Token { get; } = token;
        public TransferFile DisplayFile { get; } = displayFile;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Received;
    }

    private sealed class LocalSendUploadEntry(
        string sessionId,
        IncomingTransfer offer,
        string remoteAddress,
        string? clientFingerprint,
        string stage,
        IReadOnlyDictionary<string, LocalSendUploadFile> files)
    {
        public string SessionId { get; } = sessionId;
        public IncomingTransfer Offer { get; } = offer;
        public string RemoteAddress { get; } = remoteAddress;
        public string? ClientFingerprint { get; } = clientFingerprint;
        public string Stage { get; } = stage;
        public IReadOnlyDictionary<string, LocalSendUploadFile> Files { get; } = files;
        public SemaphoreSlim CommitGate { get; } = new(1, 1);
        public CancellationTokenSource Cancellation { get; } = new();
        public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddMinutes(30);
        public volatile string State = "pending";
    }

    private readonly ConcurrentDictionary<string, LocalSendUploadEntry> _localSendUploads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _localSendRateLimits = new(StringComparer.Ordinal);

    private void MapLocalSendRoutes(WebApplication app)
    {
        var prefix = "/api/localsend/v2";
        app.MapPost(prefix + "/register", RegisterLocalSendDeviceAsync);
        app.MapGet(prefix + "/info", () => Results.Json(CreateLocalSendInfo()));
        app.MapPost(prefix + "/prepare-upload", (Delegate)PrepareLocalSendUploadAsync);
        app.MapPost(prefix + "/upload", UploadLocalSendFileAsync);
        app.MapPost(prefix + "/cancel", (Delegate)CancelLocalSendUploadAsync);
    }

    private IResult RegisterLocalSendDeviceAsync(HttpContext context, LocalSendDeviceInfo? info)
    {
        if (info is null || !IsValidLocalSendInfo(info, requireEndpoint: true)) return Results.BadRequest();
        // LocalSend v2 explicitly ignores the JSON fingerprint in HTTPS mode;
        // the TLS certificate is the transport identity. Do not require clients
        // with separate signing/transport certificates to mirror it in JSON.
        return Results.Json(CreateLocalSendRegistrationInfo(context));
    }

    private static bool IsValidLocalSendInfo(LocalSendDeviceInfo info, bool requireEndpoint)
    {
        if (string.IsNullOrWhiteSpace(info.Alias) || info.Alias.Length > 64 || info.Version is null || info.Version.Length > 16 ||
            info.DeviceType is { Length: > 32 } || info.DeviceModel is { Length: > 128 } || info.Fingerprint is { Length: > 256 }) return false;
        return !requireEndpoint || info.Port is >= 1 and <= 65535 && (info.Protocol is "http" or "https");
    }

    private LocalSendDeviceInfo CreateLocalSendInfo() => new(DeviceName, Fingerprint: Fingerprint, Download: false);

    private LocalSendDeviceInfo CreateLocalSendRegistrationInfo(HttpContext context)
    {
        var https = context.Request.IsHttps;
        return new(DeviceName, Fingerprint: https ? Fingerprint : LocalSendHttpFingerprint,
            Port: context.Connection.LocalPort, Protocol: https ? "https" : "http", Download: false);
    }

    private async Task<IResult> PrepareLocalSendUploadAsync(HttpContext context)
    {
        await PruneLocalSendUploadsAsync(DateTimeOffset.UtcNow);
        var remote = NormalizeRemoteAddress(context.Connection.RemoteIpAddress);
        var pinStatus = ValidateLocalSendReceivePin(context, remote);
        if (pinStatus != StatusCodes.Status200OK) return Results.StatusCode(pinStatus);
        if (_localSendRateLimits.Count > 4096)
        {
            foreach (var attempt in _localSendRateLimits.Where(item => item.Value <= DateTimeOffset.UtcNow))
                _localSendRateLimits.TryRemove(attempt.Key, out _);
        }

        if (_localSendUploads.Values.Count(item => item.State is "pending" or "accepted") >= 8 || _localSendUploads.Count >= 128)
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        if (context.Request.ContentLength is > 2 * 1024 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        var bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = 2 * 1024 * 1024;

        LocalSendPrepareUploadRequest? request;
        try { request = await context.Request.ReadFromJsonAsync<LocalSendPrepareUploadRequest>(context.RequestAborted); }
        catch (System.Text.Json.JsonException) { return Results.BadRequest(); }
        if (request is null || !IsValidLocalSendInfo(request.Info, requireEndpoint: true) || request.Files is null ||
            request.Files.Count is < 1 or > 2000) return Results.BadRequest();
        var clientFingerprint = GetClientCertificateFingerprint(context);

        var metadataFiles = new Dictionary<string, LocalSendUploadFile>(StringComparer.Ordinal);
        var displayFiles = new List<TransferFile>(request.Files.Count);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var pair in request.Files)
        {
            var metadata = pair.Value;
            if (metadata is null || string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 256 || metadata.Id != pair.Key ||
                metadata.FileName is null || metadata.FileName.Length is < 1 or > 1024 || metadata.FileType is null || metadata.FileType.Length > 256 ||
                metadata.Size < 0 || metadata.Size > _options.MaxReceiveBytes || totalBytes > _options.MaxReceiveBytes - metadata.Size ||
                metadata.Sha256 is { Length: not 64 } || metadata.Sha256 is { } hash && !hash.All(Uri.IsHexDigit)) return Results.BadRequest();
            totalBytes += metadata.Size;

            var safeName = metadata.FileName.Replace('\\', '/');
            if (safeName.Length is < 1 or > 200) return Results.BadRequest();
            try { TransferFiles.ValidateRelativePath(safeName); }
            catch (InvalidDataException) { return Results.BadRequest(); }
            var separator = safeName.LastIndexOf('/');
            var directory = separator < 0 ? string.Empty : safeName[..(separator + 1)];
            var leafName = safeName[(separator + 1)..];
            var uniqueName = safeName;
            var suffix = 2;
            while (!usedNames.Add(uniqueName))
            {
                var extension = Path.GetExtension(leafName);
                uniqueName = directory + Path.GetFileNameWithoutExtension(leafName) + $" ({suffix++})" + extension;
            }
            try { TransferFiles.ValidateRelativePath(uniqueName); }
            catch (InvalidDataException) { return Results.BadRequest(); }

            var internalFile = new TransferFile(Guid.NewGuid().ToString("N"), uniqueName, metadata.Size,
                metadata.Sha256 ?? new string('0', 64));
            var file = new LocalSendUploadFile(metadata, uniqueName, TransferFiles.NewToken(), internalFile);
            metadataFiles.Add(pair.Key, file);
            displayFiles.Add(internalFile);
        }
        try { TransferFiles.ValidateManifest(displayFiles, _options.MaxReceiveBytes); }
        catch (InvalidDataException) { return Results.BadRequest(); }

        if (_localSendRateLimits.TryGetValue(remote, out var lastOffer) && lastOffer > DateTimeOffset.UtcNow.AddSeconds(-1))
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        _localSendRateLimits[remote] = DateTimeOffset.UtcNow;
        if (_localSendUploads.Count >= 128) return Results.StatusCode(StatusCodes.Status429TooManyRequests);

        var sessionId = Guid.NewGuid().ToString("N");
        var offer = new IncomingTransfer(sessionId, request.Info.Alias, remote, displayFiles);
        var stage = Path.Combine(_options.ReceiveDirectory, ".localsend-incoming", sessionId);
        var entry = new LocalSendUploadEntry(sessionId, offer, remote, clientFingerprint, stage, metadataFiles);
        if (!_localSendUploads.TryAdd(sessionId, entry)) return Results.StatusCode(StatusCodes.Status409Conflict);
        try { if (Incoming is not null) Incoming.Invoke(offer); else offer.Reject(); }
        catch { offer.Reject(); }

        bool accepted;
        try { accepted = await offer.Decision.WaitAsync(TimeSpan.FromMinutes(2), context.RequestAborted); }
        catch (TimeoutException)
        {
            entry.State = "rejected";
            Emit(new(sessionId, "受信", offer.Sender, 0, offer.TotalBytes, "拒否", "受信確認がタイムアウトしました。"));
            await RemoveLocalSendUploadAsync(entry);
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            entry.State = "cancelled";
            await RemoveLocalSendUploadAsync(entry);
            return Results.Empty;
        }

        if (!accepted || entry.Cancellation.IsCancellationRequested)
        {
            entry.State = "rejected";
            Emit(new(sessionId, "受信", offer.Sender, 0, offer.TotalBytes, "拒否"));
            await RemoveLocalSendUploadAsync(entry);
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_options.ReceiveDirectory))!);
            if (drive.AvailableFreeSpace < totalBytes + 64L * 1024 * 1024)
            {
                entry.State = "rejected";
                await RemoveLocalSendUploadAsync(entry);
                return Results.StatusCode(StatusCodes.Status507InsufficientStorage);
            }
            Directory.CreateDirectory(entry.Stage);
            entry.State = "accepted";
            entry.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
            Emit(new(sessionId, "受信", offer.Sender, 0, totalBytes, "受信中"));
            return Results.Json(new LocalSendPrepareUploadResponse(sessionId,
                metadataFiles.ToDictionary(item => item.Key, item => item.Value.Token, StringComparer.Ordinal)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            entry.State = "rejected";
            await RemoveLocalSendUploadAsync(entry);
            Emit(new(sessionId, "受信", offer.Sender, 0, totalBytes, "エラー", "受信先を準備できません。空き容量とフォルダー権限を確認してください。"));
            return Results.StatusCode(StatusCodes.Status507InsufficientStorage);
        }
    }

    private int ValidateLocalSendReceivePin(HttpContext context, string remoteAddress)
    {
        if (_localSendReceivePinHash is null || _localSendReceivePinSalt is null) return StatusCodes.Status200OK;
        var now = DateTimeOffset.UtcNow;
        if (_localSendPinAttempts.Count >= 4096)
            foreach (var expiredAttempt in _localSendPinAttempts.Where(item => item.Value.WindowStart <= now.AddMinutes(-1)))
                _localSendPinAttempts.TryRemove(expiredAttempt.Key, out _);
        if (_localSendPinAttempts.TryGetValue(remoteAddress, out var current) &&
            current.WindowStart > now.AddMinutes(-1) && current.Count >= 8)
            return StatusCodes.Status429TooManyRequests;
        if (_localSendPinAttempts.Count >= 4096 && !_localSendPinAttempts.ContainsKey(remoteAddress))
            return StatusCodes.Status429TooManyRequests;

        var supplied = context.Request.Query["pin"].ToString();
        var valid = supplied.Length == 6 && supplied.All(character => character is >= '0' and <= '9');
        byte[]? candidate = null;
        try
        {
            if (valid)
            {
                candidate = Rfc2898DeriveBytes.Pbkdf2(supplied, _localSendReceivePinSalt, 100_000, HashAlgorithmName.SHA256, 32);
                valid = CryptographicOperations.FixedTimeEquals(candidate, _localSendReceivePinHash);
            }
        }
        finally
        {
            if (candidate is not null) CryptographicOperations.ZeroMemory(candidate);
        }

        if (valid)
        {
            _localSendPinAttempts.TryRemove(remoteAddress, out _);
            return StatusCodes.Status200OK;
        }

        var attempt = _localSendPinAttempts.AddOrUpdate(remoteAddress,
            _ => (now, 1),
            (_, previous) => previous.WindowStart <= now.AddMinutes(-1) ? (now, 1) : (previous.WindowStart, previous.Count + 1));
        return attempt.Count > 8 ? StatusCodes.Status429TooManyRequests : StatusCodes.Status401Unauthorized;
    }

    private async Task UploadLocalSendFileAsync(HttpContext context)
    {
        var sessionId = context.Request.Query["sessionId"].ToString();
        var fileId = context.Request.Query["fileId"].ToString();
        var token = context.Request.Query["token"].ToString();
        if (!_localSendUploads.TryGetValue(sessionId, out var entry) || entry.ExpiresAt <= DateTimeOffset.UtcNow || entry.State != "accepted")
        { context.Response.StatusCode = StatusCodes.Status410Gone; return; }
        if (!entry.Files.TryGetValue(fileId, out var file) || !FixedTokenEquals(file.Token, token) ||
            !string.Equals(entry.RemoteAddress, NormalizeRemoteAddress(context.Connection.RemoteIpAddress), StringComparison.Ordinal) ||
            !SameClientIdentity(entry.ClientFingerprint, GetClientCertificateFingerprint(context)))
        { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        if (context.Request.ContentLength is long requestLength && requestLength != file.Metadata.Size)
        { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        var sizeLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeLimit is { IsReadOnly: false })
        {
            // Kestrel counts HTTP chunk framing toward this limit. LocalSend clients
            // commonly stream uploads with Transfer-Encoding: chunked, so leave a
            // bounded allowance for framing while the handler still enforces the
            // exact advertised payload size and rejects any extra payload byte.
            sizeLimit.MaxRequestBodySize = file.Metadata.Size > long.MaxValue - LocalSendChunkedBodyOverheadLimit
                ? null
                : file.Metadata.Size + LocalSendChunkedBodyOverheadLimit;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, entry.Cancellation.Token, _lifetime.Token);
        await file.Gate.WaitAsync(linked.Token);
        try
        {
            if (file.Received) { context.Response.StatusCode = StatusCodes.Status409Conflict; return; }
            var path = TransferFiles.UnderDirectory(entry.Stage, file.SafeName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                var buffer = new byte[128 * 1024];
                long received = 0;
                while (received < file.Metadata.Size)
                {
                    var count = await context.Request.Body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, file.Metadata.Size - received)), linked.Token);
                    if (count == 0) throw new EndOfStreamException("LocalSendアップロードが途中で終了しました。");
                    await output.WriteAsync(buffer.AsMemory(0, count), linked.Token);
                    hash.AppendData(buffer, 0, count);
                    received += count;
                    Emit(new(entry.SessionId, "受信", entry.Offer.Sender,
                        entry.Files.Values.Sum(item => item.Received ? item.Metadata.Size : ReferenceEquals(item, file) ? received : 0),
                        entry.Offer.TotalBytes, "受信中", file.Metadata.FileName));
                }
                var extra = new byte[1];
                if (await context.Request.Body.ReadAsync(extra, linked.Token) != 0) throw new InvalidDataException("LocalSendアップロードが申告サイズを超えています。");
                await output.FlushAsync(linked.Token);
            }
            if (file.Metadata.Sha256 is { } expected &&
                !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                File.Delete(path);
                return;
            }
            if (file.Metadata.Sha256 is null) _ = hash.GetHashAndReset();
            file.Received = true;
            entry.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30);

            await entry.CommitGate.WaitAsync(linked.Token);
            try
            {
                if (entry.Files.Values.All(item => item.Received) && entry.State == "accepted")
                {
                    var destination = Path.Combine(_options.ReceiveDirectory,
                        $"LocalSend-{DateTime.Now:yyyyMMdd-HHmmss}-{entry.SessionId[..8]}");
                    await MoveDirectoryWithRetryAsync(entry.Stage, destination, linked.Token);
                    entry.State = "completed";
                    entry.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
                    Emit(new(entry.SessionId, "受信", entry.Offer.Sender, entry.Offer.TotalBytes, entry.Offer.TotalBytes, "完了", destination));
                }
            }
            finally { entry.CommitGate.Release(); }
            // LocalSend's upload response body is empty. Some current clients
            // (including the official CLI) require 200 rather than accepting
            // every 2xx status, so use the most broadly interoperable success code.
            context.Response.StatusCode = StatusCodes.Status200OK;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (!context.RequestAborted.IsCancellationRequested) context.Response.StatusCode = StatusCodes.Status410Gone;
            else context.Abort();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(Path.Combine(entry.Stage, file.SafeName)); } catch (IOException) { }
            try { Faulted?.Invoke(error); } catch { }
            context.Response.StatusCode = error is InvalidDataException ? StatusCodes.Status400BadRequest : StatusCodes.Status500InternalServerError;
        }
        finally { file.Gate.Release(); }
    }

    private async Task<IResult> CancelLocalSendUploadAsync(HttpContext context)
    {
        var sessionId = context.Request.Query["sessionId"].ToString();
        if (!_localSendUploads.TryGetValue(sessionId, out var entry)) return Results.NoContent();
        if (!string.Equals(entry.RemoteAddress, NormalizeRemoteAddress(context.Connection.RemoteIpAddress), StringComparison.Ordinal) ||
            !SameClientIdentity(entry.ClientFingerprint, GetClientCertificateFingerprint(context)))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (entry.State is not ("completed" or "cancelled"))
        {
            entry.State = "cancelled";
            entry.Cancellation.Cancel();
            entry.Offer.Reject();
            Emit(new(entry.SessionId, "受信", entry.Offer.Sender, 0, entry.Offer.TotalBytes, "キャンセル"));
            await RemoveLocalSendUploadAsync(entry);
        }
        return Results.NoContent();
    }

    private async Task PruneLocalSendUploadsAsync(DateTimeOffset now)
    {
        foreach (var entry in _localSendUploads.Values.Where(item => item.ExpiresAt <= now))
        {
            if (_localSendUploads.TryRemove(entry.SessionId, out _))
            {
                if (entry.State == "accepted") entry.Cancellation.Cancel();
                if (entry.State is not "completed") Emit(new(entry.SessionId, "受信", entry.Offer.Sender, 0, entry.Offer.TotalBytes, "キャンセル", "LocalSendセッションの期限が切れました。"));
                await DeleteLocalSendStageAsync(entry);
                entry.Cancellation.Dispose();
            }
        }
    }

    private async Task RemoveLocalSendUploadAsync(LocalSendUploadEntry entry)
    {
        if (!_localSendUploads.TryRemove(entry.SessionId, out var removed) || !ReferenceEquals(removed, entry)) return;
        entry.Cancellation.Cancel();
        await DeleteLocalSendStageAsync(entry);
        entry.Cancellation.Dispose();
    }

    private static async Task DeleteLocalSendStageAsync(LocalSendUploadEntry entry)
    {
        var lockedFiles = new List<SemaphoreSlim>(entry.Files.Count);
        try
        {
            foreach (var file in entry.Files.Values)
            {
                await file.Gate.WaitAsync();
                lockedFiles.Add(file.Gate);
            }
            await entry.CommitGate.WaitAsync();
            try { if (Directory.Exists(entry.Stage)) Directory.Delete(entry.Stage, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally { entry.CommitGate.Release(); }
        }
        finally
        {
            for (var index = lockedFiles.Count - 1; index >= 0; index--) lockedFiles[index].Release();
        }
    }

    private static bool FixedTokenEquals(string expected, string actual)
    {
        if (expected.Length != actual.Length) return false;
        return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(actual));
    }

    private static string? GetClientCertificateFingerprint(HttpContext context) => context.Connection.ClientCertificate is { } certificate
        ? Convert.ToHexString(SHA256.HashData(certificate.RawData))
        : null;

    private static bool SameClientIdentity(string? expected, string? actual) =>
        expected is null ? actual is null : actual is not null && expected.Equals(actual, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRemoteAddress(IPAddress? address) =>
        (address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address)?.ToString() ?? "unknown";
}
