using System.IO;
using System.Text.Json;
using System.Windows;
using EZConverter.Sharing;
using Microsoft.Web.WebView2.Core;

namespace MediaConverter.Views;

public static class P2PInvitationSender
{
    public static async Task SendAsync(
        Uri invitation,
        IReadOnlyList<LocalFile> files,
        string senderName,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken,
        Window? owner = null)
    {
        invitation = TransferClient.ValidateUrl(invitation.AbsoluteUri);
        if (!TransferClient.IsInvitationUri(invitation))
            throw new ArgumentException("EZ Converterの受信招待URLを指定してください。", nameof(invitation));
        TransferFiles.ValidateManifest(files.Select(file => file.File).ToList(), long.MaxValue);
        var total = files.Sum(file => file.File.Length);
        var operationId = "p2p-" + Guid.NewGuid().ToString("N");
        if (!PeerSessionWindow.IsRuntimeAvailable)
            progress?.Report(new(operationId, "送信", invitation.Host, 0, total, "準備中", "P2P用ブラウザーを初回セットアップしています..."));
        await PeerSessionWindow.EnsureRuntimeAvailableAsync(cancellationToken);

        var sources = files.ToDictionary(file => file.File.Id, StringComparer.Ordinal);
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var readGate = new SemaphoreSlim(1, 1);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new PeerSessionWindow(invitation, "EZ Converter · P2P直接送信") { Owner = owner };
        CoreWebView2? browser = null;
        FileStream? activeSource = null;
        string? activeSourceId = null;
        long reportedBytes = 0;
        var closing = false;

        void Report(string state, string? detail = null) =>
            progress?.Report(new(operationId, "送信", invitation.Host, reportedBytes, total, state, detail));

        void PostReply(object value)
        {
            if (browser is null || closing) return;
            try { browser.PostWebMessageAsJson(JsonSerializer.Serialize(value)); }
            catch (InvalidOperationException) { }
        }

        async Task<byte[]> ReadChunkAsync(string fileId, long offset, int length, CancellationToken ct)
        {
            if (length is < 1 or > 48 * 1024 || !sources.TryGetValue(fileId, out var source) ||
                offset < 0 || offset > source.File.Length - length)
                throw new InvalidDataException("送信元からの読み出し要求が不正です。");

            await readGate.WaitAsync(ct);
            try
            {
                if (!File.Exists(source.SourcePath))
                    throw new IOException("送信元ファイルが見つからなくなりました。");
                var lastWriteTicks = File.GetLastWriteTimeUtc(source.SourcePath).Ticks;
                if (new FileInfo(source.SourcePath).Length != source.File.Length ||
                    source.LastWriteTicks != 0 && lastWriteTicks != source.LastWriteTicks)
                    throw new IOException("送信準備後にファイルが変更されたため、安全のため送信を停止しました。");

                if (activeSourceId != fileId)
                {
                    activeSource?.Dispose();
                    activeSource = new FileStream(source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
                    activeSourceId = fileId;
                    if (activeSource.Length != source.File.Length)
                        throw new IOException("送信準備後にファイルサイズが変わりました。");
                }

                var stream = activeSource ?? throw new IOException("送信元ファイルを開けませんでした。");
                stream.Position = offset;
                var bytes = new byte[length];
                await stream.ReadExactlyAsync(bytes, ct);
                return bytes;
            }
            finally { readGate.Release(); }
        }

        async void OnWebMessage(object? _, CoreWebView2WebMessageReceivedEventArgs eventArgs)
        {
            if (closing || !IsInvitationOrigin(eventArgs.Source, invitation)) return;
            string? requestId = null;
            try
            {
                var json = eventArgs.WebMessageAsJson;
                if (json.Length > 4096) throw new InvalidDataException("P2Pページからの制御メッセージが大きすぎます。");
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeValue)) return;
                var type = typeValue.GetString();
                switch (type)
                {
                    case "file-read":
                    {
                        requestId = root.GetProperty("requestId").GetString();
                        var fileId = root.GetProperty("fileId").GetString();
                        var offset = root.GetProperty("offset").GetInt64();
                        var length = root.GetProperty("length").GetInt32();
                        if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(fileId))
                            throw new InvalidDataException("P2Pページからの読み出し識別子が不正です。");
                        try
                        {
                            var bytes = await ReadChunkAsync(fileId, offset, length, sessionCancellation.Token);
                            PostReply(new { type = "file-read-result", requestId, base64 = Convert.ToBase64String(bytes) });
                        }
                        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException)
                        {
                            PostReply(new { type = "file-read-result", requestId, error = error is OperationCanceledException ? "送信をキャンセルしました。" : error.Message });
                        }
                        return;
                    }
                    case "file-closed":
                        if (root.TryGetProperty("fileId", out var closedFileId) && closedFileId.GetString() == activeSourceId)
                        {
                            await readGate.WaitAsync(sessionCancellation.Token);
                            try { activeSource?.Dispose(); activeSource = null; activeSourceId = null; }
                            finally { readGate.Release(); }
                        }
                        return;
                    case "progress":
                        if (root.TryGetProperty("completed", out var completed) && completed.TryGetInt64(out var completedBytes) &&
                            root.TryGetProperty("total", out var reportedTotal) && reportedTotal.TryGetInt64(out var totalBytes) &&
                            totalBytes == total && completedBytes >= reportedBytes && completedBytes <= total)
                        {
                            reportedBytes = completedBytes;
                            var detail = root.TryGetProperty("detail", out var detailValue) ? detailValue.GetString() : null;
                            Report("送信中", detail);
                        }
                        return;
                    case "status":
                        if (root.TryGetProperty("text", out var textValue) && textValue.GetString() is { Length: > 0 and <= 500 } text)
                            Report(text.StartsWith("P2P直接接続中（", StringComparison.Ordinal) ? "P2P直接接続" :
                                text.Contains("送信中", StringComparison.Ordinal) ? "送信中" : "接続中", text);
                        return;
                    case "complete":
                        completion.TrySetResult(true);
                        return;
                    case "cancelled":
                        completion.TrySetException(new OperationCanceledException("P2P送信をキャンセルしました。"));
                        return;
                    case "failed":
                        var message = root.TryGetProperty("message", out var messageValue) ? messageValue.GetString() : null;
                        completion.TrySetException(new IOException(string.IsNullOrWhiteSpace(message) ? "P2P送信に失敗しました。" : message));
                        return;
                }
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or InvalidDataException or OperationCanceledException or IOException or UnauthorizedAccessException)
            {
                if (requestId is not null) PostReply(new { type = "file-read-result", requestId, error = "送信元ファイルを読み出せませんでした。" });
                else completion.TrySetException(error);
            }
        }

        window.Closed += (_, _) =>
        {
            if (!completion.Task.IsCompleted) sessionCancellation.Cancel();
        };

        try
        {
            window.Show();
            await window.Ready.WaitAsync(TimeSpan.FromSeconds(30), sessionCancellation.Token);
            if (window.InitializationError is not null)
                throw new InvalidOperationException("P2P招待ページを開けませんでした。URLの有効期限とネットワークを確認してください。", window.InitializationError);
            browser = window.Browser ?? throw new InvalidOperationException("P2Pブラウザーを初期化できませんでした。");
            browser.WebMessageReceived += OnWebMessage;

            var descriptors = files.Select(file => new
            {
                id = file.File.Id,
                name = Path.GetFileName(file.File.RelativePath),
                length = file.File.Length,
                relativePath = file.File.RelativePath,
                sha256 = file.File.Sha256
            });
            var script = "window.EZSetNativeFiles(" + JsonSerializer.Serialize(descriptors) + "," + JsonSerializer.Serialize(senderName) + ");";
            await browser.ExecuteScriptAsync(script);
            Report("接続待ち", "受信側で接続を許可すると、選択したファイルをWebRTCで直接送信します。");

            var cancellationWait = Task.Delay(Timeout.InfiniteTimeSpan, sessionCancellation.Token);
            var finished = await Task.WhenAny(completion.Task, cancellationWait);
            if (finished != completion.Task)
            {
                try { await browser.ExecuteScriptAsync("document.querySelector('#cancel')?.click()"); }
                catch (InvalidOperationException) { }
                sessionCancellation.Token.ThrowIfCancellationRequested();
            }
            await completion.Task;
            reportedBytes = total;
            Report("完了", "P2P直接送信と受信側のSHA-256検証が完了しました。");
        }
        catch (OperationCanceledException)
        {
            Report("キャンセル", "P2P送信をキャンセルしました。");
            throw;
        }
        catch (Exception error)
        {
            Report("エラー", error.Message);
            throw;
        }
        finally
        {
            closing = true;
            if (browser is not null) browser.WebMessageReceived -= OnWebMessage;
            try { await readGate.WaitAsync(); }
            catch (ObjectDisposedException) { }
            activeSource?.Dispose();
            activeSource = null;
            if (readGate.CurrentCount == 0) readGate.Release();
            window.Close();
        }
    }

    private static bool IsInvitationOrigin(string source, Uri invitation) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == invitation.Scheme &&
        uri.Host.Equals(invitation.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == invitation.Port &&
        uri.AbsolutePath.StartsWith("/i/", StringComparison.Ordinal);
}
