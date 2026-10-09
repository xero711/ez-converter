using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;

namespace MediaConverter.Views;

public sealed record DetectedPageMedia(string Url, string Kind, string PageUrl, string UserAgent)
{
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>
/// Opens a page in a temporary, isolated WebView2 profile and discovers ordinary
/// HTTP(S) video manifests or files requested by its player. Browser cookies are
/// never copied to the downloader.
/// </summary>
public sealed partial class VideoPageProbeWindow : Window
{
    private const int MaximumCandidates = 32;
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(1200);

    private readonly Uri _pageUri;
    private readonly string _userDataFolder;
    private readonly WebView2 _browser;
    private readonly TextBlock _status;
    private readonly ListBox _mediaList;
    private readonly Button _saveButton;
    private readonly DispatcherTimer _scanTimer;
    private readonly ObservableCollection<DetectedPageMedia> _media = [];
    private readonly HashSet<string> _mediaUrls = new(StringComparer.Ordinal);
    private bool _navigationCompleted;
    private bool _scanInProgress;
    private bool _wasClosed;
    private string _userAgent = string.Empty;

    public DetectedPageMedia? SelectedMedia { get; private set; }
    public ObservableCollection<DetectedPageMedia> MediaCandidates => _media;

    public VideoPageProbeWindow(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri) || !IsHttpUri(pageUri))
        {
            throw new ArgumentException("HTTPまたはHTTPSのページURLを指定してください。", nameof(pageUrl));
        }

