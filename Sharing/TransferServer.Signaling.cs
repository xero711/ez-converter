using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EZConverter.Sharing;

public sealed partial class TransferServer
{
    private const int MaximumSignalMessageBytes = 64 * 1024;
    private readonly ConcurrentDictionary<string, SignalRoom> _signalRooms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _invitationHostSecrets = new(StringComparer.Ordinal);
    private WebApplication? _signalApp;
    public int SignalPort { get; private set; }
    public string LocalSignalOrigin => $"http://127.0.0.1:{SignalPort}";
    public string PeerLink(LinkInfo link, string? address = null) => $"http://{FormatUriHost(address ?? LocalAddresses().FirstOrDefault() ?? "127.0.0.1")}:{SignalPort}/{(link.IsInvitation ? "i" : "s")}/{link.Token}";

    public Uri HostPage(LinkInfo link)
    {
        if (!IsLiveToken(link.Token)) throw new InvalidOperationException("この接続リンクは終了しています。");
        var secret = GetHostSecret(link.Token);
        if (secret.Length != 64) throw new InvalidOperationException("接続を準備できません。");
        return new Uri($"{LocalSignalOrigin}/host/{link.Token}#{secret}");
    }

    private async Task StartSignalingAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(TransferServer).Assembly.FullName });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = TransferServerOptions.ChunkSize;
            k.Limits.MaxConcurrentConnections = 32;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            k.ListenAnyIP(_options.SignalPort);
        });
        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            if (ctx.Request.Path.StartsWithSegments("/host-api", out var suffix))
            {
                var token = suffix.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                var supplied = ctx.Request.Headers["X-EZ-Host"].ToString();
                var expected = token is null ? string.Empty : GetHostSecret(token);
                if (token is null || !IsLiveToken(token) || supplied.Length != expected.Length || !supplied.All(Uri.IsHexDigit) ||
                    !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(supplied), Encoding.ASCII.GetBytes(expected)))
                { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            }
            await next(ctx);
        });
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.MapGet("/health", () => Results.NoContent());
        app.MapGet("/s/{token}", (string token) => IsLiveShare(token)
            ? Results.Content(RenderBrowserPage("share.html", token), "text/html; charset=utf-8")
            : Results.Content("この共有は終了しました。", "text/plain; charset=utf-8", statusCode: 410));
        app.MapGet("/i/{token}", (string token) => IsLiveInvitation(token)
            ? Results.Content(RenderBrowserPage("invite.html", token), "text/html; charset=utf-8")
            : Results.Content("この招待は終了しました。", "text/plain; charset=utf-8", statusCode: 410));
        app.MapGet("/host/{token}", (HttpContext ctx, string token) =>
        {
            if (ctx.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)) return Results.NotFound();
            return IsLiveToken(token) ? Results.Content(RenderBrowserPage("host.html", token), "text/html; charset=utf-8") : Results.NotFound();
        });
        app.MapPost("/host-api/{token}/authorize", async (HttpContext ctx, string token) =>
        {
            var sizeLimit = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (sizeLimit is { IsReadOnly: false }) sizeLimit.MaxRequestBodySize = 1024;
            var request = await ctx.Request.ReadFromJsonAsync<HostUnlockRequest>(ctx.RequestAborted);
            if (request?.Password is null) { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
            if (!UnlockHostShare(token, request.Password, out var rateLimited))
            { ctx.Response.StatusCode = rateLimited ? StatusCodes.Status429TooManyRequests : StatusCodes.Status401Unauthorized; return; }
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        });
        app.MapGet("/host-api/{token}/info", (string token) =>
        {
            if (_shares.TryGetValue(token, out var share) && share.Info.ExpiresAt > DateTimeOffset.UtcNow && !share.Cancellation.IsCancellationRequested)
                return Results.Json(new { name = share.Info.Name, mode = "share", expiresAt = share.Info.ExpiresAt, passwordRequired = share.Info.PasswordRequired });
            if (_invitations.TryGetValue(token, out var invitation) && invitation.ExpiresAt > DateTimeOffset.UtcNow)
                return Results.Json(new { name = DeviceName, mode = "receive", expiresAt = invitation.ExpiresAt, passwordRequired = false });
            return Results.StatusCode(StatusCodes.Status410Gone);
        });
        app.MapGet("/host-api/{token}/manifest", (string token) =>
        {
            if (!_shares.TryGetValue(token, out var share) || share.Info.ExpiresAt <= DateTimeOffset.UtcNow || share.Cancellation.IsCancellationRequested)
                return Results.StatusCode(StatusCodes.Status410Gone);
            if (share.Info.PasswordRequired && !share.HostPasswordUnlocked) return Results.StatusCode(StatusCodes.Status401Unauthorized);
            return Results.Json(new ShareManifest(share.Info.Name, share.Info.ExpiresAt, share.Files.Select(f => f.File).ToList()));
        });
        app.MapGet("/host-api/{token}/files/{fileId}", async (HttpContext ctx, string token, string fileId) =>
        {
            if (!_shares.TryGetValue(token, out var share) || share.Info.ExpiresAt <= DateTimeOffset.UtcNow || share.Cancellation.IsCancellationRequested)
            { ctx.Response.StatusCode = StatusCodes.Status410Gone; return; }
            if (share.Info.PasswordRequired && !share.HostPasswordUnlocked) { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
            var file = share.Files.FirstOrDefault(f => f.File.Id == fileId);
            if (file is null) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            if (!long.TryParse(ctx.Request.Query["offset"], out var offset) || !long.TryParse(ctx.Request.Query["length"], out var length) ||
                offset < 0 || length < 0 || length > TransferServerOptions.ChunkSize || offset > file.File.Length - length)
            { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
            await using var source = OpenSharedFile(file, share, ctx.RequestAborted, bytes => Emit(new(token, "共有", file.File.RelativePath, bytes, file.File.Length, "送信中")));
            source.Position = offset;
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength = length;
            var buffer = new byte[128 * 1024];
            var remaining = length;
            while (remaining > 0)
            {
                var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ctx.RequestAborted);
                if (count == 0) throw new EndOfStreamException();
                await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, count), ctx.RequestAborted);
                remaining -= count;
            }
        });
        MapReceiveRoutes(app.MapGroup("/host-api/{invitation}"), includeInfo: false);
        app.MapGet("/rtc/{token}", HandleSignalingAsync);
        app.MapGet("/ice.js", () => Results.Text(ReadPage("ice.js"), "text/javascript; charset=utf-8"));
        app.MapGet("/transfer-window.js", () => Results.Text(ReadPage("transfer-window.js"), "text/javascript; charset=utf-8"));
        app.MapGet("/sha256.js", () => Results.Text(ReadPage("sha256.js"), "text/javascript; charset=utf-8"));
        app.MapGet("/host.js", () => Results.Text(ReadPage("host.js"), "text/javascript; charset=utf-8"));
        app.MapGet("/share.js", () => Results.Text(ReadPage("share.js"), "text/javascript; charset=utf-8"));
        app.MapGet("/invite.js", () => Results.Text(ReadPage("invite.js"), "text/javascript; charset=utf-8"));
        try
        {
            await app.StartAsync(ct);
            var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.Single();
            SignalPort = new Uri(address).Port;
            _signalApp = app;
        }
        catch { await app.DisposeAsync(); throw; }
    }

    private bool IsLiveToken(string token) => IsLiveShare(token) || IsLiveInvitation(token);
    private bool IsLiveShare(string token) => _shares.TryGetValue(token, out var share) && share.Info.ExpiresAt > DateTimeOffset.UtcNow && !share.Cancellation.IsCancellationRequested;
    private bool IsLiveInvitation(string token) => _invitations.TryGetValue(token, out var invitation) && invitation.ExpiresAt > DateTimeOffset.UtcNow;
    private string GetHostSecret(string token) => _shares.TryGetValue(token, out var share) ? share.HostSecret : _invitationHostSecrets.TryGetValue(token, out var secret) ? secret : string.Empty;
    private sealed record HostUnlockRequest(string Password);

    private async Task HandleSignalingAsync(HttpContext ctx, string token)
    {
        if (!ctx.WebSockets.IsWebSocketRequest || !IsLiveToken(token))
        { ctx.Response.StatusCode = IsLiveToken(token) ? StatusCodes.Status400BadRequest : StatusCodes.Status410Gone; return; }
        var role = ctx.Request.Query["role"].ToString();
        if (role is not ("host" or "guest")) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        if (role == "host" && !await AuthenticateHostAsync(token, socket, ctx.RequestAborted))
        {
            await CloseSocketAsync(socket, WebSocketCloseStatus.PolicyViolation, "host authentication failed");
            return;
        }
        var room = _signalRooms.GetOrAdd(token, _ => new SignalRoom());
        SignalRoom.PendingGuest? queuedGuest = null;
        Task<string?>? queuedReceive = null;
        try
        {
            bool paired;
            if (role == "host")
            {
                var join = room.JoinHost(socket);
                if (!join.Accepted)
                {
                    await CloseSocketAsync(socket, WebSocketCloseStatus.PolicyViolation, "host already connected");
                    return;
                }
                paired = join.Paired;
                if (join.PromotedGuest is not null)
                {
                    room.Activate(join.PromotedGuest);
                    await SendPeerReadyAsync(room, ctx.RequestAborted);
                    paired = false;
                }
            }
            else
            {
                var join = room.JoinGuest(socket);
                if (!join.Accepted)
                {
                    try
                    {
                        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"queue-full\"}"),
                            WebSocketMessageType.Text, true, ctx.RequestAborted);
                    }
                    catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
                    await CloseSocketAsync(socket, WebSocketCloseStatus.PolicyViolation, "receiver queue is full");
                    return;
                }

                queuedGuest = join.Pending;
                paired = join.Paired;
                if (queuedGuest is not null)
                {
                    var queuedMessage = JsonSerializer.Serialize(new { type = "queued", position = join.Position });
                    await socket.SendAsync(Encoding.UTF8.GetBytes(queuedMessage),
                        WebSocketMessageType.Text, true, ctx.RequestAborted);
                    queuedReceive = ReceiveTextAsync(socket, ctx.RequestAborted);
                    while (true)
                    {
                        var completed = await Task.WhenAny(queuedGuest.Admission.Task, queuedReceive);
                        if (completed == queuedGuest.Admission.Task)
                        {
                            if (!await queuedGuest.Admission.Task) return;
                            break;
                        }

                        var waitingMessage = await queuedReceive;
                        if (queuedGuest.Admission.Task.IsCompleted)
                        {
                            if (!await queuedGuest.Admission.Task) return;
                            break;
                        }
                        if (waitingMessage is null) return;
                        await CloseSocketAsync(socket, WebSocketCloseStatus.PolicyViolation, "signaling messages are not allowed while queued");
                        return;
                    }
                    paired = false; // The room owner sends peer-ready when it promotes this queued socket.
                }
            }

            if (paired) await SendPeerReadyAsync(room, ctx.RequestAborted);
            try { await ReceiveAndForwardAsync(room, role, socket, ctx.RequestAborted, queuedReceive); }
            catch (WebSocketException) { await CloseSocketAsync(socket, WebSocketCloseStatus.PolicyViolation, "signaling messages only"); }
        }
        catch (WebSocketException)
        {
            await CloseSocketAsync(socket, WebSocketCloseStatus.PolicyViolation, "signaling messages only");
        }
        finally
        {
            var departure = room.Leave(role, socket, queuedGuest);
            if (departure.Peer is not null)
            {
                try { await room.SendAsync(role == "host" ? "guest" : "host", "{\"type\":\"peer-left\"}", CancellationToken.None); }
                catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
            }
            if (departure.PromotedGuest is not null)
            {
                try
                {
                    room.Activate(departure.PromotedGuest);
                    await SendPeerReadyAsync(room, CancellationToken.None);
                }
                catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException)
                {
                    room.Activate(departure.PromotedGuest, admitted: false);
                }
            }
        }
    }

    private static async Task SendPeerReadyAsync(SignalRoom room, CancellationToken ct)
    {
        await room.SendAsync("host", "{\"type\":\"peer-ready\"}", ct);
        await room.SendAsync("guest", "{\"type\":\"peer-ready\"}", ct);
    }

    private async Task<bool> AuthenticateHostAsync(string token, WebSocket socket, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var text = await ReceiveTextAsync(socket, timeout.Token);
            if (text is null) return false;
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
                !root.TryGetProperty("type", out var type) || type.GetString() != "auth" ||
                !root.TryGetProperty("secret", out var secretElement)) return false;
            var secret = secretElement.GetString();
            var expected = GetHostSecret(token);
            if (secret is null || secret.Length != expected.Length || !secret.All(Uri.IsHexDigit)) return false;
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(secret), Convert.FromHexString(expected));
        }
        catch (Exception e) when (e is OperationCanceledException or JsonException or FormatException or InvalidOperationException or WebSocketException) { return false; }
    }

    private async Task ReceiveAndForwardAsync(SignalRoom room, string role, WebSocket socket, CancellationToken ct, Task<string?>? pendingRead = null)
    {
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var message = pendingRead is null ? await ReceiveTextAsync(socket, ct) : await pendingRead;
            pendingRead = null;
            if (message is null) break;
            if (!IsAllowedSignalMessage(message))
            { await CloseSocketAsync(socket, WebSocketCloseStatus.PolicyViolation, "signaling messages only"); break; }
            if (!await room.SendAsync(role == "host" ? "guest" : "host", message, ct)) break;
        }
    }

    private static bool IsAllowedSignalMessage(string message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeElement)) return false;
            if (typeElement.ValueKind != JsonValueKind.String) return false;
            var type = typeElement.GetString();
            if (type == "candidate")
                return root.EnumerateObject().Count() <= 4 && root.TryGetProperty("candidate", out var candidate) && candidate.ValueKind == JsonValueKind.String && candidate.GetString()!.Length <= 4096 &&
                    (!root.TryGetProperty("sdpMid", out var mid) || mid.ValueKind == JsonValueKind.String && mid.GetString()!.Length <= 256) &&
                    (!root.TryGetProperty("sdpMLineIndex", out var index) || index.ValueKind == JsonValueKind.Number && index.TryGetInt32(out _));
            if (type is "offer" or "answer")
                return root.EnumerateObject().Count() == 2 && root.TryGetProperty("sdp", out var sdp) && sdp.ValueKind == JsonValueKind.String && sdp.GetString()!.Length <= 48 * 1024;
            return false;
        }
        catch (JsonException) { return false; }
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) throw new WebSocketException("Binary payload is disabled on the signaling connection.");
            if (message.Length + result.Count > MaximumSignalMessageBytes) throw new WebSocketException("Signaling message is too large.");
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
    }

    private static async Task CloseSocketAsync(WebSocket socket, WebSocketCloseStatus status, string description)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await socket.CloseAsync(status, description, CancellationToken.None); }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private sealed class SignalRoom
    {
        private const int MaximumQueuedGuests = 16;
        private readonly object _sync = new();
        private readonly SemaphoreSlim _hostSend = new(1, 1);
        private readonly SemaphoreSlim _guestSend = new(1, 1);
        private readonly Queue<PendingGuest> _guestQueue = new();
        private WebSocket? _host;
        private WebSocket? _guest;

        public JoinResult JoinHost(WebSocket socket)
        {
            lock (_sync)
            {
                if (_host is { State: WebSocketState.Open }) return new(false, false, null, 0);
                _host = socket;
                PendingGuest? promoted = null;
                if (_guest is not { State: WebSocketState.Open })
                {
                    _guest = null;
                    promoted = DequeueNextGuest();
                    if (promoted is not null) _guest = promoted.Socket;
                }
                return new(true, _guest is { State: WebSocketState.Open }, null, 0, promoted);
            }
        }

        public JoinResult JoinGuest(WebSocket socket)
        {
            lock (_sync)
            {
                if (_guest is not { State: WebSocketState.Open })
                {
                    _guest = socket;
                    return new(true, _host is { State: WebSocketState.Open }, null, 0);
                }
                if (_guestQueue.Count >= MaximumQueuedGuests) return new(false, false, null, 0);
                var pending = new PendingGuest(socket);
                _guestQueue.Enqueue(pending);
                return new(true, false, pending, _guestQueue.Count);
            }
        }

        public LeaveResult Leave(string role, WebSocket socket, PendingGuest? pendingGuest)
        {
            lock (_sync)
            {
                if (pendingGuest is not null && !ReferenceEquals(_guest, socket))
                {
                    pendingGuest.Canceled = true;
                    pendingGuest.Admission.TrySetResult(false);
                    RemoveQueuedGuest(pendingGuest);
                    return new(null, null);
                }

                if (role == "host")
                {
                    if (!ReferenceEquals(_host, socket)) return new(null, null);
                    _host = null;
                    return new(_guest, null);
                }

                if (!ReferenceEquals(_guest, socket)) return new(null, null);
                _guest = null;
                var peer = _host;
                var promoted = _host is { State: WebSocketState.Open } ? DequeueNextGuest() : null;
                if (promoted is not null) _guest = promoted.Socket;
                return new(peer, promoted);
            }
        }

        public void Activate(PendingGuest guest, bool admitted = true) => guest.Admission.TrySetResult(admitted);

        private PendingGuest? DequeueNextGuest()
        {
            while (_guestQueue.TryDequeue(out var candidate))
            {
                if (!candidate.Canceled && candidate.Socket.State == WebSocketState.Open) return candidate;
                candidate.Canceled = true;
                candidate.Admission.TrySetResult(false);
            }
            return null;
        }

        private void RemoveQueuedGuest(PendingGuest guest)
        {
            if (_guestQueue.Count == 0) return;
            var remaining = _guestQueue.Where(candidate => !ReferenceEquals(candidate, guest)).ToArray();
            _guestQueue.Clear();
            foreach (var candidate in remaining) _guestQueue.Enqueue(candidate);
        }

        public async Task<bool> SendAsync(string role, string message, CancellationToken ct)
        {
            WebSocket? socket;
            var gate = role == "host" ? _hostSend : _guestSend;
            lock (_sync) socket = role == "host" ? _host : _guest;
            if (socket is not { State: WebSocketState.Open }) return false;
            await gate.WaitAsync(ct);
            try
            {
                if (socket.State != WebSocketState.Open) return false;
                await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, ct);
                return true;
            }
            finally { gate.Release(); }
        }

        public void Close()
        {
            lock (_sync)
            {
                _host?.Abort();
                _guest?.Abort();
                foreach (var pending in _guestQueue)
                {
                    pending.Canceled = true;
                    pending.Socket.Abort();
                    pending.Admission.TrySetResult(false);
                }
                _guestQueue.Clear();
                _host = _guest = null;
            }
        }

        public sealed class PendingGuest(WebSocket socket)
        {
            public WebSocket Socket { get; } = socket;
            public TaskCompletionSource<bool> Admission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Canceled { get; set; }
        }

        public sealed record JoinResult(bool Accepted, bool Paired, PendingGuest? Pending, int Position, PendingGuest? PromotedGuest = null);
        public sealed record LeaveResult(WebSocket? Peer, PendingGuest? PromotedGuest);
    }

    private async Task StopSignalingAsync()
    {
        foreach (var room in _signalRooms.Values) room.Close();
        _signalRooms.Clear();
        if (_signalApp is not null)
        {
            await _signalApp.StopAsync(CancellationToken.None);
            await _signalApp.DisposeAsync();
            _signalApp = null;
        }
    }
}
