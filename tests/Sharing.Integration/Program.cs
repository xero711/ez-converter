using System.IO.Compression;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using EZConverter.Sharing;
using MediaConverter.Models;
using MediaConverter.Services;

try
{
if (args is ["--live-localsend-discovery"])
{
    await RunLiveLocalSendDiscoveryAsync();
    return;
}

var root = Path.Combine(Directory.GetCurrentDirectory(), "work", "sharing-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var pass = 0;
string stableIdentityForRestartTest = string.Empty;
string stableIdentityPathForRestartTest = string.Empty;
void Assert(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); pass++; }
{
    const string tunnelToken = "tunnel-token.secret-value_123";
    var origin = CloudflareTunnel.ValidateNamedTunnel(53318, "share.example.com", tunnelToken);
    Assert(origin == new Uri("https://share.example.com/"),
        "Cloudflare Named Tunnel validates the configured public DNS hostname and produces an HTTPS origin");
    var namedStart = CloudflareTunnel.CreateNamedTunnelStartInfo("C:\\cloudflared\\cloudflared.exe", tunnelToken);
    Assert(namedStart.ArgumentList.SequenceEqual(["tunnel", "--no-autoupdate", "run"]) &&
           !namedStart.ArgumentList.Any(argument => argument.Contains(tunnelToken, StringComparison.Ordinal)) &&
           namedStart.Environment["TUNNEL_TOKEN"] == tunnelToken,
        "Cloudflare Named Tunnel passes its credential only through the child environment, never process arguments");
    var invalidTunnelRejected = false;
    try { _ = CloudflareTunnel.ValidateNamedTunnel(53318, "http://127.0.0.1", tunnelToken); }
    catch (ArgumentException) { invalidTunnelRejected = true; }
    Assert(invalidTunnelRejected,
        "Cloudflare Named Tunnel rejects URLs and private IPs instead of treating them as public hostnames");
    var invalidPortRejected = false;
    try { _ = CloudflareTunnel.ValidateNamedTunnel(80, "share.example.com", tunnelToken); }
    catch (ArgumentOutOfRangeException) { invalidPortRejected = true; }
    Assert(invalidPortRejected, "Cloudflare Named Tunnel rejects invalid local service ports");
}
{
    var token = new string('a', 64);
    var invitationUrl = "https://example.com/i/" + token;
    var code = InvitationCodeService.CreateStructuralCode(invitationUrl);
    Assert(InvitationCodeService.TryValidate(code), "EZC1 registration code validates its versioned URL-safe encoding");
    Assert(InvitationCodeService.TryRestore(code, out var restoredUrl) && restoredUrl == invitationUrl,
        "EZC1 registration code restores its exact HTTPS invitation URL");
    Assert(!InvitationCodeService.TryValidate(code[..^1] + (code.EndsWith('A') ? "B" : "A")),
        "EZC1 registration code rejects a tampered payload");
    var privateAddressRejected = false;
    try { InvitationCodeService.CreateStructuralCode("https://127.0.0.1/i/" + token); }
    catch (FormatException) { privateAddressRejected = true; }
    Assert(privateAddressRejected, "EZC1 registration code refuses IP-address destinations");

    var stableIdentityPath = Path.Combine(root, "named-tunnel", "invitation-identity.dat");
    stableIdentityPathForRestartTest = stableIdentityPath;
    var stableIdentity = new NamedTunnelInvitationIdentityStore(stableIdentityPath).LoadOrCreateToken();
    var reloadedStableIdentity = new NamedTunnelInvitationIdentityStore(stableIdentityPath).LoadOrCreateToken();
    stableIdentityForRestartTest = reloadedStableIdentity;
    var protectedIdentity = await File.ReadAllTextAsync(stableIdentityPath);
    Assert(stableIdentity.Length == 64 && stableIdentity.All(Uri.IsHexDigit) && stableIdentity == reloadedStableIdentity,
        "Named Tunnel invitation identity survives a new store instance with a valid 256-bit route token");
    Assert(protectedIdentity.StartsWith("EZNAMEDINV1:", StringComparison.Ordinal) &&
           !protectedIdentity.Contains(stableIdentity, StringComparison.Ordinal),
        "stable invitation route token is protected with DPAPI instead of saved as plaintext");

    var contactPath = Path.Combine(root, "contacts", "registered.dat");
    var contactStore = new InvitationContactStore(contactPath);
    contactStore.Upsert("Remote PC", code);
    var storedContact = contactStore.Load().Single();
    Assert(storedContact.DisplayName == "Remote PC" && storedContact.RegistrationCode == code,
        "registered internet contact survives a DPAPI-protected save/load roundtrip");
    var protectedDocument = await File.ReadAllTextAsync(contactPath);
    Assert(protectedDocument.StartsWith("EZCONTACTS1:", StringComparison.Ordinal) &&
           !protectedDocument.Contains(code, StringComparison.Ordinal) &&
           !protectedDocument.Contains("example.com", StringComparison.Ordinal),
        "registered invitation bearer data is not stored as plaintext");
    Assert(contactStore.Remove(code) && contactStore.Load().Count == 0,
        "registered internet contact can be removed without leaving stale records");

    var preferencesPath = Path.Combine(root, "app-settings", "settings.json");
    const string savedTunnelToken = "named-tunnel-sensitive-token.123";
    AppPreferencesStore.SaveToPath(new AppPreferences
    {
        UseNamedTunnel = true,
        NamedTunnelHostname = "share.example.com",
        NamedTunnelPort = 53318,
        NamedTunnelToken = savedTunnelToken
    }, preferencesPath);
    var savedPreferencesText = await File.ReadAllTextAsync(preferencesPath);
    var reloadedPreferences = AppPreferencesStore.LoadFromPath(preferencesPath);
    Assert(reloadedPreferences.UseNamedTunnel && reloadedPreferences.NamedTunnelHostname == "share.example.com" &&
           reloadedPreferences.NamedTunnelPort == 53318 && reloadedPreferences.NamedTunnelToken == savedTunnelToken &&
           !savedPreferencesText.Contains(savedTunnelToken, StringComparison.Ordinal),
        "Named Tunnel settings and token survive DPAPI-protected preference persistence without plaintext credentials");
}
{
    var receiveRoot = Path.Combine(root, "orphan-stage-cleanup");
    var stagingRoot = Path.Combine(receiveRoot, ".ez-incoming");
    var staleId = Guid.NewGuid().ToString("N");
    var staleStage = Path.Combine(stagingRoot, staleId);
    Directory.CreateDirectory(staleStage);
    await File.WriteAllBytesAsync(Path.Combine(staleStage, "partial.part"), [1, 2, 3]);
    Directory.SetLastWriteTimeUtc(staleStage, DateTime.UtcNow.AddDays(-9));

    var activeId = Guid.NewGuid().ToString("N");
    var activeStage = Path.Combine(stagingRoot, activeId);
    Directory.CreateDirectory(activeStage);
    await File.WriteAllBytesAsync(Path.Combine(activeStage, "active.part"), [4, 5, 6]);
    using var activeLease = new FileStream(Path.Combine(stagingRoot, activeId + ".lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    var cleanupServer = new TransferServer(new()
    {
        ReceiveDirectory = receiveRoot,
        StateDirectory = Path.Combine(root, "orphan-stage-state"),
        BindAddress = IPAddress.Loopback
    });
    await cleanupServer.StartAsync();
    Assert(!Directory.Exists(staleStage), "server startup removes abandoned receive staging left by a crashed prior run");
    Assert(Directory.Exists(activeStage), "server startup preserves an in-progress receive protected by its exclusive lease");
await cleanupServer.DisposeAsync();
}
using (var signalPortProbe = new TcpListener(IPAddress.Loopback, 0))
{
    signalPortProbe.Start();
    var namedTunnelPort = ((IPEndPoint)signalPortProbe.LocalEndpoint).Port;
    signalPortProbe.Stop();
    var fixedPortServer = new TransferServer(new()
    {
        ReceiveDirectory = Path.Combine(root, "fixed-signal-receive"),
        StateDirectory = Path.Combine(root, "fixed-signal-state"),
        BindAddress = IPAddress.Loopback,
        HttpPort = 0,
        HttpsPort = 0,
        SignalPort = namedTunnelPort
    });
    await fixedPortServer.StartAsync();
    Assert(fixedPortServer.SignalPort == namedTunnelPort,
        "signaling listener honors a configured stable port for a remotely managed tunnel route");
    await fixedPortServer.DisposeAsync();
}
var originProbeAttempts = 0;
var originProbeErrors = 0;
var originRecovered = await CloudflareTunnel.WaitForHealthyOriginAsync(_ =>
{
    originProbeAttempts++;
    if (originProbeAttempts < 3) throw new HttpRequestException("Temporary DNS resolution failure.");
    return Task.FromResult(true);
}, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(5),
    onProbeError: _ => originProbeErrors++);
Assert(originRecovered && originProbeAttempts == 3 && originProbeErrors == 2,
    "Cloudflare origin health checks retry and report transient DNS failures until the origin is reachable");
var originTimeoutTimer = Stopwatch.StartNew();
var originTimedOut = await CloudflareTunnel.WaitForHealthyOriginAsync(async token =>
{
    await Task.Delay(Timeout.InfiniteTimeSpan, token);
    return true;
}, TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(10));
Assert(!originTimedOut && originTimeoutTimer.Elapsed < TimeSpan.FromSeconds(2),
    "Cloudflare origin health checks respect one total startup deadline instead of multiplying per-request timeouts");
using (var startupCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20)))
{
    var startupCancelled = false;
    try
    {
        await CloudflareTunnel.WaitForHealthyOriginAsync(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        }, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10), startupCancellation.Token);
    }
    catch (OperationCanceledException) { startupCancelled = true; }
Assert(startupCancelled, "Cloudflare origin health checks honor user cancellation immediately");
}
Assert(TransferClient.IsTransient(new HttpRequestException("Timed out.", null, HttpStatusCode.RequestTimeout), CancellationToken.None),
    "LAN sender retries transient HTTP 408 request timeouts");
Assert(TransferClient.IsTransient(new HttpRequestException("Rate limited.", null, HttpStatusCode.TooManyRequests), CancellationToken.None),
    "LAN sender retries transient HTTP 429 rate limits");
Assert(TransferClient.IsTransient(new HttpRequestException("Server unavailable.", null, HttpStatusCode.ServiceUnavailable), CancellationToken.None),
    "LAN sender retries transient HTTP 5xx server failures");
Assert(!TransferClient.IsTransient(new HttpRequestException("Invalid request.", null, HttpStatusCode.BadRequest), CancellationToken.None),
    "LAN sender does not retry permanent HTTP 4xx request errors");
using (var cancelledRetry = new CancellationTokenSource())
{
    cancelledRetry.Cancel();
    Assert(!TransferClient.IsTransient(new IOException("Canceled transfer."), cancelledRetry.Token),
        "LAN sender cancellation is not misclassified as a recoverable network error");
}
var offerAttempts = 0;
using (var retriedOffer = await TransferClient.PostOfferWithRateLimitRetryAsync(_ =>
{
    var attempt = Interlocked.Increment(ref offerAttempts);
    var response = new HttpResponseMessage(attempt <= 2 ? HttpStatusCode.TooManyRequests : HttpStatusCode.NoContent);
    if (attempt <= 2) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
    return Task.FromResult(response);
}, CancellationToken.None))
{
    Assert(retriedOffer.StatusCode == HttpStatusCode.NoContent && offerAttempts == 3,
        "EZ Converter sender retries a safe offer-level HTTP 429 and then continues after the receiver becomes available");
}
await using (var idleTunnel = new CloudflareTunnel(Path.Combine(root, "unused-cloudflare-tool")))
{
    await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => idleTunnel.StopAsync()));
    Assert(!idleTunnel.IsRunning && idleTunnel.PublicOrigin is null,
        "parallel tunnel stop requests are serialized and remain idempotent without launching a tunnel");
}
var childStart = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
{
    UseShellExecute = false,
    CreateNoWindow = true
};
childStart.ArgumentList.Add("/d");
childStart.ArgumentList.Add("/c");
childStart.ArgumentList.Add("ping -n 30 127.0.0.1 > nul");
using (var child = Process.Start(childStart) ?? throw new InvalidOperationException("Could not start the helper-process lifetime test."))
{
    try
    {
        using (var job = WindowsChildProcessJob.CreateKillOnClose()) job.Assign(child);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert(child.HasExited, "closing the Windows lifetime job terminates its assigned helper process");
    }
    finally
    {
        if (!child.HasExited) child.Kill(entireProcessTree: true);
    }
}
var fixture = Path.Combine(root, "送信用フォルダー");
Directory.CreateDirectory(Path.Combine(fixture, "sub"));
await File.WriteAllTextAsync(Path.Combine(fixture, "日本語 & <sample>.txt".Replace('<', '(').Replace('>', ')')), "EZ Converter synthetic transfer fixture\n日本語の転送テスト");
await File.WriteAllBytesAsync(Path.Combine(fixture, "sub", "multi-chunk.bin"), RandomNumberGenerator.GetBytes(9 * 1024 * 1024 + 17));
await File.WriteAllBytesAsync(Path.Combine(fixture, "empty.txt"), []);
var files = await TransferFiles.CollectAsync([fixture]);
var retryFile = files.Single(file => file.File.RelativePath.EndsWith("multi-chunk.bin", StringComparison.Ordinal));
var retryOffsets = new List<long>();
var retryBytes = new MemoryStream();
var committedOffset = 0L;
var injectedFailure = false;
using (var retryHttp = new HttpClient(new CallbackHttpMessageHandler(async (request, token) =>
{
    var path = request.RequestUri!.AbsolutePath;
    if (request.Method == HttpMethod.Post && path.EndsWith("/offers", StringComparison.Ordinal))
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new OfferReceipt("retry-session", "retry-secret")) };
    if (request.Method == HttpMethod.Get && path.EndsWith("/offers/retry-session", StringComparison.Ordinal))
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new OfferStatus("accepted", null, new Dictionary<string, long> { [retryFile.File.Id] = committedOffset }))
        };
    if (request.Method == HttpMethod.Put && path.EndsWith("/files/" + retryFile.File.Id, StringComparison.Ordinal))
    {
        var query = request.RequestUri.Query;
        var offsetStart = query.IndexOf("offset=", StringComparison.Ordinal);
        if (offsetStart < 0 || !long.TryParse(query.AsSpan(offsetStart + 7), out var offset))
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        retryOffsets.Add(offset);
        if (offset == TransferServerOptions.ChunkSize && !injectedFailure)
        {
            injectedFailure = true;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
        if (offset != committedOffset) return new HttpResponseMessage(HttpStatusCode.Conflict);
        var payload = await request.Content!.ReadAsByteArrayAsync(token);
        retryBytes.Position = offset;
        await retryBytes.WriteAsync(payload, token);
        committedOffset = offset + payload.Length;
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }
    if (request.Method == HttpMethod.Post && path.EndsWith("/complete", StringComparison.Ordinal))
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    throw new InvalidOperationException($"Unexpected retry-fixture request: {request.Method} {request.RequestUri}");
})))
{
    using var retrySender = new TransferClient(retryHttp, new Uri("http://retry-fixture.invalid/api/ez/v1/"),
        new DeviceInfo("ez-share/1", "Retry Fixture", "fixture-fingerprint", 53317));
    await retrySender.SendAsync([retryFile], "Retry Test", null, CancellationToken.None);
}
var retryHash = Convert.ToHexString(SHA256.HashData(retryBytes.ToArray()));
Assert(injectedFailure && retryOffsets.SequenceEqual([0L, TransferServerOptions.ChunkSize, TransferServerOptions.ChunkSize, 2L * TransferServerOptions.ChunkSize]) &&
       committedOffset == retryFile.File.Length && retryHash == retryFile.File.Sha256,
    "EZ Converter sender queries the durable receiver offset after a transient chunk failure and resumes with the exact remaining bytes");
