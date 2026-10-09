using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace EZConverter.Sharing;

public sealed partial class TransferServer
{
    private sealed class ShareEntry(LinkInfo info, List<LocalFile> files, string? password)
    {
        public LinkInfo Info { get; } = info;
        public string HostSecret { get; } = TransferFiles.NewToken();
        public List<LocalFile> Files { get; } = files;
        public CancellationTokenSource Cancellation { get; } = new();
        public byte[] Salt { get; } = RandomNumberGenerator.GetBytes(16);
        public byte[]? PasswordHash { get; set; }
        public volatile bool HostPasswordUnlocked;
        public string? InitialPassword { get; set; } = password;
        public ConcurrentDictionary<string, byte> Cookies { get; } = new();
        public ConcurrentDictionary<string, (DateTimeOffset Start, int Count)> Attempts { get; } = new();
        public object HostAttemptGate { get; } = new();
        public DateTimeOffset? HostAttemptWindowStart { get; set; }
        public int HostAttemptCount { get; set; }
    }

    public LinkInfo CreateShare(List<LocalFile> files, TimeSpan lifetime, string? password = null)
    {
        ValidateLifetime(lifetime);
        TransferFiles.ValidateManifest(files.Select(f => f.File).ToList(), long.MaxValue);
        if (_shares.Count >= 20) throw new InvalidOperationException("先に古い共有を停止してください。");
        if (password?.Length > 128) throw new ArgumentException("パスワードが長すぎます。");
        var info = new LinkInfo(TransferFiles.NewToken(), ExpiresAt(lifetime), DeviceName, files.Count, files.Sum(f => f.File.Length), !string.IsNullOrEmpty(password), false);
        var share = new ShareEntry(info, [.. files], password);
        if (info.PasswordRequired) share.PasswordHash = Rfc2898DeriveBytes.Pbkdf2(password!, share.Salt, 100_000, HashAlgorithmName.SHA256, 32);
        share.InitialPassword = null;
        _shares[info.Token] = share;
        return info;
    }

