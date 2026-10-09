using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using MediaConverter.Models;
using MediaConverter.Services;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace MediaConverter;

public partial class MainWindow : Window
{
    private readonly DependencyChecker _dependencyChecker = new();
    private readonly ConversionService _conversionService = new();
    private readonly YtDlpUpdateService _ytDlpUpdateService = new();
    private readonly DenoUpdateService _denoUpdateService = new();
    private readonly FfmpegUpdateService _ffmpegUpdateService = new();
    private readonly AppUpdateService _appUpdateService = new();
    private readonly string _logFilePath;
    private readonly ICollectionView _itemsView;
    private AppPreferences _appPreferences = new();
    private readonly ExplorerArchiveLaunchRequest? _explorerArchiveLaunchRequest;
    private readonly Dictionary<ConversionBackend, string?> _toolPaths = new();
    private readonly System.Windows.Threading.DispatcherTimer _ytDlpUpdateTimer = new()
    {
        Interval = TimeSpan.FromHours(12)
    };
    private CancellationTokenSource? _conversionCancellation;
    private CancellationTokenSource? _youtubeCancellation;

    public MainWindow()
    {
        InitializeComponent();
        _appPreferences = AppPreferencesStore.Load();
        EnableTurnRelayCheckBox.IsChecked = _appPreferences.EnableTurnRelay;
        TurnServerUrlBox.Text = _appPreferences.TurnServerUrl;
        UpdateTurnSettingsStatus();
        UseNamedTunnelCheckBox.IsChecked = _appPreferences.UseNamedTunnel;
        NamedTunnelHostnameBox.Text = _appPreferences.NamedTunnelHostname;
        NamedTunnelPortBox.Text = _appPreferences.NamedTunnelPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        UpdateNamedTunnelSettingsStatus();
        ExplorerContextMenuCheckBox.IsChecked = _appPreferences.AddExplorerContextMenu;
        UpdateExplorerContextMenuStatus(_appPreferences.AddExplorerContextMenu);
        try
        {
            ExplorerContextMenuService.SetEnabled(_appPreferences.AddExplorerContextMenu);
        }
            catch (Exception exception)
            {
            UpdateExplorerContextMenuStatus(_appPreferences.AddExplorerContextMenu, exception.Message);
        }

        _explorerArchiveLaunchRequest = ExplorerArchiveLaunchRequest.TryParse(Environment.GetCommandLineArgs().Skip(1));
        if (_explorerArchiveLaunchRequest is not null)
        {
            ArchiveTabItem.IsSelected = true;
            ArchivePanel.PrepareFromExplorer(_explorerArchiveLaunchRequest);
        }

        var startupOptions = WindowsStartupService.GetCurrent();
        _appPreferences.StartWithWindows = startupOptions.Enabled;
        if (startupOptions.Enabled)
        {
            _appPreferences.StartMinimized = startupOptions.StartMinimized;
        }

        CheckAppUpdatesCheckBox.IsChecked = _appPreferences.CheckForAppUpdatesOnStartup;
        AutoUpdateMediaToolsCheckBox.IsChecked = _appPreferences.AutoUpdateMediaTools;
        StartWithWindowsCheckBox.IsChecked = _appPreferences.StartWithWindows;
        StartMinimizedCheckBox.IsChecked = _appPreferences.StartMinimized;
        StartMinimizedCheckBox.IsEnabled = _appPreferences.StartWithWindows;
        AppVersionText.Text = $"現在のバージョン: {_appUpdateService.CurrentVersion}";
        AppUpdateSettingsStatusText.Text = _appUpdateService.IsConfigured
            ? "更新が見つかった場合は通知し、確認後に更新します。"
            : "アプリの更新元が未設定のため、更新確認は利用できません。";

        if (Environment.GetCommandLineArgs().Skip(1)
            .Any(argument => string.Equals(argument, "--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            WindowState = System.Windows.WindowState.Minimized;
        }

        DataContext = this;
        UpdateWindowStateGlyph();
        _itemsView = CollectionViewSource.GetDefaultView(Items);
        _itemsView.Filter = IsVisibleInSearch;
        Items.CollectionChanged += (_, _) => UpdateFileCount();
        OutputDirectoryTextBox.Text = GetDefaultOutputDirectory();
        YouTubeOutputDirectoryTextBox.Text = GetDefaultOutputDirectory();
        _logFilePath = Path.Combine(AppContext.BaseDirectory, "conversion.log");
        _ytDlpUpdateTimer.Tick += YtDlpUpdateTimer_Tick;
        Closed += (_, _) => _ytDlpUpdateTimer.Stop();
        Closing += MainWindow_ClosingForSharing;
        Loaded += MainWindow_Loaded;
        UpdateYouTubeFormats();
    }

    public ObservableCollection<MediaItem> Items { get; } = [];
    public ICollectionView ItemsView => _itemsView;

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void SettingsWindowButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsTabItem.IsSelected = true;
    }

    private async void SettingsPreference_Click(object sender, RoutedEventArgs e)
    {
        var previousPreferences = _appPreferences;
        var updatedPreferences = new AppPreferences
        {
            CheckForAppUpdatesOnStartup = CheckAppUpdatesCheckBox.IsChecked == true,
            AutoUpdateMediaTools = AutoUpdateMediaToolsCheckBox.IsChecked == true,
            StartWithWindows = StartWithWindowsCheckBox.IsChecked == true,
            StartMinimized = StartMinimizedCheckBox.IsChecked == true,
            AddExplorerContextMenu = ExplorerContextMenuCheckBox.IsChecked == true,
            EnableTurnRelay = _appPreferences.EnableTurnRelay,
            TurnServerUrl = _appPreferences.TurnServerUrl,
            TurnSharedSecret = _appPreferences.TurnSharedSecret,
            ProtectedTurnSharedSecret = _appPreferences.ProtectedTurnSharedSecret,
            UseNamedTunnel = _appPreferences.UseNamedTunnel,
            NamedTunnelHostname = _appPreferences.NamedTunnelHostname,
            NamedTunnelPort = _appPreferences.NamedTunnelPort,
            NamedTunnelToken = _appPreferences.NamedTunnelToken,
            ProtectedNamedTunnelToken = _appPreferences.ProtectedNamedTunnelToken
        };
        var startupSettingChanged = ReferenceEquals(sender, StartWithWindowsCheckBox)
            || ReferenceEquals(sender, StartMinimizedCheckBox);
        var explorerMenuSettingChanged = ReferenceEquals(sender, ExplorerContextMenuCheckBox);

        try
        {
            if (startupSettingChanged)
            {
                WindowsStartupService.Set(updatedPreferences.StartWithWindows, updatedPreferences.StartMinimized);
            }
            if (explorerMenuSettingChanged)
            {
                ExplorerContextMenuService.SetEnabled(updatedPreferences.AddExplorerContextMenu);
            }

            AppPreferencesStore.Save(updatedPreferences);
            _appPreferences = updatedPreferences;
            StartMinimizedCheckBox.IsEnabled = updatedPreferences.StartWithWindows;
            if (explorerMenuSettingChanged) UpdateExplorerContextMenuStatus(updatedPreferences.AddExplorerContextMenu);

            if (ReferenceEquals(sender, AutoUpdateMediaToolsCheckBox))
            {
                if (updatedPreferences.AutoUpdateMediaTools)
                {
                    _ytDlpUpdateTimer.Start();
                    if (_youtubeCancellation is null && _conversionCancellation is null)
                    {
                        await UpdateYtDlpAsync(force: false, showError: false);
                        await UpdateDenoAsync(force: false, showError: false);
                        await UpdateFfmpegAsync(force: false);
                    }
                }
                else
                {
                    _ytDlpUpdateTimer.Stop();
                }
            }
        }
        catch (Exception exception)
        {
            if (startupSettingChanged)
            {
                try
                {
                    WindowsStartupService.Set(previousPreferences.StartWithWindows, previousPreferences.StartMinimized);
                }
                catch
                {
                    // Keep the preference UI usable even if Windows rejects rollback.
                }
            }
            if (explorerMenuSettingChanged)
            {
                try
                {
                    ExplorerContextMenuService.SetEnabled(previousPreferences.AddExplorerContextMenu);
                }
                catch
                {
                    // Keep the preference UI usable if Windows rejects the rollback.
                }
            }

            _appPreferences = previousPreferences;
            CheckAppUpdatesCheckBox.IsChecked = previousPreferences.CheckForAppUpdatesOnStartup;
            AutoUpdateMediaToolsCheckBox.IsChecked = previousPreferences.AutoUpdateMediaTools;
            StartWithWindowsCheckBox.IsChecked = previousPreferences.StartWithWindows;
            StartMinimizedCheckBox.IsChecked = previousPreferences.StartMinimized;
            StartMinimizedCheckBox.IsEnabled = previousPreferences.StartWithWindows;
            ExplorerContextMenuCheckBox.IsChecked = previousPreferences.AddExplorerContextMenu;
            EnableTurnRelayCheckBox.IsChecked = previousPreferences.EnableTurnRelay;
            TurnServerUrlBox.Text = previousPreferences.TurnServerUrl;
            UseNamedTunnelCheckBox.IsChecked = previousPreferences.UseNamedTunnel;
            NamedTunnelHostnameBox.Text = previousPreferences.NamedTunnelHostname;
            NamedTunnelPortBox.Text = previousPreferences.NamedTunnelPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            UpdateTurnSettingsStatus(exception.Message);
            UpdateNamedTunnelSettingsStatus(exception.Message);
            UpdateExplorerContextMenuStatus(previousPreferences.AddExplorerContextMenu, exception.Message);
            MessageBox.Show(this, $"設定を保存できませんでした。\n\n{exception.Message}", "設定", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveTurnSettings_Click(object sender, RoutedEventArgs e)
    {
        var previous = _appPreferences;
        var enabled = EnableTurnRelayCheckBox.IsChecked == true;
        var url = TurnServerUrlBox.Text.Trim();
        var secret = TurnSharedSecretBox.Password.Length > 0 ? TurnSharedSecretBox.Password : previous.TurnSharedSecret;
        try
        {
            if (enabled) EZConverter.Sharing.TransferServer.ValidateTurnRelaySettings(url, secret);
            var updated = new AppPreferences
            {
                CheckForAppUpdatesOnStartup = previous.CheckForAppUpdatesOnStartup,
                AutoUpdateMediaTools = previous.AutoUpdateMediaTools,
                StartWithWindows = previous.StartWithWindows,
                StartMinimized = previous.StartMinimized,
                AddExplorerContextMenu = previous.AddExplorerContextMenu,
                EnableTurnRelay = enabled,
                TurnServerUrl = url,
                TurnSharedSecret = secret,
                ProtectedTurnSharedSecret = previous.ProtectedTurnSharedSecret,
                UseNamedTunnel = previous.UseNamedTunnel,
                NamedTunnelHostname = previous.NamedTunnelHostname,
                NamedTunnelPort = previous.NamedTunnelPort,
                NamedTunnelToken = previous.NamedTunnelToken,
                ProtectedNamedTunnelToken = previous.ProtectedNamedTunnelToken
            };
            AppPreferencesStore.Save(updated);
            SharingPanel.ConfigureTurnRelay(enabled, url, secret);
            _appPreferences = updated;
            TurnSharedSecretBox.Clear();
            UpdateTurnSettingsStatus();
        }
        catch (Exception exception)
        {
            EnableTurnRelayCheckBox.IsChecked = previous.EnableTurnRelay;
            TurnServerUrlBox.Text = previous.TurnServerUrl;
            UpdateTurnSettingsStatus(exception.Message);
            MessageBox.Show(this, $"TURN設定を保存できませんでした。\n\n{exception.Message}", "TURN設定", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearTurnSecret_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "保存済みのTURN共有シークレットを削除してTURN中継を無効にします。よろしいですか？", "TURN設定", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        var previous = _appPreferences;
        try
        {
            var updated = new AppPreferences
            {
                CheckForAppUpdatesOnStartup = previous.CheckForAppUpdatesOnStartup,
                AutoUpdateMediaTools = previous.AutoUpdateMediaTools,
                StartWithWindows = previous.StartWithWindows,
                StartMinimized = previous.StartMinimized,
                AddExplorerContextMenu = previous.AddExplorerContextMenu,
                EnableTurnRelay = false,
                TurnServerUrl = previous.TurnServerUrl,
                UseNamedTunnel = previous.UseNamedTunnel,
                NamedTunnelHostname = previous.NamedTunnelHostname,
                NamedTunnelPort = previous.NamedTunnelPort,
                NamedTunnelToken = previous.NamedTunnelToken,
                ProtectedNamedTunnelToken = previous.ProtectedNamedTunnelToken
            };
            AppPreferencesStore.Save(updated);
            SharingPanel.ConfigureTurnRelay(false, updated.TurnServerUrl, string.Empty);
            _appPreferences = updated;
            EnableTurnRelayCheckBox.IsChecked = false;
            TurnSharedSecretBox.Clear();
            UpdateTurnSettingsStatus();
        }
        catch (Exception exception)
        {
            UpdateTurnSettingsStatus(exception.Message);
            MessageBox.Show(this, $"保存済みシークレットを削除できませんでした。\n\n{exception.Message}", "TURN設定", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateTurnSettingsStatus(string? error = null)
    {
        TurnSettingsStatusText.Text = error is not null
            ? $"設定を適用できませんでした: {error}"
            : _appPreferences.EnableTurnRelay && string.IsNullOrEmpty(_appPreferences.TurnSharedSecret)
                ? "TURNが有効ですが共有シークレットが未設定です。設定を保存し直してください。"
            : _appPreferences.EnableTurnRelay
                ? "TURN中継は有効です。相手が開く共有ページに有効期限付きの接続資格情報を渡します。"
                : string.IsNullOrEmpty(_appPreferences.TurnSharedSecret)
                    ? "TURN中継は無効です。直接接続のみを試します。"
                    : "TURN中継は無効です。共有シークレットはこのWindowsユーザー向けに保護して保存済みです。";
    }

    private void SaveNamedTunnelSettings_Click(object sender, RoutedEventArgs e)
    {
        var previous = _appPreferences;
        var enabled = UseNamedTunnelCheckBox.IsChecked == true;
        var hostname = NamedTunnelHostnameBox.Text.Trim();
        var token = NamedTunnelTokenBox.Password.Length > 0 ? NamedTunnelTokenBox.Password : previous.NamedTunnelToken;
        try
        {
            if (!int.TryParse(NamedTunnelPortBox.Text.Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var port))
                throw new ArgumentException("ローカル接続ポートは1024～65535の整数で入力してください。");
            if (enabled) _ = EZConverter.Sharing.CloudflareTunnel.ValidateNamedTunnel(port, hostname, token);
            else if (port is < 1024 or > 65535) throw new ArgumentException("ローカル接続ポートは1024～65535で指定してください。");

            var updated = new AppPreferences
            {
                CheckForAppUpdatesOnStartup = previous.CheckForAppUpdatesOnStartup,
                AutoUpdateMediaTools = previous.AutoUpdateMediaTools,
                StartWithWindows = previous.StartWithWindows,
                StartMinimized = previous.StartMinimized,
                AddExplorerContextMenu = previous.AddExplorerContextMenu,
                EnableTurnRelay = previous.EnableTurnRelay,
                TurnServerUrl = previous.TurnServerUrl,
                TurnSharedSecret = previous.TurnSharedSecret,
                ProtectedTurnSharedSecret = previous.ProtectedTurnSharedSecret,
                UseNamedTunnel = enabled,
                NamedTunnelHostname = hostname,
                NamedTunnelPort = port,
                NamedTunnelToken = token,
                ProtectedNamedTunnelToken = previous.ProtectedNamedTunnelToken
            };
            AppPreferencesStore.Save(updated);
            _appPreferences = updated;
            NamedTunnelTokenBox.Clear();
            UpdateNamedTunnelSettingsStatus();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception)
        {
            UseNamedTunnelCheckBox.IsChecked = previous.UseNamedTunnel;
            NamedTunnelHostnameBox.Text = previous.NamedTunnelHostname;
            NamedTunnelPortBox.Text = previous.NamedTunnelPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            UpdateNamedTunnelSettingsStatus(exception.Message);
            MessageBox.Show(this, $"Named Tunnel設定を保存できませんでした。\n\n{exception.Message}", "Named Tunnel設定", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearNamedTunnelToken_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "保存済みのNamed Tunnelトークンを削除してNamed Tunnelを無効にします。よろしいですか？", "Named Tunnel設定", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        var previous = _appPreferences;
        try
        {
            var updated = new AppPreferences
            {
                CheckForAppUpdatesOnStartup = previous.CheckForAppUpdatesOnStartup,
                AutoUpdateMediaTools = previous.AutoUpdateMediaTools,
                StartWithWindows = previous.StartWithWindows,
                StartMinimized = previous.StartMinimized,
                AddExplorerContextMenu = previous.AddExplorerContextMenu,
                EnableTurnRelay = previous.EnableTurnRelay,
                TurnServerUrl = previous.TurnServerUrl,
                TurnSharedSecret = previous.TurnSharedSecret,
                ProtectedTurnSharedSecret = previous.ProtectedTurnSharedSecret,
                UseNamedTunnel = false,
                NamedTunnelHostname = previous.NamedTunnelHostname,
                NamedTunnelPort = previous.NamedTunnelPort
            };
            AppPreferencesStore.Save(updated);
            _appPreferences = updated;
            UseNamedTunnelCheckBox.IsChecked = false;
            NamedTunnelTokenBox.Clear();
            UpdateNamedTunnelSettingsStatus();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception)
        {
            UpdateNamedTunnelSettingsStatus(exception.Message);
            MessageBox.Show(this, $"保存済みトークンを削除できませんでした。\n\n{exception.Message}", "Named Tunnel設定", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateNamedTunnelSettingsStatus(string? error = null)
    {
        NamedTunnelSettingsStatusText.Text = error is not null
            ? $"設定を適用できませんでした: {error}"
            : _appPreferences.UseNamedTunnel && string.IsNullOrEmpty(_appPreferences.NamedTunnelToken)
                ? "Named Tunnelが有効ですがトークンがありません。設定を保存し直してください。"
                : _appPreferences.UseNamedTunnel
                    ? "Named Tunnelを使用します。公開ホスト名とサービス先ポートがCloudflare側の設定と一致することを確認してください。"
                    : string.IsNullOrEmpty(_appPreferences.NamedTunnelToken)
                        ? "Named Tunnelは無効です。オンライン共有では一時Quick Tunnelを使用します。"
                        : "Named Tunnelは無効です。トークンはこのWindowsユーザー向けに保護して保存済みです。";
    }

    private void UpdateExplorerContextMenuStatus(bool enabled, string? error = null)
    {
        ExplorerContextMenuStatusText.Text = error is not null
            ? $"右クリック項目を設定できませんでした: {error}"
            : enabled
                ? "このPCだけに追加します。Windows 11では「その他のオプションを表示」にあります。"
                : "右クリックメニューには追加されていません。";
    }

    private async void CheckForUpdatesNowButton_Click(object sender, RoutedEventArgs e)
    {
        CheckForUpdatesNowButton.IsEnabled = false;
        AppUpdateSettingsStatusText.Text = "更新を確認しています...";
        try
        {
            await CheckForAppUpdateAsync(manual: true);
        }
        finally
        {
            CheckForUpdatesNowButton.IsEnabled = true;
        }
    }

    private void ToggleWindowState_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == System.Windows.WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        UpdateWindowStateGlyph();
    }

    private void UpdateWindowStateGlyph()
    {
        var isMaximized = WindowState == System.Windows.WindowState.Maximized;
        WindowStateGlyph.Data = Geometry.Parse(isMaximized
            ? "M 4,1 H 13 V 10 M 10,4 H 1 V 13 H 10 Z"
            : "M 1,1 H 13 V 13 H 1 Z");
        WindowStateButton.ToolTip = isMaximized ? "元のサイズに戻す" : "最大化";
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_explorerArchiveLaunchRequest is not null)
        {
            _ = Dispatcher.BeginInvoke(new Action(ArchivePanel.StartFromExplorer), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        await CheckDependenciesAsync(showMessage: false);
        if (_appPreferences.AutoUpdateMediaTools)
        {
            await UpdateYtDlpAsync(force: false, showError: false);
            await UpdateDenoAsync(force: false, showError: false);
            await UpdateFfmpegAsync(force: false);
            _ytDlpUpdateTimer.Start();
        }

        if (_appPreferences.CheckForAppUpdatesOnStartup)
        {
            await CheckForAppUpdateAsync();
        }
    }

    private async void YtDlpUpdateTimer_Tick(object? sender, EventArgs e)
    {
        if (_appPreferences.AutoUpdateMediaTools
            && _youtubeCancellation is null
            && _conversionCancellation is null)
        {
            await UpdateYtDlpAsync(force: false, showError: false);
            await UpdateDenoAsync(force: false, showError: false);
            await UpdateFfmpegAsync(force: false);
        }
    }

    private async Task UpdateFfmpegAsync(bool force)
    {
        try
        {
            var result = await _ffmpegUpdateService.UpdateIfNeededAsync(force);
            YouTubeDependencyText.Text = result.Detail;
            AppendYouTubeLog($"[FFmpeg] {result.Detail}");
        }
        catch (Exception exception)
        {
            var fallbackPath = ToolLocator.FindFfmpeg();
            var detail = fallbackPath is null
                ? $"FFmpegの自動取得に失敗しました。インターネット接続を確認してください。{Environment.NewLine}{exception.Message}"
                : $"FFmpegの更新を確認できませんでした。現在のエンジンを継続使用します。{Environment.NewLine}{exception.Message}";
            YouTubeDependencyText.Text = fallbackPath is null ? "FFmpegの自動取得に失敗" : "FFmpeg更新確認に失敗。既存版を使用中";
            AppendYouTubeLog($"[FFmpeg更新エラー] {detail}");
        }

        await CheckDependenciesAsync(showMessage: false);
    }

    private void SelectFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "変換するファイルを選択",
            Multiselect = true,
            CheckFileExists = true,
            Filter = MediaFormatCatalog.GetOpenFileDialogFilter()
        };

        if (dialog.ShowDialog(this) == true)
        {
            AddFiles(dialog.FileNames);
        }
    }

    private void FilesDropArea_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void FilesDropArea_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
        {
            AddFiles(paths);
        }

        e.Handled = true;
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Items.Any(item => item.SourcePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var item = new MediaItem(path);
            if (item.Kind == MediaKind.Unknown)
            {
                AppendLog($"[スキップ] 対応していない形式: {path}");
                continue;
            }

            Items.Add(item);
            added++;
        }

        OverallStatusText.Text = added > 0
            ? $"{added}件を追加しました。出力形式を選択して変換を開始してください。"
            : "追加できる対応ファイルがありません。";
    }

    private bool IsVisibleInSearch(object item)
    {
        if (item is not MediaItem mediaItem || string.IsNullOrWhiteSpace(SearchFilesTextBox?.Text))
        {
            return true;
        }

        var query = SearchFilesTextBox.Text.Trim();
        return mediaItem.FileName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || mediaItem.SourcePath.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || mediaItem.KindLabel.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || (mediaItem.SourceFormat?.Extension.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }

    private void SearchFilesTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _itemsView.Refresh();
    }

    private void FilesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RemoveSelectedButton.IsEnabled = FilesGrid.SelectedItems.Count > 0;
    }

    private void UpdateFileCount()
    {
        FileCountText.Text = $"{Items.Count} 件";
        if (FilesGrid is not null && RemoveSelectedButton is not null)
        {
            RemoveSelectedButton.IsEnabled = FilesGrid.SelectedItems.Count > 0;
        }
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = FilesGrid.SelectedItems.Cast<MediaItem>().ToList();
        foreach (var item in selected)
        {
            Items.Remove(item);
        }

        OverallStatusText.Text = $"{Items.Count}件のファイルを選択中です。";
    }

    private void ClearFiles_Click(object sender, RoutedEventArgs e)
    {
        Items.Clear();
        BottomProgressBar.Value = 0;
        OverallStatusText.Text = "ファイル一覧をクリアしました。";
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "変換後ファイルの保存先を選択してください。",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(OutputDirectoryTextBox.Text)
                ? OutputDirectoryTextBox.Text
                : GetDefaultOutputDirectory()
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            OutputDirectoryTextBox.Text = dialog.SelectedPath;
        }
    }

    private void YouTubeModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateYouTubeFormats();
    }

    private void UpdateYouTubeFormats()
    {
        if (YouTubeFormatCombo is null || YouTubeModeCombo is null)
        {
            return;
        }

        var isAudio = IsYouTubeAudioMode();
        YouTubeFormatCombo.ItemsSource = isAudio
            ? new[] { "mp3", "m4a", "flac", "wav", "opus", "aac" }
            : new[] { "mp4", "mkv", "webm", "mov", "avi" };
        YouTubeFormatCombo.SelectedIndex = 0;

        YouTubeQualityCombo.Items.Clear();
        var qualities = isAudio
            ? new[] { "最高品質", "320k", "256k", "192k", "128k" }
            : new[] { "最高品質", "1080p", "720p", "480p", "360p" };
        foreach (var quality in qualities)
        {
            YouTubeQualityCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = quality });
        }

        YouTubeQualityCombo.SelectedIndex = 0;
    }

    private bool IsYouTubeAudioMode()
    {
        return (YouTubeModeCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() == "音声";
    }

    private void BrowseYouTubeOutput_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "URLから取得したファイルの保存先を選択してください。",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(YouTubeOutputDirectoryTextBox.Text)
                ? YouTubeOutputDirectoryTextBox.Text
                : GetDefaultOutputDirectory()
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            YouTubeOutputDirectoryTextBox.Text = dialog.SelectedPath;
        }
    }

    private async void UpdateYtDlp_Click(object sender, RoutedEventArgs e)
    {
        await UpdateYtDlpAsync(force: true, showError: true);
    }

    private async Task UpdateYtDlpAsync(bool force, bool showError, bool allowDuringDownload = false)
    {
        if (_youtubeCancellation is not null && !allowDuringDownload)
        {
            return;
        }

        YtDlpUpdateButton.IsEnabled = false;
        YouTubeDependencyText.Text = force ? "yt-dlpを更新しています..." : "yt-dlpの自動更新を確認しています...";
        try
        {
            var result = await _ytDlpUpdateService.UpdateIfNeededAsync(force);
            YouTubeDependencyText.Text = result.Detail;
            AppendYouTubeLog($"[yt-dlp] {result.Detail}");
            await CheckDependenciesAsync(showMessage: false);
            if (showError)
            {
                MessageBox.Show(this, result.Detail, "yt-dlp更新", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception exception)
        {
            var fallbackPath = ToolLocator.FindYtDlp();
            var message = fallbackPath is null
                ? $"自動更新に失敗しました。インターネット接続を確認してください。\n{exception.Message}"
                : $"自動更新を確認できませんでした。現在のyt-dlpを継続使用します。\n{exception.Message}";
            YouTubeDependencyText.Text = fallbackPath is null ? "更新できませんでした（未導入）" : "更新確認に失敗。既存版を使用中";
            AppendYouTubeLog($"[yt-dlp更新エラー] {message}");
            await CheckDependenciesAsync(showMessage: false);
            if (showError)
            {
                MessageBox.Show(this, message, "yt-dlp更新", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            YtDlpUpdateButton.IsEnabled = true;
        }
    }

    private async Task UpdateDenoAsync(bool force, bool showError, bool allowDuringDownload = false)
    {
        if (_youtubeCancellation is not null && !allowDuringDownload)
        {
            return;
        }

        try
        {
            var result = await _denoUpdateService.UpdateIfNeededAsync(force);
            YouTubeDependencyText.Text = result.Detail;
            AppendYouTubeLog($"[Deno] {result.Detail}");
        }
        catch (Exception exception)
        {
            var fallbackPath = ToolLocator.FindDeno();
            var message = fallbackPath is null
                ? $"YouTubeなどのJavaScript抽出に必要なDenoを自動取得できませんでした。インターネット接続を確認してください。\n{exception.Message}"
                : $"Denoの更新を確認できませんでした。現在の実行環境を継続使用します。\n{exception.Message}";
            YouTubeDependencyText.Text = fallbackPath is null ? "Denoの自動取得に失敗" : "Deno更新確認に失敗。既存版を使用中";
            AppendYouTubeLog($"[Deno更新エラー] {message}");
            if (showError)
            {
                MessageBox.Show(this, message, "Deno更新", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        await CheckDependenciesAsync(showMessage: false);
    }

    private async void DownloadYouTube_Click(object sender, RoutedEventArgs e)
    {
        if (_youtubeCancellation is not null)
        {
            return;
        }

        var url = NormalizeMediaUrl(YouTubeUrlTextBox.Text);
        if (url is null)
        {
            MessageBox.Show(this, "動画ページ、埋め込み、またはメディアのHTTP(S) URLを入力してください。", "URLから保存", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        YouTubeUrlTextBox.Text = url;

        var outputDirectory = YouTubeOutputDirectoryTextBox.Text.Trim();
        var format = YouTubeFormatCombo.SelectedItem?.ToString();
        var quality = (YouTubeQualityCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "最高品質";
        if (string.IsNullOrWhiteSpace(outputDirectory) || string.IsNullOrWhiteSpace(format))
        {
            MessageBox.Show(this, "保存先と形式を指定してください。", "URLから保存", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"保存先を作成できません。\n{exception.Message}", "URLから保存", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var isAudio = IsYouTubeAudioMode();
        _youtubeCancellation = new CancellationTokenSource();
        SetYouTubeState(true);
        BottomProgressBar.IsIndeterminate = true;
        BottomProgressBar.Value = 0;
        YouTubeStatusText.Text = "準備中...";

        try
        {
            var ytDlpPath = GetYtDlpPath();
            if (ytDlpPath is null)
            {
                await UpdateYtDlpAsync(force: false, showError: false, allowDuringDownload: true);
                ytDlpPath = GetYtDlpPath();
            }

            if (ytDlpPath is null)
            {
                throw new InvalidOperationException("URLの保存に必要な機能を準備できませんでした。インターネット接続を確認して、もう一度お試しください。");
            }

            if (ToolLocator.FindDeno() is null)
            {
                await UpdateDenoAsync(force: false, showError: false, allowDuringDownload: true);
            }

            var ffmpegPath = _toolPaths.TryGetValue(ConversionBackend.Ffmpeg, out var cachedFfmpegPath)
                ? cachedFfmpegPath
                : ToolLocator.FindFfmpeg();
            if (ffmpegPath is not null)
            {
                _toolPaths[ConversionBackend.Ffmpeg] = ffmpegPath;
            }
            else
            {
                await UpdateFfmpegAsync(force: false);
                ffmpegPath = ToolLocator.FindFfmpeg();
                if (ffmpegPath is not null)
                {
                    _toolPaths[ConversionBackend.Ffmpeg] = ffmpegPath;
                }
            }

            if (ffmpegPath is null)
            {
                throw new InvalidOperationException("音声や形式の変換に必要な機能を準備できませんでした。アプリを再起動して、もう一度お試しください。");
            }

            _youtubeCancellation.Token.ThrowIfCancellationRequested();
            var arguments = BuildMediaArguments(url, outputDirectory, format, quality, isAudio, ffmpegPath);
            YouTubeStatusText.Text = "ページ内の動画を解析して保存しています...";
            AppendYouTubeLog($"[開始] {SanitizeUrlForLog(url)} -> {format}");
            var result = await ExternalToolRunner.RunAsync(ytDlpPath, arguments, _youtubeCancellation.Token);
            AppendYouTubeLog(result.StandardOutput);
            AppendYouTubeLog(result.StandardError);
            _youtubeCancellation.Token.ThrowIfCancellationRequested();
            if (result.ExitCode != 0)
            {
                throw new ConversionException(FormatDownloadError(result, url));
            }

            BottomProgressBar.IsIndeterminate = false;
            BottomProgressBar.Value = 100;
            var downloadedPath = FindDownloadedFilePath(result, outputDirectory);
            var downloadedName = downloadedPath is null ? "ファイル" : Path.GetFileName(downloadedPath);
            YouTubeStatusText.Text = $"保存完了: {downloadedName}";
            YouTubeStatusText.ToolTip = downloadedPath ?? outputDirectory;
            AppendYouTubeLog($"[完了] 保存先: {outputDirectory}");
            MessageBox.Show(
                this,
                downloadedPath is null
                    ? $"保存が完了しました。\n\n保存先: {outputDirectory}"
                    : $"保存が完了しました。\n\n{downloadedPath}",
                "保存完了",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            BottomProgressBar.IsIndeterminate = false;
            BottomProgressBar.Value = 0;
            YouTubeStatusText.Text = "キャンセルしました。";
            AppendYouTubeLog("[キャンセル]");
        }
        catch (Exception exception)
        {
            BottomProgressBar.IsIndeterminate = false;
            BottomProgressBar.Value = 0;
            YouTubeStatusText.Text = "保存に失敗しました。";
            AppendYouTubeLog($"[エラー] {exception.Message}");
            MessageBox.Show(this, exception.Message, "URLから保存", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _youtubeCancellation.Dispose();
            _youtubeCancellation = null;
            SetYouTubeState(false);
        }
    }

    private void CancelYouTube_Click(object sender, RoutedEventArgs e)
    {
        _youtubeCancellation?.Cancel();
    }

    private static IReadOnlyList<string> BuildMediaArguments(string url, string outputDirectory, string format, string quality, bool isAudio, string ffmpegPath)
    {
        var arguments = new List<string>
        {
            "--ignore-config",
            "--no-playlist",
            "--newline",
            "--progress",
            "--no-overwrites",
            "--print", "after_move:filepath",
            "--ffmpeg-location", ffmpegPath,
            "-P", outputDirectory,
            "-o", "%(title)s.%(ext)s"
        };

        if (ToolLocator.FindDeno() is { } denoPath)
        {
            arguments.AddRange(["--js-runtimes", $"deno:{Path.GetFullPath(denoPath)}"]);
        }

        if (isAudio)
        {
            arguments.AddRange(["-x", "--audio-format", format, "--audio-quality", quality == "最高品質" ? "0" : quality]);
        }
        else
        {
            var formatSelector = quality switch
            {
                "1080p" => "bv*[height<=1080]+ba/b[height<=1080]",
                "720p" => "bv*[height<=720]+ba/b[height<=720]",
                "480p" => "bv*[height<=480]+ba/b[height<=480]",
                "360p" => "bv*[height<=360]+ba/b[height<=360]",
                _ => "bv*+ba/b"
            };
            arguments.AddRange(["-f", formatSelector, "--merge-output-format", format, "--recode-video", format, "--no-keep-video"]);
        }

        arguments.Add(url);
        return arguments;
    }

    private static string? GetYtDlpPath() => ToolLocator.FindYtDlp();

    private void SetYouTubeState(bool busy)
    {
        BottomProgressSurface.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        YouTubeDownloadButton.IsEnabled = !busy;
        YouTubeCancelButton.IsEnabled = busy && _youtubeCancellation is not null;
        YouTubeUrlTextBox.IsEnabled = !busy;
        YouTubeOutputDirectoryTextBox.IsEnabled = !busy;
        YouTubeModeCombo.IsEnabled = !busy;
        YouTubeFormatCombo.IsEnabled = !busy;
        YouTubeQualityCombo.IsEnabled = !busy;
        YtDlpUpdateButton.IsEnabled = !busy;
    }

    private static string? NormalizeMediaUrl(string input)
    {
        var url = input.Trim();
        if (url.Length == 0 || url.Any(char.IsWhiteSpace))
        {
            return null;
        }

        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }
        else if (!url.Contains("://", StringComparison.Ordinal))
        {
            url = "https://" + url;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && string.IsNullOrWhiteSpace(uri.UserInfo)
            && !string.IsNullOrWhiteSpace(uri.Host)
                ? url
                : null;
    }

    private static bool IsSupportedMediaUrl(string url) => NormalizeMediaUrl(url) is not null;

    private static string FormatDownloadError(ToolRunResult result, string url)
    {
        var output = string.Join(
            Environment.NewLine,
            new[] { result.StandardError, result.StandardOutput }
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .SelectMany(text => text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .TakeLast(6));
        var lower = output.ToLowerInvariant();
        var explanation = lower.Contains("private") || lower.Contains("login") || lower.Contains("sign in") || lower.Contains("authentication")
            ? "非公開または認証が必要なコンテンツは取得できません。"
            : lower.Contains("javascript runtime") || lower.Contains("no supported javascript")
                ? "動画サイトの解析に必要なJavaScript実行環境を準備できませんでした。ネット接続を確認してDenoを更新してください。"
            : lower.Contains("expired") || lower.Contains("403") || lower.Contains("forbidden") || lower.Contains("not found")
                ? "URLが期限切れ、無効、またはアクセス権のない可能性があります。"
                : lower.Contains("drm") || lower.Contains("encrypted")
                    ? "DRMや暗号化による保護の回避には対応していません。"
                    : "公開範囲、利用権限、URL、出力形式を確認してください。";
        var detail = string.IsNullOrWhiteSpace(output) ? string.Empty : $"\n\n詳細:\n{output}";
        return $"URLを取得できませんでした。{explanation}\n合法的にアクセスできるURLのみ利用してください。{detail}\nURL: {SanitizeUrlForLog(url)}";
    }

    private static string? FindDownloadedFilePath(ToolRunResult result, string outputDirectory)
    {
        var lines = new[] { result.StandardOutput, result.StandardError }
            .SelectMany(text => text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
            .Select(line => line.Trim().Trim('"'))
            .Reverse();

        foreach (var line in lines)
        {
            if (Path.IsPathFullyQualified(line) && File.Exists(line))
            {
                return Path.GetFullPath(line);
            }

            var candidate = Path.Combine(outputDirectory, line);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static string SanitizeUrlForLog(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Path)
            : url;
    }

    private async Task CheckForAppUpdateAsync(bool manual = false)
    {
        if (!_appUpdateService.IsConfigured)
        {
            AppUpdateSettingsStatusText.Text = "アプリの更新元が未設定のため、更新確認は利用できません。";
            if (manual)
            {
                MessageBox.Show(this, AppUpdateSettingsStatusText.Text, "アプリの更新", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }

        AppUpdateInfo? update = null;
        var updateWasAccepted = false;
        try
        {
            AppUpdateSettingsStatusText.Text = "GitHub Releasesで更新を確認しています...";
            update = await _appUpdateService.CheckForUpdateAsync();
            if (update is null)
            {
                AppUpdateSettingsStatusText.Text = $"最新のバージョンです（{_appUpdateService.CurrentVersion}）。";
                if (manual)
                {
                    MessageBox.Show(this, AppUpdateSettingsStatusText.Text, "アプリの更新", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }

            AppUpdateSettingsStatusText.Text = $"新しいバージョン {update.Version} が利用できます。";
            var notes = string.IsNullOrWhiteSpace(update.ReleaseNotes) ? "リリースノートはありません。" : update.ReleaseNotes;
            var answer = MessageBox.Show(
                this,
                $"新しいバージョン {update.Version} があります。現在のバージョンは {_appUpdateService.CurrentVersion} です。\n\n{notes}\n\n今すぐダウンロードして更新しますか？",
                "新しいバージョンがあります",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes)
            {
                AppUpdateSettingsStatusText.Text = $"バージョン {update.Version} の更新を保留しました。";
                return;
            }

            updateWasAccepted = true;
            IsEnabled = false;
            Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            var archivePath = await _appUpdateService.DownloadAndVerifyAsync(update);
            _appUpdateService.StartUpdateAgent(archivePath);
            Mouse.OverrideCursor = null;
            MessageBox.Show(this, "更新ファイルを検証しました。アプリを終了して更新後のバージョンを起動します。", "更新", MessageBoxButton.OK, MessageBoxImage.Information);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            AppendLog($"[アプリ更新エラー] {exception.Message}");
            AppUpdateSettingsStatusText.Text = "更新を確認できませんでした。ネットワーク接続を確認してください。";
            if (updateWasAccepted)
            {
                Mouse.OverrideCursor = null;
                IsEnabled = true;
                MessageBox.Show(this, $"更新に失敗しました。現在のバージョンを継続使用します。\n\n{exception.Message}", "更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else if (manual)
            {
                MessageBox.Show(this, $"更新を確認できませんでした。\n\n{exception.Message}", "アプリの更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            if (!updateWasAccepted || !IsEnabled)
            {
                Mouse.OverrideCursor = null;
            }
        }
    }

    private async void CheckDependencies_Click(object sender, RoutedEventArgs e)
    {
        await CheckDependenciesAsync(showMessage: true);
    }

    private async Task<bool> CheckDependenciesAsync(bool showMessage)
    {
        try
        {
            var results = (await _dependencyChecker.CheckAsync()).ToList();
            _toolPaths.Clear();
            foreach (var result in results)
            {
                if (result.Name != "Deno")
                {
                    _toolPaths[GetBackend(result.Name)] = result.IsAvailable ? result.Path : null;
                }
                UpdateToolStatus(result);
            }

            var allAvailable = results.All(result => result.IsAvailable);
            if (showMessage)
            {
                OverallStatusText.Text = allAvailable
                    ? "準備ができました。"
                    : "一部の変換機能を準備できませんでした。";
            }

            if (showMessage)
            {
                MessageBox.Show(this, BuildDependencyMessage(results), "依存関係チェック", MessageBoxButton.OK,
                    allAvailable ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }

            return allAvailable;
        }
        catch (Exception exception)
        {
            OverallStatusText.Text = "準備に失敗しました。";
            AppendLog($"[依存関係エラー] {exception.Message}");
            if (showMessage)
            {
                MessageBox.Show(this, exception.Message, "依存関係チェック", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return false;
        }
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (_conversionCancellation is not null)
        {
            return;
        }

        if (Items.Count == 0)
        {
            MessageBox.Show(this, "変換するファイルを追加してください。", "変換", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var missingFormats = Items.Where(item => item.SelectedOutputFormat is null).ToList();
        if (missingFormats.Count > 0)
        {
            var names = string.Join(Environment.NewLine, missingFormats.Take(5).Select(item => $"・{item.FileName}"));
            var more = missingFormats.Count > 5 ? $"{Environment.NewLine}ほか{missingFormats.Count - 5}件" : string.Empty;
            MessageBox.Show(this, $"出力形式を選択してください。{Environment.NewLine}{names}{more}", "変換", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var unsupportedItems = Items.Where(item => item.SourceFormat is null
            || ConversionService.ResolveBackend(item.SourceFormat, item.SelectedOutputFormat!) is null).ToList();
        if (unsupportedItems.Count > 0)
        {
            var names = string.Join(Environment.NewLine, unsupportedItems.Take(5).Select(item => $"・{item.FileName}"));
            MessageBox.Show(this, $"この組み合わせでは変換できません。別の形式を選んでください。{Environment.NewLine}{names}", "変換できません", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var outputDirectory = OutputDirectoryTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            MessageBox.Show(this, "出力先フォルダを指定してください。", "変換", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"出力先フォルダを作成できません。\n{exception.Message}", "変換", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var requiredBackends = Items
            .Select(item => ConversionService.ResolveBackend(item.SourceFormat!, item.SelectedOutputFormat!))
            .Where(backend => backend.HasValue)
            .Select(backend => backend!.Value)
            .Distinct()
            .Where(backend => backend != ConversionBackend.PdfText)
            .ToList();
        _conversionCancellation = new CancellationTokenSource();
        SetConvertingState(true);
        BottomProgressBar.IsIndeterminate = true;
        BottomProgressBar.Value = 0;
        OverallStatusText.Text = "準備中...";
        OverallStatusText.ToolTip = null;
        var completed = 0;
        var converted = 0;
        var failed = 0;

        try
        {
            if (requiredBackends.Contains(ConversionBackend.Ffmpeg) && ToolLocator.FindFfmpeg() is null)
            {
                await UpdateFfmpegAsync(force: false);
            }

            if (requiredBackends.Any(backend => !_toolPaths.TryGetValue(backend, out var path) || path is null || !File.Exists(path)))
            {
                await CheckDependenciesAsync(showMessage: false);
            }

            _conversionCancellation.Token.ThrowIfCancellationRequested();
            var missingBackends = requiredBackends
                .Where(backend => !_toolPaths.TryGetValue(backend, out var path) || path is null || !File.Exists(path))
                .ToList();
            if (missingBackends.Count > 0)
            {
                var missingNames = string.Join(", ", missingBackends.Select(GetBackendName));
                throw new InvalidOperationException($"変換に必要な機能を準備できませんでした: {missingNames}。アプリを再起動して、もう一度お試しください。");
            }

            BottomProgressBar.IsIndeterminate = false;
            foreach (var item in Items.ToList())
            {
                _conversionCancellation.Token.ThrowIfCancellationRequested();
                item.Status = "変換中";
                item.Message = string.Empty;
                item.Progress = 0;
                var targetFormat = item.SelectedOutputFormat!;
                OverallStatusText.Text = $"{item.FileName} を変換しています... ({completed + 1}/{Items.Count})";
                AppendLog($"[開始] {item.SourcePath} -> {targetFormat.Extension}");

                var itemProgress = new Progress<int>(value =>
                {
                    item.Progress = value;
                    BottomProgressBar.Value = ((completed * 100d) + value) / Items.Count;
                });

                try
                {
                    var outputPath = await _conversionService.ConvertAsync(
                        item, targetFormat, outputDirectory, _toolPaths, itemProgress, _conversionCancellation.Token);
                    item.Status = "完了";
                    item.Progress = 100;
                    item.Message = Path.GetFileName(outputPath);
                    converted++;
                    AppendLog($"[完了] {outputPath}");
                    RememberConvertedForSharing(outputPath);
                }
                catch (OperationCanceledException)
                {
                    item.Status = "キャンセル";
                    item.Message = "ユーザーによりキャンセルされました。";
                    AppendLog($"[キャンセル] {item.FileName}");
                    throw;
                }
                catch (Exception exception)
                {
                    item.Status = "エラー";
                    item.Message = exception.Message;
                    failed++;
                    AppendLog($"[エラー] {item.FileName}: {exception.Message}");
                }

                completed++;
                BottomProgressBar.Value = completed * 100d / Items.Count;
            }

            var firstFailure = Items.FirstOrDefault(item => item.Status == "エラー");
            OverallStatusText.Text = firstFailure is null
                ? $"変換完了: 成功 {converted}件。"
                : $"変換完了: 成功 {converted}件 / エラー {failed}件。{firstFailure.FileName}: {FirstLine(firstFailure.Message)}";
            OverallStatusText.ToolTip = firstFailure?.Message;
        }
        catch (OperationCanceledException)
        {
            OverallStatusText.Text = $"変換をキャンセルしました。完了 {converted}件。";
        }
        catch (Exception exception)
        {
            OverallStatusText.Text = "変換の準備に失敗しました。";
            OverallStatusText.ToolTip = exception.Message;
            AppendLog($"[準備エラー] {exception.Message}");
            MessageBox.Show(this, exception.Message, "変換を開始できません", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _conversionCancellation.Dispose();
            _conversionCancellation = null;
            BottomProgressBar.IsIndeterminate = false;
            SetConvertingState(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _conversionCancellation?.Cancel();
    }

    private void SetConvertingState(bool isConverting)
    {
        BottomProgressSurface.Visibility = isConverting ? Visibility.Visible : Visibility.Collapsed;
        ConvertButton.IsEnabled = !isConverting;
        CancelButton.IsEnabled = isConverting;
        FilesGrid.IsEnabled = !isConverting;
        SearchFilesTextBox.IsEnabled = !isConverting;
        RemoveSelectedButton.IsEnabled = !isConverting && FilesGrid.SelectedItems.Count > 0;
        AddFilesButton.IsEnabled = !isConverting;
        ClearFilesButton.IsEnabled = !isConverting;
        OutputDirectoryTextBox.IsEnabled = !isConverting;
    }

    private static string FormatToolStatus(LocatedTool tool)
    {
        return tool.IsAvailable
            ? $"利用可能 ({tool.Version ?? "確認済み"})"
            : "未検出";
    }

    private void UpdateToolStatus(LocatedTool tool)
    {
        var statusText = tool.Name switch
        {
            "ImageMagick" => ImageMagickStatusText,
            "FFmpeg" => FfmpegStatusText,
            "LibreOffice" => LibreOfficeStatusText,
            "7-Zip" => SevenZipStatusText,
            "Calibre" => CalibreStatusText,
            "FontForge" => FontForgeStatusText,
            "yt-dlp" => YtDlpStatusText,
            "Deno" => DenoStatusText,
            _ => null
        };

        if (statusText is not null)
        {
            statusText.Text = FormatToolStatus(tool);
            statusText.ToolTip = tool.Detail;
        }
    }

    private static string BuildDependencyMessage(IReadOnlyList<LocatedTool> tools)
    {
        return string.Join(Environment.NewLine + Environment.NewLine,
            tools.Select(tool => $"{tool.Name}: {FormatToolStatus(tool)}\n{tool.Detail}"));
    }

    private static ConversionBackend GetBackend(string toolName) => toolName switch
    {
        "ImageMagick" => ConversionBackend.ImageMagick,
        "FFmpeg" => ConversionBackend.Ffmpeg,
        "LibreOffice" => ConversionBackend.LibreOffice,
        "7-Zip" => ConversionBackend.SevenZip,
        "Calibre" => ConversionBackend.Calibre,
        "FontForge" => ConversionBackend.FontForge,
        "yt-dlp" => ConversionBackend.YtDlp,
        _ => throw new InvalidOperationException($"未知の外部ツールです: {toolName}")
    };

    private static string GetBackendName(ConversionBackend backend) => backend switch
    {
        ConversionBackend.ImageMagick => "ImageMagick",
        ConversionBackend.Ffmpeg => "FFmpeg",
        ConversionBackend.LibreOffice => "LibreOffice",
        ConversionBackend.SevenZip => "7-Zip",
        ConversionBackend.Calibre => "Calibre",
        ConversionBackend.FontForge => "FontForge",
        ConversionBackend.YtDlp => "yt-dlp",
        _ => "変換エンジン"
    };

    private static string GetDefaultOutputDirectory()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        return Directory.Exists(videos) ? videos : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private static string FirstLine(string text)
    {
        return text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text;
    }

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
        try
        {
            File.AppendAllText(_logFilePath, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // Logging must not stop a conversion.
        }
    }

    private void AppendYouTubeLog(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var safeMessage = Regex.Replace(
            message.Trim(),
            @"(?<url>https?://[^\s?#]+)(?:[?#][^\s]*)?",
            "${url}",
            RegexOptions.IgnoreCase);
        YouTubeLogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {safeMessage}{Environment.NewLine}");
        YouTubeLogTextBox.ScrollToEnd();
        AppendLog($"[動画取得] {safeMessage}");
    }
}