retryBytes.Dispose();
var parallelFiles = files.Where(file => file.File.Length > 0).Take(2).ToList();
var parallelFileIds = parallelFiles.Select(file => file.File.Id).ToHashSet(StringComparer.Ordinal);
var parallelStartedIds = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
var parallelOffsets = new ConcurrentDictionary<string, long>(parallelFileIds.Select(id => new KeyValuePair<string, long>(id, 0)), StringComparer.Ordinal);
var allFilesStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var parallelProgress = new ConcurrentQueue<TransferProgress>();
var activePuts = 0;
var maximumActivePuts = 0;
using (var parallelHttp = new HttpClient(new CallbackHttpMessageHandler(async (request, token) =>
{
    var path = request.RequestUri!.AbsolutePath;
    if (request.Method == HttpMethod.Post && path.EndsWith("/offers", StringComparison.Ordinal))
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new OfferReceipt("parallel-session", "parallel-secret")) };
    if (request.Method == HttpMethod.Get && path.EndsWith("/offers/parallel-session", StringComparison.Ordinal))
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new OfferStatus("accepted", null, parallelOffsets.ToDictionary(pair => pair.Key, pair => pair.Value)) ) };
    if (request.Method == HttpMethod.Put && path.Contains("/offers/parallel-session/files/", StringComparison.Ordinal))
    {
        var fileId = path[(path.IndexOf("/files/", StringComparison.Ordinal) + 7)..];
        if (!parallelFileIds.Contains(fileId)) return new HttpResponseMessage(HttpStatusCode.BadRequest);
        var active = Interlocked.Increment(ref activePuts);
        while (true)
        {
            var previous = Volatile.Read(ref maximumActivePuts);
            if (active <= previous || Interlocked.CompareExchange(ref maximumActivePuts, active, previous) == previous) break;
        }
        try
        {
            parallelStartedIds.TryAdd(fileId, 0);
            if (parallelStartedIds.Count == parallelFileIds.Count) allFilesStarted.TrySetResult(true);
            var allStarted = await Task.WhenAny(allFilesStarted.Task, Task.Delay(TimeSpan.FromSeconds(2), token));
            if (allStarted != allFilesStarted.Task) throw new TimeoutException("The sender did not upload different files concurrently.");
            var payload = await request.Content!.ReadAsByteArrayAsync(token);
            var query = request.RequestUri.Query;
            var offsetStart = query.IndexOf("offset=", StringComparison.Ordinal);
            if (offsetStart < 0 || !long.TryParse(query.AsSpan(offsetStart + 7), out var offset))
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            parallelOffsets[fileId] = offset + payload.Length;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        finally { Interlocked.Decrement(ref activePuts); }
    }
    if (request.Method == HttpMethod.Post && path.EndsWith("/complete", StringComparison.Ordinal))
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    throw new InvalidOperationException($"Unexpected parallel fixture request: {request.Method} {request.RequestUri}");
})))
{
    using var parallelSender = new TransferClient(parallelHttp, new Uri("http://parallel-fixture.invalid/api/ez/v1/"),
        new DeviceInfo("ez-share/1", "Parallel Fixture", "fixture-fingerprint", 53317));
    await parallelSender.SendAsync(parallelFiles, "Parallel Test", new CallbackProgress<TransferProgress>(parallelProgress.Enqueue), CancellationToken.None);
}
Assert(maximumActivePuts >= 2 && parallelStartedIds.Count == parallelFileIds.Count,
    "EZ Converter sends separate files concurrently while tracking aggregate progress");
var parallelProgressSnapshot = parallelProgress.ToArray();
var parallelTotalBytes = parallelFiles.Sum(file => file.File.Length);
Assert(parallelProgressSnapshot.Length > 0 && parallelProgressSnapshot[^1].State == "完了" &&
       parallelProgressSnapshot.All(sample => sample.Completed >= 0 && sample.Completed <= parallelTotalBytes) &&
       parallelProgressSnapshot.Select((sample, index) => index == 0 || sample.Completed >= parallelProgressSnapshot[index - 1].Completed).All(monotonic => monotonic),
    "parallel send reports bounded, monotonic aggregate progress and completes at the full batch size");
var qrPng = ShareQrCode.CreatePng("https://example.invalid/s/" + new string('a', 64));
Assert(qrPng.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "share URL creates a local PNG QR code");
var historyPath = Path.Combine(root, "transfer-history.json");
var historyRecords = Enumerable.Range(0, TransferHistoryStore.MaximumEntries + 5)
    .Select(index => new TransferHistoryRecord(Guid.NewGuid().ToString("N"), "送信", $"item-{index}", 1, 1, "完了", DateTimeOffset.UtcNow.AddMinutes(-index)))
    .Append(new TransferHistoryRecord(Guid.NewGuid().ToString("N"), "送信", "C:\\private\\secret.txt", 1, 1, "完了", DateTimeOffset.UtcNow))
    .ToArray();
TransferHistoryStore.Save(historyPath, historyRecords);
var restoredHistory = TransferHistoryStore.Load(historyPath);
Assert(restoredHistory.Count == TransferHistoryStore.MaximumEntries && restoredHistory[0].Name == "item-0" && restoredHistory[^1].Name == $"item-{TransferHistoryStore.MaximumEntries - 1}",
    "transfer history keeps only the newest 100 validated records");
var historyJson = await File.ReadAllTextAsync(historyPath);
Assert(!historyJson.Contains("private", StringComparison.OrdinalIgnoreCase) && !historyJson.Contains("secret.txt", StringComparison.OrdinalIgnoreCase) && !historyJson.Contains("https://", StringComparison.OrdinalIgnoreCase),
    "transfer history does not store source paths or share URLs");
var turnNow = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
var turnExpiry = turnNow.AddHours(3);
var turnCredential = TurnRelayCredentials.CreateTemporaryCredential("integration-shared-secret-123", turnExpiry, turnNow);
var expectedTurnPassword = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes("integration-shared-secret-123"), Encoding.UTF8.GetBytes(turnCredential.Username)));
Assert(turnCredential.Username == $"{turnExpiry.ToUnixTimeSeconds()}:ezconverter" && turnCredential.Credential == expectedTurnPassword && turnCredential.ExpiresAtUnixSeconds == turnExpiry.ToUnixTimeSeconds(),
    "TURN credentials use expiring coturn REST HMAC-SHA1 credentials");
var manualStopTurnCredential = TurnRelayCredentials.CreateTemporaryCredential("integration-shared-secret-123", DateTimeOffset.MaxValue, turnNow);
Assert(manualStopTurnCredential.ExpiresAtUnixSeconds == turnNow.AddDays(7).ToUnixTimeSeconds(),
    "manual-stop links use bounded seven-day TURN credentials that can be renewed by reloading");
var rejectsInvalidTurnUrl = false;
try { TurnRelayCredentials.Validate("https://turn.example.invalid", "integration-shared-secret-123"); }
catch (ArgumentException) { rejectsInvalidTurnUrl = true; }
Assert(TurnRelayCredentials.Validate("turns:turn.example.invalid:5349?transport=tcp", "integration-shared-secret-123").Url.StartsWith("turns:", StringComparison.Ordinal) && rejectsInvalidTurnUrl,
    "TURN settings accept secure TURN URLs and reject unrelated URL schemes");
if (OperatingSystem.IsWindows())
{
    const string dpapiTestSecret = "integration-secret-not-plaintext";
    var protectedSecret = DpapiStringProtector.Protect(dpapiTestSecret);
    Assert(!protectedSecret.Contains(dpapiTestSecret, StringComparison.Ordinal) && DpapiStringProtector.Unprotect(protectedSecret) == dpapiTestSecret,
        "sensitive app settings round-trip through current-user Windows DPAPI without plaintext storage");

    var identityState = Path.Combine(root, "stable-localsend-identity");
    var firstIdentity = new TransferServer(new() { ReceiveDirectory = Path.Combine(root, "identity-receive"), StateDirectory = identityState });
    var firstFingerprint = firstIdentity.Fingerprint;
    await firstIdentity.DisposeAsync();
    var restartedIdentity = new TransferServer(new() { ReceiveDirectory = Path.Combine(root, "identity-receive"), StateDirectory = identityState });
    var restartedFingerprint = restartedIdentity.Fingerprint;
    await restartedIdentity.DisposeAsync();
    Assert(firstFingerprint == restartedFingerprint,
        "LocalSend device fingerprint remains stable across server restarts using a DPAPI-protected identity");
    var otherIdentity = new TransferServer(new() { ReceiveDirectory = Path.Combine(root, "other-identity-receive"), StateDirectory = Path.Combine(root, "other-identity") });
    Assert(firstFingerprint != otherIdentity.Fingerprint, "separate LocalSend state directories receive distinct device identities");
    await otherIdentity.DisposeAsync();
}
var receive = Path.Combine(root, "received");
await using var server = new TransferServer(new()
{
    ReceiveDirectory = receive, StateDirectory = Path.Combine(root, "state"), BindAddress = IPAddress.Loopback,
    HttpsPort = TransferServer.LocalSendDefaultPort, TerminalReceiveRetention = TimeSpan.FromMilliseconds(50)
});
server.Faulted += exception => Console.Error.WriteLine("SERVER FAULT: " + exception);
await server.StartAsync();
Console.WriteLine($"SERVER http={server.HttpPort} https={server.HttpsPort} root={root}");
Assert(server.UsesLocalSendDefaultPort == (server.HttpsPort == TransferServer.LocalSendDefaultPort),
    "LocalSend advertises whether the standard TCP port is active");
var pinReceiveDirectory = Path.Combine(root, "pin-protected-receive");
await using var pinServer = new TransferServer(new()
{
    ReceiveDirectory = pinReceiveDirectory,
    StateDirectory = Path.Combine(root, "pin-protected-state"),
    BindAddress = IPAddress.Loopback,
    LocalSendReceivePin = "123456"
});
var pinOfferCount = 0;
pinServer.Incoming += offer => { pinOfferCount++; offer.Accept(); };
await pinServer.StartAsync();
using (var pinHttp = new HttpClient())
{
    var pinEndpoint = $"{pinServer.LoopbackOrigin}/api/localsend/v2/prepare-upload";
    var pinFileId = Guid.NewGuid().ToString("N");
    var pinMetadata = new LocalSendFileMetadata(pinFileId, "pin-protected.bin", 0, "application/octet-stream",
        Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())));
    var pinRequest = new LocalSendPrepareUploadRequest(
        new LocalSendDeviceInfo("PIN fixture", Port: 53317, Protocol: "http", Fingerprint: "pin-fixture"),
        new Dictionary<string, LocalSendFileMetadata> { [pinFileId] = pinMetadata });
    using (var missingPin = await pinHttp.PostAsJsonAsync(pinEndpoint, pinRequest))
        Assert(missingPin.StatusCode == HttpStatusCode.Unauthorized && pinOfferCount == 0,
            "LocalSend receiver PIN rejects requests that omit pin before prompting for file acceptance");
    using (var wrongPin = await pinHttp.PostAsJsonAsync(pinEndpoint + "?pin=123455", pinRequest))
        Assert(wrongPin.StatusCode == HttpStatusCode.Unauthorized && pinOfferCount == 0,
            "LocalSend receiver PIN rejects an incorrect PIN without creating an incoming offer");
    using (var correctPin = await pinHttp.PostAsJsonAsync(pinEndpoint + "?pin=123456", pinRequest))
    {
        Assert(correctPin.IsSuccessStatusCode && pinOfferCount == 1,
            "LocalSend receiver PIN accepts the correct six-digit query parameter");
        var receipt = await correctPin.Content.ReadFromJsonAsync<LocalSendPrepareUploadResponse>();
        if (receipt is not null)
        {
            using var cancelPinSession = await pinHttp.PostAsync(pinServer.LoopbackOrigin + "/api/localsend/v2/cancel?sessionId=" + Uri.EscapeDataString(receipt.SessionId), null);
            Assert(cancelPinSession.StatusCode == HttpStatusCode.NoContent, "PIN-authenticated LocalSend session can be cancelled normally");
        }
    }
    var unauthorizedAttempts = true;
    for (var attempt = 0; attempt < 8; attempt++)
    {
        using var wrongPin = await pinHttp.PostAsJsonAsync(pinEndpoint + "?pin=999999", pinRequest);
        unauthorizedAttempts &= wrongPin.StatusCode == HttpStatusCode.Unauthorized;
    }
    using (var throttledPin = await pinHttp.PostAsJsonAsync(pinEndpoint + "?pin=999999", pinRequest))
        Assert(unauthorizedAttempts && throttledPin.StatusCode == HttpStatusCode.TooManyRequests,
            "LocalSend PIN guessing is throttled after eight failed attempts per sender address");
}
if (server.UsesLocalSendDefaultPort)
{
    await using var fallbackServer = new TransferServer(new()
    {
        ReceiveDirectory = Path.Combine(root, "fallback-received"), StateDirectory = Path.Combine(root, "fallback-state"),
        BindAddress = IPAddress.Loopback, HttpsPort = TransferServer.LocalSendDefaultPort
    });
    await fallbackServer.StartAsync();
    Assert(!fallbackServer.UsesLocalSendDefaultPort && fallbackServer.HttpsPort != TransferServer.LocalSendDefaultPort,
        "a second EZ Converter instance falls back cleanly when LocalSend's standard TCP port is occupied");
}
using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
var local = server.LoopbackOrigin;
Assert(TransferServer.IsLocalSendAddress(IPAddress.Loopback) &&
       TransferServer.IsLocalSendAddress(IPAddress.Parse("192.168.10.25")) &&
       TransferServer.IsLocalSendAddress(IPAddress.Parse("10.20.30.40")) &&
       !TransferServer.IsLocalSendAddress(IPAddress.Parse("203.0.113.25")) &&
       !TransferServer.IsLocalSendAddress(IPAddress.Parse("8.8.8.8")),
    "temporary LocalSend browser shares accept only loopback or private LAN client addresses");
