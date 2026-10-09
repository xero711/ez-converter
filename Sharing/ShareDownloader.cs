using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace EZConverter.Sharing;

public static class ShareDownloader
{
    public static async Task<string> DownloadAsync(string url, string? password, string receiveDirectory, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var uri = TransferClient.ValidateUrl(url);
        if (!System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, @"/s/[a-f0-9]{64}/?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)) throw new ArgumentException("ファイルの共有URL（/s/を含むURL）を入力してください。");
        if (uri.Scheme == Uri.UriSchemeHttps && !TransferClient.IsPrivateHost(uri.Host))
            throw new NotSupportedException("インターネット共有URLはHTTPで受け取れません。P2P受信ページを開いてください。");
        var root = new UriBuilder(uri) { Fragment = "", Path = uri.AbsolutePath.TrimEnd('/') + "/" }.Uri;
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer(), UseProxy = !TransferClient.IsPrivateHost(uri.Host) };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        if (!string.IsNullOrEmpty(password))
        {
            using var unlock = await http.PostAsJsonAsync(new Uri(root, "unlock"), new { password }, ct);
            await TransferClient.RequireSuccess(unlock, ct);
        }
        using var manifestResponse = await http.GetAsync(new Uri(root, "manifest"), ct);
        await TransferClient.RequireSuccess(manifestResponse, ct);
        var manifest = await manifestResponse.Content.ReadFromJsonAsync<ShareManifest>(ct) ?? throw new IOException("ファイル一覧を取得できません。");
        TransferFiles.ValidateManifest(manifest.Files, 100L * 1024 * 1024 * 1024);
        var id = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(receiveDirectory, ".ez-download-" + id);
        Directory.CreateDirectory(stage);
        long completed = 0;
        var total = manifest.Files.Sum(f => f.Length);
        try
        {
            foreach (var file in manifest.Files)
            {
                var part = Path.Combine(stage, file.Id + ".part");
                var failures = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var offset = File.Exists(part) ? new FileInfo(part).Length : 0;
                    if (offset > file.Length) throw new IOException("受信データの長さが不正です。");
                    try
                    {
                        if (offset < file.Length || !File.Exists(part))
                        {
                            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, "files/" + file.Id));
                            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                            await TransferClient.RequireSuccess(response, ct);
                            if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From != offset) throw new InvalidDataException("再開位置が一致しません。");
                            if (response.StatusCode != HttpStatusCode.PartialContent) offset = 0;
                            await using var source = await response.Content.ReadAsStreamAsync(ct);
                            await using var destination = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 128 * 1024, true);
                            destination.SetLength(offset);
                            destination.Position = offset;
                            var buffer = new byte[128 * 1024];
                            long last = 0;
                            while (true)
                            {
                                var read = await source.ReadAsync(buffer, ct);
                                if (read == 0) break;
                                if (destination.Position + read > file.Length) throw new InvalidDataException("ファイル一覧と実際のサイズが一致しません。");
                                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                                if (Environment.TickCount64 - last > 200) { last = Environment.TickCount64; progress?.Report(new(id, "受信", manifest.Name, completed + destination.Position, total, "受信中", file.RelativePath)); }
                            }
                            if (destination.Length != file.Length) throw new EndOfStreamException("接続が途中で切れました。");
                        }
                        break;
                    }
                    catch (Exception e) when (TransferClient.IsTransient(e, ct) && ++failures <= 5)
                    { progress?.Report(new(id, "受信", manifest.Name, completed + offset, total, "再接続中")); await Task.Delay(TimeSpan.FromSeconds(failures * 2), ct); }
                }
                await using (var stream = File.OpenRead(part))
                    if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256が一致しません。共有元でファイルを選び直してください。");
                var target = TransferFiles.UnderDirectory(Path.Combine(stage, "ready"), file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(part, target, false);
                completed += file.Length;
            }
            var final = Path.Combine(receiveDirectory, $"EZ-{DateTime.Now:yyyyMMdd-HHmmss}-{id[..8]}");
            await MoveDirectoryWithRetryAsync(Path.Combine(stage, "ready"), final, ct);
            progress?.Report(new(id, "受信", manifest.Name, total, total, "完了", final));
            return final;
        }
        finally { try { Directory.Delete(stage, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static async Task MoveDirectoryWithRetryAsync(string source, string destination, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (1 << attempt)), ct);
            }
        }
    }
}
