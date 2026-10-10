using System.Net;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.StaticFiles;

namespace EZConverter.Sharing;

public sealed record LocalSendDeviceInfo(
    [property: JsonPropertyName("alias")] string Alias,
    [property: JsonPropertyName("version")] string Version = "2.0",
    [property: JsonPropertyName("deviceModel")] string? DeviceModel = "Windows",
    [property: JsonPropertyName("deviceType")] string? DeviceType = "desktop",
    [property: JsonPropertyName("fingerprint")] string? Fingerprint = null,
    [property: JsonPropertyName("port")] int? Port = null,
    [property: JsonPropertyName("protocol")] string? Protocol = null,
    [property: JsonPropertyName("download")] bool? Download = false);

public sealed record LocalSendFileMetadata(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("fileType")] string FileType,
    [property: JsonPropertyName("sha256")] string? Sha256,
    // LocalSend preview data is optional, unused here, and may be large (for
    // example, a Base64 image thumbnail). Ignore it during both serialization
    // and deserialization instead of retaining untrusted preview strings.
    [property: JsonPropertyName("preview"), JsonIgnore(Condition = JsonIgnoreCondition.Always)] string? Preview = null,
    [property: JsonPropertyName("metadata")] LocalSendFileMetadataTimes? Metadata = null);

public sealed record LocalSendFileMetadataTimes(
    [property: JsonPropertyName("modified")] DateTimeOffset? Modified,
    [property: JsonPropertyName("accessed")] DateTimeOffset? Accessed)
{
    public static LocalSendFileMetadataTimes? FromTicks(long modifiedTicks, long accessedTicks)
    {
        if (modifiedTicks <= 0 && accessedTicks <= 0) return null;
        return new(
            modifiedTicks > 0 ? new DateTimeOffset(new DateTime(modifiedTicks, DateTimeKind.Utc)) : null,
            accessedTicks > 0 ? new DateTimeOffset(new DateTime(accessedTicks, DateTimeKind.Utc)) : null);
    }
}

public sealed record LocalSendPrepareUploadRequest(
    [property: JsonPropertyName("info")] LocalSendDeviceInfo Info,
    [property: JsonPropertyName("files")] Dictionary<string, LocalSendFileMetadata> Files);

public sealed record LocalSendPrepareUploadResponse(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("files")] Dictionary<string, string> Files);

public sealed record LocalSendSendResult(int AcceptedFileCount, int RejectedFileCount, long TransferredBytes, bool NoTransferNeeded = false)
{
    public bool IsPartial => RejectedFileCount > 0;
}