Assert(TransferServer.IsLocalSendInterfaceType(NetworkInterfaceType.Ethernet) &&
       TransferServer.IsLocalSendInterfaceType(NetworkInterfaceType.Wireless80211) &&
       !TransferServer.IsLocalSendInterfaceType(NetworkInterfaceType.Loopback) &&
       !TransferServer.IsLocalSendInterfaceType(NetworkInterfaceType.Tunnel) &&
       !TransferServer.IsLocalSendInterfaceType(NetworkInterfaceType.Ppp) &&
       !TransferServer.IsLocalSendInterfaceType((NetworkInterfaceType)53),
    "LocalSend LAN discovery and advertised addresses exclude VPN/tunnel and PPP interfaces");
Assert(TransferServer.IsLocalSendConnection(IPAddress.Loopback, IPAddress.Loopback) &&
       !TransferServer.IsLocalSendConnection(IPAddress.Parse("fd7a:115c:a1e0::1"), IPAddress.Parse("fd7a:115c:a1e0::2")),
    "LocalSend connection guard preserves loopback tests but rejects unassigned overlay addresses");
var excludedInterfaceAddresses = NetworkInterface.GetAllNetworkInterfaces()
    .Where(network => network.OperationalStatus == OperationalStatus.Up && !TransferServer.IsLocalSendInterface(network))
    .SelectMany(network => network.GetIPProperties().UnicastAddresses)
    .Select(unicast => unicast.Address)
    .Where(address => TransferServer.IsLocalSendAddress(address) && !IPAddress.IsLoopback(address))
    .ToArray();
Assert(!TransferServer.LocalAddresses().Intersect(excludedInterfaceAddresses.Select(address => address.ToString()), StringComparer.Ordinal).Any() &&
       excludedInterfaceAddresses.All(address => !TransferServer.IsLocalSendConnection(address, address) &&
                                                  !TransferServer.IsLocalSendPeerAddress(address)),
    "LocalSend share URLs and receiver endpoints do not use addresses assigned to VPN or non-LAN interfaces");
var syntheticOverlayAddress = IPAddress.Parse("10.254.14.7");
Assert(TransferServer.IsLocalSendPeerAddress(IPAddress.Loopback) &&
       !TransferServer.IsLocalSendPeerAddress(syntheticOverlayAddress, [syntheticOverlayAddress]) &&
       TransferServer.IsLocalSendPeerAddress(syntheticOverlayAddress, Array.Empty<IPAddress>()) &&
       excludedInterfaceAddresses.All(address => !TransferServer.IsLocalSendPeerAddress(address)),
    "LocalSend peer discovery rejects source addresses assigned to VPN or non-LAN adapters");
var localSendProgress = new List<TransferProgress>();
await using var localSend = await LocalSendDownloadServer.StartAsync("LocalSend互換テスト", files, null, TimeSpan.FromMinutes(10));
localSend.Progress += localSendProgress.Add;
var localSendOrigin = new Uri(localSend.LocalLink("127.0.0.1"));
var localSendPage = await http.GetStringAsync(localSendOrigin);
Assert(localSendPage.Contains("LocalSend", StringComparison.Ordinal) && localSendPage.Contains("/localsend.js", StringComparison.Ordinal),
    "temporary sender-PC browser page is served at the LocalSend root URL");
var lanIpv4 = TransferServer.LocalIpv4Addresses()
    .Select(IPAddress.Parse)
    .FirstOrDefault(address => !IPAddress.IsLoopback(address) &&
        !(address.GetAddressBytes()[0] == 169 && address.GetAddressBytes()[1] == 254));
if (lanIpv4 is not null)
{
    var lanPage = await http.GetStringAsync(localSend.LocalLink(lanIpv4.ToString()));
    Assert(lanPage.Contains("/localsend.js", StringComparison.Ordinal),
        "temporary LocalSend browser share is reachable through an active LAN IPv4 address, not only loopback");
}
else Console.WriteLine("SKIP: host has no active private LAN IPv4 address for the browser-share route test");
Assert(localSendPage.Contains("フォルダー構造を保持", StringComparison.Ordinal) && localSendPage.Contains("download-all", StringComparison.Ordinal),
    "browser share adds an optional folder-preserving bulk ZIP without changing LocalSend endpoints");
Assert((await http.GetAsync(new Uri(localSendOrigin, "/health"))).StatusCode == HttpStatusCode.NoContent,
    "Cloudflare tunnel health probe can verify the temporary LocalSend listener");
using (var forwardedAddress = new HttpRequestMessage(HttpMethod.Get, new Uri(localSendOrigin, "/health")))
{
    forwardedAddress.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.25");
    using var response = await http.SendAsync(forwardedAddress);
    Assert(response.StatusCode == HttpStatusCode.NoContent,
        "LocalSend browser-share access decisions use the socket peer, not spoofable forwarding headers");
}
var prepareUrl = new Uri(localSendOrigin, "/api/localsend/v2/prepare-download");
using var prepareRequest = new HttpRequestMessage(HttpMethod.Post, prepareUrl);
using var prepareResponse = await http.SendAsync(prepareRequest);
Assert(prepareResponse.IsSuccessStatusCode, "LocalSend v2 prepare-download accepts an empty POST");
using var prepareJson = JsonDocument.Parse(await prepareResponse.Content.ReadAsStringAsync());
var lsRoot = prepareJson.RootElement;
Assert(lsRoot.GetProperty("info").GetProperty("version").GetString() == "2.0" &&
       lsRoot.GetProperty("info").GetProperty("deviceType").GetString() == "desktop" &&
       lsRoot.GetProperty("info").GetProperty("download").GetBoolean(),
    "prepare-download responds with LocalSend v2 device info");
var localSendSession = lsRoot.GetProperty("sessionId").GetString()!;
Assert(lsRoot.GetProperty("files").EnumerateObject().Count() == files.Count &&
       lsRoot.GetProperty("files").GetProperty(files[0].File.Id).GetProperty("sha256").GetString() == files[0].File.Sha256,
    "prepare-download returns the LocalSend files metadata map without source paths");
var nestedLocalSendSample = files.First(file => file.File.RelativePath.Contains('/', StringComparison.Ordinal));
Assert(lsRoot.GetProperty("files").GetProperty(nestedLocalSendSample.File.Id).GetProperty("fileName").GetString() == nestedLocalSendSample.File.RelativePath,
    "LocalSend reverse-download metadata keeps folder-relative paths");
