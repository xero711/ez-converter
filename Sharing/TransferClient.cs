using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace EZConverter.Sharing;

public sealed class TransferClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _api;
    public DeviceInfo Device { get; private set; } = null!;
    private TransferClient(HttpClient http, Uri api) => (_http, _api) = (http, api);
    internal TransferClient(HttpClient http, Uri api, DeviceInfo device) : this(http, api) => Device = device;

    public static async Task<TransferClient> ConnectAsync(string connectionUrl, CancellationToken ct = default)
    {
        var uri = ValidateUrl(connectionUrl);
        var fingerprint = uri.Fragment.TrimStart('#');
        var isInvitation = IsInvitationUri(uri);
        if (!isInvitation && (uri.Scheme != "https" || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)))
            throw new ArgumentException("PCの接続URLまたは受信用の招待URLを入力してください。");
        if (isInvitation && !IsPrivateHost(uri.Host))
            throw new InvalidOperationException("オンライン招待へのファイル送信はWebRTC P2Pを使用してください。HTTP経由の送信は許可されていません。");
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = !IsPrivateHost(uri.Host) };
        if (!isInvitation)
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null &&
                Convert.ToHexString(SHA256.HashData(cert.RawData)).Equals(fingerprint, StringComparison.OrdinalIgnoreCase);
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        var root = new UriBuilder(uri) { Fragment = "", Query = "", Path = isInvitation ? uri.AbsolutePath.TrimEnd('/') + "/api/" : "/api/ez/v1/" }.Uri;
        var client = new TransferClient(http, root);
        try
        {
            client.Device = await http.GetFromJsonAsync<DeviceInfo>(new Uri(root, "info"), ct) ?? throw new IOException("接続先から応答がありません。");
            if (client.Device.Protocol != "ez-share/1") throw new IOException("対応していない送信プロトコルです。");
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    public async Task SendAsync(List<LocalFile> files, string sender, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        TransferFiles.ValidateManifest(files.Select(f => f.File).ToList(), long.MaxValue);
        using var response = await PostOfferWithRateLimitRetryAsync(
            token => _http.PostAsJsonAsync(new Uri(_api, "offers"), new OfferRequest(sender, files.Select(f => f.File).ToList()), token), ct);
        await RequireSuccess(response, ct);
        var receipt = await response.Content.ReadFromJsonAsync<OfferReceipt>(ct) ?? throw new IOException("送信要求への応答が不正です。");
        var total = files.Sum(f => f.File.Length);
        var id = receipt.Id;
        progress?.Report(new(id, "送信", Device.Name, 0, total, "相手の確認待ち"));
        try
        {
            var acceptedUntil = DateTimeOffset.UtcNow.AddMinutes(3);
            while (true)
            {
                var status = await GetStatus(receipt, ct);
                if (status.State == "accepted") break;
                if (status.State != "pending") throw new IOException(status.Error ?? "相手が受信を許可しませんでした。");
                if (DateTimeOffset.UtcNow >= acceptedUntil) throw new IOException("受信確認がタイムアウトしました。");
                await Task.Delay(600, ct);
            }
            var progressOffsets = new long[files.Count];
            var progressIndexes = files.Select((file, index) => new { file.File.Id, Index = index })
                .ToDictionary(item => item.Id, item => item.Index, StringComparer.Ordinal);
            var progressGate = new object();
            long transferredBytes = 0;
            void ReportProgress(string state, string? detail = null)
            {
                lock (progressGate)
                    progress?.Report(new(id, "送信", Device.Name, Interlocked.Read(ref transferredBytes), total, state, detail));
            }
            void UpdateFileProgress(string fileId, long offset, string detail)
            {
                var previous = Interlocked.Exchange(ref progressOffsets[progressIndexes[fileId]], offset);
                Interlocked.Add(ref transferredBytes, offset - previous);
                ReportProgress("送信中", detail);
            }
            await Parallel.ForEachAsync(files, new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Math.Min(files.Count, 4)
            }, async (file, token) =>
            {
                await using var source = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
                if (source.Length != file.File.Length) throw new IOException("準備後にファイルが変更されました。選び直してください。");
                var buffer = new byte[TransferServerOptions.ChunkSize];
                long offset = 0;
                var failures = 0;
                while (offset < file.File.Length)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        source.Position = offset;
                        var count = (int)Math.Min(buffer.Length, file.File.Length - offset);
                        await source.ReadExactlyAsync(buffer.AsMemory(0, count), token);
                        using var request = Request(HttpMethod.Put, $"offers/{id}/files/{file.File.Id}?offset={offset}", receipt.Secret);
                        request.Content = new ByteArrayContent(buffer, 0, count);
                        using var result = await _http.SendAsync(request, token);
                        if (result.StatusCode == HttpStatusCode.Conflict)
                            offset = (await GetStatus(receipt, token)).Offsets[file.File.Id];
                        else { await RequireSuccess(result, token); offset += count; }
                        if (offset < 0 || offset > file.File.Length) throw new IOException("受信側の進捗が不正です。");
                        failures = 0;
                        UpdateFileProgress(file.File.Id, offset, file.File.RelativePath);
                    }
                    catch (Exception e) when (IsTransient(e, token) && ++failures <= 5)
                    {
                        ReportProgress("再接続中", file.File.RelativePath);
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, failures * 2)), token);
                        // A lost response may follow a committed chunk; query the receiver before retrying.
                        try { offset = (await GetStatus(receipt, token)).Offsets[file.File.Id]; } catch (Exception statusError) when (IsTransient(statusError, token)) { }
                        if (offset < 0 || offset > file.File.Length) throw new IOException("受信側の進捗が不正です。");
                        UpdateFileProgress(file.File.Id, offset, file.File.RelativePath);
                    }
                }
                await PostComplete($"offers/{id}/files/{file.File.Id}/complete", receipt.Secret, token);
                UpdateFileProgress(file.File.Id, file.File.Length, file.File.RelativePath);
            });
            await PostComplete($"offers/{id}/complete", receipt.Secret, ct);
            progress?.Report(new(id, "送信", Device.Name, total, total, "完了"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CancelRemoteAsync(id, receipt.Secret);
            progress?.Report(new(id, "送信", Device.Name, 0, total, "キャンセル"));
            throw;
        }
        catch (Exception error)
        {
            await CancelRemoteAsync(id, receipt.Secret);
            progress?.Report(new(id, "送信", Device.Name, 0, total, "エラー", error.Message));
            throw;
        }
    }

    private async Task CancelRemoteAsync(string id, string secret)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { using var request = Request(HttpMethod.Delete, $"offers/{id}", secret); using var result = await _http.SendAsync(request, timeout.Token); }
        catch (Exception error) when (IsTransient(error, CancellationToken.None)) { }
    }

    internal static async Task<HttpResponseMessage> PostOfferWithRateLimitRetryAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> postOffer,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(postOffer);
        const int maximumRateLimitRetries = 5;
        for (var attempt = 0; ; attempt++)
        {
            var response = await postOffer(ct);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= maximumRateLimitRetries)
                return response;

            var retryAfter = response.Headers.RetryAfter;
            var suggestedDelay = retryAfter?.Delta ??
                (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(Math.Min(8, (attempt + 1) * 2)));
            var delay = TimeSpan.FromMilliseconds(Math.Clamp(suggestedDelay.TotalMilliseconds, 250, 8000));
            response.Dispose();
            await Task.Delay(delay, ct);
        }
    }

    private async Task PostComplete(string route, string secret, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { using var request = Request(HttpMethod.Post, route, secret); using var result = await _http.SendAsync(request, ct); await RequireSuccess(result, ct); return; }
            catch (Exception e) when (attempt < 3 && IsTransient(e, ct)) { await Task.Delay(1000 * (attempt + 1), ct); }
        }
    }
    private async Task<OfferStatus> GetStatus(OfferReceipt receipt, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, $"offers/{receipt.Id}", receipt.Secret);
        using var response = await _http.SendAsync(request, ct);
        await RequireSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<OfferStatus>(ct) ?? throw new IOException("進捗の応答がありません。");
    }
    private HttpRequestMessage Request(HttpMethod method, string route, string secret)
    {
        var request = new HttpRequestMessage(method, new Uri(_api, route));
        request.Headers.Add("X-EZ-Session", secret);
        return request;
    }
    internal static async Task RequireSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "パスワードが必要か、正しくありません。",
            HttpStatusCode.Forbidden => "この転送は許可されていません。",
            HttpStatusCode.Gone => "リンクの期限が切れたか、転送が停止されました。",
            HttpStatusCode.Conflict => "転送状態が変わりました。相手の受信状態を確認してください。",
            HttpStatusCode.UnprocessableEntity => "ファイルのSHA-256が一致しません。ファイルを選び直して再送してください。",
            HttpStatusCode.TooManyRequests => "接続が混み合っています。少し待ってやり直してください。",
            _ => $"接続先でエラーが発生しました（HTTP {(int)response.StatusCode}）。"
        };
        throw new HttpRequestException(message, null, response.StatusCode);
    }
    internal static bool IsTransient(Exception e, CancellationToken ct) => !ct.IsCancellationRequested &&
        (e is TaskCanceledException || e is IOException || e is HttpRequestException h &&
            (h.StatusCode is null || h.StatusCode == HttpStatusCode.RequestTimeout || h.StatusCode == HttpStatusCode.TooManyRequests || (int)h.StatusCode.Value >= 500));
    internal static bool IsPrivateHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var ip) && TransferServer.IsLocalSendAddress(ip);
    public static bool IsInvitationUri(Uri uri) => uri.IsAbsoluteUri &&
        System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, @"^/i/[a-f0-9]{64}/?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    public static Uri ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Scheme == "http" && !IsPrivateHost(uri.Host))
            throw new ArgumentException("有効なEZ共有URLを入力してください。インターネットではHTTPSが必要です。");
        return uri;
    }
    public void Dispose() => _http.Dispose();
}
