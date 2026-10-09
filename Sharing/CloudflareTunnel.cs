using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EZConverter.Sharing;

public sealed class CloudflareTunnel : IAsyncDisposable
{
    private static readonly SemaphoreSlim UpdateGate = new(1, 1);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HealthRequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HealthRetryDelay = TimeSpan.FromSeconds(1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly string _toolDirectory;
    private Process? _process;
    private WindowsChildProcessJob? _processJob;
    private Task? _stdout;
    private Task? _stderr;
    private bool _stopping;
    private string? _connectionFailure;
    private string? _activeTunnelIdentity;
    public Uri? PublicOrigin { get; private set; }
    public bool IsRunning => _process is { HasExited: false };
    public event Action<string>? Status;
    public event Action<string>? Diagnostic;
    public event Action? Disconnected;
    public CloudflareTunnel(string? toolDirectory = null) => _toolDirectory = toolDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EZConverter", "Tools", "cloudflared");

    public async Task<string> EnsureToolAsync(CancellationToken ct = default)
    {
        await UpdateGate.WaitAsync(ct);
        var executable = Path.Combine(_toolDirectory, "cloudflared.exe");
        var state = Path.Combine(_toolDirectory, "checked-at.txt");
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(_toolDirectory);
            if (File.Exists(executable) && File.Exists(state) && DateTime.UtcNow - File.GetLastWriteTimeUtc(state) < TimeSpan.FromHours(24)) return executable;
            Status?.Invoke("インターネット接続ツールを準備しています...");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EZConverter/1.0");
            using var document = JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/cloudflare/cloudflared/releases/latest", ct));
            var asset = document.RootElement.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "cloudflared-windows-amd64.exe");
            var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            var digest = asset.GetProperty("digest").GetString();
            var size = asset.GetProperty("size").GetInt64();
            if (uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/cloudflare/cloudflared/releases/download/", StringComparison.Ordinal) ||
                digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit) || size is < 1_000_000 or > 150_000_000)
                throw new InvalidDataException("接続ツールの公式配布情報を検証できません。");
            if (File.Exists(executable))
            {
                await using var existing = File.OpenRead(executable);
                if (Convert.ToHexString(await SHA256.HashDataAsync(existing, ct)).Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
                { await File.WriteAllTextAsync(state, document.RootElement.GetProperty("tag_name").GetString(), ct); return executable; }
            }
            temporary = Path.Combine(_toolDirectory, Guid.NewGuid().ToString("N") + ".download");
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                var buffer = new byte[128 * 1024];
                long count = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                { count += read; if (count > size) throw new InvalidDataException("配布サイズが一致しません。"); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
                if (count != size) throw new InvalidDataException("ダウンロードが途中で終了しました。");
            }
            await using (var stream = File.OpenRead(temporary))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(digest[7..], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("接続ツールのSHA-256検証に失敗しました。");
            File.Move(temporary, executable, true);
            await File.WriteAllTextAsync(state, document.RootElement.GetProperty("tag_name").GetString(), ct);
            return executable;
        }
        catch (Exception e) when (File.Exists(executable) && e is HttpRequestException or IOException or JsonException)
        { Status?.Invoke("更新を確認できなかったため、前回検証した接続ツールを使用します。"); return executable; }
        finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); UpdateGate.Release(); }
    }

    public async Task<Uri> StartAsync(int httpPort, CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct);
        try { return await StartCoreAsync(httpPort, null, null, ct); }
        finally { _lifecycleGate.Release(); }
    }

    public async Task<Uri> StartNamedAsync(int httpPort, string hostname, string tunnelToken, CancellationToken ct = default)
    {
        _ = ValidateNamedTunnel(httpPort, hostname, tunnelToken);
        await _lifecycleGate.WaitAsync(ct);
        try { return await StartCoreAsync(httpPort, hostname, tunnelToken, ct); }
        finally { _lifecycleGate.Release(); }
    }

    public static Uri ValidateNamedTunnel(int httpPort, string hostname, string tunnelToken)
    {
        if (httpPort is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(httpPort), "Named Tunnelのローカルポートは1024～65535で指定してください。");
        var host = (hostname ?? string.Empty).Trim().TrimEnd('.');
        string asciiHost;
        try { asciiHost = new IdnMapping().GetAscii(host).ToLowerInvariant(); }
        catch (ArgumentException) { throw new ArgumentException("Cloudflare Named Tunnelには、設定済みの公開DNSホスト名を入力してください。", nameof(hostname)); }
        var labels = asciiHost.Split('.');
        if (asciiHost.Length is < 3 or > 253 || labels.Length < 2 || labels.Any(label =>
                label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-' ||
                label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')) ||
            asciiHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) || asciiHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            asciiHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || Uri.CheckHostName(asciiHost) != UriHostNameType.Dns)
            throw new ArgumentException("Cloudflare Named Tunnelには、設定済みの公開DNSホスト名を入力してください。", nameof(hostname));
        if (string.IsNullOrWhiteSpace(tunnelToken) || tunnelToken.Length > 4096 || tunnelToken.Any(char.IsWhiteSpace) || tunnelToken.Any(char.IsControl))
            throw new ArgumentException("Cloudflare Tunnelトークンを確認してください。", nameof(tunnelToken));
        return new UriBuilder(Uri.UriSchemeHttps, asciiHost).Uri;
    }

    internal static ProcessStartInfo CreateNamedTunnelStartInfo(string executable, string tunnelToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(tunnelToken);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        foreach (var argument in new[] { "tunnel", "--no-autoupdate", "run" }) start.ArgumentList.Add(argument);
        // Cloudflare supports TUNNEL_TOKEN for remotely managed tunnels. Keeping
        // the credential out of argv prevents it from appearing in process listings.
        start.Environment["TUNNEL_TOKEN"] = tunnelToken;
        return start;
    }

    private async Task<Uri> StartCoreAsync(int httpPort, string? namedHostname, string? tunnelToken, CancellationToken ct)
    {
        var namedOrigin = namedHostname is null ? null : ValidateNamedTunnel(httpPort, namedHostname, tunnelToken ?? string.Empty);
        var tunnelIdentity = namedOrigin is null
            ? "quick:" + httpPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "named:" + namedOrigin.IdnHost + ":" + httpPort.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
                Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tunnelToken!)));
        if (IsRunning && PublicOrigin is not null && _activeTunnelIdentity == tunnelIdentity) return PublicOrigin;
        await StopCoreAsync();
        _stopping = false;
        _connectionFailure = null;
        var executable = await EnsureToolAsync(ct);
        var start = namedOrigin is null
            ? CreateQuickTunnelStartInfo(executable, httpPort)
            : CreateNamedTunnelStartInfo(executable, tunnelToken!);
        var ready = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startupTimer = Stopwatch.StartNew();
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.Exited += (_, _) =>
        {
            if (namedOrigin is null) ready.TrySetException(new IOException("インターネット接続ツールが終了しました。ネットワークを確認して再発行してください。"));
            if (ReferenceEquals(_process, process) && !_stopping)
            {
                PublicOrigin = null;
                _activeTunnelIdentity = null;
                Disconnected?.Invoke();
            }
        };
        _process = process;
        _activeTunnelIdentity = tunnelIdentity;
        try
        {
            _processJob = WindowsChildProcessJob.CreateKillOnClose();
            if (!process.Start()) throw new IOException("インターネット接続ツールを起動できませんでした。");
            _processJob.Assign(process);
            _stdout = ReadLinesAsync(process.StandardOutput, ready, allowQuickTunnelOrigin: namedOrigin is null, sensitiveValue: tunnelToken);
            _stderr = ReadLinesAsync(process.StandardError, ready, allowQuickTunnelOrigin: namedOrigin is null, sensitiveValue: tunnelToken);
            Uri publicOrigin;
            if (namedOrigin is null)
            {
                Status?.Invoke("インターネット用URLを発行しています...");
                try { publicOrigin = await ready.Task.WaitAsync(StartupTimeout, ct); }
                catch (TimeoutException exception)
                {
                    throw new IOException(_connectionFailure ?? "インターネット接続ツールからURLが返りませんでした。ネットワークを確認してください。LAN内の共有URLは利用できます。", exception);
                }
            }
            else
            {
                publicOrigin = namedOrigin;
                Status?.Invoke("登録済みホスト名のオンライン接続を確認しています...");
            }
            PublicOrigin = publicOrigin;
            Diagnostic?.Invoke("Allocated origin: " + publicOrigin);
            // The tunnel is not ready merely because cloudflared printed a URL. Keep
            // URL acquisition and DNS/HTTP propagation inside one bounded startup window.
            var remainingStartup = StartupTimeout - startupTimer.Elapsed;
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var healthy = remainingStartup > TimeSpan.Zero && await WaitForHealthyOriginAsync(
                async token =>
                {
                    using var response = await http.GetAsync(TunnelHealthProbe.CreateUri(publicOrigin), HttpCompletionOption.ResponseHeadersRead, token);
                    if (!response.IsSuccessStatusCode) Diagnostic?.Invoke("Origin check HTTP " + (int)response.StatusCode);
                    return response.IsSuccessStatusCode;
                }, remainingStartup, HealthRequestTimeout, HealthRetryDelay, ct,
                error => Diagnostic?.Invoke("Origin check: " + error.Message));
            if (healthy && IsRunning) { Status?.Invoke("インターネット接続を開始しました。"); return publicOrigin; }
            throw new IOException(_connectionFailure ?? "公開URLに接続できません。別の回線で再発行してください。LAN内の共有URLはそのまま利用できます。");
        }
        catch { await StopCoreAsync(); throw; }
    }

    private static ProcessStartInfo CreateQuickTunnelStartInfo(string executable, int httpPort)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))! };
        foreach (var argument in new[] { "tunnel", "--no-autoupdate", "--url", $"http://127.0.0.1:{httpPort}" }) start.ArgumentList.Add(argument);
        return start;
    }

    internal static async Task<bool> WaitForHealthyOriginAsync(
        Func<CancellationToken, Task<bool>> probe,
        TimeSpan totalTimeout,
        TimeSpan perAttemptTimeout,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default,
        Action<Exception>? onProbeError = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (perAttemptTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(perAttemptTimeout));
        if (retryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryDelay));
        if (totalTimeout <= TimeSpan.Zero) return false;

        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < totalTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = totalTimeout - timer.Elapsed;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(remaining < perAttemptTimeout ? remaining : perAttemptTimeout);
            try
            {
                if (await probe(attempt.Token)) return true;
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested) { onProbeError?.Invoke(error); }
            catch (HttpRequestException error) { onProbeError?.Invoke(error); }

            remaining = totalTimeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < retryDelay ? remaining : retryDelay, cancellationToken);
        }

        return false;
    }
    private async Task ReadLinesAsync(StreamReader reader, TaskCompletionSource<Uri> ready, bool allowQuickTunnelOrigin, string? sensitiveValue)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                var safeLine = string.IsNullOrEmpty(sensitiveValue) ? line : line.Replace(sensitiveValue, "[redacted]", StringComparison.Ordinal);
                Diagnostic?.Invoke(safeLine);
                if (line.Contains("TCP Connectivity", StringComparison.Ordinal) && line.Contains("FAIL", StringComparison.Ordinal) || line.Contains(":7844: i/o timeout", StringComparison.Ordinal))
                    _connectionFailure = "この回線からオンライン中継へ接続できません（送信先ポート7844）。別の回線を使うか、ネットワーク管理者に接続可否を確認してください。LAN内の共有URLは利用できます。";
                var match = Regex.Match(line, @"https://[a-z0-9-]+\.trycloudflare\.com\b", RegexOptions.CultureInvariant);
                if (allowQuickTunnelOrigin && match.Success) ready.TrySetResult(new Uri(match.Value));
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _lifecycleGate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        _stopping = true;
        PublicOrigin = null;
        _activeTunnelIdentity = null;
        try
        {
            if (_process is not null)
            {
                try { if (!_process.HasExited) _process.Kill(true); await _process.WaitForExitAsync(); } catch (InvalidOperationException) { }
                if (_stdout is not null) await _stdout;
                if (_stderr is not null) await _stderr;
            }
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            _processJob?.Dispose();
            _processJob = null;
        }
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}