var textSample = files.First(file => file.File.RelativePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
Assert(lsRoot.GetProperty("files").GetProperty(textSample.File.Id).GetProperty("fileType").GetString() == "text/plain",
    "LocalSend file metadata includes the correct MIME type");
var localSendSample = files.First(file => file.File.Length > 1024);
var browserFileMetadata = lsRoot.GetProperty("files").GetProperty(localSendSample.File.Id).GetProperty("metadata");
Assert(browserFileMetadata.GetProperty("modified").GetDateTimeOffset().UtcDateTime.Ticks == localSendSample.LastWriteTicks &&
       browserFileMetadata.GetProperty("accessed").GetDateTimeOffset().UtcDateTime.Ticks == localSendSample.LastAccessTicks,
    "LocalSend reverse-download metadata preserves source modified and accessed timestamps");
var localSendDownloadUrl = new Uri(localSendOrigin,
    "/api/localsend/v2/download?sessionId=" + Uri.EscapeDataString(localSendSession) + "&fileId=" + Uri.EscapeDataString(localSendSample.File.Id));
var localSendBytes = await http.GetByteArrayAsync(localSendDownloadUrl);
Assert(localSendBytes.SequenceEqual(await File.ReadAllBytesAsync(localSendSample.SourcePath)) && localSendProgress.Any(item => item.State == "完了"),
    "LocalSend v2 download streams the source file and reports completion");
var downloadAllUrl = new Uri(localSendOrigin,
    "/api/localsend/v2/download-all?sessionId=" + Uri.EscapeDataString(localSendSession));
using (var missingSessionZip = await http.GetAsync(new Uri(localSendOrigin, "/api/localsend/v2/download-all")))
    Assert(missingSessionZip.StatusCode == HttpStatusCode.Forbidden, "bulk ZIP download requires a valid prepared LocalSend session");
using (var zipResponse = await http.GetAsync(downloadAllUrl))
{
    Assert(zipResponse.IsSuccessStatusCode && zipResponse.Content.Headers.ContentType?.MediaType == "application/zip",
        "bulk download streams a ZIP from the temporary sender-PC server");
    await using var zipBytes = new MemoryStream(await zipResponse.Content.ReadAsByteArrayAsync());
    using var archive = new ZipArchive(zipBytes, ZipArchiveMode.Read);
    var names = archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray();
    var expectedNames = files.Select(file => file.File.RelativePath).Order(StringComparer.Ordinal).ToArray();
    var hashesMatch = names.SequenceEqual(expectedNames);
    foreach (var file in files)
    {
        var entry = archive.GetEntry(file.File.RelativePath);
        if (entry is null) { hashesMatch = false; break; }
        await using var entryStream = entry.Open();
        var entryHash = Convert.ToHexString(await SHA256.HashDataAsync(entryStream));
        if (!entryHash.Equals(file.File.Sha256, StringComparison.OrdinalIgnoreCase)) { hashesMatch = false; break; }
    }
    Assert(hashesMatch, "bulk ZIP preserves folder paths and every file SHA-256");
}
using (var resumedPrepare = await http.PostAsync(new Uri(prepareUrl + "?sessionId=" + Uri.EscapeDataString(localSendSession)), null))
    Assert(resumedPrepare.IsSuccessStatusCode, "LocalSend prepare-download reuses its sessionId after page refresh");
await localSend.StopAsync();
var localSendStopped = false;
try { await http.GetAsync(localSendOrigin); }
catch (HttpRequestException) { localSendStopped = true; }
Assert(localSendStopped, "stopping a LocalSend share closes its temporary sender-PC listener");

await using (var pinShare = await LocalSendDownloadServer.StartAsync("PIN test", files, "123456", TimeSpan.FromMinutes(5)))
{
    var pinPrepareUrl = new Uri(pinShare.LocalLink("127.0.0.1") + "api/localsend/v2/prepare-download");
    using var noPin = await http.PostAsync(pinPrepareUrl, null);
    Assert(noPin.StatusCode == HttpStatusCode.Unauthorized, "LocalSend protected share requires a PIN");
    using var incorrectPin = await http.PostAsync(new Uri(pinPrepareUrl + "?pin=123455"), null);
    Assert(incorrectPin.StatusCode == HttpStatusCode.Unauthorized, "LocalSend protected share rejects an incorrect PIN");
    using var correctPin = await http.PostAsync(new Uri(pinPrepareUrl + "?pin=123456"), null);
    Assert(correctPin.IsSuccessStatusCode, "LocalSend protected share accepts a six-digit PIN query parameter");
    var pinResult = await correctPin.Content.ReadFromJsonAsync<JsonElement>();
    var pinSession = pinResult.GetProperty("sessionId").GetString()!;
    using var protectedZip = await http.GetAsync(new Uri(pinShare.LocalLink("127.0.0.1") + "api/localsend/v2/download-all?sessionId=" + Uri.EscapeDataString(pinSession)));
    Assert(protectedZip.IsSuccessStatusCode, "bulk ZIP uses the same PIN-authorized LocalSend session");
}

var share = server.CreateShare(files, TimeSpan.FromMinutes(10));
server.ConfigureTurnRelay(true, "turns:turn.example.invalid:5349?transport=tcp", "integration-shared-secret-123");
var link = server.LocalLink(share, "127.0.0.1");
var localSharePage = await http.GetStringAsync(link);
Assert(localSharePage.Contains("window.EZTurnConfiguration", StringComparison.Ordinal) && !localSharePage.Contains("integration-shared-secret-123", StringComparison.Ordinal),
    "share page receives temporary TURN credentials without exposing the shared secret");
var peerLink = server.PeerLink(share, "127.0.0.1");
var shareHtml = await http.GetStringAsync(peerLink);
Assert(shareHtml.Contains("/transfer-window.js", StringComparison.Ordinal), "browser share page loads bounded transfer flow");
Assert(shareHtml.Contains("window.EZTurnConfiguration", StringComparison.Ordinal) && !shareHtml.Contains("integration-shared-secret-123", StringComparison.Ordinal),
    "online share page receives temporary TURN credentials without exposing the shared secret");
Assert((await http.GetStringAsync(server.LocalSignalOrigin + "/transfer-window.js")).Contains("EZTransferWindow", StringComparison.Ordinal), "bounded transfer flow is served from the embedded resource");
server.ConfigureTurnRelay(false, string.Empty, string.Empty);
Assert(!(await http.GetStringAsync(peerLink)).Contains("window.EZTurnConfiguration", StringComparison.Ordinal), "TURN relay is omitted from a link when explicitly disabled");
server.ConfigureTurnRelay(true, "turns:turn.example.invalid:5349?transport=tcp", "integration-shared-secret-123");
var manifest = await http.GetFromJsonAsync<ShareManifest>(link + "/manifest");
Assert(manifest?.Files.Count == 3, "manifest preserves folder and unicode names");
Assert(!((await http.GetStringAsync(link + "/manifest")).Contains(fixture.Replace("\\", "\\\\"), StringComparison.Ordinal)), "manifest does not expose source paths");
var sample = files.First(f => f.File.Length > 1024);
using (var request = new HttpRequestMessage(HttpMethod.Get, link + "/files/" + sample.File.Id))
{
    request.Headers.Range = new(123, 456);
    using var response = await http.SendAsync(request);
    var data = await response.Content.ReadAsByteArrayAsync();
    var expected = (await File.ReadAllBytesAsync(sample.SourcePath)).AsSpan(123, 334).ToArray();
    Assert(response.StatusCode == HttpStatusCode.PartialContent && data.SequenceEqual(expected), "HTTP Range resume bytes");
}
using (var zipData = new MemoryStream(await http.GetByteArrayAsync(link + "/all.zip")))
using (var archive = new ZipArchive(zipData))
{
    Assert(archive.Entries.Count == 3, "streaming ZIP file count");
    foreach (var file in files)
    {
        await using var stream = archive.GetEntry(file.File.RelativePath)!.Open();
        Assert(Convert.ToHexString(await SHA256.HashDataAsync(stream)) == file.File.Sha256, "ZIP hash " + file.File.RelativePath);
    }
}
var protectedShare = server.CreateShare(files, TimeSpan.FromMinutes(10), "test-only-password");
var protectedUrl = server.LocalLink(protectedShare, "127.0.0.1");
Assert((await http.GetAsync(protectedUrl + "/manifest")).StatusCode == HttpStatusCode.Unauthorized, "password prevents listing");
Assert((await http.GetAsync(protectedUrl + "/files/" + sample.File.Id)).StatusCode == HttpStatusCode.Unauthorized, "password prevents download");
using (var wrong = await http.PostAsJsonAsync(protectedUrl + "/unlock", new { password = "wrong" })) Assert(wrong.StatusCode == HttpStatusCode.Unauthorized, "incorrect password rejected");
var folder = await ShareDownloader.DownloadAsync(protectedUrl, "test-only-password", Path.Combine(root, "downloaded"), null, CancellationToken.None);
await VerifyFolder(folder);
var publicP2pHttpFallbackBlocked = false;
try
{
    await ShareDownloader.DownloadAsync($"https://example.invalid/s/{new string('a', 64)}", null,
        Path.Combine(root, "public-download-must-not-start"), null, CancellationToken.None);
}
catch (NotSupportedException) { publicP2pHttpFallbackBlocked = true; }
Assert(publicP2pHttpFallbackBlocked, "public HTTPS share URLs cannot fall back to HTTP file downloads through the tunnel");
var hostProtectedPage = server.HostPage(protectedShare);
var hostSecret = hostProtectedPage.Fragment.TrimStart('#');
var hostAuthorizeUrl = new Uri($"{server.LocalSignalOrigin}/host-api/{protectedShare.Token}/authorize");
for (var attempt = 0; attempt < 8; attempt++)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, hostAuthorizeUrl);
    request.Headers.Add("X-EZ-Host", hostSecret);
    request.Content = JsonContent.Create(new { password = "incorrect-host-password" });
    using var response = await http.SendAsync(request);
    Assert(response.StatusCode == HttpStatusCode.Unauthorized, $"host password attempt {attempt + 1} rejected");
}
using (var blockedRequest = new HttpRequestMessage(HttpMethod.Post, hostAuthorizeUrl))
{
    blockedRequest.Headers.Add("X-EZ-Host", hostSecret);
    blockedRequest.Content = JsonContent.Create(new { password = "test-only-password" });
    using var blockedResponse = await http.SendAsync(blockedRequest);
    Assert(blockedResponse.StatusCode == HttpStatusCode.TooManyRequests, "host password guessing is rate-limited per share");
}
var independentProtectedShare = server.CreateShare(files, TimeSpan.FromMinutes(10), "independent-password");
var independentHostSecret = server.HostPage(independentProtectedShare).Fragment.TrimStart('#');
using (var independentRequest = new HttpRequestMessage(HttpMethod.Post, $"{server.LocalSignalOrigin}/host-api/{independentProtectedShare.Token}/authorize"))
{
    independentRequest.Headers.Add("X-EZ-Host", independentHostSecret);
    independentRequest.Content = JsonContent.Create(new { password = "independent-password" });
    using var independentResponse = await http.SendAsync(independentRequest);
    Assert(independentResponse.StatusCode == HttpStatusCode.NoContent, "one share's password throttle does not block other shares");
}
var manualShare = server.CreateShare(files, TimeSpan.Zero);
var manualShareUrl = server.LocalLink(manualShare, "127.0.0.1");
Assert(manualShare.ExpiresAt == DateTimeOffset.MaxValue, "zero lifetime creates a share that remains live until stopped");
var manualInvitation = server.CreateInvitation(TimeSpan.Zero);
var manualInvitationUrl = server.LocalLink(manualInvitation, "127.0.0.1");
Assert(manualInvitation.ExpiresAt == DateTimeOffset.MaxValue, "zero lifetime creates a receive invitation that remains live until stopped");
var persistentNamedInvitation = server.CreateInvitation(TimeSpan.Zero, stableIdentityForRestartTest);
var persistentNamedCode = InvitationCodeService.CreateStructuralCode("https://share.example.com/i/" + persistentNamedInvitation.Token);
server.RevokeLink(persistentNamedInvitation.Token);
await using (var restartedInvitationServer = new TransferServer(new TransferServerOptions
{
    DeviceName = "Restarted invitation host",
    ReceiveDirectory = Path.Combine(root, "named-tunnel", "restarted-receive"),
    StateDirectory = Path.Combine(root, "named-tunnel", "restarted-state"),
    HttpsPort = 0,
    SignalPort = 0
}))
{
    var recreatedInvitation = restartedInvitationServer.CreateInvitation(TimeSpan.Zero, stableIdentityForRestartTest);
    var recreatedCode = InvitationCodeService.CreateStructuralCode("https://share.example.com/i/" + recreatedInvitation.Token);
    Assert(recreatedInvitation.Token == persistentNamedInvitation.Token && recreatedCode == persistentNamedCode,
        "the same Named Tunnel EZC1 registration code can be reactivated by a new server process after restart");
}
var rotatedNamedIdentity = new NamedTunnelInvitationIdentityStore(stableIdentityPathForRestartTest).RotateToken();
await using (var rotatedInvitationServer = new TransferServer(new TransferServerOptions
{
    DeviceName = "Rotated invitation host",
    ReceiveDirectory = Path.Combine(root, "named-tunnel", "rotated-receive"),
    StateDirectory = Path.Combine(root, "named-tunnel", "rotated-state"),
    HttpsPort = 0,
    SignalPort = 0
}))
{
    var rotatedInvitation = rotatedInvitationServer.CreateInvitation(TimeSpan.Zero, rotatedNamedIdentity);
    var rotatedCode = InvitationCodeService.CreateStructuralCode("https://share.example.com/i/" + rotatedInvitation.Token);
    Assert(rotatedInvitation.Token != persistentNamedInvitation.Token && rotatedCode != persistentNamedCode &&
           new NamedTunnelInvitationIdentityStore(stableIdentityPathForRestartTest).LoadOrCreateToken() == rotatedNamedIdentity,
        "rotating the Named Tunnel identity invalidates old EZC1 codes and persists the replacement token");
}
var expired = server.CreateShare(files, TimeSpan.FromSeconds(1));
await Task.Delay(1100);
Assert((await http.GetAsync(server.LocalLink(expired, "127.0.0.1") + "/manifest")).StatusCode == HttpStatusCode.Gone, "expiry enforced without maintenance tick");
Assert((await http.GetAsync(manualShareUrl + "/manifest")).IsSuccessStatusCode, "manual-stop share remains available beyond the timed-expiry window");
Assert((await http.GetAsync(manualInvitationUrl + "/api/info")).IsSuccessStatusCode, "manual-stop receive invitation remains available beyond the timed-expiry window");
server.RevokeLink(manualShare.Token);
server.RevokeLink(manualInvitation.Token);
Assert((await http.GetAsync(manualShareUrl + "/manifest")).StatusCode == HttpStatusCode.Gone &&
       (await http.GetAsync(manualInvitationUrl)).StatusCode == HttpStatusCode.Gone,
    "stopping a manual-stop share or invitation immediately invalidates its URL");