        _pageUri = pageUri;
        InitializeComponent();
        _userDataFolder = Path.Combine(Path.GetTempPath(), "EZConverter", "VideoProbe-" + Guid.NewGuid().ToString("N"));
        _browser = BrowserControl;
        _status = StatusText;
        _mediaList = MediaList;
        _saveButton = SaveMediaButton;
        DataContext = this;
        _browser.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = _userDataFolder
        };
        _scanTimer = new DispatcherTimer { Interval = ScanInterval };
        _scanTimer.Tick += ScanTimer_Tick;
        Loaded += InitializeBrowser;
        Closed += OnClosed;
    }

    public static bool IsBrowserRuntimeAvailable => PeerSessionWindow.IsRuntimeAvailable;

    public static DetectedPageMedia? PickMedia(Window owner, string pageUrl)
    {
        var window = new VideoPageProbeWindow(pageUrl) { Owner = owner };
        _ = window.ShowDialog();
        return window.SelectedMedia;
    }

    private async void InitializeBrowser(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!IsBrowserRuntimeAvailable)
            {
                _status.Text = "ページ内動画の検出に必要なMicrosoft Edge WebView2 Runtimeがありません。";
                return;
            }

            await _browser.EnsureCoreWebView2Async();
            var core = _browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) ||
                    !IsHttpUri(target))
                {
                    args.Cancel = true;
                }
            };
            core.WebResourceResponseReceived += OnWebResourceResponseReceived;
            core.NavigationCompleted += (_, args) =>
            {
                _navigationCompleted = args.IsSuccess;
                _status.Text = args.IsSuccess
                    ? "ページを開きました。必要ならプレイヤーを再生してください。検出された動画は下に表示されます。"
                    : "ページを読み込めませんでした。URLと公開範囲を確認してください。";
            };
            core.Navigate(_pageUri.AbsoluteUri);
            _scanTimer.Start();
        }
        catch (Exception exception)
        {
            _status.Text = $"ページを開けませんでした（{exception.GetType().Name}）。";
        }
    }

    private void OnWebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        try
        {
            if (args.Response.StatusCode is < 200 or >= 400)
            {
                return;
            }

            var contentType = args.Response.Headers.GetHeader("Content-Type");
            AddMediaCandidate(args.Request.Uri, contentType);
        }
        catch (Exception)
        {
            // Some browser resources do not expose headers; DOM scanning remains available.
        }
    }

    private async void ScanTimer_Tick(object? sender, EventArgs e)
    {
        if (!_navigationCompleted || _scanInProgress || _wasClosed || _browser.CoreWebView2 is null)
        {
            return;
        }

        _scanInProgress = true;
        try
        {
            var json = await _browser.CoreWebView2.ExecuteScriptAsync(
                "Array.from(document.querySelectorAll('video')).flatMap(v => " +
                "[v.currentSrc, v.src, ...Array.from(v.querySelectorAll('source')).map(s => s.src)]" +
                ".filter(u => typeof u === 'string' && /^https?:/i.test(u)).map(u => ({url:u})))");
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var candidate in document.RootElement.EnumerateArray())
            {
                if (candidate.TryGetProperty("url", out var urlElement) && urlElement.GetString() is { } url)
                {
                    AddMediaCandidate(url, string.Empty);
                }
            }

            if (string.IsNullOrWhiteSpace(_userAgent))
            {
                var userAgentJson = await _browser.CoreWebView2.ExecuteScriptAsync("navigator.userAgent");
                _userAgent = JsonSerializer.Deserialize<string>(userAgentJson) ?? string.Empty;
            }
        }
        catch (Exception)
        {
            // The page may be navigating or closing; the next timer tick can retry.
        }
        finally
        {
            _scanInProgress = false;
        }
    }

    private void AddMediaCandidate(string value, string? contentType)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !IsHttpUri(uri) ||
            !string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            return;
        }

        var kind = ClassifyMedia(uri, contentType);
        if (kind is null || _media.Count >= MaximumCandidates || !_mediaUrls.Add(uri.AbsoluteUri))
        {
            return;
        }

        var candidate = new DetectedPageMedia(uri.AbsoluteUri, kind, _pageUri.GetLeftPart(UriPartial.Path), _userAgent)
        {
            DisplayName = $"候補 {_media.Count + 1} · {kind} · {uri.IdnHost}"
        };
        _media.Add(candidate);
        if (_mediaList.SelectedItem is null)
        {
            _mediaList.SelectedItem = candidate;
        }

        _status.Text = $"動画候補を{_media.Count}件検出しました。保存する動画を選んでください。";
    }

    private void MediaList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _saveButton.IsEnabled = _mediaList.SelectedItem is DetectedPageMedia;
    }

    private static string? ClassifyMedia(Uri uri, string? contentType)
    {
        var mimeType = (contentType ?? string.Empty).Split(';', 2)[0].Trim().ToLowerInvariant();
        var path = uri.AbsolutePath.ToLowerInvariant();

        if (path.EndsWith(".m3u8", StringComparison.Ordinal) ||
            mimeType is "application/vnd.apple.mpegurl" or "application/x-mpegurl" or "audio/mpegurl")
        {
            return "HLSプレイリスト";
        }

        if (path.EndsWith(".mpd", StringComparison.Ordinal) || mimeType == "application/dash+xml")
        {
            return "DASHプレイリスト";
        }

        if (path.EndsWith(".mp4", StringComparison.Ordinal) ||
            path.EndsWith(".m4v", StringComparison.Ordinal) ||
            path.EndsWith(".webm", StringComparison.Ordinal) ||
            path.EndsWith(".mov", StringComparison.Ordinal) ||
            mimeType is "video/mp4" or "video/webm" or "video/quicktime")
        {
            return "動画ファイル";
        }

        if (path.EndsWith(".mp3", StringComparison.Ordinal) ||
            path.EndsWith(".m4a", StringComparison.Ordinal) ||
            path.EndsWith(".aac", StringComparison.Ordinal) ||
            mimeType.StartsWith("audio/", StringComparison.Ordinal))
        {
            return "音声ファイル";
        }

        return null;
    }

    private static bool IsHttpUri(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private void SaveSelectedMedia(object sender, RoutedEventArgs e)
    {
        if (_mediaList.SelectedItem is not DetectedPageMedia selected)
        {
            return;
        }

        SelectedMedia = selected with { UserAgent = _userAgent };
        DialogResult = true;
        Close();
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        _wasClosed = true;
        _scanTimer.Stop();
        try
        {
            _browser.Dispose();
        }
        catch (Exception)
        {
            // Best-effort cleanup during window shutdown.
        }

        _ = DeleteTemporaryProfileAsync();
    }

    private async Task DeleteTemporaryProfileAsync()
    {
        var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EZConverter")) + Path.DirectorySeparatorChar;
        var profilePath = Path.GetFullPath(_userDataFolder);
        if (!profilePath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(profilePath).StartsWith("VideoProbe-", StringComparison.Ordinal))
        {
            return;
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                if (Directory.Exists(profilePath))
                {
                    Directory.Delete(profilePath, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                await Task.Delay(350);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(350);
            }
        }
    }
}
