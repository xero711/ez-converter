using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using EZConverter.Sharing;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MediaConverter.Views;

public sealed class PeerSessionWindow : Window
{
    private const long MaximumNativeReceiveBytes = 100L * 1024 * 1024 * 1024;
    private const int MaximumNativeChunkBytes = 128 * 1024;
    private const string WebView2RuntimeInstallerSha256 = "2A6ADD76C37BFA872EB8C2B22D45B3216B8E2A1F193E0774006340FF62AC5FA0";
    private static readonly SemaphoreSlim RuntimeSetupGate = new(1, 1);
    private readonly Uri _page;
    private readonly WebView2 _webView;
    private readonly string? _nativeReceiveDirectory;
    private readonly ConcurrentDictionary<string, NativeSaveSession> _nativeSaveSessions = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready => _ready.Task;
    public Exception? InitializationError { get; private set; }
    public CoreWebView2? Browser => _webView.CoreWebView2;
    public event Action<PeerSessionStatus>? StatusReported;
    public static bool IsRuntimeAvailable => TryGetRuntimeVersion(out _);

    public static async Task EnsureRuntimeAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (TryGetRuntimeVersion(out _)) return;

        await RuntimeSetupGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetRuntimeVersion(out _)) return;

            var installerPath = Path.Combine(AppContext.BaseDirectory, "Tools", "WebView2", "MicrosoftEdgeWebView2RuntimeInstallerX64.exe");
            if (!File.Exists(installerPath))
                throw new FileNotFoundException("P2Pブラウザーのセットアップファイルが見つかりません。EZ Converterを配布フォルダーごと展開し直してください。", installerPath);

            await using (var installer = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var actualHash = await SHA256.HashDataAsync(installer, cancellationToken);
                var expectedHash = Convert.FromHexString(WebView2RuntimeInstallerSha256);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                        throw new InvalidDataException("P2Pブラウザーのセットアップファイルが破損しているため、安全のため実行しませんでした。EZ Converterを再展開してください。");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(actualHash);
                    CryptographicOperations.ZeroMemory(expectedHash);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "/silent", "/install" }
            }) ?? throw new InvalidOperationException("P2Pブラウザーの自動セットアップを開始できませんでした。");

            // Do not abandon an installer halfway through if the user cancels the surrounding transfer.
            await process.WaitForExitAsync();
            if (!TryGetRuntimeVersion(out _) && process.ExitCode != 0)
                throw new InvalidOperationException($"P2Pブラウザーを自動セットアップできませんでした（終了コード {process.ExitCode}）。もう一度お試しください。");
            if (!TryGetRuntimeVersion(out _))
                throw new InvalidOperationException("P2Pブラウザーのセットアップ後もランタイムを確認できませんでした。EZ Converterを再起動してもう一度お試しください。");
        }
        finally
        {
            RuntimeSetupGate.Release();
        }
    }

    private static bool TryGetRuntimeVersion(out string? version)
    {
        try
        {
            version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !string.IsNullOrWhiteSpace(version);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            version = null;
            return false;
        }
    }

    public PeerSessionWindow(Uri page, string title, string? nativeReceiveDirectory = null)
    {
        _page = page;
        _nativeReceiveDirectory = string.IsNullOrWhiteSpace(nativeReceiveDirectory) ? null : Path.GetFullPath(nativeReceiveDirectory);
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EZConverter", "WebView2", "PeerSession");
        _webView = new WebView2
        {
            CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = userDataFolder }
        };
        Title = title;
        Width = 560;
        Height = 360;
        MinWidth = 440;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(25, 31, 40));
        Content = _webView;
        Loaded += InitializeBrowser;
        Closed += async (_, _) => await CleanupNativeSaveSessionsAsync();
    }

    private async void InitializeBrowser(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureRuntimeAvailableAsync();
            await _webView.EnsureCoreWebView2Async();
            var core = _webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.WebMessageReceived += OnWebMessageReceived;
            if (_nativeReceiveDirectory is not null)
                await core.AddScriptToExecuteOnDocumentCreatedAsync("window.EZNativeReceiveEnabled = true;");
            core.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) || target.Scheme != _page.Scheme ||
                    !target.Host.Equals(_page.Host, StringComparison.OrdinalIgnoreCase) || target.Port != _page.Port)
                    args.Cancel = true;
            };
            core.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess) InitializationError = new InvalidOperationException("接続ページを読み込めませんでした: " + args.WebErrorStatus);
                _ready.TrySetResult(true);
            };
            core.Navigate(_page.AbsoluteUri);
        }
        catch (Exception exception)
        {
            InitializationError = exception;
            ShowError("P2P接続画面を開始できませんでした。" + Environment.NewLine + exception.Message);
            _ready.TrySetResult(true);
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsTrustedOrigin(e.Source)) return;
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            if (!root.TryGetProperty("channel", out var channelValue) || channelValue.GetString() is not { } channel) return;
            if (channel == "ez-share-save")
            {
                await HandleNativeSaveRequestAsync(root);
                return;
            }
            if (channel != "ez-share-status" ||
                !root.TryGetProperty("state", out var stateValue) || stateValue.GetString() is not { } state ||
                state is not ("waiting" or "connected" or "disconnected" or "progress" or "file-saved" or "failed"))
                return;

            var fileName = root.TryGetProperty("fileName", out var nameValue) ? nameValue.GetString() : null;
            if (fileName is { Length: > 200 } || fileName?.Any(char.IsControl) == true) return;
            var fileId = root.TryGetProperty("fileId", out var idValue) ? idValue.GetString() : null;
            if (fileId is not null && (fileId.Length != 32 || !fileId.All(Uri.IsHexDigit))) return;
            var percent = root.TryGetProperty("percent", out var percentValue) && percentValue.TryGetInt32(out var parsedPercent)
                ? Math.Clamp(parsedPercent, 0, 100)
                : (int?)null;
            var completed = root.TryGetProperty("completed", out var completedValue) && completedValue.TryGetInt64(out var parsedCompleted)
                ? parsedCompleted
                : (long?)null;
            var total = root.TryGetProperty("total", out var totalValue) && totalValue.TryGetInt64(out var parsedTotal)
                ? parsedTotal
                : (long?)null;
            if (completed is < 0 || total is < 0 || completed is not null && total is not null && completed > total) return;
            StatusReported?.Invoke(new(state, fileId, fileName, percent, completed, total));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            // Ignore unexpected or malformed messages from the embedded page.
        }
    }

    private async Task HandleNativeSaveRequestAsync(JsonElement root)
    {
        if (_nativeReceiveDirectory is null ||
            !root.TryGetProperty("requestId", out var requestValue) || requestValue.GetString() is not { } requestId ||
            requestId.Length != 32 || !requestId.All(Uri.IsHexDigit) ||
            !root.TryGetProperty("action", out var actionValue) || actionValue.GetString() is not { } action)
            return;

        try
        {
            string? resultToken = null;
            switch (action)
            {
                case "begin":
                {
                    var fileId = RequiredString(root, "fileId", 32);
                    var fileName = RequiredString(root, "fileName", 200);
                    var sha256 = RequiredString(root, "sha256", 64);
                    var size = RequiredInt64(root, "size");
                    var file = new TransferFile(fileId, fileName, size, sha256);
                    TransferFiles.ValidateManifest([file], MaximumNativeReceiveBytes);
                    var token = TransferFiles.NewToken();
                    var session = await NativeSaveSession.StartAsync(_nativeReceiveDirectory, token, file);
                    if (!_nativeSaveSessions.TryAdd(token, session))
                    {
                        await session.DisposeAsync();
                        throw new IOException("受信セッションを作成できませんでした。");
                    }
                    resultToken = token;
                    break;
                }
                case "write":
                {
                    var session = GetNativeSaveSession(root);
                    var encoded = RequiredString(root, "data", (MaximumNativeChunkBytes * 4 / 3) + 8);
                    var bytes = Convert.FromBase64String(encoded);
                    if (bytes.Length > MaximumNativeChunkBytes) throw new InvalidDataException("受信データのサイズが不正です。");
                    await session.WriteAsync(bytes);
                    break;
                }
                case "truncate":
                    await GetNativeSaveSession(root).TruncateAsync(RequiredInt64(root, "length"));
                    break;
                case "seek":
                    await GetNativeSaveSession(root).SeekAsync(RequiredInt64(root, "position"));
                    break;
                case "complete":
                {
                    var token = RequiredToken(root);
                    if (!_nativeSaveSessions.TryRemove(token, out var session)) throw new InvalidDataException("受信セッションが終了しています。");
                    await session.CompleteAsync();
                    break;
                }
                case "abort":
                {
                    var token = RequiredToken(root);
                    if (_nativeSaveSessions.TryRemove(token, out var session)) await session.DisposeAsync();
                    break;
                }
                default:
                    throw new InvalidDataException("未対応の保存操作です。");
            }
            PostNativeSaveResult(requestId, true, resultToken, null);
        }
        catch (Exception error)
        {
            var publicMessage = error switch
            {
                InvalidDataException => error.Message,
                UnauthorizedAccessException => "受信フォルダーへのアクセスが拒否されました。保存先を確認してください。",
                IOException => "ファイルを保存できません。保存先の空き容量と権限を確認してください。",
                FormatException or ArgumentException or InvalidOperationException => error.Message,
                _ => "受信ファイルを安全に保存できませんでした。もう一度お試しください。"
            };
            System.Diagnostics.Debug.WriteLine(error);
            PostNativeSaveResult(requestId, false, null, publicMessage);
        }
    }

    private NativeSaveSession GetNativeSaveSession(JsonElement root)
    {
        var token = RequiredToken(root);
        return _nativeSaveSessions.TryGetValue(token, out var session)
            ? session
            : throw new InvalidDataException("受信セッションが終了しています。");
    }

    private static string RequiredToken(JsonElement root)
    {
        var token = RequiredString(root, "token", 64);
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) throw new InvalidDataException("受信セッション情報が不正です。");
        return token;
    }

    private static string RequiredString(JsonElement root, string name, int maximumLength)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { Length: > 0 } text || text.Length > maximumLength)
            throw new InvalidDataException("受信ファイル情報が不正です。");
        return text;
    }

    private static long RequiredInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || !value.TryGetInt64(out var number) || number < 0)
            throw new InvalidDataException("受信サイズ情報が不正です。");
        return number;
    }

    private void PostNativeSaveResult(string requestId, bool ok, string? token, string? error)
    {
        try
        {
            _webView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                channel = "ez-share-save-result", requestId, ok, token, error
            }));
        }
        catch (InvalidOperationException) { }
    }

    private async Task CleanupNativeSaveSessionsAsync()
    {
        foreach (var pair in _nativeSaveSessions.ToArray())
            if (_nativeSaveSessions.TryRemove(pair.Key, out var session))
                await session.DisposeAsync();
    }

    private bool IsTrustedOrigin(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals(_page.Scheme, StringComparison.OrdinalIgnoreCase) &&
        uri.Host.Equals(_page.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == _page.Port;

    private void ShowError(string message) => Content = new Border
    {
        Padding = new Thickness(24),
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(25, 31, 40)),
        Child = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.White, VerticalAlignment = VerticalAlignment.Center }
    };

    private sealed class NativeSaveSession : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly string _root;
        private readonly string _stagingDirectory;
        private readonly string _stagingFile;
        private readonly TransferFile _file;
        private FileStream? _stream;
        private bool _completed;

        private NativeSaveSession(string root, string stagingDirectory, string stagingFile, TransferFile file, FileStream stream) =>
            (_root, _stagingDirectory, _stagingFile, _file, _stream) = (root, stagingDirectory, stagingFile, file, stream);

        public static Task<NativeSaveSession> StartAsync(string receiveDirectory, string token, TransferFile file)
        {
            var root = Path.GetFullPath(receiveDirectory);
            Directory.CreateDirectory(root);
            var stagingDirectory = Path.Combine(root, ".EZConverter-P2P-" + token);
            Directory.CreateDirectory(stagingDirectory);
            try
            {
                File.SetAttributes(stagingDirectory, File.GetAttributes(stagingDirectory) | FileAttributes.Hidden);
                var stagingFile = Path.Combine(stagingDirectory, file.Id + ".part");
                var stream = new FileStream(stagingFile, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                return Task.FromResult(new NativeSaveSession(root, stagingDirectory, stagingFile, file, stream));
            }
            catch
            {
                try { Directory.Delete(stagingDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }

        public async Task WriteAsync(byte[] bytes)
        {
            await _gate.WaitAsync();
            try
            {
                var stream = RequireOpenStream();
                if (bytes.Length == 0 || stream.Position > _file.Length - bytes.Length)
                    throw new InvalidDataException("受信データが予定サイズを超えました。");
                await stream.WriteAsync(bytes);
            }
            finally { _gate.Release(); }
        }

        public async Task TruncateAsync(long length)
        {
            await _gate.WaitAsync();
            try
            {
                var stream = RequireOpenStream();
                if (length > _file.Length) throw new InvalidDataException("再開位置が予定サイズを超えています。");
                stream.SetLength(length);
                stream.Position = length;
            }
            finally { _gate.Release(); }
        }

        public async Task SeekAsync(long position)
        {
            await _gate.WaitAsync();
            try
            {
                var stream = RequireOpenStream();
                if (position > stream.Length) throw new InvalidDataException("再開位置が保存済みデータを超えています。");
                stream.Position = position;
            }
            finally { _gate.Release(); }
        }

        public async Task CompleteAsync()
        {
            await _gate.WaitAsync();
            try
            {
                var stream = RequireOpenStream();
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
                if (stream.Length != _file.Length) throw new InvalidDataException("受信ファイルのサイズが一致しません。");
                await stream.DisposeAsync();
                _stream = null;

                await using (var verify = new FileStream(_stagingFile, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
                {
                    var actual = await SHA256.HashDataAsync(verify);
                    var expected = Convert.FromHexString(_file.Sha256);
                    try
                    {
                        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                            throw new InvalidDataException("SHA-256が一致しないため、ファイルを保存しませんでした。");
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(actual);
                        CryptographicOperations.ZeroMemory(expected);
                    }
                }

                var target = await MoveToUniqueTargetAsync();
                _completed = true;
                try { Directory.Delete(_stagingDirectory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                _ = target;
            }
            catch
            {
                await AbortCoreAsync();
                throw;
            }
            finally { _gate.Release(); }
        }

        private async Task<string> MoveToUniqueTargetAsync()
        {
            var relative = _file.RelativePath.Replace('\\', '/');
            var segments = relative.Split('/');
            var directory = _root;
            foreach (var segment in segments[..^1])
            {
                directory = Path.Combine(directory, segment);
                if (File.Exists(directory)) throw new IOException("受信先のフォルダー名が既存ファイルと競合しています。");
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("受信先にリンクされたフォルダーがあるため保存できません。");
            }

            var leaf = segments[^1];
            var extension = Path.GetExtension(leaf);
            var stem = Path.GetFileNameWithoutExtension(leaf);
            for (var index = 0; index < 10_000; index++)
            {
                var name = index == 0 ? leaf : $"{stem} ({index}){extension}";
                var target = Path.Combine(directory, name);
                if (File.Exists(target) || Directory.Exists(target)) continue;
                try
                {
                    File.Move(_stagingFile, target, overwrite: false);
                    return target;
                }
                catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { }
            }
            throw new IOException("同名ファイルが多すぎるため、安全な保存先を作成できません。");
        }

        private FileStream RequireOpenStream() => _stream ?? throw new InvalidOperationException("受信セッションはすでに終了しています。");

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync();
            try { await AbortCoreAsync(); }
            finally { _gate.Release(); }
        }

        private async Task AbortCoreAsync()
        {
            if (_completed) return;
            if (_stream is not null)
            {
                await _stream.DisposeAsync();
                _stream = null;
            }
            try { Directory.Delete(_stagingDirectory, recursive: true); }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

public sealed record PeerSessionStatus(
    string State,
    string? FileId = null,
    string? FileName = null,
    int? Percent = null,
    long? Completed = null,
    long? Total = null);