server.RevokeLink(share.Token);
Assert((await http.GetAsync(link + "/files/" + sample.File.Id)).StatusCode == HttpStatusCode.Gone, "revoked token rejected");
Assert((await http.GetAsync(local + "/api/ez/v1/info")).StatusCode == HttpStatusCode.NotFound, "public HTTP origin cannot expose native LAN API");
using (var request = new HttpRequestMessage(HttpMethod.Get, local + "/api/ez/v1/info"))
{ request.Headers.Add("X-Forwarded-Proto", "https"); Assert((await http.SendAsync(request)).StatusCode == HttpStatusCode.NotFound, "forwarded headers cannot bypass LAN gate"); }
var accept = true;
server.Incoming += offer => { if (accept) offer.Accept(); else offer.Reject(); };
await VerifyPeerDiscovery();
var localSendRegistrationUrl = new Uri(local + "/api/localsend/v2/register");
using (var registration = await http.PostAsJsonAsync(localSendRegistrationUrl,
    new LocalSendDeviceInfo("Synthetic LocalSend peer", Port: 53317, Protocol: "http", Fingerprint: "synthetic-fingerprint")))
{
    var registrationInfo = await registration.Content.ReadFromJsonAsync<LocalSendDeviceInfo>();
    Assert(registration.IsSuccessStatusCode && registrationInfo?.Protocol == "http" && registrationInfo.Port == server.HttpPort,
        "LocalSend registration responds with the active HTTP listener endpoint");
}
var localSendHttpsRegistrationHandler = new HttpClientHandler
{
    UseProxy = false,
    ClientCertificateOptions = ClientCertificateOption.Manual,
    ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
        Convert.ToHexString(SHA256.HashData(certificate.RawData)).Equals(server.Fingerprint, StringComparison.OrdinalIgnoreCase)
};
localSendHttpsRegistrationHandler.ClientCertificates.Add(server.LocalSendClientCertificate);
using (localSendHttpsRegistrationHandler)
using (var localSendHttpsClient = new HttpClient(localSendHttpsRegistrationHandler))
using (var httpsRegistration = await localSendHttpsClient.PostAsJsonAsync(
    $"https://127.0.0.1:{server.HttpsPort}/api/localsend/v2/register",
    new LocalSendDeviceInfo("Independent HTTPS LocalSend peer", Port: 53317, Protocol: "https", Fingerprint: "separate-json-fingerprint")))
{
    Assert(httpsRegistration.IsSuccessStatusCode,
        "HTTPS LocalSend registration ignores the JSON fingerprint and uses the TLS certificate identity");
}
async Task<HttpResponseMessage> PrepareSameLanPeerSession(string id, string name)
{
    var metadata = new LocalSendFileMetadata(id, name, 0, "application/octet-stream",
        Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())));
    return await http.PostAsJsonAsync(local + "/api/localsend/v2/prepare-upload",
        new LocalSendPrepareUploadRequest(
            new LocalSendDeviceInfo("Same LAN peer", Port: 53317, Protocol: "http", Fingerprint: "same-peer"),
            new Dictionary<string, LocalSendFileMetadata> { [id] = metadata }));
}
using (var firstPeerSession = await PrepareSameLanPeerSession("same-peer-file-1", "same-peer-1.bin"))
{
    await Task.Delay(TimeSpan.FromMilliseconds(1100));
    using var secondPeerSession = await PrepareSameLanPeerSession("same-peer-file-2", "same-peer-2.bin");
    var firstReceipt = await firstPeerSession.Content.ReadFromJsonAsync<LocalSendPrepareUploadResponse>();
    var secondReceipt = await secondPeerSession.Content.ReadFromJsonAsync<LocalSendPrepareUploadResponse>();
    Assert(firstPeerSession.IsSuccessStatusCode && secondPeerSession.IsSuccessStatusCode &&
           firstReceipt?.SessionId is { Length: > 0 } && secondReceipt?.SessionId is { Length: > 0 } &&
           firstReceipt.SessionId != secondReceipt.SessionId,
        "multiple independent LocalSend receive sessions from the same LAN address are allowed");
    if (firstReceipt is not null && secondReceipt is not null)
    {
        using var cancelFirst = await http.PostAsync(local + "/api/localsend/v2/cancel?sessionId=" + Uri.EscapeDataString(firstReceipt.SessionId), null);
        using var cancelSecond = await http.PostAsync(local + "/api/localsend/v2/cancel?sessionId=" + Uri.EscapeDataString(secondReceipt.SessionId), null);
        Assert(cancelFirst.StatusCode == HttpStatusCode.NoContent && cancelSecond.StatusCode == HttpStatusCode.NoContent,
            "same-address LocalSend sessions can be independently cancelled");
    }
}
await Task.Delay(TimeSpan.FromMilliseconds(1100));
var smallLocalSendFiles = await TransferFiles.CollectAsync([files.First(file => file.File.RelativePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).SourcePath]);
await LocalSendClient.SendAsync(local + "/", files, "HTTP LocalSend Sender", server.LocalSendInfo);
var localSendReceiveFolder = Directory.GetDirectories(receive).Single(directory => Path.GetFileName(directory).StartsWith("LocalSend-", StringComparison.Ordinal));
await VerifyFolder(localSendReceiveFolder, files);
Assert(true, "LocalSend HTTP mode preserves folder paths and verifies large-file SHA-256 uploads");
await Task.Delay(TimeSpan.FromMilliseconds(1100));
var externalPayload = RandomNumberGenerator.GetBytes(4097);
var externalFileHash = Convert.ToHexString(SHA256.HashData(externalPayload));
var externalPrepareJson = JsonSerializer.Serialize(new
{
    info = new { alias = "Independent LocalSend client", version = "2.0", deviceModel = "Linux", deviceType = "desktop",
        fingerprint = "fixture-fingerprint", port = 53317, protocol = "http", download = false },
    files = new Dictionary<string, object>
    {
        ["opaque-file-id"] = new { id = "opaque-file-id", fileName = "External Project/nested/external-localsend-fixture.bin", size = externalPayload.Length,
            fileType = "application/octet-stream", sha256 = externalFileHash, preview = (string?)null }
    }
}, new JsonSerializerOptions(JsonSerializerDefaults.Web));
using (var externalPrepare = await http.PostAsync(local + "/api/localsend/v2/prepare-upload",
    new StringContent(externalPrepareJson, Encoding.UTF8, "application/json")))
{
    Assert(externalPrepare.IsSuccessStatusCode,
        $"LocalSend receiver accepts an independently encoded prepare-upload request (HTTP {(int)externalPrepare.StatusCode})");
    using var externalReceiptJson = JsonDocument.Parse(await externalPrepare.Content.ReadAsStringAsync());
    var externalReceipt = externalReceiptJson.RootElement;
    var externalSession = externalReceipt.GetProperty("sessionId").GetString()!;
    var externalToken = externalReceipt.GetProperty("files").GetProperty("opaque-file-id").GetString()!;
    var externalUpload = new Uri(local + "/api/localsend/v2/upload?sessionId=" + Uri.EscapeDataString(externalSession) +
        "&fileId=opaque-file-id&token=" + Uri.EscapeDataString(externalToken));
    using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, externalUpload)
    {
        Content = new ByteArrayContent(externalPayload),
        Version = HttpVersion.Version11
    };
    uploadRequest.Headers.TransferEncodingChunked = true;
    using var uploadResponse = await http.SendAsync(uploadRequest);
    Assert(uploadResponse.StatusCode == HttpStatusCode.OK, "LocalSend receiver accepts chunked raw uploads and opaque file IDs with a CLI-compatible success code");
    var externalFolder = Directory.GetDirectories(receive).Single(directory => Path.GetFileName(directory).StartsWith("LocalSend-", StringComparison.Ordinal) &&
        File.Exists(Path.Combine(directory, "External Project", "nested", "external-localsend-fixture.bin")));
    Assert((await File.ReadAllBytesAsync(Path.Combine(externalFolder, "External Project", "nested", "external-localsend-fixture.bin"))).SequenceEqual(externalPayload),
        "independent LocalSend folder upload preserves nested paths and verifies SHA-256");
}
await Task.Delay(TimeSpan.FromMilliseconds(1100));
var largePreviewJson = JsonSerializer.Serialize(new
{
    info = new { alias = "LocalSend large-preview client", version = "2.0", deviceModel = "Linux", deviceType = "desktop",
        fingerprint = "large-preview-fingerprint", port = 53317, protocol = "http", download = false },
    files = new Dictionary<string, object>
    {
        ["previewed-file-id"] = new { id = "previewed-file-id", fileName = "previewed.bin", size = 0,
            fileType = "application/octet-stream", sha256 = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())),
            preview = new string('A', 3 * 1024 * 1024) }
    }
}, new JsonSerializerOptions(JsonSerializerDefaults.Web));
using (var largePreviewPrepare = await http.PostAsync(local + "/api/localsend/v2/prepare-upload",
    new StringContent(largePreviewJson, Encoding.UTF8, "application/json")))
{
    Assert(largePreviewPrepare.IsSuccessStatusCode,
        $"LocalSend receiver accepts optional multi-megabyte preview metadata (HTTP {(int)largePreviewPrepare.StatusCode})");
    using var receiptJson = JsonDocument.Parse(await largePreviewPrepare.Content.ReadAsStringAsync());
    var sessionId = receiptJson.RootElement.GetProperty("sessionId").GetString()!;
    Assert(receiptJson.RootElement.GetProperty("files").TryGetProperty("previewed-file-id", out _),
        "LocalSend receiver skips unused preview data without dropping the file token");
    using var cancelledPreview = await http.PostAsync(local + "/api/localsend/v2/cancel?sessionId=" + Uri.EscapeDataString(sessionId), null);
    Assert(cancelledPreview.StatusCode == HttpStatusCode.NoContent,
        "LocalSend large-preview metadata session can be cancelled without retaining its preview");
}
var directUrl = $"https://127.0.0.1:{server.HttpsPort}/#{server.Fingerprint}";
using (var sender = await TransferClient.ConnectAsync(directUrl)) await sender.SendAsync(files, "Integration Sender", null, CancellationToken.None);
var directFolder = Directory.GetDirectories(receive).Single(d => Path.GetFileName(d).StartsWith("EZ-"));
await VerifyFolder(directFolder);
try { using var wrong = await TransferClient.ConnectAsync($"https://127.0.0.1:{server.HttpsPort}/#{new string('0', 64)}"); Assert(false, "wrong certificate"); }
catch (HttpRequestException) { Assert(true, "wrong certificate fingerprint blocked"); }
var invitation = server.CreateInvitation(TimeSpan.FromMinutes(10));
var invite = server.LocalLink(invitation, "127.0.0.1");
try
{
    using var blockedPublicUpload = await TransferClient.ConnectAsync($"https://receiver.example.invalid/i/{invitation.Token}");
    Assert(false, "public invitation must not upload file bytes over HTTP");
}
catch (InvalidOperationException) { Assert(true, "public invitation rejects HTTP file transfer and requires WebRTC P2P"); }
using (var sender = await TransferClient.ConnectAsync(invite)) await sender.SendAsync(files, "Invitation Sender", null, CancellationToken.None);
Assert(Directory.GetDirectories(receive).Count(d => Path.GetFileName(d).StartsWith("EZ-")) == 2, "invitation accepts remote native transfers");
accept = false;
var rejectedOfferId = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
var rejectedProgress = new Progress<TransferProgress>(progress =>
{
    if (progress.State == "相手の確認待ち") rejectedOfferId.TrySetResult(progress.Id);
});
try { using var sender = await TransferClient.ConnectAsync(invite); await sender.SendAsync(files, "Rejected Sender", rejectedProgress, CancellationToken.None); Assert(false, "reject"); }
catch (IOException) { Assert(true, "receiver rejection stops upload"); }
Assert(Directory.GetDirectories(receive).Count(d => Path.GetFileName(d).StartsWith("EZ-")) == 2, "rejection creates no received files");
var rejectedTransferId = await rejectedOfferId.Task.WaitAsync(TimeSpan.FromSeconds(2));
Assert(rejectedTransferId.Length == 32, "rejected transfer is identified for cleanup");
await Task.Delay(100);
using (var cleanupHttp = new HttpClient(new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true }))
{
    var offersUri = new Uri($"https://127.0.0.1:{server.HttpsPort}/api/ez/v1/offers");
    using var trigger = await cleanupHttp.PostAsJsonAsync(offersUri, new OfferRequest("Cleanup trigger", files.Select(file => file.File).ToList()));
    Assert(trigger.IsSuccessStatusCode, "new receive offer prunes old terminal sessions before applying the session cap");
    using var stale = new HttpRequestMessage(HttpMethod.Get, new Uri(offersUri.ToString().TrimEnd('/') + "/" + rejectedTransferId));
    stale.Headers.Add("X-EZ-Session", "cleanup-probe");
    using var staleResponse = await cleanupHttp.SendAsync(stale);
    Assert(staleResponse.StatusCode == HttpStatusCode.Gone, "rejected receive session is removed after its terminal retention window");
}
accept = true;
var changingSource = Path.Combine(root, "changed-after-scan.bin");
var changingBytes = RandomNumberGenerator.GetBytes(1024);
await File.WriteAllBytesAsync(changingSource, changingBytes);
var changingFiles = await TransferFiles.CollectAsync([changingSource]);
changingBytes[0] ^= 0xFF;
await File.WriteAllBytesAsync(changingSource, changingBytes);
var failedOfferId = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
var failedProgress = new Progress<TransferProgress>(progress =>
{
    if (progress.State == "相手の確認待ち") failedOfferId.TrySetResult(progress.Id);
});
try { using var sender = await TransferClient.ConnectAsync(invite); await sender.SendAsync(changingFiles, "Changed source", failedProgress, CancellationToken.None); Assert(false, "changed source is rejected"); }
catch (HttpRequestException) { Assert(true, "source content changed after hashing fails receiver SHA-256 verification"); }
var failedTransferId = await failedOfferId.Task.WaitAsync(TimeSpan.FromSeconds(2));
using (var failureProbe = new HttpClient(new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true }))
{
    var failedOfferUri = new Uri($"https://127.0.0.1:{server.HttpsPort}/i/{invitation.Token}/api/offers/{failedTransferId}");
    using var failedStatus = await failureProbe.GetAsync(failedOfferUri);
    Assert(failedStatus.StatusCode == HttpStatusCode.Gone, "sender failure cancels and invalidates the pending receiver session");
}
Assert(!Directory.Exists(Path.Combine(receive, ".ez-incoming", failedTransferId)) &&
       !File.Exists(Path.Combine(receive, ".ez-incoming", failedTransferId + ".lock")),
    "failed transfer removes staged partial files and releases its receive lease immediately");
using (var localCancelHttp = new HttpClient(new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true }))
{
    var receiveOffersUri = new Uri($"https://127.0.0.1:{server.HttpsPort}/api/ez/v1/offers");
    var cancelFile = files.First(file => file.File.Length > 0 && file.File.Length < 1024).File;
    using var offerResponse = await localCancelHttp.PostAsJsonAsync(receiveOffersUri, new OfferRequest("Local cancel test", [cancelFile]));
    var cancelReceipt = await offerResponse.Content.ReadFromJsonAsync<OfferReceipt>() ?? throw new IOException("Cancellation test offer was not created.");
    var statusUri = new Uri(receiveOffersUri.ToString().TrimEnd('/') + "/" + cancelReceipt.Id);
    var accepted = false;
    for (var attempt = 0; attempt < 20; attempt++)
    {
        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, statusUri);
        statusRequest.Headers.Add("X-EZ-Session", cancelReceipt.Secret);
        using var statusResponse = await localCancelHttp.SendAsync(statusRequest);
        var status = await statusResponse.Content.ReadFromJsonAsync<OfferStatus>();
        if (status?.State == "accepted") { accepted = true; break; }
        await Task.Delay(50);
    }
    Assert(accepted, "receiver can approve a transfer before local cancellation");
    using var chunkRequest = new HttpRequestMessage(HttpMethod.Put, new Uri(statusUri.ToString() + "/files/" + cancelFile.Id + "?offset=0"));
    chunkRequest.Headers.Add("X-EZ-Session", cancelReceipt.Secret);
    chunkRequest.Content = new ByteArrayContent([0x5A]);
    using var chunkResponse = await localCancelHttp.SendAsync(chunkRequest);
    Assert(chunkResponse.IsSuccessStatusCode, "partial receive data is staged before cancellation");
    var stage = Path.Combine(receive, ".ez-incoming", cancelReceipt.Id);
    var stageLease = Path.Combine(receive, ".ez-incoming", cancelReceipt.Id + ".lock");
    Assert(File.Exists(Path.Combine(stage, cancelFile.Id + ".part")), "partial receive file exists in the temporary stage");
    Assert(File.Exists(stageLease), "active receive holds an exclusive stage lease against startup cleanup");
    await server.CancelTransferAsync(cancelReceipt.Id);
    Assert(!Directory.Exists(stage) && !File.Exists(stageLease), "receiver-side cancel immediately deletes partial data and releases its lease");
}
foreach (var invalid in new[] { "../escape.txt", "C:/escape.txt", "dir/../escape", "dir\\escape", "file.txt:stream", "/absolute", "NUL.txt", "folder./a" })
{ try { TransferFiles.ValidateRelativePath(invalid); Assert(false, invalid); } catch (InvalidDataException) { Assert(true, "path rejected " + invalid); } }
server.RevokeLink(invitation.Token);
Assert((await http.GetAsync(invite + "/api/info")).StatusCode == HttpStatusCode.Gone, "revoked invitation unavailable");
var signalShare = server.CreateShare(files, TimeSpan.FromMinutes(10));
var publicSignalShare = new Uri($"http://127.0.0.1:{server.SignalPort}/s/{signalShare.Token}");
Assert((await http.GetAsync(server.LocalSignalOrigin)).StatusCode == HttpStatusCode.NotFound, "signaling origin does not expose a default page");
var tunnelHealthUri = TunnelHealthProbe.CreateUri(new Uri(server.LocalSignalOrigin + "/ignored/base"));
Assert(tunnelHealthUri.AbsolutePath == "/health" && tunnelHealthUri.Host == "127.0.0.1", "Cloudflare tunnel health probe targets the fixed health endpoint");
Assert((await http.GetAsync(tunnelHealthUri)).StatusCode == HttpStatusCode.NoContent, "public tunnel health probe has a dedicated no-content endpoint");
var publicShareHtml = await http.GetStringAsync(publicSignalShare);
Assert(publicShareHtml.Contains("STUN", StringComparison.Ordinal) && publicShareHtml.Contains("外部IPアドレス", StringComparison.Ordinal),
    "browser share page explains STUN address visibility and direct file transfer");
Assert(publicShareHtml.Contains("window.EZTurnConfiguration", StringComparison.Ordinal) && !publicShareHtml.Contains("integration-shared-secret-123", StringComparison.Ordinal), "signaling-only public share page receives only temporary TURN credentials");
Assert((await http.GetAsync(publicSignalShare + "/files/" + files[0].File.Id)).StatusCode == HttpStatusCode.NotFound, "public signaling port cannot serve file bytes");
var signalInvite = server.CreateInvitation(TimeSpan.FromMinutes(10));
var invitePage = await http.GetStringAsync(server.PeerLink(signalInvite, "127.0.0.1"));
Assert(invitePage.Contains("STUN", StringComparison.Ordinal) && invitePage.Contains("外部IPアドレス", StringComparison.Ordinal),
    "browser invitation explains STUN address visibility and direct file transfer");
Assert(invitePage.Contains("window.EZTurnConfiguration", StringComparison.Ordinal) && !invitePage.Contains("integration-shared-secret-123", StringComparison.Ordinal),
    "browser invitation receives temporary TURN credentials without exposing the shared secret");
var publicSignalInvite = new Uri($"http://127.0.0.1:{server.SignalPort}/i/{signalInvite.Token}/api/offers");
Assert((await http.GetAsync(publicSignalInvite)).StatusCode == HttpStatusCode.NotFound, "public signaling port has no upload API");
var inviteHostPage = server.HostPage(signalInvite);
var inviteHostApi = $"{server.LocalSignalOrigin}/host-api/{signalInvite.Token}/offers";
using (var unauthenticated = await http.PostAsJsonAsync(inviteHostApi, new OfferRequest("Browser guest", [files[0].File])))
    Assert(unauthenticated.StatusCode == HttpStatusCode.NotFound, "incoming P2P bridge rejects missing host credential");