    private void MapShareRoutes(WebApplication app)
    {
        app.MapGet("/s/{token}", (string token) => _shares.TryGetValue(token, out var share) && share.Info.ExpiresAt > DateTimeOffset.UtcNow
            ? Results.Content(RenderBrowserPage("share.html", token), "text/html; charset=utf-8") : Results.Content("共有は終了しました。", "text/plain; charset=utf-8", statusCode: 410));
        app.MapPost("/s/{token}/unlock", async (HttpContext ctx, string token) =>
        {
            if (!GetLiveShare(ctx, token, out var share)) return;
            var attempt = share.Attempts.AddOrUpdate(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", (DateTimeOffset.UtcNow, 1), (_, a) => DateTimeOffset.UtcNow - a.Start > TimeSpan.FromMinutes(1) ? (DateTimeOffset.UtcNow, 1) : (a.Start, a.Count + 1));
            if (attempt.Count > 8 || share.Attempts.Count > 4096 || share.Cookies.Count > 256) { ctx.Response.StatusCode = 429; return; }
            if (ctx.Request.ContentLength > 1024) { ctx.Response.StatusCode = 400; return; }
            var sizeLimit = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeLimit is { IsReadOnly: false }) sizeLimit.MaxRequestBodySize = 1024;
            var request = await ctx.Request.ReadFromJsonAsync<UnlockRequest>(ctx.RequestAborted);
            if (request?.Password is null || request.Password.Length > 128) { ctx.Response.StatusCode = 400; return; }
            var hash = Rfc2898DeriveBytes.Pbkdf2(request.Password, share.Salt, 100_000, HashAlgorithmName.SHA256, 32);
            if (share.PasswordHash is not null && !CryptographicOperations.FixedTimeEquals(hash, share.PasswordHash)) { ctx.Response.StatusCode = 401; return; }
            var cookie = TransferFiles.NewToken();
            share.Cookies[cookie] = 0;
            var cookieOptions = new CookieOptions
            {
                HttpOnly = true,
                Secure = ctx.Request.IsHttps || !TransferClient.IsPrivateHost(ctx.Request.Host.Host),
                SameSite = SameSiteMode.Strict,
                Path = $"/s/{token}"
            };
            if (share.Info.ExpiresAt != DateTimeOffset.MaxValue) cookieOptions.Expires = share.Info.ExpiresAt;
            ctx.Response.Cookies.Append("ez-share", cookie, cookieOptions);
            await ctx.Response.WriteAsJsonAsync(new { unlocked = true }, ctx.RequestAborted);
        });
        app.MapGet("/s/{token}/manifest", (HttpContext ctx, string token) =>
        {
            if (!AuthorizeShare(ctx, token, out var share)) return Results.Empty;
            return Results.Json(new ShareManifest(DeviceName, share.Info.ExpiresAt, share.Files.Select(f => f.File).ToList()));
        });
        app.MapGet("/s/{token}/files/{fileId}", async (HttpContext ctx, string token, string fileId) =>
        {
            if (!AuthorizeShare(ctx, token, out var share)) return;
            var file = share.Files.FirstOrDefault(f => f.File.Id == fileId);
            if (file is null) { ctx.Response.StatusCode = 404; return; }
            await using var stream = OpenSharedFile(file, share, ctx.RequestAborted, bytes => Emit(new(token, "共有", file.File.RelativePath, bytes, file.File.Length, "送信中")));
            ctx.Response.Headers.ETag = $"\"{file.File.Sha256}\"";
            await Results.File(stream, "application/octet-stream", Path.GetFileName(file.File.RelativePath), enableRangeProcessing: true).ExecuteAsync(ctx);
            if (!ctx.RequestAborted.IsCancellationRequested) Emit(new(token, "共有", file.File.RelativePath, stream.LastPosition, file.File.Length, "送信済み"));
        });
        app.MapGet("/s/{token}/all.zip", async (HttpContext ctx, string token) =>
        {
            if (!AuthorizeShare(ctx, token, out var share)) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, share.Cancellation.Token);
            var bodyControl = ctx.Features.Get<IHttpBodyControlFeature>();
            if (bodyControl is not null) bodyControl.AllowSynchronousIO = true;
            ctx.Response.ContentType = "application/zip";
            ctx.Response.Headers.ContentDisposition = "attachment; filename=EZ-Share.zip";
            long completed = 0;
            using (var zip = new ZipArchive(ctx.Response.Body, ZipArchiveMode.Create, true, Encoding.UTF8))
            {
                foreach (var file in share.Files)
                {
                    var baseline = completed;
                    await using var source = OpenSharedFile(file, share, linked.Token, bytes => Emit(new(token, "共有", "まとめてダウンロード", baseline + bytes, share.Info.TotalBytes, "送信中")));
                    await using var destination = zip.CreateEntry(file.File.RelativePath, CompressionLevel.NoCompression).Open();
                    await source.CopyToAsync(destination, linked.Token);
                    completed += file.File.Length;
                }
            }
            Emit(new(token, "共有", "まとめてダウンロード", completed, share.Info.TotalBytes, "完了"));
        });
    }

    private bool UnlockHostShare(string token, string password, out bool rateLimited)
    {
        rateLimited = false;
        if (!_shares.TryGetValue(token, out var share) || share.Info.ExpiresAt <= DateTimeOffset.UtcNow || share.Cancellation.IsCancellationRequested) return false;
        if (password.Length > 128) return false;
        lock (share.HostAttemptGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (share.HostAttemptWindowStart is not { } started || now - started >= TimeSpan.FromMinutes(1))
            {
                share.HostAttemptWindowStart = now;
                share.HostAttemptCount = 0;
            }
            rateLimited = ++share.HostAttemptCount > 8;
        }
        if (rateLimited) return false;
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, share.Salt, 100_000, HashAlgorithmName.SHA256, 32);
        var accepted = share.PasswordHash is null || CryptographicOperations.FixedTimeEquals(hash, share.PasswordHash);
        if (accepted) share.HostPasswordUnlocked = true;
        return accepted;
    }
    private sealed record UnlockRequest(string Password);
    private bool GetLiveShare(HttpContext ctx, string token, out ShareEntry share)
    {
        if (!_shares.TryGetValue(token, out share!) || share.Info.ExpiresAt <= DateTimeOffset.UtcNow || share.Cancellation.IsCancellationRequested)
        { ctx.Response.StatusCode = 410; return false; }
        return true;
    }
    private bool AuthorizeShare(HttpContext ctx, string token, out ShareEntry share)
    {
        if (!GetLiveShare(ctx, token, out share)) return false;
        if (share.Info.PasswordRequired && (!ctx.Request.Cookies.TryGetValue("ez-share", out var cookie) || !share.Cookies.ContainsKey(cookie)))
        { ctx.Response.StatusCode = 401; return false; }
        return true;
    }
    private static TrackedReadStream OpenSharedFile(LocalFile file, ShareEntry share, CancellationToken ct, Action<long> progress)
    {
        var stream = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        if (stream.Length != file.File.Length || file.LastWriteTicks != 0 && File.GetLastWriteTimeUtc(file.SourcePath).Ticks != file.LastWriteTicks) { stream.Dispose(); throw new IOException("共有開始後にファイルが変更されました。"); }
        return new TrackedReadStream(stream, share.Cancellation.Token, ct, progress);
    }
}

internal sealed class TrackedReadStream(Stream inner, CancellationToken shareToken, CancellationToken requestToken, Action<long> progress) : Stream
{
    private readonly CancellationTokenSource _ct = CancellationTokenSource.CreateLinkedTokenSource(shareToken, requestToken);
    private long _last;
    public long LastPosition { get; private set; }
    private void Report() { LastPosition = inner.Position; var now = Environment.TickCount64; if (now - _last > 200) { _last = now; progress(LastPosition); } }
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) { _ct.Token.ThrowIfCancellationRequested(); var read = inner.Read(buffer, offset, count); Report(); return read; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { using var linked = CancellationTokenSource.CreateLinkedTokenSource(_ct.Token, cancellationToken); var read = await inner.ReadAsync(buffer, linked.Token); Report(); return read; }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() => inner.Flush();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); _ct.Dispose(); } base.Dispose(disposing); }
}