public static class LocalSendClient
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static async Task<LocalSendSendResult> SendAsync(
        string connectionUrl,
        IReadOnlyList<LocalFile> files,
        string sender,
        LocalSendDeviceInfo? senderInfo = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? targetName = null,
        string? pin = null,
        X509Certificate2? clientCertificate = null)
    {
        if (files.Count is < 1 or > 2000) throw new ArgumentException("送信ファイル数が不正です。", nameof(files));
        if (string.IsNullOrWhiteSpace(sender) || sender.Length > 64) throw new ArgumentException("送信者名が不正です。", nameof(sender));
        if (!string.IsNullOrEmpty(pin) && (pin.Length != 6 || pin.Any(character => character is < '0' or > '9')))
            throw new ArgumentException("LocalSendのPINは数字6桁で入力してください。", nameof(pin));
        var uri = ParseConnectionUrl(connectionUrl);
        var fingerprint = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
                throw new ArgumentException("HTTPSのLocalSend接続には、発見情報に含まれる証明書フィンガープリントが必要です。", nameof(connectionUrl));
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
                Convert.ToHexString(SHA256.HashData(certificate.RawData)).Equals(fingerprint, StringComparison.OrdinalIgnoreCase);
            if (clientCertificate is { HasPrivateKey: true })
            {
                handler.ClientCertificateOptions = ClientCertificateOption.Manual;
                handler.ClientCertificates.Add(clientCertificate);
            }
        }

        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var endpoint = new UriBuilder(uri)
        {
            Fragment = "", Query = string.IsNullOrEmpty(pin) ? "" : "pin=" + Uri.EscapeDataString(pin),
            Path = "/api/localsend/v2/prepare-upload"
        }.Uri;
        var destinationName = string.IsNullOrWhiteSpace(targetName) ? uri.Host : targetName;
        var metadata = new Dictionary<string, LocalSendFileMetadata>(StringComparer.Ordinal);
        foreach (var local in files)
        {
            var name = local.File.RelativePath.Replace('\\', '/');
            TransferFiles.ValidateRelativePath(name);
            var leafName = name[(name.LastIndexOf('/') + 1)..];
            metadata.Add(local.File.Id, new(local.File.Id, name, local.File.Length,
                ContentTypes.TryGetContentType(leafName, out var mime) ? mime : "application/octet-stream", local.File.Sha256,
                Metadata: LocalSendFileMetadataTimes.FromTicks(local.LastWriteTicks, local.LastAccessTicks)));
        }

        senderInfo ??= new(sender,
            Fingerprint: clientCertificate is null ? TransferFiles.NewToken() : Convert.ToHexString(SHA256.HashData(clientCertificate.RawData)),
            Port: 53317,
            Protocol: clientCertificate is null ? "http" : "https",
            Download: false);
        if (clientCertificate is not null && !Convert.ToHexString(SHA256.HashData(clientCertificate.RawData))
                .Equals(senderInfo.Fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("送信端末情報のフィンガープリントとクライアント証明書が一致しません。", nameof(senderInfo));
        var requestBody = new LocalSendPrepareUploadRequest(senderInfo with { Alias = sender, Version = "2.0", DeviceType = "desktop" }, metadata);
        LocalSendPrepareUploadResponse? receipt = null;
        string transferId = Guid.NewGuid().ToString("N");
        var total = files.Sum(file => file.File.Length);
        try
        {
            using var prepared = await http.PostAsJsonAsync(endpoint, requestBody, cancellationToken);
            if (prepared.StatusCode == HttpStatusCode.NoContent)
            {
                progress?.Report(new(transferId, "送信", destinationName, total, total, "完了", "受信側は追加の転送が不要と応答しました。"));
                return new(0, 0, 0, NoTransferNeeded: true);
            }
            await RequireSuccessAsync(prepared, cancellationToken);
            receipt = await prepared.Content.ReadFromJsonAsync<LocalSendPrepareUploadResponse>(cancellationToken)
                ?? throw new IOException("LocalSend受信準備への応答が不正です。");
            var sourceIds = files.Select(file => file.File.Id).ToHashSet(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(receipt.SessionId) || receipt.SessionId.Length > 256 || receipt.Files is null ||
                receipt.Files.Keys.Any(id => !sourceIds.Contains(id)) || receipt.Files.Values.Any(string.IsNullOrWhiteSpace))
                throw new IOException("LocalSend受信準備の応答に必要なファイルトークンがありません。");

            var acceptedFiles = files.Where(file => receipt.Files.ContainsKey(file.File.Id)).ToArray();
            var acceptedBytes = acceptedFiles.Sum(file => file.File.Length);
            var rejectedCount = files.Count - acceptedFiles.Length;
            if (acceptedFiles.Length == 0)
            {
                await CancelAsync(http, endpoint, receipt.SessionId);
                progress?.Report(new(transferId, "送信", destinationName, 0, total, "拒否", $"受信側が{files.Count}件すべてを受け入れませんでした。"));
                return new(0, files.Count, 0);
            }

            progress?.Report(new(transferId, "送信", destinationName, 0, total, "送信中"));
            using var transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var fileProgress = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
            await Parallel.ForEachAsync(acceptedFiles, new ParallelOptions
            {
                CancellationToken = transferCancellation.Token,
                MaxDegreeOfParallelism = Math.Min(files.Count, 4)
            }, async (file, token) =>
            {
                try
                {
                    await using var source = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
                    if (source.Length != file.File.Length || file.LastWriteTicks != 0 && File.GetLastWriteTimeUtc(file.SourcePath).Ticks != file.LastWriteTicks)
                        throw new IOException($"送信準備後にファイルが変更されました: {file.File.RelativePath}");

                    using var request = new HttpRequestMessage(HttpMethod.Post, BuildUploadUri(endpoint, receipt.SessionId, file.File.Id, receipt.Files[file.File.Id]));
                    request.Content = new ProgressStreamContent(source, file.File.Length, bytes =>
                    {
                        fileProgress[file.File.Id] = bytes;
                        progress?.Report(new(transferId, "送信", destinationName, fileProgress.Values.Sum(), total, "送信中", file.File.RelativePath));
                    });
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    await RequireSuccessAsync(response, token);
                    fileProgress[file.File.Id] = file.File.Length;
                    progress?.Report(new(transferId, "送信", destinationName, fileProgress.Values.Sum(), total, "送信中", file.File.RelativePath));
                }
                catch
                {
                    transferCancellation.Cancel();
                    throw;
                }
            });

            progress?.Report(new(transferId, "送信", destinationName, acceptedBytes, total,
                rejectedCount == 0 ? "完了" : "一部完了",
                rejectedCount == 0 ? null : $"{acceptedFiles.Length}/{files.Count}件を送信。{rejectedCount}件は受信側が受け入れませんでした。"));
            return new(acceptedFiles.Length, rejectedCount, acceptedBytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (receipt is not null) await CancelAsync(http, endpoint, receipt.SessionId);
            progress?.Report(new(transferId, "送信", destinationName, 0, total, "キャンセル"));
            throw;
        }
        catch (Exception error)
        {
            if (receipt is not null) await CancelAsync(http, endpoint, receipt.SessionId);
            progress?.Report(new(transferId, "送信", destinationName, 0, total, "エラー", error.Message));
            throw;
        }
    }

    private static Uri ParseConnectionUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.AbsolutePath != "/" ||
            uri.Scheme == "http" && !TransferClient.IsPrivateHost(uri.Host))
            throw new ArgumentException("LAN内のLocalSend接続URLを入力してください。", nameof(value));
        return uri;
    }

    private static Uri BuildUploadUri(Uri endpoint, string sessionId, string fileId, string token) => new UriBuilder(endpoint)
    {
        Path = "/api/localsend/v2/upload",
        Query = $"sessionId={Uri.EscapeDataString(sessionId)}&fileId={Uri.EscapeDataString(fileId)}&token={Uri.EscapeDataString(token)}"
    }.Uri;

    private static async Task CancelAsync(HttpClient client, Uri endpoint, string sessionId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var cancel = new UriBuilder(endpoint) { Path = "/api/localsend/v2/cancel", Query = "sessionId=" + Uri.EscapeDataString(sessionId) }.Uri;
            using var response = await client.PostAsync(cancel, null, timeout.Token);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException) { }
    }

    private static Task RequireSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "LocalSend受信側でPINが必要か、認証に失敗しました。",
            HttpStatusCode.Forbidden => "LocalSend受信側が受信を拒否しました。",
            HttpStatusCode.Conflict => "LocalSend受信側で別の転送が進行中です。",
            HttpStatusCode.TooManyRequests => "LocalSend受信側への要求が多すぎます。少し待って再試行してください。",
            HttpStatusCode.UnprocessableEntity => "受信したファイルのSHA-256が一致しません。",
            HttpStatusCode.Gone => "LocalSend受信セッションは終了しました。",
            _ => $"LocalSend受信側でエラーが発生しました（HTTP {(int)response.StatusCode}）。"
        };
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private sealed class ProgressStreamContent(Stream source, long length, Action<long> report) : HttpContent
    {
        protected override bool TryComputeLength(out long value) { value = length; return true; }

        protected override async Task SerializeToStreamAsync(Stream target, System.Net.TransportContext? context)
        {
            var buffer = new byte[128 * 1024];
            long copied = 0;
            while (copied < length)
            {
                var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - copied)));
                if (count == 0) throw new EndOfStreamException("送信中にファイルが短くなりました。");
                await target.WriteAsync(buffer.AsMemory(0, count));
                copied += count;
                report(copied);
            }
            if (source.ReadByte() != -1) throw new IOException("送信中にファイルサイズが変わりました。");
        }

        protected override async Task SerializeToStreamAsync(Stream target, System.Net.TransportContext? context, CancellationToken cancellationToken)
        {
            var buffer = new byte[128 * 1024];
            long copied = 0;
            while (copied < length)
            {
                var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - copied)), cancellationToken);
                if (count == 0) throw new EndOfStreamException("送信中にファイルが短くなりました。");
                await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                copied += count;
                report(copied);
            }
            var extra = new byte[1];
            if (await source.ReadAsync(extra, cancellationToken) != 0) throw new IOException("送信中にファイルサイズが変わりました。");
        }
    }
}