using (var offerRequest = new HttpRequestMessage(HttpMethod.Post, inviteHostApi))
{
    offerRequest.Headers.Add("X-EZ-Host", inviteHostPage.Fragment[1..]);
    offerRequest.Content = System.Net.Http.Json.JsonContent.Create(new OfferRequest("Browser guest", [files[0].File]));
    using var offerResponse = await http.SendAsync(offerRequest);
    var incomingReceipt = await offerResponse.Content.ReadFromJsonAsync<OfferReceipt>();
    Assert(offerResponse.IsSuccessStatusCode && incomingReceipt is not null, "authorized browser P2P offer reaches the receiver approval flow");
    using var cancel = new HttpRequestMessage(HttpMethod.Delete, inviteHostApi + "/" + incomingReceipt!.Id);
    cancel.Headers.Add("X-EZ-Host", inviteHostPage.Fragment[1..]);
    cancel.Headers.Add("X-EZ-Session", incomingReceipt.Secret);
    Assert((await http.SendAsync(cancel)).IsSuccessStatusCode, "browser P2P offer can be cancelled safely");
}
var hostPage = server.HostPage(signalShare);
var hostPageHtml = await http.GetStringAsync(new Uri(hostPage.GetLeftPart(UriPartial.Path)));
Assert(hostPageHtml.Contains("STUN", StringComparison.Ordinal) && hostPageHtml.Contains("外部IPアドレス", StringComparison.Ordinal),
    "host P2P page explains STUN address visibility and that it is not the file transfer path");
Assert(hostPage.Fragment.Length == 65 && hostPageHtml.Contains("window.EZTurnConfiguration", StringComparison.Ordinal) &&
       !hostPageHtml.Contains("integration-shared-secret-123", StringComparison.Ordinal), "host-only page keeps its host secret in the URL fragment and omits the TURN shared secret");
var hostApi = new Uri($"{server.LocalSignalOrigin}/host-api/{signalShare.Token}");
var unauthenticatedHostInfo = await http.GetAsync(hostApi + "/info");
Assert(unauthenticatedHostInfo.StatusCode == HttpStatusCode.NotFound, "host file API rejects requests without local credential (HTTP " + (int)unauthenticatedHostInfo.StatusCode + ")");
using (var request = new HttpRequestMessage(HttpMethod.Get, hostApi + "/info"))
{
    request.Headers.Add("X-EZ-Host", hostPage.Fragment[1..]);
    Assert((await http.SendAsync(request)).IsSuccessStatusCode, "host page authenticates with fragment-only credential");
}
var hostApiFile = files.First(f => f.File.Length > 64);
const int hostApiOffset = 11, hostApiLength = 53;
using (var request = new HttpRequestMessage(HttpMethod.Get, hostApi + $"/files/{hostApiFile.File.Id}?offset={hostApiOffset}&length={hostApiLength}"))
{
    request.Headers.Add("X-EZ-Host", hostPage.Fragment[1..]);
    var actual = await (await http.SendAsync(request)).Content.ReadAsByteArrayAsync();
    var expected = (await File.ReadAllBytesAsync(hostApiFile.SourcePath)).AsSpan(hostApiOffset, hostApiLength).ToArray();
    Assert(actual.SequenceEqual(expected), "host-only file chunks preserve exact bytes");
}
Assert((await http.GetStringAsync(new Uri(server.LocalSignalOrigin + "/host.js"))).Contains("new RTCPeerConnection", StringComparison.Ordinal), "host P2P page uses WebRTC");
Assert((await http.GetStringAsync(new Uri(server.LocalSignalOrigin + "/ice.js"))).Contains("EZRemoteIceQueue", StringComparison.Ordinal), "early ICE candidates are buffered until the remote description arrives");
var shareScript = await http.GetStringAsync(new Uri(server.LocalSignalOrigin + "/share.js"));
Assert(shareScript.Contains("type: 'download'", StringComparison.Ordinal), "browser share requests file data over the peer channel");
Assert(shareScript.Contains("data.type === 'queued'", StringComparison.Ordinal) && shareScript.Contains("waitingForPeerSlot", StringComparison.Ordinal) &&
       shareScript.Contains("data.type === 'queue-full'", StringComparison.Ordinal), "browser share explains first-in-first-out waiting and full-queue handling");
Assert(shareScript.Contains("showDirectoryPicker") && (await http.GetStringAsync(new Uri(server.LocalSignalOrigin + "/s/" + signalShare.Token))).Contains("downloadFolder", StringComparison.Ordinal), "browser share can save multiple files with folder structure preserved");
var inviteScript = await http.GetStringAsync(new Uri(server.LocalSignalOrigin + "/invite.js"));
Assert(inviteScript.Contains("type:'offer-file'", StringComparison.Ordinal), "browser invitation sends file metadata over the peer channel");
Assert(inviteScript.Contains("順番待ちです", StringComparison.Ordinal), "browser invitation shows its server-managed queue position");
var hostSignalUrl = new UriBuilder("ws", "127.0.0.1", server.SignalPort, $"/rtc/{signalShare.Token}") { Query = "role=host" }.Uri;
using (var invalidHost = new ClientWebSocket())
{
    await invalidHost.ConnectAsync(hostSignalUrl, CancellationToken.None);
    await invalidHost.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("{\"type\":\"auth\",\"secret\":\"" + new string('0', 64) + "\"}")), WebSocketMessageType.Text, true, CancellationToken.None);
    var close = await invalidHost.ReceiveAsync(new ArraySegment<byte>(new byte[256]), CancellationToken.None);
    Assert(close.MessageType == WebSocketMessageType.Close && close.CloseStatus == WebSocketCloseStatus.PolicyViolation, "public host role requires local secret");
}
using (var hostSocket = new ClientWebSocket())
using (var guestSocket = new ClientWebSocket())
{
    await hostSocket.ConnectAsync(hostSignalUrl, CancellationToken.None);
    await hostSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "auth", secret = hostPage.Fragment[1..] }))), WebSocketMessageType.Text, true, CancellationToken.None);
    await guestSocket.ConnectAsync(new UriBuilder("ws", "127.0.0.1", server.SignalPort, $"/rtc/{signalShare.Token}") { Query = "role=guest" }.Uri, CancellationToken.None);
    Assert(JsonDocument.Parse(await ReadSignalAsync(hostSocket)).RootElement.GetProperty("type").GetString() == "peer-ready", "host peer-ready signal");
    Assert(JsonDocument.Parse(await ReadSignalAsync(guestSocket)).RootElement.GetProperty("type").GetString() == "peer-ready", "guest peer-ready signal");
    const string offer = "{\"type\":\"offer\",\"sdp\":\"v=0\\r\\n" + "m=application 9 UDP/DTLS/SCTP webrtc-datachannel\\r\\n" + "\"}";
    await hostSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(offer)), WebSocketMessageType.Text, true, CancellationToken.None);
    Assert(await ReadSignalAsync(guestSocket) == offer, "SDP offer forwarded between peers");
    const string candidate = "{\"type\":\"candidate\",\"candidate\":\"candidate:1 1 UDP 1 192.0.2.1 5000 typ host\",\"sdpMid\":\"0\",\"sdpMLineIndex\":0}";
    await guestSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(candidate)), WebSocketMessageType.Text, true, CancellationToken.None);
    Assert(await ReadSignalAsync(hostSocket) == candidate, "ICE candidate forwarded between peers");
    var shareGuestUrl = new UriBuilder("ws", "127.0.0.1", server.SignalPort, $"/rtc/{signalShare.Token}") { Query = "role=guest" }.Uri;
    using var queuedGuest = new ClientWebSocket();
    await queuedGuest.ConnectAsync(shareGuestUrl, CancellationToken.None);
    var queuedMessage = JsonDocument.Parse(await ReadSignalAsync(queuedGuest));
    Assert(queuedMessage.RootElement.GetProperty("type").GetString() == "queued" &&
           queuedMessage.RootElement.GetProperty("position").GetInt32() == 1 && queuedGuest.State == WebSocketState.Open,
        "a second browser receiver is kept in a first-in-first-out wait queue");
    using var canceledQueuedGuest = new ClientWebSocket();
    await canceledQueuedGuest.ConnectAsync(shareGuestUrl, CancellationToken.None);
    var canceledQueueMessage = JsonDocument.Parse(await ReadSignalAsync(canceledQueuedGuest));
    Assert(canceledQueueMessage.RootElement.GetProperty("type").GetString() == "queued" &&
           canceledQueueMessage.RootElement.GetProperty("position").GetInt32() == 2,
        "additional receivers receive their FIFO position from the temporary sender server");
    using var lastQueuedGuest = new ClientWebSocket();
    await lastQueuedGuest.ConnectAsync(shareGuestUrl, CancellationToken.None);
    var lastQueueMessage = JsonDocument.Parse(await ReadSignalAsync(lastQueuedGuest));
    Assert(lastQueueMessage.RootElement.GetProperty("type").GetString() == "queued" &&
           lastQueueMessage.RootElement.GetProperty("position").GetInt32() == 3,
        "the server preserves order for multiple waiting receivers");
    canceledQueuedGuest.Abort();
    await guestSocket.SendAsync(new ArraySegment<byte>([1, 2, 3, 4]), WebSocketMessageType.Binary, true, CancellationToken.None);
    var close = await guestSocket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), CancellationToken.None);
    Assert(close.MessageType == WebSocketMessageType.Close && close.CloseStatus == WebSocketCloseStatus.PolicyViolation, "binary file payload rejected on signaling socket");
    await guestSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "close acknowledged", CancellationToken.None);
    Assert(JsonDocument.Parse(await ReadSignalAsync(hostSocket)).RootElement.GetProperty("type").GetString() == "peer-left",
        "the active browser receiver releases its peer slot after disconnecting");
    var hostReady = JsonDocument.Parse(await ReadSignalAsync(hostSocket)).RootElement.GetProperty("type").GetString();
    var queuedReady = JsonDocument.Parse(await ReadSignalAsync(queuedGuest)).RootElement.GetProperty("type").GetString();
    Assert(hostReady == "peer-ready" && queuedReady == "peer-ready",
        "the first queued browser receiver is promoted automatically after the active receiver leaves");
    const string nextOffer = "{\"type\":\"offer\",\"sdp\":\"v=0\\r\\nnext session\\r\\n\"}";
    await queuedGuest.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(nextOffer)), WebSocketMessageType.Text, true, CancellationToken.None);
    Assert(await ReadSignalAsync(hostSocket) == nextOffer,
        "the server forwards signaling to the promoted queued receiver");
    await queuedGuest.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    Assert(JsonDocument.Parse(await ReadSignalAsync(hostSocket)).RootElement.GetProperty("type").GetString() == "peer-left",
        "a promoted queued receiver releases the peer slot when it leaves");
    var lastHostReady = JsonDocument.Parse(await ReadSignalAsync(hostSocket)).RootElement.GetProperty("type").GetString();
    var lastGuestReady = JsonDocument.Parse(await ReadSignalAsync(lastQueuedGuest)).RootElement.GetProperty("type").GetString();
    Assert(lastHostReady == "peer-ready" && lastGuestReady == "peer-ready",
        "closing a waiting receiver does not block the next live receiver in the FIFO queue");
    const string lastOffer = "{\"type\":\"offer\",\"sdp\":\"v=0\\r\\nlast queued session\\r\\n\"}";
    await lastQueuedGuest.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(lastOffer)), WebSocketMessageType.Text, true, CancellationToken.None);
    Assert(await ReadSignalAsync(hostSocket) == lastOffer,
        "the server forwards signaling after skipping a disconnected queued receiver");
}
var shutdownUrl = "";
await using (var shutdownProbe = new TransferServer(new()
{
    ReceiveDirectory = Path.Combine(root, "shutdown-received"),
    StateDirectory = Path.Combine(root, "shutdown-state"),
    BindAddress = IPAddress.Loopback
}))
{
    await shutdownProbe.StartAsync();
    shutdownUrl = shutdownProbe.LocalSignalOrigin + "/health";
    using var shutdownHttp = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
    Assert((await shutdownHttp.GetAsync(shutdownUrl)).StatusCode == HttpStatusCode.NoContent, "temporary sender server is reachable while running");
    await shutdownProbe.DisposeAsync();
    try
    {
        using var response = await shutdownHttp.GetAsync(shutdownUrl);
        Assert(false, "disposed sender server must stop listening");
    }
    catch (HttpRequestException) { Assert(true, "disposing the temporary sender server closes its listener"); }
}
CleanupSuccessfulWorkDirectory(root, "sharing-tests");
Console.WriteLine($"INTEGRATION PASSED: {pass}");

async Task VerifyFolder(string directory, IReadOnlyList<LocalFile>? expectedFiles = null)
{
    foreach (var file in expectedFiles ?? files)
    {
        await using var receivedFile = File.OpenRead(TransferFiles.UnderDirectory(directory, file.File.RelativePath));
        Assert(Convert.ToHexString(await SHA256.HashDataAsync(receivedFile)) == file.File.Sha256, "received hash " + file.File.RelativePath);
    }
}

