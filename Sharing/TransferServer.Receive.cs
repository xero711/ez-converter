using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace EZConverter.Sharing;

public sealed partial class TransferServer
{
    private sealed class ReceiveFile(TransferFile metadata)
    {
        public TransferFile Metadata { get; } = metadata;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public long Offset;
        public bool Verified;
    }
    private sealed class ReceiveEntry(string id, string secret, IncomingTransfer offer, string stage, DateTimeOffset expires, string? invitation)
    {
        public string Id { get; } = id;
        public string Secret { get; } = secret;
        public IncomingTransfer Offer { get; } = offer;
        public string Stage { get; } = stage;
        public string StageLeasePath { get; } = Path.Combine(Path.GetDirectoryName(stage)!, id + ".lock");
        public FileStream? StageLease;
        public DateTimeOffset ExpiresAt { get; } = expires;
        public string? Invitation { get; } = invitation;
        public Dictionary<string, ReceiveFile> Files { get; } = offer.Files.ToDictionary(f => f.Id, f => new ReceiveFile(f));
        public CancellationTokenSource Cancellation { get; } = new();
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public long TerminalAtUtcTicks;
        public volatile string State = "pending";
        public string? Error;
    }

    private void MapReceiveRoutes(RouteGroupBuilder group, bool includeInfo = true)
    {
        if (includeInfo) group.MapGet("/info", () => Results.Json(Info));
        group.MapPost("/offers", async (HttpContext ctx) =>
        {
            await PruneFinishedReceivesAsync(DateTimeOffset.UtcNow);
            var invitation = ctx.Request.RouteValues["invitation"]?.ToString();
            if (invitation is not null && !IsLiveInvitation(invitation)) { ctx.Response.StatusCode = 410; return; }
            if (_receives.Values.Count(r => r.State is "pending" or "accepted") >= 8 || _receives.Count >= 128)
            { ctx.Response.StatusCode = 429; return; }
            if (ctx.Request.ContentLength > 1024 * 1024) { ctx.Response.StatusCode = 413; return; }
            var sizeLimit = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeLimit is { IsReadOnly: false }) sizeLimit.MaxRequestBodySize = 1024 * 1024;
            var request = await ctx.Request.ReadFromJsonAsync<OfferRequest>(ctx.RequestAborted);
            if (request?.Files is null || string.IsNullOrWhiteSpace(request.Sender) || request.Sender.Length > 64)
            { ctx.Response.StatusCode = 400; return; }
            TransferFiles.ValidateManifest(request.Files, _options.MaxReceiveBytes);
            var id = Guid.NewGuid().ToString("N");
            var secret = TransferFiles.NewToken();
            var offer = new IncomingTransfer(id, request.Sender, ctx.Connection.RemoteIpAddress?.ToString() ?? "不明", request.Files);
            var entry = new ReceiveEntry(id, secret, offer, Path.Combine(_options.ReceiveDirectory, ".ez-incoming", id), DateTimeOffset.UtcNow + _options.OfferLifetime, ctx.Request.RouteValues["invitation"]?.ToString());
            _receives[id] = entry;
            _ = DecideAsync(entry);
            try { if (Incoming is not null) Incoming.Invoke(offer); else offer.Reject(); } catch { offer.Reject(); }
            await ctx.Response.WriteAsJsonAsync(new OfferReceipt(id, secret), ctx.RequestAborted);
        });
        group.MapGet("/offers/{id}", (HttpContext ctx, string id) =>
        {
            if (!AuthorizeReceive(ctx, id, out var entry)) return Results.Empty;
            return Results.Json(new OfferStatus(entry.State, entry.Error, entry.Files.ToDictionary(p => p.Key, p => Interlocked.Read(ref p.Value.Offset))));
        });
        group.MapPut("/offers/{id}/files/{fileId}", async (HttpContext ctx, string id, string fileId) =>
        {
            if (!AuthorizeReceive(ctx, id, out var entry)) return;
            if (entry.State != "accepted" || !entry.Files.TryGetValue(fileId, out var file)) { ctx.Response.StatusCode = 409; return; }
            if (!long.TryParse(ctx.Request.Query["offset"], out var offset) || offset < 0 || ctx.Request.ContentLength is not long length || length < 1 || length > TransferServerOptions.ChunkSize || offset > file.Metadata.Length - length)
            { ctx.Response.StatusCode = 400; return; }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, entry.Cancellation.Token);
            await file.Gate.WaitAsync(linked.Token);
            try
            {
                if (file.Offset != offset || file.Verified) { ctx.Response.StatusCode = 409; return; }
                var path = Path.Combine(entry.Stage, fileId + ".part");
                await using var destination = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 128 * 1024, true);
                destination.SetLength(offset);
                destination.Position = offset;
                var buffer = new byte[128 * 1024];
                long remaining = length;
                try
                {
                    while (remaining > 0)
                    {
                        var read = await ctx.Request.Body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), linked.Token);
                        if (read == 0) throw new EndOfStreamException();
                        await destination.WriteAsync(buffer.AsMemory(0, read), linked.Token);
                        remaining -= read;
                    }
                    await destination.FlushAsync(linked.Token);
                    Interlocked.Exchange(ref file.Offset, offset + length);
                }
                catch { destination.SetLength(offset); throw; }
                Emit(new(id, "受信", entry.Offer.Sender, entry.Files.Values.Sum(f => Interlocked.Read(ref f.Offset)), entry.Offer.TotalBytes, "受信中"));
                await ctx.Response.WriteAsJsonAsync(new { offset = file.Offset }, ctx.RequestAborted);
            }
            finally { file.Gate.Release(); }
        });
        group.MapPost("/offers/{id}/files/{fileId}/complete", async (HttpContext ctx, string id, string fileId) =>
        {
            if (!AuthorizeReceive(ctx, id, out var entry)) return;
            if (entry.State != "accepted" || !entry.Files.TryGetValue(fileId, out var file)) { ctx.Response.StatusCode = 409; return; }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, entry.Cancellation.Token);
            await file.Gate.WaitAsync(linked.Token);
            try
            {
                if (file.Offset != file.Metadata.Length) { ctx.Response.StatusCode = 409; return; }
                var path = Path.Combine(entry.Stage, fileId + ".part");
                if (file.Metadata.Length == 0 && !File.Exists(path)) await File.WriteAllBytesAsync(path, [], linked.Token);
                await using var stream = File.OpenRead(path);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, linked.Token));
                if (!hash.Equals(file.Metadata.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    file.Offset = 0;
                    ctx.Response.StatusCode = 422;
                    entry.Error = $"{file.Metadata.RelativePath} の整合性確認に失敗しました。再送してください。";
                    return;
                }
                file.Verified = true;
                ctx.Response.StatusCode = 204;
            }
            finally { file.Gate.Release(); }
        });
        group.MapPost("/offers/{id}/complete", async (HttpContext ctx, string id) =>
        {
            if (!AuthorizeReceive(ctx, id, out var entry)) return;
            await entry.Gate.WaitAsync(ctx.RequestAborted);
            try
            {
                if (entry.State == "completed") { ctx.Response.StatusCode = 204; return; }
                if (entry.State != "accepted" || entry.Files.Values.Any(f => !f.Verified)) { ctx.Response.StatusCode = 409; return; }
                entry.Cancellation.Token.ThrowIfCancellationRequested();
                var prepared = Path.Combine(entry.Stage, "ready");
                Directory.CreateDirectory(prepared);
                foreach (var file in entry.Files.Values)
                {
                    var target = TransferFiles.UnderDirectory(prepared, file.Metadata.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (!File.Exists(target)) File.Move(Path.Combine(entry.Stage, file.Metadata.Id + ".part"), target, false);
                }
                var output = Path.Combine(_options.ReceiveDirectory, $"EZ-{DateTime.Now:yyyyMMdd-HHmmss}-{id[..8]}");
                await MoveDirectoryWithRetryAsync(prepared, output, ctx.RequestAborted);
                entry.State = "completed";
                MarkReceiveTerminal(entry);
                ReleaseStageLease(entry);
                Emit(new(id, "受信", entry.Offer.Sender, entry.Offer.TotalBytes, entry.Offer.TotalBytes, "完了", output));
                ctx.Response.StatusCode = 204;
            }
            finally { entry.Gate.Release(); }
        });
        group.MapDelete("/offers/{id}", async (HttpContext ctx, string id) =>
        {
            if (!AuthorizeReceive(ctx, id, out var entry)) return Results.Empty;
            CancelReceive(entry);
            await DeleteStagingAsync(entry);
            return Results.NoContent();
        });
    }

    private async Task PruneFinishedReceivesAsync(DateTimeOffset now)
    {
        foreach (var entry in _receives.Values)
        {
            var expired = entry.ExpiresAt <= now;
            var terminalAt = Interlocked.Read(ref entry.TerminalAtUtcTicks);
            var terminalExpired = terminalAt != 0 && now.UtcTicks - terminalAt >= _options.TerminalReceiveRetention.Ticks;
            if (!expired && !terminalExpired) continue;
            if (expired && terminalAt == 0) CancelReceive(entry);
            if (_receives.TryRemove(entry.Id, out var removed)) await DeleteStagingAsync(removed);
        }
    }

    private static void MarkReceiveTerminal(ReceiveEntry entry) =>
        Interlocked.CompareExchange(ref entry.TerminalAtUtcTicks, DateTimeOffset.UtcNow.UtcTicks, 0);

    private static async Task MoveDirectoryWithRetryAsync(string source, string destination, CancellationToken ct)
    {
        const int maximumRetries = 5;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (IOException error) when (attempt < maximumRetries && IsTransientWindowsMoveFailure(error))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), ct);
            }
        }
    }

    private static bool IsTransientWindowsMoveFailure(IOException error) => (error.HResult & 0xFFFF) is 5 or 32 or 33;

    private async Task DecideAsync(ReceiveEntry entry)
    {
        try
        {
            var accepted = await entry.Offer.Decision.WaitAsync(TimeSpan.FromMinutes(2), _lifetime.Token);
            await entry.Gate.WaitAsync(_lifetime.Token);
            try
            {
                if (entry.State != "pending") return;
                if (accepted)
                {
                    var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_options.ReceiveDirectory))!);
                    if (drive.AvailableFreeSpace < entry.Offer.TotalBytes + 64L * 1024 * 1024) throw new IOException("受信先の空き容量が不足しています。");
                    Directory.CreateDirectory(Path.GetDirectoryName(entry.StageLeasePath)!);
                    if ((File.GetAttributes(Path.GetDirectoryName(entry.StageLeasePath)!) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("受信一時フォルダーが安全ではありません。");
                    entry.StageLease = new FileStream(entry.StageLeasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    try { Directory.CreateDirectory(entry.Stage); }
                    catch { ReleaseStageLease(entry); throw; }
                }
                entry.State = accepted ? "accepted" : "rejected";
                if (!accepted) MarkReceiveTerminal(entry);
                Emit(new(entry.Id, "受信", entry.Offer.Sender, 0, entry.Offer.TotalBytes, accepted ? "受信待ち" : "拒否"));
            }
            finally { entry.Gate.Release(); }
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException or UnauthorizedAccessException)
        { entry.State = "rejected"; MarkReceiveTerminal(entry); entry.Error = e is IOException ? "保存先の空き容量・権限を確認してください。" : "受信確認が終了しました。"; Emit(new(entry.Id, "受信", entry.Offer.Sender, 0, entry.Offer.TotalBytes, "拒否", entry.Error)); }
    }
    private bool AuthorizeReceive(HttpContext ctx, string id, out ReceiveEntry entry)
    {
        if (!_receives.TryGetValue(id, out entry!) || entry.ExpiresAt <= DateTimeOffset.UtcNow || entry.Cancellation.IsCancellationRequested)
        { ctx.Response.StatusCode = 410; return false; }
        // An invitation authorizes only the sessions created through that invitation.
        if (ctx.Request.RouteValues["invitation"]?.ToString() != entry.Invitation) { ctx.Response.StatusCode = 403; return false; }
        var supplied = ctx.Request.Headers["X-EZ-Session"].ToString();
        if (supplied.Length != entry.Secret.Length || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(supplied), System.Text.Encoding.ASCII.GetBytes(entry.Secret)))
        { ctx.Response.StatusCode = 403; return false; }
        return true;
    }
    private void CancelReceive(ReceiveEntry entry)
    {
        if (entry.State is "completed" or "cancelled") return;
        entry.State = "cancelled";
        MarkReceiveTerminal(entry);
        entry.Offer.Reject();
        entry.Cancellation.Cancel();
        Emit(new(entry.Id, "受信", entry.Offer.Sender, entry.Files.Values.Sum(f => Interlocked.Read(ref f.Offset)), entry.Offer.TotalBytes, "キャンセル"));
    }
    private static async Task DeleteStagingAsync(ReceiveEntry entry)
    {
        await entry.Gate.WaitAsync();
        foreach (var file in entry.Files.Values) await file.Gate.WaitAsync();
        try { if (Directory.Exists(entry.Stage)) Directory.Delete(entry.Stage, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally
        {
            foreach (var file in entry.Files.Values) file.Gate.Release();
            entry.Gate.Release();
            ReleaseStageLease(entry);
        }
    }

    private void CleanupOrphanedReceiveStages()
    {
        var stagingRoot = Path.Combine(_options.ReceiveDirectory, ".ez-incoming");
        if (!Directory.Exists(stagingRoot)) return;
        try
        {
            if ((File.GetAttributes(stagingRoot) & FileAttributes.ReparsePoint) != 0) return;
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        var legacyCutoffUtc = DateTime.UtcNow - TimeSpan.FromDays(8);
        string[] stages;
        try { stages = Directory.GetDirectories(stagingRoot, "*", SearchOption.TopDirectoryOnly); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        foreach (var stage in stages)
        {
            var id = Path.GetFileName(stage);
            if (!Guid.TryParseExact(id, "N", out _)) continue;

            var leasePath = Path.Combine(stagingRoot, id + ".lock");
            var hasLeaseMarker = File.Exists(leasePath);
            if (!hasLeaseMarker)
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(stage) > legacyCutoffUtc) continue;
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
            }

            FileStream? lease = null;
            try
            {
                if (Directory.Exists(leasePath) || File.Exists(leasePath) &&
                    (File.GetAttributes(leasePath) & FileAttributes.ReparsePoint) != 0)
                    continue;
                lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if ((File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0 || ContainsReparsePoint(stage))
                    continue;
                Directory.Delete(stage, recursive: true);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            finally { lease?.Dispose(); }

            try { File.Delete(leasePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool ContainsReparsePoint(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
            }
        }
        return false;
    }

    private static void ReleaseStageLease(ReceiveEntry entry)
    {
        Interlocked.Exchange(ref entry.StageLease, null)?.Dispose();
        try
        {
            if (Directory.Exists(entry.Stage) && !Directory.EnumerateFileSystemEntries(entry.Stage).Any())
                Directory.Delete(entry.Stage);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (Directory.Exists(entry.Stage)) return;
        try { File.Delete(entry.StageLeasePath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