async Task VerifyPeerDiscovery()
{
    Assert(PeerDiscovery.CalculateBroadcastAddress(IPAddress.Parse("192.168.1.37"), IPAddress.Parse("255.255.255.0")).Equals(IPAddress.Parse("192.168.1.255")),
        "LAN discovery calculates a /24 directed broadcast address");
    Assert(PeerDiscovery.CalculateBroadcastAddress(IPAddress.Parse("10.24.17.19"), IPAddress.Parse("255.255.240.0")).Equals(IPAddress.Parse("10.24.31.255")),
        "LAN discovery calculates a non-/24 directed broadcast address");
    var probeHosts = PeerDiscovery.EnumerateHostAddresses(IPAddress.Parse("192.168.1.37"), IPAddress.Parse("255.255.255.0"), 512);
    Assert(probeHosts.Count == 253 && !probeHosts.Contains(IPAddress.Parse("192.168.1.0")) &&
           !probeHosts.Contains(IPAddress.Parse("192.168.1.255")) && !probeHosts.Contains(IPAddress.Parse("192.168.1.37")),
        "legacy LocalSend fallback enumerates usable subnet hosts without the network, broadcast, or local address");
    var largeSubnetHosts = PeerDiscovery.EnumerateHostAddresses(IPAddress.Parse("10.24.80.125"), IPAddress.Parse("255.255.0.0"), 8);
    Assert(largeSubnetHosts.SequenceEqual(new[]
        {
            IPAddress.Parse("10.24.80.124"), IPAddress.Parse("10.24.80.126"),
            IPAddress.Parse("10.24.80.123"), IPAddress.Parse("10.24.80.127"),
            IPAddress.Parse("10.24.80.122"), IPAddress.Parse("10.24.80.128"),
            IPAddress.Parse("10.24.80.121"), IPAddress.Parse("10.24.80.129")
        }), "large-subnet LocalSend fallback spends its bounded scan budget near the current PC first");
    var alreadyFoundPeer = new PeerDevice("Found LocalSend peer", "192.168.1.10", 53317, "fixture", DateTimeOffset.UtcNow, "localsend/2");
    Assert(!PeerDiscovery.ShouldProbeLegacyAddress([alreadyFoundPeer], IPAddress.Parse("192.168.1.10")) &&
           PeerDiscovery.ShouldProbeLegacyAddress([alreadyFoundPeer], IPAddress.Parse("192.168.1.11")),
        "finding one LocalSend peer does not stop fallback discovery of other LAN hosts");

    int discoveryPort;
    using (var portProbe = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Any, 0)))
        discoveryPort = ((IPEndPoint)portProbe.Client.LocalEndPoint!).Port;

    var lanReceive = Path.Combine(root, "multicast-received");
    await using var senderServer = new TransferServer(new()
    {
        DeviceName = "Integration LAN Sender", ReceiveDirectory = Path.Combine(root, "lan-sender-received"),
        StateDirectory = Path.Combine(root, "lan-sender-state"), BindAddress = IPAddress.Any
    });
    await using var receiverServer = new TransferServer(new()
    {
        DeviceName = "Integration LAN Receiver", ReceiveDirectory = lanReceive,
        StateDirectory = Path.Combine(root, "lan-receiver-state"), BindAddress = IPAddress.Any
    });
    var secondLanReceive = Path.Combine(root, "multicast-received-second");
    await using var secondReceiverServer = new TransferServer(new()
    {
        DeviceName = "Integration LAN Receiver 2", ReceiveDirectory = secondLanReceive,
        StateDirectory = Path.Combine(root, "lan-receiver-state-second"), BindAddress = IPAddress.Any
    });
    senderServer.Faulted += exception => Console.Error.WriteLine("LAN SENDER FAULT: " + exception);
    receiverServer.Faulted += exception => Console.Error.WriteLine("LAN RECEIVER FAULT: " + exception);
    secondReceiverServer.Faulted += exception => Console.Error.WriteLine("SECOND LAN RECEIVER FAULT: " + exception);
    receiverServer.Incoming += offer => offer.Accept();
    secondReceiverServer.Incoming += offer => offer.Accept();
    await senderServer.StartAsync();
    await receiverServer.StartAsync();
    await secondReceiverServer.StartAsync();

    var legacyHttpsBuilder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
    legacyHttpsBuilder.Logging.ClearProviders();
    legacyHttpsBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
        listen => listen.UseHttps(senderServer.LocalSendClientCertificate)));
    await using var legacyHttpsApp = legacyHttpsBuilder.Build();
    var legacyHttpsPort = 0;
    legacyHttpsApp.MapPost("/api/localsend/v2/register", (HttpContext _) => Results.Json(
        new LocalSendDeviceInfo("Legacy HTTPS fixture", Fingerprint: "json-fingerprint-is-ignored", Port: legacyHttpsPort, Protocol: "https")));
    await legacyHttpsApp.StartAsync();
    var legacyHttpsAddresses = legacyHttpsApp.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
        .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
        ?? throw new InvalidOperationException("HTTPS discovery fixture did not expose its listener address.");
    legacyHttpsPort = new Uri(legacyHttpsAddresses.Addresses.Single(address => address.StartsWith("https:", StringComparison.Ordinal))).Port;

    await using (var legacyHttpsDiscovery = new PeerDiscovery(
        new LocalSendDeviceInfo("LocalSend legacy scanner", Fingerprint: "scanner-fingerprint", Port: 53317, Protocol: "https"),
        discoveryPort, enableMulticast: false, enableBroadcast: false))
    {
        var legacyHttpsPeerSeen = new TaskCompletionSource<PeerDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        legacyHttpsDiscovery.Changed += peers =>
        {
            var peer = peers.FirstOrDefault(item => item.Name == "Legacy HTTPS fixture");
            if (peer is not null) legacyHttpsPeerSeen.TrySetResult(peer);
        };
        await legacyHttpsDiscovery.ProbeLegacyDeviceAsync(IPAddress.Loopback, "https", legacyHttpsPort,
            timeoutOverride: TimeSpan.FromSeconds(5));
        var legacyHttpsPeer = await legacyHttpsPeerSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var certificateFingerprint = Convert.ToHexString(SHA256.HashData(senderServer.LocalSendClientCertificate.RawData));
        Assert(legacyHttpsPeer.Fingerprint == certificateFingerprint && legacyHttpsPeer.Port == legacyHttpsPort &&
               legacyHttpsPeer.ConnectionUrl == $"https://127.0.0.1:{legacyHttpsPort}/#{certificateFingerprint}",
            "legacy HTTPS discovery ignores the JSON fingerprint and pins the certificate actually observed over TLS");
    }

    var globalIpv6 = NetworkInterface.GetAllNetworkInterfaces()
        .Where(network => network.OperationalStatus == OperationalStatus.Up)
        .SelectMany(network => network.GetIPProperties().UnicastAddresses)
        .Select(unicast => unicast.Address)
        .FirstOrDefault(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
            !IPAddress.IsLoopback(address) && !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6Multicast &&
            (address.GetAddressBytes()[0] & 0xFE) != 0xFC);
    if (globalIpv6 is not null)
    {
        using var publicAddressHandler = new HttpClientHandler { UseProxy = false };
        using var publicAddressHttp = new HttpClient(publicAddressHandler) { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var publicAddressResponse = await publicAddressHttp.GetAsync($"http://[{globalIpv6}]:{senderServer.HttpPort}/");
            Assert(publicAddressResponse.StatusCode == HttpStatusCode.NotFound,
                "main LocalSend listener denies requests addressed through a public IPv6 interface");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        { Console.WriteLine("SKIP: host network does not permit a loopback request through its public IPv6 address"); }
    }
    var ipv6Peer = new PeerDevice("IPv6 fixture", "fd12:3456:789a::5", 53317, new string('A', 64),
        DateTimeOffset.UtcNow, "localsend/2", "https");
    var ipv6PeerUri = new Uri(ipv6Peer.ConnectionUrl);
    Assert(ipv6PeerUri.AbsoluteUri.StartsWith("https://[fd12:3456:789a::5]:53317/", StringComparison.Ordinal) &&
           TransferServer.FormatUriHost("fd12:3456:789a::5") == "[fd12:3456:789a::5]",
        "IPv6 LocalSend peers and share URLs use bracketed URI literals");
    if (System.Net.Sockets.Socket.OSSupportsIPv6)
    {
        using var ipv6Handler = new HttpClientHandler { UseProxy = false };
        using var ipv6Http = new HttpClient(ipv6Handler);
        using var ipv6Response = await ipv6Http.GetAsync($"http://[::1]:{senderServer.HttpPort}/");
        Assert(ipv6Response.IsSuccessStatusCode,
            "default sender server accepts IPv6 loopback while retaining its IPv4 listener");
    }
    Assert(senderServer.LocalSendInfo.Fingerprint == Convert.ToHexString(SHA256.HashData(senderServer.LocalSendClientCertificate.RawData)),
        "LocalSend device identity fingerprint is derived from its client/server certificate");

    await using var senderDiscovery = new PeerDiscovery(senderServer.LocalSendInfo, discoveryPort, senderServer.LocalSendClientCertificate);
    await using var receiverDiscovery = new PeerDiscovery(receiverServer.LocalSendInfo, discoveryPort, receiverServer.LocalSendClientCertificate);
    var senderSawReceiver = new TaskCompletionSource<PeerDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
    var senderSawReceiverIpv6 = new TaskCompletionSource<PeerDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
    var receiverSawSender = new TaskCompletionSource<PeerDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
    senderDiscovery.Changed += peers =>
    {
        var peer = peers.FirstOrDefault(item => item.Fingerprint == receiverServer.Fingerprint);
        if (peer is not null)
        {
            senderSawReceiver.TrySetResult(peer);
            if (IPAddress.TryParse(peer.Address, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                senderSawReceiverIpv6.TrySetResult(peer);
        }
    };
    receiverDiscovery.Changed += peers =>
    {
        var peer = peers.FirstOrDefault(item => item.Fingerprint == senderServer.Fingerprint);
        if (peer is not null) receiverSawSender.TrySetResult(peer);
    };
    senderDiscovery.Start();
    receiverDiscovery.Start();
    var discovered = await Task.WhenAll(
        senderSawReceiver.Task.WaitAsync(TimeSpan.FromSeconds(15)),
        receiverSawSender.Task.WaitAsync(TimeSpan.FromSeconds(15)));
    var receiver = discovered[0];
    var sender = discovered[1];
    Assert(receiver.Name == receiverServer.DeviceName && receiver.Port == receiverServer.HttpsPort && receiver.Fingerprint == receiverServer.Fingerprint && receiver.IsLocalSend &&
        sender.Name == senderServer.DeviceName && sender.Port == senderServer.HttpsPort && sender.Fingerprint == senderServer.Fingerprint && sender.IsLocalSend,
        "LocalSend v2 multicast discovers both peers and preserves their HTTPS certificate fingerprints");

    if (senderDiscovery.Ipv6MulticastSocketCount > 0 && receiverDiscovery.Ipv6MulticastSocketCount > 0)
    {
        var ipv6Receiver = await senderSawReceiverIpv6.Task.WaitAsync(TimeSpan.FromSeconds(8));
        var ipv6ProbePath = Path.Combine(root, "ipv6-multicast-transfer.bin");
        var ipv6ProbeBytes = RandomNumberGenerator.GetBytes(64 * 1024 + 19);
        await File.WriteAllBytesAsync(ipv6ProbePath, ipv6ProbeBytes);
        var ipv6ProbeFiles = await TransferFiles.CollectAsync([ipv6ProbePath]);
        await LocalSendClient.SendAsync(ipv6Receiver.ConnectionUrl, ipv6ProbeFiles, senderServer.DeviceName,
            senderInfo: senderServer.LocalSendInfo, clientCertificate: senderServer.LocalSendClientCertificate);
        var ipv6ReceivedPath = Directory.EnumerateFiles(lanReceive, "ipv6-multicast-transfer.bin", SearchOption.AllDirectories).SingleOrDefault();
        Assert(ipv6ReceivedPath is not null &&
               Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(ipv6ReceivedPath))) == Convert.ToHexString(SHA256.HashData(ipv6ProbeBytes)),
            "LocalSend IPv6 multicast discovery produces a usable IPv6 route for a verified transfer");
        await Task.Delay(TimeSpan.FromMilliseconds(1100)); // The receiver rate-limits new sessions from the same IP to one per second.
    }

    var url = new Uri(receiver.ConnectionUrl);
    Assert(url.Scheme == "https" && url.Port == receiverServer.HttpsPort && url.Fragment == "#" + receiverServer.Fingerprint && IPAddress.TryParse(receiver.Address, out _),
        "discovered LocalSend peer produces a fingerprint-pinned HTTPS endpoint");
    var externalPeerSeen = new TaskCompletionSource<PeerDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
    senderDiscovery.Changed += peers =>
    {
        var peer = peers.FirstOrDefault(item => item.Name == "External LocalSend fixture");
        if (peer is not null) externalPeerSeen.TrySetResult(peer);
    };
    var externalAnnouncement = """
        {"alias":"External LocalSend fixture","version":"2.0","deviceModel":"Linux","deviceType":"desktop","fingerprint":"external-http-fingerprint","port":53317,"protocol":"http","download":false,"announce":false}
        """;
    using (var externalUdp = new System.Net.Sockets.UdpClient())
        await externalUdp.SendAsync(Encoding.UTF8.GetBytes(externalAnnouncement), new IPEndPoint(IPAddress.Loopback, discoveryPort));
    var externalPeer = await externalPeerSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Assert(externalPeer.IsLocalSend && externalPeer.TransportProtocol == "http" && externalPeer.Port == 53317 &&
           externalPeer.Fingerprint == "external-http-fingerprint",
        "LocalSend discovery accepts an independently encoded camelCase v2 announcement");

    var smallSourcePath = Path.Combine(root, "localsend-metadata-roundtrip.txt");
    await File.WriteAllTextAsync(smallSourcePath, "LocalSend metadata round-trip fixture.", Encoding.UTF8);
    var expectedModified = new DateTime(2022, 4, 5, 6, 7, 8, DateTimeKind.Utc);
    File.SetLastWriteTimeUtc(smallSourcePath, expectedModified);
    var smallFiles = await TransferFiles.CollectAsync([smallSourcePath]);
    async Task SendToPeerAsync(PeerDevice target)
    {
        await LocalSendClient.SendAsync(target.ConnectionUrl, smallFiles, senderServer.DeviceName,
            senderInfo: senderServer.LocalSendInfo,
            progress: null,
            cancellationToken: CancellationToken.None,
            targetName: target.Name,
            clientCertificate: senderServer.LocalSendClientCertificate);
    }
    var secondTarget = new PeerDevice(secondReceiverServer.DeviceName, "127.0.0.1", secondReceiverServer.HttpsPort,
        secondReceiverServer.Fingerprint, DateTimeOffset.UtcNow, "localsend/2", "https");
    await Task.WhenAll(
        SendToPeerAsync(receiver),
        SendToPeerAsync(secondTarget));

    using var mismatchedClientKey = RSA.Create(2048);
    var mismatchedClientRequest = new CertificateRequest("CN=LocalSend User", mismatchedClientKey,
        HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var generatedMismatchedClientCertificate = mismatchedClientRequest.CreateSelfSigned(
        DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    using var mismatchedClientCertificate = new X509Certificate2(generatedMismatchedClientCertificate.Export(X509ContentType.Pfx));
    await Task.Delay(TimeSpan.FromSeconds(1.1));
    using var mismatchHandler = new HttpClientHandler
    {
        UseProxy = false,
        ClientCertificateOptions = ClientCertificateOption.Manual,
        ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
            Convert.ToHexString(SHA256.HashData(certificate.RawData)).Equals(receiverServer.Fingerprint, StringComparison.OrdinalIgnoreCase)
    };
    mismatchHandler.ClientCertificates.Add(mismatchedClientCertificate);
    using var mismatchHttp = new HttpClient(mismatchHandler);
    var priorReceiveFolders = Directory.GetDirectories(lanReceive).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var mismatchEndpoint = new UriBuilder(receiver.ConnectionUrl)
    {
        Fragment = "",
        Path = "/api/localsend/v2/prepare-upload"
    }.Uri;
    var remoteModified = new DateTimeOffset(2020, 11, 12, 13, 14, 15, TimeSpan.Zero);
    var mismatchedMetadata = smallFiles.ToDictionary(item => item.File.Id,
        item => new LocalSendFileMetadata(item.File.Id, Path.GetFileName(item.File.RelativePath), item.File.Length,
            "text/plain", item.File.Sha256,
            Metadata: new LocalSendFileMetadataTimes(remoteModified, null)), StringComparer.Ordinal);
    using var mismatchResponse = await mismatchHttp.PostAsJsonAsync(mismatchEndpoint,
        new LocalSendPrepareUploadRequest(senderServer.LocalSendInfo, mismatchedMetadata));
    Assert(mismatchResponse.StatusCode == HttpStatusCode.OK,
        $"HTTPS LocalSend accepts a self-signed client certificate without EKU and uses its TLS identity (HTTP {(int)mismatchResponse.StatusCode})");
    var mismatchReceipt = await mismatchResponse.Content.ReadFromJsonAsync<LocalSendPrepareUploadResponse>()
        ?? throw new Exception("HTTPS LocalSend did not return a prepared upload session.");
    var uploadUri = new UriBuilder(mismatchEndpoint)
    {
        Path = "/api/localsend/v2/upload",
        Query = "sessionId=" + Uri.EscapeDataString(mismatchReceipt.SessionId) +
            "&fileId=" + Uri.EscapeDataString(smallFiles[0].File.Id) +
            "&token=" + Uri.EscapeDataString(mismatchReceipt.Files[smallFiles[0].File.Id])
    }.Uri;
    using var wrongIdentityHandler = new HttpClientHandler
    {
        UseProxy = false,
        ClientCertificateOptions = ClientCertificateOption.Manual,
        ServerCertificateCustomValidationCallback = mismatchHandler.ServerCertificateCustomValidationCallback
    };
    wrongIdentityHandler.ClientCertificates.Add(senderServer.LocalSendClientCertificate);
    using var wrongIdentityHttp = new HttpClient(wrongIdentityHandler);
    using var wrongIdentityUpload = await wrongIdentityHttp.PostAsync(uploadUri,
        new ByteArrayContent(await File.ReadAllBytesAsync(smallFiles[0].SourcePath)));
    Assert(wrongIdentityUpload.StatusCode == HttpStatusCode.Forbidden,
        "LocalSend upload session remains bound to the mTLS certificate even if the JSON fingerprint differs");
    using var validIdentityUpload = await mismatchHttp.PostAsync(uploadUri,
        new ByteArrayContent(await File.ReadAllBytesAsync(smallFiles[0].SourcePath)));
    Assert(validIdentityUpload.StatusCode == HttpStatusCode.OK,
        "LocalSend transfer succeeds when the prepared TLS certificate identity is preserved");
    Assert(Directory.GetDirectories(secondLanReceive).Length > 0, "same file batch can send concurrently to multiple LAN peers");
    var receivedFolder = priorReceiveFolders.Single(directory =>
        File.Exists(Path.Combine(directory, Path.GetFileName(smallFiles[0].SourcePath))));
    var secondReceivedFolder = Directory.GetDirectories(secondLanReceive).Single(directory => Path.GetFileName(directory).StartsWith("LocalSend-", StringComparison.Ordinal));
    foreach (var (folder, label) in new[] { (receivedFolder, "discovered receiver"), (secondReceivedFolder, "second receiver") })
    {
        var receivedFile = Path.Combine(folder, Path.GetFileName(smallFiles[0].SourcePath));
        Assert(Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(receivedFile))) == smallFiles[0].File.Sha256,
            $"LocalSend upload verifies the received file hash ({label})");
        Assert(Math.Abs((File.GetLastWriteTimeUtc(receivedFile) - expectedModified).TotalSeconds) < 1,
            $"LocalSend sender sends and receiver preserves the modified timestamp ({label})");
    }
    var identityBoundFolder = Directory.GetDirectories(lanReceive).Single(directory => !priorReceiveFolders.Contains(directory));
    var identityBoundFile = Path.Combine(identityBoundFolder, Path.GetFileName(smallFiles[0].SourcePath));
    Assert(Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(identityBoundFile))) == smallFiles[0].File.Sha256,
        "LocalSend upload preserves bytes when certificate identity is authoritative over the JSON fingerprint");
    Assert(Math.Abs((File.GetLastWriteTimeUtc(identityBoundFile) - remoteModified.UtcDateTime).TotalSeconds) < 1,
        "LocalSend receiver applies a supplied metadata.modified value from an independent sender");

    var originalHash = smallFiles[0].File.Sha256;
    var incorrectHash = (originalHash[0] == '0' ? '1' : '0') + originalHash[1..];
    var incorrectHashMetadata = new Dictionary<string, LocalSendFileMetadata>(StringComparer.Ordinal)
    {
        [smallFiles[0].File.Id] = new(smallFiles[0].File.Id, Path.GetFileName(smallFiles[0].File.RelativePath),
            smallFiles[0].File.Length, "text/plain", incorrectHash)
    };
    await Task.Delay(TimeSpan.FromSeconds(1.1)); // Respect the receiver's per-address prepare-upload rate limit.
    using var incorrectHashPrepare = await mismatchHttp.PostAsJsonAsync(mismatchEndpoint,
        new LocalSendPrepareUploadRequest(senderServer.LocalSendInfo, incorrectHashMetadata));
    incorrectHashPrepare.EnsureSuccessStatusCode();
    var incorrectHashReceipt = await incorrectHashPrepare.Content.ReadFromJsonAsync<LocalSendPrepareUploadResponse>()
        ?? throw new Exception("LocalSend checksum test did not receive a session.");
    var incorrectHashUploadUri = new UriBuilder(mismatchEndpoint)
    {
        Path = "/api/localsend/v2/upload",
        Query = "sessionId=" + Uri.EscapeDataString(incorrectHashReceipt.SessionId) +
            "&fileId=" + Uri.EscapeDataString(smallFiles[0].File.Id) +
            "&token=" + Uri.EscapeDataString(incorrectHashReceipt.Files[smallFiles[0].File.Id])
    }.Uri;
    using var checksumMismatchResponse = await mismatchHttp.PostAsync(incorrectHashUploadUri,
        new ByteArrayContent(await File.ReadAllBytesAsync(smallFiles[0].SourcePath)));
    var incorrectHashStage = Path.Combine(lanReceive, ".localsend-incoming", incorrectHashReceipt.SessionId);
    Assert(checksumMismatchResponse.StatusCode == HttpStatusCode.UnprocessableEntity &&
           Directory.Exists(incorrectHashStage) && Directory.GetFiles(incorrectHashStage, "*", SearchOption.AllDirectories).Length == 0,
        "LocalSend v2.2 returns HTTP 422 for a SHA-256 mismatch and removes the rejected payload");
    using var cancelIncorrectHash = await mismatchHttp.PostAsync(new UriBuilder(mismatchEndpoint)
    {
        Path = "/api/localsend/v2/cancel",
        Query = "sessionId=" + Uri.EscapeDataString(incorrectHashReceipt.SessionId)
    }.Uri, null);
    Assert(cancelIncorrectHash.StatusCode == HttpStatusCode.NoContent && !Directory.Exists(incorrectHashStage),
        "LocalSend checksum-mismatch session can be cancelled and its staging folder is removed");

    var priorBatchFolders = Directory.GetDirectories(secondLanReceive).ToHashSet(StringComparer.OrdinalIgnoreCase);
    await Task.Delay(TimeSpan.FromSeconds(1.1));
    await LocalSendClient.SendAsync(secondTarget.ConnectionUrl, files, senderServer.DeviceName,
        senderInfo: senderServer.LocalSendInfo,
        cancellationToken: CancellationToken.None,
        targetName: secondReceiverServer.DeviceName,
        clientCertificate: senderServer.LocalSendClientCertificate);
    var batchFolder = Directory.GetDirectories(secondLanReceive).Single(directory => !priorBatchFolders.Contains(directory));
    foreach (var file in files)
    {
        var receivedPath = Path.Combine(batchFolder, file.File.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        await using var receivedStream = File.OpenRead(receivedPath);
        Assert(Convert.ToHexString(await SHA256.HashDataAsync(receivedStream)) == file.File.Sha256,
            $"parallel LocalSend batch preserves SHA-256 ({file.File.RelativePath})");
    }

    var fixtureUploads = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
    async Task<(Microsoft.AspNetCore.Builder.WebApplication App, string Origin)> StartFixtureReceiverAsync(HttpStatusCode prepareStatus, string? acceptedFileId = null)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapPost("/api/localsend/v2/prepare-upload", async context =>
        {
            if (prepareStatus == HttpStatusCode.NoContent)
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            var tokens = acceptedFileId is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal) { [acceptedFileId] = "fixture-file-token" };
            await context.Response.WriteAsJsonAsync(new { sessionId = "fixture-session", files = tokens });
        });
        app.MapPost("/api/localsend/v2/upload", async context =>
        {
            using var payload = new MemoryStream();
            await context.Request.Body.CopyToAsync(payload, context.RequestAborted);
            fixtureUploads[context.Request.Query["fileId"].ToString()] = payload.ToArray();
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        });
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Fixture receiver did not expose its listener address.");
        var origin = addresses.Addresses.Single();
        return (app, origin);
    }

    var acceptedSource = files.First(file => file.File.RelativePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
    var declinedSource = acceptedSource with
    {
        File = acceptedSource.File with { Id = Guid.NewGuid().ToString("N"), RelativePath = "not-accepted.txt" }
    };
    var partialBatch = new[] { acceptedSource, declinedSource };
    var partialFixture = await StartFixtureReceiverAsync(HttpStatusCode.OK, acceptedSource.File.Id);
    await using (partialFixture.App)
    {
        var result = await LocalSendClient.SendAsync(partialFixture.Origin, partialBatch, "Fixture Sender",
            new LocalSendDeviceInfo("Fixture Sender", Fingerprint: "fixture-fingerprint", Port: 53317, Protocol: "http"));
        Assert(result.AcceptedFileCount == 1 && result.RejectedFileCount == 1 && result.TransferredBytes == acceptedSource.File.Length && result.IsPartial,
            "LocalSend sender honors partial acceptance from the prepare-upload file-token map");
        Assert(fixtureUploads.Count == 1 && fixtureUploads.TryGetValue(acceptedSource.File.Id, out var acceptedPayload) &&
               acceptedPayload.SequenceEqual(await File.ReadAllBytesAsync(acceptedSource.SourcePath)),
            "LocalSend sender uploads only the file explicitly accepted by the receiver");
    }

    var noTransferFixture = await StartFixtureReceiverAsync(HttpStatusCode.NoContent);
    await using (noTransferFixture.App)
    {
        var result = await LocalSendClient.SendAsync(noTransferFixture.Origin, partialBatch, "Fixture Sender",
            new LocalSendDeviceInfo("Fixture Sender", Fingerprint: "fixture-fingerprint", Port: 53317, Protocol: "http"));
        Assert(result.NoTransferNeeded && result.AcceptedFileCount == 0 && result.RejectedFileCount == 0,
            "LocalSend sender treats the protocol's HTTP 204 prepare-upload response as a successful no-op");
    }

}

static async Task<string> ReadSignalAsync(ClientWebSocket socket)
{
    var buffer = new byte[8192];
    using var stream = new MemoryStream();
    WebSocketReceiveResult result;
    do
    {
        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        if (result.MessageType == WebSocketMessageType.Close) throw new IOException("The signaling peer closed unexpectedly.");
        stream.Write(buffer, 0, result.Count);
    } while (!result.EndOfMessage);
    return Encoding.UTF8.GetString(stream.ToArray());
}

}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    Environment.ExitCode = 1;
}

static async Task RunLiveLocalSendDiscoveryAsync()
{
    var root = Path.Combine(Directory.GetCurrentDirectory(), "work", "live-localsend-discovery", Guid.NewGuid().ToString("N"));
    await using var server = new TransferServer(new()
    {
        DeviceName = "EZ Converter Live Interop",
        ReceiveDirectory = Path.Combine(root, "received"),
        StateDirectory = Path.Combine(root, "state")
    });
    await server.StartAsync();
    await using var discovery = new PeerDiscovery(server.LocalSendInfo, clientCertificate: server.LocalSendClientCertificate);
    var foundPeer = new TaskCompletionSource<PeerDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
    discovery.Changed += peers =>
    {
        var peer = peers.FirstOrDefault(item => item.Fingerprint != server.Fingerprint && item.IsLocalSend);
        if (peer is not null) foundPeer.TrySetResult(peer);
    };
    discovery.Warning += warning => Console.Error.WriteLine("DISCOVERY: " + warning);
    discovery.Start();
    Console.WriteLine("Listening for a real LocalSend-compatible device on UDP 53317 for up to 30 seconds...");
    var discovered = await foundPeer.Task.WaitAsync(TimeSpan.FromSeconds(30));
    Console.WriteLine($"PASS live LocalSend discovery + HTTPS registration: protocol={discovered.TransportProtocol}, port={discovered.Port}");
    CleanupSuccessfulWorkDirectory(root, "live-localsend-discovery");
}

static void CleanupSuccessfulWorkDirectory(string path, string category)
{
    var expectedParent = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "work", category));
    var fullPath = Path.GetFullPath(path);
    if (!string.Equals(Path.GetDirectoryName(fullPath), expectedParent, StringComparison.OrdinalIgnoreCase) ||
        !Guid.TryParse(Path.GetFileName(fullPath), out _))
        throw new InvalidOperationException("Generated test cleanup refused an unexpected path: " + fullPath);

    try { if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Successful test artifacts were retained at '{fullPath}' because cleanup failed: {error.Message}");
    }
}

sealed class CallbackHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        callback(request, cancellationToken);
}

sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
