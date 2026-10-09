using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EZConverter.Sharing;
using MediaConverter.Models;
using MediaConverter.Services;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace MediaConverter.Views;

public partial class SharingView : UserControl, IAsyncDisposable
{
    private TransferServer? _server;
    private PeerDiscovery? _discovery;
    private readonly CloudflareTunnel _tunnel = new();
    private readonly Dictionary<string, LocalSendDownloadServer> _browserShares = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PeerSessionWindow> _peerWindows = new(StringComparer.Ordinal);
    private readonly HashSet<PeerSessionWindow> _receiveWindows = [];
    private CancellationTokenSource? _operationCancellation;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _stopping;
    private bool _disposed;
    private bool _busy;
    private string? _currentOperationId;
    private DateTimeOffset? _peerDiscoveryStartedAt;
    private readonly string _stateRoot;
    private readonly int _localSendHttpsPort;
    private readonly InvitationContactStore _registeredContactStore;
    private readonly NamedTunnelInvitationIdentityStore _namedTunnelInvitationIdentityStore;
    public ObservableCollection<SelectedPath> SelectedFiles { get; } = [];
    public ObservableCollection<PeerDevice> Peers { get; } = [];
    public ObservableCollection<IncomingRow> IncomingOffers { get; } = [];
    public ObservableCollection<LinkRow> Links { get; } = [];
    public ObservableCollection<InvitationContact> RegisteredContacts { get; } = [];
    public ObservableCollection<TransferRow> Transfers { get; } = [];
    public bool IsRunning => _server is not null || _browserShares.Count > 0 || _receiveWindows.Count > 0 || _busy;

    public void ConfigureTurnRelay(bool enabled, string url, string sharedSecret) =>
        _server?.ConfigureTurnRelay(enabled, url, sharedSecret);

    public SharingView() : this(null, TransferServer.LocalSendDefaultPort) { }

    internal SharingView(string? stateRoot, int localSendHttpsPort = TransferServer.LocalSendDefaultPort)
    {
        _localSendHttpsPort = localSendHttpsPort;
        _stateRoot = string.IsNullOrWhiteSpace(stateRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EZConverter", "Sharing")
            : Path.GetFullPath(stateRoot);
        _registeredContactStore = new InvitationContactStore(Path.Combine(_stateRoot, "registered-internet-contacts.dat"));
        _namedTunnelInvitationIdentityStore = new NamedTunnelInvitationIdentityStore(Path.Combine(_stateRoot, "named-tunnel-invitation.dat"));
        InitializeComponent();
        DataContext = this;
        ShowPage("Send");
        UpdateFileCount();
        UpdateIncomingEmptyState();
        UpdateHistoryEmptyState();
        RestoreTransferHistory();
        ReloadRegisteredContacts();
        DeviceNameBox.Text = Environment.MachineName;
        ReceiveDirectoryBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "EZ Share");
        try
        {
            var path = Path.Combine(_stateRoot, "preferences.json");
            if (File.Exists(path) && JsonSerializer.Deserialize<SharePreferences>(File.ReadAllText(path)) is { } saved)
            {
                DeviceNameBox.Text = saved.Name;
                ReceiveDirectoryBox.Text = saved.Directory;
                if (!string.IsNullOrEmpty(saved.ProtectedReceivePin))
                {
                    try { LocalSendReceivePinBox.Password = DpapiStringProtector.Unprotect(saved.ProtectedReceivePin); }
                    catch (Exception e) when (e is FormatException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException) { WriteLog(e); }
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        _tunnel.Status += message => Ui(() => StatusText.Text = message);
        _tunnel.Diagnostic += message => Ui(() => DiagnosticText(message));
        _tunnel.Disconnected += () => Ui(HandleTunnelDisconnected);
        _timer.Tick += async (_, _) =>
        {
            foreach (var link in Links.Where(l => l.Active && l.Info.ExpiresAt <= DateTimeOffset.UtcNow).ToArray()) await StopLinkAsync(link, "期限切れ");
            if (!_busy && _tunnel.IsRunning && !HasActiveOnlinePeerLink()) await _tunnel.StopAsync();
        };
        _timer.Start();
    }
    private sealed record SharePreferences(string Name, string Directory, string? ProtectedReceivePin = null);
    public enum OnlineTunnelProvider { Cloudflare }
    private bool HasActiveOnlinePeerLink() => ContainsActiveOnlinePeerLink(
        Links.Where(link => !_browserShares.ContainsKey(link.Info.Token)));
    internal static bool ContainsActiveOnlinePeerLink(IEnumerable<LinkRow> links) =>
        links.Any(link => link.Online && link.Active);
    private void HandleTunnelDisconnected()
    {
        StatusText.Text = "Cloudflare Tunnelの接続が終了しました。オンラインURLを作り直してください。";
        foreach (var link in Links.Where(link => link.Online && link.Active && !_browserShares.ContainsKey(link.Info.Token)).ToArray())
            _ = StopLinkAsync(link, "接続終了");
    }
    private void DiagnosticText(string message)
    {
        try
        {
            Directory.CreateDirectory(_stateRoot);
            var safeMessage = Regex.Replace(message, @"https://[a-z0-9-]+\.trycloudflare\.com(?:/[^\s]*)?", "[一時URL]");
            File.AppendAllText(Path.Combine(_stateRoot, "sharing.log"), $"[{DateTimeOffset.Now:O}] Cloudflare: {safeMessage}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is string page) ShowPage(page);
    }
    private void ShowPage(string page)
    {
        SendPage.Visibility = page == "Send" ? Visibility.Visible : Visibility.Collapsed;
        ReceivePage.Visibility = page == "Receive" ? Visibility.Visible : Visibility.Collapsed;
        LinksPage.Visibility = page == "Links" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == "History" ? Visibility.Visible : Visibility.Collapsed;

        var pageText = page switch
        {
            "Receive" => (Title: "受け取る", Subtitle: "受信を開始して、届いたファイルを確認します。"),
            "Links" => (Title: "URL共有", Subtitle: "送信元PCで一時配信。停止またはPC終了で無効になります。"),
            "History" => (Title: "転送履歴", Subtitle: "進み具合や、完了したファイルを確認できます。"),
            _ => (Title: "送る", Subtitle: "LAN内のLocalSend対応PC、または受信招待URLへ直接送信します。")
        };
        PageTitleText.Text = pageText.Title;
        PageSubtitleText.Text = pageText.Subtitle;

        foreach (var button in new[] { SendPageButton, ReceivePageButton, LinksPageButton, HistoryPageButton })
        {
            button.Tag = Equals(button.CommandParameter, page) ? "Active" : "Inactive";
        }
    }
    private void UpdateFileCount()
    {
        FileCountText.Text = SelectedFiles.Count == 0 ? "ファイルを追加してください" : $"{SelectedFiles.Count}件を選択中";
        LinkFileCountText.Text = SelectedFiles.Count == 0 ? "共有するファイルを選んでください。" : $"{SelectedFiles.Count}件のファイル・フォルダーを共有します。";
    }
    private void UpdateIncomingEmptyState() => NoIncomingText.Visibility = IncomingOffers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    private void UpdateHistoryEmptyState() => HistoryEmptyText.Visibility = Transfers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    private string TransferHistoryPath => Path.Combine(_stateRoot, "transfer-history.json");
    private void RestoreTransferHistory()
    {
        try
        {
            foreach (var entry in TransferHistoryStore.Load(TransferHistoryPath))
                Transfers.Add(new(entry.ToProgress(), entry.FinishedAtUtc, entry.EntryId));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { WriteLog(e); }
        UpdateHistoryEmptyState();
    }
    private void PersistTransferHistory()
    {
        try
        {
            var records = Transfers.Where(row => row.FinishedAtUtc is not null).Select(row => row.ToHistoryRecord()).ToArray();
            TransferHistoryStore.Save(TransferHistoryPath, records);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { WriteLog(e); }
    }
    private void Ui(Action action) { if (!_disposed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(action); }
    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_server is not null) return;
        var name = DeviceNameBox.Text.Trim();
        if (name.Length is < 1 or > 64) throw new InvalidOperationException("PCの表示名を入力してください。");
        var receivePin = LocalSendReceivePinBox.Password.Trim();
        if (receivePin.Length != 0 && (receivePin.Length != 6 || receivePin.Any(character => character is < '0' or > '9')))
            throw new InvalidOperationException("LocalSend受信PINは数字6桁で入力してください。");
        var directory = Path.GetFullPath(ReceiveDirectoryBox.Text.Trim());
        Directory.CreateDirectory(_stateRoot);
        var protectedReceivePin = string.IsNullOrEmpty(receivePin) ? null : DpapiStringProtector.Protect(receivePin);
        File.WriteAllText(Path.Combine(_stateRoot, "preferences.json"), JsonSerializer.Serialize(new SharePreferences(name, directory, protectedReceivePin)));
        var preferences = AppPreferencesStore.Load();
        var server = new TransferServer(new()
        {
            DeviceName = name,
            ReceiveDirectory = directory,
            StateDirectory = _stateRoot,
            HttpsPort = _localSendHttpsPort,
            SignalPort = preferences.UseNamedTunnel ? preferences.NamedTunnelPort : 0,
            LocalSendReceivePin = string.IsNullOrEmpty(receivePin) ? null : receivePin,
            EnableTurnRelay = preferences.EnableTurnRelay,
            TurnServerUrl = preferences.TurnServerUrl,
            TurnSharedSecret = preferences.TurnSharedSecret
        });
        server.Incoming += incoming => Ui(() =>
        {
            IncomingOffers.Add(new(incoming));
            UpdateIncomingEmptyState();
            ShowPage("Receive");
            StatusText.Text = $"{incoming.Sender} から受信確認が届いています。";
        });
        server.Progress += progress => Ui(() => Report(progress));
        server.Faulted += exception => Ui(() => WriteLog(exception));
        try { await server.StartAsync(ct); } catch { await server.DisposeAsync(); throw; }
        _server = server;
        _discovery = new(server.LocalSendInfo, clientCertificate: server.LocalSendClientCertificate);
        _discovery.Changed += peers => Ui(() =>
        {
            var selected = PeersList.SelectedItems.OfType<PeerDevice>().Select(peer => peer.Fingerprint).ToHashSet(StringComparer.Ordinal);
            Peers.Clear();
            foreach (var peer in peers) Peers.Add(peer);
            foreach (var peer in Peers.Where(peer => selected.Contains(peer.Fingerprint))) PeersList.SelectedItems.Add(peer);
        PeerStatusText.Text = GetPeerStatusText();
        });
        _discovery.Warning += text => Ui(() => PeerStatusText.Text = text);
        _peerDiscoveryStartedAt = DateTimeOffset.UtcNow;
        _discovery.Start();
        AddressCombo.ItemsSource = TransferServer.LocalAddresses().DefaultIfEmpty("127.0.0.1").ToArray();
        AddressCombo.SelectedIndex = 0;
        UpdateConnectionUrl();
        DeviceNameBox.IsEnabled = BrowseReceiveButton.IsEnabled = LocalSendReceivePinBox.IsEnabled = false;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        ListeningStatusText.Text = server.UsesLocalSendDefaultPort ? "受信中 · LocalSend標準ポート" : "受信中 · 自動検出";
        PeerStatusText.Text = GetPeerStatusText();
        StatusText.Text = "受信待機中です。送信要求は確認後に受け取ります。";
    }
    private string GetPeerStatusText()
    {
        var elapsed = _peerDiscoveryStartedAt is { } startedAt ? DateTimeOffset.UtcNow - startedAt : TimeSpan.Zero;
        return FormatPeerDiscoveryStatus(Peers.Count, elapsed, _server is { UsesLocalSendDefaultPort: false });
    }
    internal static string FormatPeerDiscoveryStatus(int peerCount, TimeSpan elapsed, bool usesAlternatePort)
    {
        var status = peerCount == 0
            ? elapsed >= TimeSpan.FromSeconds(15)
                ? "まだ端末が見つかりません。相手側でも受信を開始し、同じLANか確認してください。ファイアウォールやルーターで遮断される場合は、下の「接続URLを使って送る」から手動接続できます。"
                : "LocalSend対応端末を検索中です。相手側も受信を開始してください。"
            : $"{peerCount}台が見つかりました。Ctrlキーで複数選択できます。";
        return usesAlternatePort
            ? status + " 標準ポート53317が使用中のため、マルチキャスト検出を利用します。"
            : status;
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy || _disposed || _stopping) return;
        _busy = true;
        _currentOperationId = null;
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        CurrentOperationProgressBar.Value = 0;
        SetBusy(true);
        try { await action(cancellation.Token); }
        catch (OperationCanceledException)
        {
            StatusText.Text = "操作をキャンセルしました。";
            Transfers.FirstOrDefault(r => r.Id == _currentOperationId)?.End("キャンセル", null);
            PersistTransferHistory();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            Transfers.FirstOrDefault(r => r.Id == _currentOperationId)?.End("エラー", exception.Message);
            PersistTransferHistory();
            WriteLog(exception);
            System.Windows.MessageBox.Show(Window.GetWindow(this), exception.Message, "ファイル送信", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _operationCancellation = null; _busy = false; SetBusy(false); }
    }
    private void SetBusy(bool busy)
    {
        foreach (var button in new[] { SendPeerButton, SendUrlButton, ReceiveUrlButton, CreateShareButton, CreateInviteButton,
                     FindNamedElement<Button>("AddRegisteredContactButton"), FindNamedElement<Button>("RemoveRegisteredContactButton"),
                     FindNamedElement<Button>("SendRegisteredContactButton") }) button.IsEnabled = !busy;
        StartButton.IsEnabled = !busy && _server is null;
        CancelOperationButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CurrentOperationProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
    private void WriteLog(Exception exception)
    {
        try { Directory.CreateDirectory(_stateRoot); File.AppendAllText(Path.Combine(_stateRoot, "sharing.log"), $"[{DateTimeOffset.Now:O}] {exception.GetType().Name}: {exception.Message}{Environment.NewLine}"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private T FindNamedElement<T>(string name) where T : FrameworkElement =>
        FindLogicalElement<T>(this, name) ?? throw new InvalidOperationException($"共有画面のコントロールが見つかりません: {name}");
    private static T? FindLogicalElement<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T element && string.Equals(element.Name, name, StringComparison.Ordinal)) return element;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            var found = FindLogicalElement<T>(child, name);
            if (found is not null) return found;
        }
        return null;
    }
    private void ReloadRegisteredContacts()
    {
        RegisteredContacts.Clear();
        try
        {
            foreach (var contact in _registeredContactStore.Load()) RegisteredContacts.Add(contact);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            WriteLog(exception);
            StatusText.Text = "登録済み連絡先を読み込めませんでした。";
        }
    }
    private async void AddRegisteredContact_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var name = FindNamedElement<TextBox>("RegisteredContactNameBox").Text.Trim();
        var code = FindNamedElement<TextBox>("RegistrationCodeBox").Text.Trim();
        await InvitationCodeService.RestoreAndValidateAsync(code, ct);
        _registeredContactStore.Upsert(name, code);
        ReloadRegisteredContacts();
        FindNamedElement<ComboBox>("RegisteredContactsBox").SelectedItem = RegisteredContacts.FirstOrDefault(contact =>
            string.Equals(contact.RegistrationCode, code, StringComparison.Ordinal));
        StatusText.Text = $"{name} を登録しました。相手の招待が期限切れになった場合は、新しい登録コードに更新してください。";
    });
    private async void RemoveRegisteredContact_Click(object sender, RoutedEventArgs e) => await RunAsync(_ =>
    {
        if (FindNamedElement<ComboBox>("RegisteredContactsBox").SelectedItem is not InvitationContact contact)
            throw new InvalidOperationException("削除する登録済みの相手を選んでください。");
        _registeredContactStore.Remove(contact.RegistrationCode);
        ReloadRegisteredContacts();
        StatusText.Text = $"{contact.DisplayName} の登録を削除しました。";
        return Task.CompletedTask;
    });
    private async void SendRegisteredContact_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        if (FindNamedElement<ComboBox>("RegisteredContactsBox").SelectedItem is not InvitationContact contact)
            throw new InvalidOperationException("送信先の登録済みの相手を選んでください。");
        var invitation = await InvitationCodeService.RestoreAndValidateAsync(contact.RegistrationCode, ct);
        var files = await PrepareFilesAsync(ct);
        StatusText.Text = $"{contact.DisplayName} のPCへ接続しています。受信側で許可されると、WebRTCで直接送信します...";
        await P2PInvitationSender.SendAsync(invitation, files, DeviceNameBox.Text.Trim(),
            new Progress<TransferProgress>(ReportOperation), ct, Window.GetWindow(this));
        StatusText.Text = $"{contact.DisplayName} へのP2P直接送信が完了しました。受信側でSHA-256照合済みです。";
    });
    public void AddPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (SelectedFiles.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            if (Directory.Exists(path)) SelectedFiles.Add(new(path, Path.GetFileName(path), "フォルダー（中のファイルをまとめて送信）"));
            else if (File.Exists(path)) SelectedFiles.Add(new(path, Path.GetFileName(path), TransferFiles.Size(new FileInfo(path).Length)));
        }
        UpdateFileCount();
    }
    private async Task<List<LocalFile>> PrepareFilesAsync(CancellationToken ct)
    {
        if (SelectedFiles.Count == 0) throw new InvalidOperationException("送信するファイルを追加してください。");
        StatusText.Text = "ファイルの整合性を確認しています。大きいファイルは少し時間がかかります...";
        var paths = SelectedFiles.Select(p => p.Path).ToArray();
        return await Task.Run(() => TransferFiles.CollectAsync(paths, ct), ct);
    }
    private TimeSpan LinkLifetime() => TimeSpan.FromMinutes(int.Parse((string)((ComboBoxItem)LifetimeCombo.SelectedItem).Tag));
    private async void Start_Click(object sender, RoutedEventArgs e) => await RunAsync(EnsureStartedAsync);
    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        await StopAsync();
        StatusText.Text = "接続を停止しました。共有URLもすべて無効になりました。";
    }
    private async Task StopAsync()
    {
        if (_stopping) return;
        _stopping = true;
        try
        {
            _operationCancellation?.Cancel();
            await _tunnel.StopAsync();
            foreach (var share in _browserShares.Values.ToArray()) await share.DisposeAsync();
            _browserShares.Clear();
            foreach (var window in _receiveWindows.ToArray()) window.Close();
            _receiveWindows.Clear();
            foreach (var window in _peerWindows.Values.ToArray()) window.Close();
            _peerWindows.Clear();
            if (_discovery is not null) { await _discovery.DisposeAsync(); _discovery = null; }
            _peerDiscoveryStartedAt = null;
            if (_server is not null) { var server = _server; _server = null; await server.DisposeAsync(); }
            foreach (var link in Links) link.Stop("停止済み");
            foreach (var incoming in IncomingOffers) incoming.Transfer.Reject();
            IncomingOffers.Clear(); UpdateIncomingEmptyState();
            Peers.Clear(); ConnectionUrlBox.Clear(); FingerprintText.Text = "";
            DeviceNameBox.IsEnabled = BrowseReceiveButton.IsEnabled = LocalSendReceivePinBox.IsEnabled = true;
            StartButton.IsEnabled = true; StopButton.IsEnabled = false;
            ListeningStatusText.Text = "停止中";
            PeerStatusText.Text = "受信を開始すると、同じネットワーク上のLocalSend対応端末が表示されます。";
        }
        finally { _stopping = false; }
    }
    private void AddFiles_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFileDialog { Multiselect = true, Title = "送信するファイル", Filter = "すべてのファイル|*.*" }; if (dialog.ShowDialog() == true) AddPaths(dialog.FileNames); }
    private void AddFolder_Click(object sender, RoutedEventArgs e) { using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "送信するフォルダー" }; if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) AddPaths([dialog.SelectedPath]); }
    private void ClearFiles_Click(object sender, RoutedEventArgs e) { SelectedFiles.Clear(); UpdateFileCount(); }
    private void Files_DragOver(object sender, System.Windows.DragEventArgs e) { e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None; e.Handled = true; }
    private void Files_Drop(object sender, System.Windows.DragEventArgs e) { if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths) AddPaths(paths); e.Handled = true; }
    private void BrowseReceive_Click(object sender, RoutedEventArgs e) { using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "受信したファイルの保存先", SelectedPath = ReceiveDirectoryBox.Text }; if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) ReceiveDirectoryBox.Text = dialog.SelectedPath; }
    private void OpenReceive_Click(object sender, RoutedEventArgs e) { try { Directory.CreateDirectory(ReceiveDirectoryBox.Text); Process.Start(new ProcessStartInfo(ReceiveDirectoryBox.Text) { UseShellExecute = true }); } catch (Exception ex) { StatusText.Text = ex.Message; } }
    private void Address_Changed(object sender, SelectionChangedEventArgs e) => UpdateConnectionUrl();
    private void UpdateConnectionUrl() { if (_server is null || ConnectionUrlBox is null) return; ConnectionUrlBox.Text = $"https://{TransferServer.FormatUriHost(AddressCombo.SelectedItem?.ToString() ?? "127.0.0.1")}:{_server.HttpsPort}/#{_server.Fingerprint}"; FingerprintText.Text = "このPCの識別コード: " + _server.Fingerprint[..12] + "（相手のPC一覧で照合できます）"; }
    private void CopyConnection_Click(object sender, RoutedEventArgs e) => CopyText(ConnectionUrlBox.Text);
    private void CopyText(string value) { if (string.IsNullOrWhiteSpace(value)) return; try { System.Windows.Clipboard.SetText(value); StatusText.Text = "URLをコピーしました。相手に送ってください。"; } catch (System.Runtime.InteropServices.COMException) { StatusText.Text = "コピーできませんでした。URLを選択してコピーしてください。"; } }
    private async void SendPeer_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        await EnsureStartedAsync(ct);
        var peers = PeersList.SelectedItems.OfType<PeerDevice>().ToArray();
        if (peers.Length == 0) throw new InvalidOperationException("送信先のPCを1台以上選んでください。見つからない場合は接続URLを使用できます。");
        var files = await PrepareFilesAsync(ct);
        var senderName = DeviceNameBox.Text.Trim();
        var totalBytes = files.Sum(file => file.File.Length);
        IProgress<TransferProgress> progress = new Progress<TransferProgress>(ReportOperation);
        var failures = new ConcurrentBag<(string Peer, Exception Error)>();
        var partialTransfers = new ConcurrentBag<(string Peer, LocalSendSendResult Result)>();
        var noTransferPeers = new ConcurrentBag<string>();
        var succeeded = 0;
        await Parallel.ForEachAsync(peers, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 2 }, async (peer, token) =>
        {
            try
            {
                if (peer.IsLocalSend)
                {
                    var result = await LocalSendClient.SendAsync(peer.ConnectionUrl, files, senderName, _server!.LocalSendInfo,
                        progress, token, peer.Name, LocalSendPinBox.Text.Trim(), _server.LocalSendClientCertificate);
                    if (result.NoTransferNeeded) noTransferPeers.Add(peer.Name);
                    else if (result.IsPartial) partialTransfers.Add((peer.Name, result));
                }
                else
                {
                    using var client = await TransferClient.ConnectAsync(peer.ConnectionUrl, token);
                    await client.SendAsync(files, senderName, progress, token);
                }
                Interlocked.Increment(ref succeeded);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                failures.Add((peer.Name, error));
                progress.Report(new("peer-" + Guid.NewGuid().ToString("N"), "送信", peer.Name, 0,
                    totalBytes, "エラー", error.Message));
            }
        });

        if (failures.IsEmpty && partialTransfers.IsEmpty && noTransferPeers.IsEmpty)
            StatusText.Text = $"{succeeded}台すべてに送信が完了しました。";
        else if (failures.IsEmpty && !partialTransfers.IsEmpty)
            StatusText.Text = $"{succeeded}台に接続しました。一部のみ送信: {string.Join("、", partialTransfers.Select(item => $"{item.Peer} ({item.Result.AcceptedFileCount}/{files.Count}件)"))}";
        else if (failures.IsEmpty)
            StatusText.Text = $"{succeeded}台に接続しました。追加の転送が不要: {string.Join("、", noTransferPeers)}";
        else
        {
            foreach (var failure in failures) WriteLog(failure.Error);
            var failedPeers = string.Join("、", failures.Select(failure => failure.Peer));
            var partial = partialTransfers.IsEmpty ? "" : $" 一部のみ送信: {string.Join("、", partialTransfers.Select(item => $"{item.Peer} ({item.Result.AcceptedFileCount}/{files.Count}件)"))}。";
            var noTransfer = noTransferPeers.IsEmpty ? "" : $" 追加転送不要: {string.Join("、", noTransferPeers)}。";
            StatusText.Text = $"{succeeded}/{peers.Length}台に送信しました。失敗: {failedPeers}。{partial}{noTransfer}";
            if (succeeded == 0) throw new IOException("選択したPCへの送信にすべて失敗しました: " + failedPeers);
        }
    });
    private async void SendUrl_Click(object sender, RoutedEventArgs e) => await RunAsync(ct => SendAsync(TargetUrlBox.Text, ct));
    private async Task SendAsync(string url, CancellationToken ct)
    {
        var destination = url.Trim();
        Uri? invitationUri = null;
        if (destination.StartsWith("EZC1-", StringComparison.Ordinal))
        {
            invitationUri = await InvitationCodeService.RestoreAndValidateAsync(destination, ct);
        }
        else if (Uri.TryCreate(destination, UriKind.Absolute, out var parsedInvitationUri) &&
                 TransferClient.IsInvitationUri(parsedInvitationUri))
        {
            invitationUri = parsedInvitationUri;
        }

        if (invitationUri is not null)
        {
            var invitationFiles = await PrepareFilesAsync(ct);
            StatusText.Text = "受信招待へ接続しています。受信側で許可されると、ファイル本体をWebRTCで直接送ります...";
            await P2PInvitationSender.SendAsync(invitationUri, invitationFiles, DeviceNameBox.Text.Trim(),
                new Progress<TransferProgress>(ReportOperation), ct, Window.GetWindow(this));
            StatusText.Text = "P2P直接送信が完了しました。受信側でSHA-256照合済みです。";
            return;
        }

        await EnsureStartedAsync(ct);
        var files = await PrepareFilesAsync(ct);
        if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" && uri.AbsolutePath == "/" &&
            (uri.Scheme == "http" || uri.Fragment.TrimStart('#').Length == 64))
        {
            var result = await LocalSendClient.SendAsync(url, files, DeviceNameBox.Text.Trim(), _server!.LocalSendInfo,
                new Progress<TransferProgress>(ReportOperation), ct, pin: LocalSendPinBox.Text.Trim(), clientCertificate: _server.LocalSendClientCertificate);
            StatusText.Text = result.NoTransferNeeded ? "受信側は追加の転送が不要と応答しました。" :
                result.RejectedFileCount == 0 ? "LocalSend互換の送信が完了しました。" :
                result.AcceptedFileCount == 0 ? "受信側がファイルを受け入れませんでした。" :
                $"一部のみ送信しました: {result.AcceptedFileCount}/{files.Count}件。{result.RejectedFileCount}件は受信側が受け入れませんでした。";
            return;
        }
        using var client = await TransferClient.ConnectAsync(url, ct);
        StatusText.Text = $"{client.Device.Name} の受信許可を待っています...";
        await client.SendAsync(files, DeviceNameBox.Text.Trim(), new Progress<TransferProgress>(ReportOperation), ct);
        StatusText.Text = "送信が完了しました。";
    }
    private async void ReceiveUrl_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var url = ReceiveUrlBox.Text.Trim();
        if (TryGetOnlineP2PShareUri(url, out var onlineShareUri))
        {
            await EnsureP2PBrowserReadyAsync(ct);
            var window = new PeerSessionWindow(onlineShareUri, "EZ Converter · P2P共有受信", ReceiveDirectoryBox.Text) { Owner = Window.GetWindow(this) };
            window.StatusReported += status => Ui(() => UpdateP2PReceiveStatus(onlineShareUri, status));
            window.Closed += (_, _) =>
            {
                _receiveWindows.Remove(window);
                StopButton.IsEnabled = _server is not null || _browserShares.Count > 0 || _receiveWindows.Count > 0;
            };
            _receiveWindows.Add(window);
            StopButton.IsEnabled = true;
            window.Show();
            try
            {
                await window.Ready.WaitAsync(TimeSpan.FromSeconds(30), ct);
                if (window.InitializationError is not null) throw new InvalidOperationException("P2P共有ページを開けませんでした。URLとネットワークを確認してください。", window.InitializationError);
            }
            catch
            {
                window.Close();
                throw;
            }
            StatusText.Text = "インターネット共有をP2P受信ページで開きました。ファイル本体は送信元PCから直接受信します。";
            return;
        }

        StatusText.Text = "LAN共有からファイルを受け取っています...";
        var folder = await ShareDownloader.DownloadAsync(url, ReceivePasswordBox.Password, ReceiveDirectoryBox.Text,
            new Progress<TransferProgress>(ReportOperation), ct);
        StatusText.Text = "受信完了: " + folder;
    });

    private static bool TryGetOnlineP2PShareUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps &&
            string.IsNullOrEmpty(parsed.UserInfo) && string.IsNullOrEmpty(parsed.Query) &&
            Regex.IsMatch(parsed.AbsolutePath, @"^/s/[a-f0-9]{64}/?$", RegexOptions.CultureInvariant))
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    private async Task EnsureP2PBrowserReadyAsync(CancellationToken cancellationToken)
    {
        StatusText.Text = PeerSessionWindow.IsRuntimeAvailable
            ? "P2Pブラウザーを確認しています..."
            : "初回利用のため、P2P用ブラウザーを自動セットアップしています...";
        await PeerSessionWindow.EnsureRuntimeAvailableAsync(cancellationToken);
    }

    private void UpdateP2PReceiveStatus(Uri shareUri, PeerSessionStatus status)
    {
        var fileLabel = string.IsNullOrWhiteSpace(status.FileName) ? "" : ": " + status.FileName;
        StatusText.Text = status.State switch
        {
            "waiting" => "P2P受信: 送信元PCとの接続を待機中...",
            "connected" => "P2P受信: 送信元PCへ直接接続しました。ファイルを選んで受信してください。",
            "progress" => $"P2P受信中{fileLabel} · {status.Percent ?? 0}%",
            "file-saved" => "P2P受信完了" + fileLabel + "（SHA-256確認済み）",
            "disconnected" => "P2P接続が一時切断されました。ページを開いたまま再接続を待っています。",
            "failed" => "P2P受信に失敗しました。受信ページのエラーを確認してください。",
            _ => $"P2P共有ページ: {shareUri.Host}"
        };
    }
    private async void CreateShare_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var files = await PrepareFilesAsync(ct);
        await CreateLinkAsync(false, files, ct);
    });
    private async void CreateInvite_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct => { await EnsureStartedAsync(ct); await CreateLinkAsync(true, null, ct); });
    private async void RotateNamedTunnelInvitation_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        if (!AppPreferencesStore.Load().UseNamedTunnel)
            throw new InvalidOperationException("先に「設定」でNamed Tunnelの固定ホスト名を設定してください。");
        if (System.Windows.MessageBox.Show(Window.GetWindow(this),
                "以前に登録したEZC1コードを無効にし、新しい登録コードを発行します。登録済みの相手には新しいコードを伝えてください。続行しますか？",
                "インターネット登録コードの更新", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            return;

        foreach (var link in Links.Where(link => link.StableRegistrationAddress && link.Active).ToArray())
            await StopLinkAsync(link, "登録コードを更新しました");
        _namedTunnelInvitationIdentityStore.RotateToken();
        OnlineCheckBox.IsChecked = true;
        await EnsureStartedAsync(ct);
        await CreateLinkAsync(true, null, ct);
    });
    private async Task CreateLinkAsync(bool invite, List<LocalFile>? files, CancellationToken ct)
    {
        var online = OnlineCheckBox.IsChecked == true;
        if (invite)
        {
            var preferences = AppPreferencesStore.Load();
            var stableRegistrationToken = online && preferences.UseNamedTunnel
                ? _namedTunnelInvitationIdentityStore.LoadOrCreateToken()
                : null;
            if (stableRegistrationToken is not null)
            {
                var existingInvitation = Links.FirstOrDefault(link => link.Info.IsInvitation &&
                    string.Equals(link.Info.Token, stableRegistrationToken, StringComparison.Ordinal));
                if (existingInvitation is { Active: true } && existingInvitation.Info.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    StatusText.Text = "固定ホスト名の受け取り招待はすでに有効です。登録済みの相手は同じコードを使えます。";
                    return;
                }

                if (existingInvitation is { Active: true }) await StopLinkAsync(existingInvitation, "期限切れ");
            }
            await EnsureP2PBrowserReadyAsync(ct);
            await EnsureStartedAsync(ct);
            var origin = online ? await StartOnlineTunnelAsync(ct) : null;
            ct.ThrowIfCancellationRequested();
            var info = stableRegistrationToken is null
                ? _server!.CreateInvitation(LinkLifetime())
                : _server!.CreateInvitation(LinkLifetime(), stableRegistrationToken);
            var url = online ? new Uri(origin!, "/i/" + info.Token).ToString() : _server.PeerLink(info, AddressCombo.SelectedItem?.ToString());
            var link = new LinkRow(info, url, online, OnlineTunnelProvider.Cloudflare, stableRegistrationToken is not null);
            try { OpenPeerWindow(info, link); Links.Insert(0, link); }
            catch { _server.RevokeLink(info.Token); if (!HasActiveOnlinePeerLink()) await _tunnel.StopAsync(); throw; }
            StatusText.Text = stableRegistrationToken is null
                ? "受け取り用URLを相手に渡してください。受信のたびにこのPCで確認できます。"
                : "固定ホスト名の受け取り招待を開始しました。登録済みの相手は招待を再作成した後も同じコードで接続できます。受信のたびにこのPCで確認してください。";
            return;
        }

        var pin = string.IsNullOrEmpty(SharePasswordBox.Password) ? null : SharePasswordBox.Password;
        if (online)
        {
            await EnsureP2PBrowserReadyAsync(ct);
            await EnsureStartedAsync(ct);
            var server = _server ?? throw new InvalidOperationException("共有サーバーを開始できませんでした。");
            var origin = await StartOnlineTunnelAsync(ct);
            ct.ThrowIfCancellationRequested();
            LinkInfo? info = null;
            try
            {
                info = server.CreateShare(files!, LinkLifetime(), pin);
                var url = new Uri(origin, "/s/" + info.Token).ToString();
                var link = new LinkRow(info, url, true, OnlineTunnelProvider.Cloudflare);
                OpenPeerWindow(info, link);
                Links.Insert(0, link);
                StatusText.Text = "P2Pオンライン共有を開始しました。Cloudflare Tunnelは接続案内だけに使い、ファイル本体は端末間で直接転送します。";
                return;
            }
            catch
            {
                if (info is not null) server.RevokeLink(info.Token);
                if (!HasActiveOnlinePeerLink()) await _tunnel.StopAsync();
                throw;
            }
        }

        LocalSendDownloadServer? shareServer = null;
        try
        {
            shareServer = await LocalSendDownloadServer.StartAsync(DeviceNameBox.Text.Trim(), files!, pin, LinkLifetime(), ct);
            shareServer.Progress += progress => Ui(() => Report(progress));
            var url = shareServer.LocalLink(AddressCombo.SelectedItem?.ToString());

            var info = new LinkInfo(shareServer.Id, shareServer.ExpiresAt, shareServer.Alias, shareServer.FileCount, shareServer.TotalBytes, shareServer.PinRequired, false);
            var link = new LinkRow(info, url, false, OnlineTunnelProvider.Cloudflare);
            _browserShares.Add(info.Token, shareServer);
            StopButton.IsEnabled = true;
            Links.Insert(0, link);
            StatusText.Text = "LocalSend互換の一時サーバーをこのPCで起動しました。URLから直接受け取れます。";
        }
        catch
        {
            if (shareServer is not null) await shareServer.DisposeAsync();
            throw;
        }
    }

    private async Task<Uri> StartOnlineTunnelAsync(CancellationToken ct)
    {
        var preferences = AppPreferencesStore.Load();
        if (!preferences.UseNamedTunnel) return await _tunnel.StartAsync(_server!.SignalPort, ct);
        if (_server!.SignalPort != preferences.NamedTunnelPort)
            throw new InvalidOperationException("Named Tunnel設定を適用するには、共有画面で「停止」を押してから受信を再開してください。");
        return await _tunnel.StartNamedAsync(_server.SignalPort, preferences.NamedTunnelHostname, preferences.NamedTunnelToken, ct);
    }
    private void OpenPeerWindow(LinkInfo info, LinkRow link)
    {
        var window = new PeerSessionWindow(_server!.HostPage(info), info.IsInvitation ? "EZ Converter · 受信待機" : "EZ Converter · URL共有") { Owner = Window.GetWindow(this) };
        if (!info.IsInvitation) window.StatusReported += status =>
        {
            if (link.Active) link.UpdatePeerStatus(status);
        };
        window.Closed += async (_, _) =>
        {
            _peerWindows.Remove(info.Token);
            if (link.Active && !_stopping)
            {
                await StopLinkAsync(link, "接続画面を閉じました");
            }
        };
        _peerWindows[info.Token] = window;
        window.Show();
    }
    private void ClosePeerWindow(string token) { if (_peerWindows.Remove(token, out var window)) window.Close(); }
    private async void Revoke_Click(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is LinkRow link) await StopLinkAsync(link, "停止済み"); }
    private async Task StopLinkAsync(LinkRow link, string state)
    {
        if (!link.Active) return;
        var usesPeerTunnel = link.Online && !_browserShares.ContainsKey(link.Info.Token);
        _server?.RevokeLink(link.Info.Token);
        if (_browserShares.Remove(link.Info.Token, out var share)) await share.DisposeAsync();
        link.Stop(state);
        ClosePeerWindow(link.Info.Token);
        if (usesPeerTunnel && !HasActiveOnlinePeerLink()) await _tunnel.StopAsync();
        StopButton.IsEnabled = _server is not null || _browserShares.Count > 0 || _receiveWindows.Count > 0;
    }
    private void CopyLink_Click(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is LinkRow link) CopyText(link.Url); }
    private async void CopyRegistrationCode_Click(object sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        if ((sender as Button)?.Tag is not LinkRow { Active: true, CanRegister: true } link)
            throw new InvalidOperationException("有効なインターネット受け取り招待を選んでください。");
        var code = await InvitationCodeService.EncodeAsync(link.Url, ct);
        try
        {
            System.Windows.Clipboard.SetText(code);
            StatusText.Text = link.StableRegistrationAddress
                ? "固定ホスト名用のインターネット登録コードをコピーしました。同じNamed Tunnel設定で招待を再作成すると、登録済みの相手はこのコードを引き続き使えます。"
                : "インターネット登録コードをコピーしました。相手に渡して登録してもらってください。受信側アプリを終了すると新しいコードが必要です。";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            throw new InvalidOperationException("コードをクリップボードへコピーできませんでした。招待URLのQR画面を開いてください。");
        }
    });
    private void ShowQr_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not LinkRow { Active: true } link) return;
        try
        {
            var dialog = new ShareQrWindow(link.Url) { Owner = Window.GetWindow(this) };
            dialog.ShowDialog();
        }
        catch (Exception exception)
        {
            StatusText.Text = "QRコードを作成できませんでした。";
            WriteLog(exception);
        }
    }
    private void OpenLink_Click(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is LinkRow link) { try { Process.Start(new ProcessStartInfo(link.Url) { UseShellExecute = true }); } catch (Exception ex) { StatusText.Text = ex.Message; } } }
    private void Accept_Click(object sender, RoutedEventArgs e) => Decide(sender, true);
    private void Reject_Click(object sender, RoutedEventArgs e) => Decide(sender, false);
    private void Decide(object sender, bool accept)
    {
        if ((sender as Button)?.Tag is not IncomingRow row) return;
        if (accept) row.Transfer.Accept(); else row.Transfer.Reject();
        IncomingOffers.Remove(row);
        UpdateIncomingEmptyState();
    }
    private void Report(TransferProgress progress)
    {
        var row = Transfers.FirstOrDefault(r => r.Id == progress.Id);
        if (row is null) { row = new(progress); Transfers.Insert(0, row); } else row.Update(progress);
        while (Transfers.Count > 100 && Transfers.LastOrDefault(r => !r.CanCancel) is { } old) Transfers.Remove(old);
        if (progress.State is "完了" or "拒否" or "キャンセル")
        {
            var incoming = IncomingOffers.FirstOrDefault(r => r.Transfer.Id == progress.Id);
            if (incoming is not null) IncomingOffers.Remove(incoming);
            UpdateIncomingEmptyState();
        }
        if (TransferHistoryStore.IsTerminal(progress.State)) PersistTransferHistory();
        UpdateHistoryEmptyState();
    }
    private void ReportOperation(TransferProgress progress)
    {
        _currentOperationId = progress.Id;
        CurrentOperationProgressBar.Value = Math.Clamp(progress.Percent, 0, 100);
        Report(progress);
    }
    private async void CancelTransfer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not TransferRow row) return;
        if (row.Id == _currentOperationId) { _operationCancellation?.Cancel(); return; }
        if (_server is null) return;
        try { await _server.CancelTransferAsync(row.Id); }
        catch (Exception exception) { StatusText.Text = "転送を停止できませんでした: " + exception.Message; WriteLog(exception); }
    }
    private void CancelOperation_Click(object sender, RoutedEventArgs e) => _operationCancellation?.Cancel();
    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(Window.GetWindow(this), "保存済みの転送履歴を消去します。転送中の項目は残ります。", "転送履歴", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var active = Transfers.Where(row => row.CanCancel).ToArray();
        Transfers.Clear();
        foreach (var row in active) Transfers.Add(row);
        PersistTransferHistory();
        UpdateHistoryEmptyState();
    }
    public async ValueTask DisposeAsync() { if (_disposed) return; _timer.Stop(); await StopAsync(); _disposed = true; await _tunnel.DisposeAsync(); }

    public sealed record SelectedPath(string Path, string Name, string Description);
    public sealed record IncomingRow(IncomingTransfer Transfer)
    { public string Summary => $"{Transfer.Sender} · {Transfer.Files.Count}件 · {TransferFiles.Size(Transfer.TotalBytes)}"; public string FileNames => string.Join("\n", Transfer.Files.Take(6).Select(f => f.RelativePath)) + (Transfer.Files.Count > 6 ? "\n…" : ""); }
    public abstract class NotifyRow : INotifyPropertyChanged { public event PropertyChangedEventHandler? PropertyChanged; protected void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
    public sealed class LinkRow(LinkInfo info, string url, bool online, OnlineTunnelProvider provider, bool stableRegistrationAddress = false) : NotifyRow
    {
        private readonly HashSet<string> _receivedFiles = new(StringComparer.OrdinalIgnoreCase);
        public LinkInfo Info { get; } = info;
        public string Url { get; } = url;
        public bool Online { get; } = online;
        public OnlineTunnelProvider Provider { get; } = provider;
        public bool StableRegistrationAddress { get; } = stableRegistrationAddress && online && info.IsInvitation;
        public bool Active { get; private set; } = true;
        public bool CanRegister => Active && Online && Info.IsInvitation;
        private string? _stopped;
        public string Caption => $"{(Info.IsInvitation ? "受け取り用URL" : $"{Info.FileCount}件のファイルを共有")} · {(Online ? $"インターネット / {Provider}" : "同じネットワーク")}";
        private string AccessStatus
        {
            get
            {
                var lifetime = Info.ExpiresAt == DateTimeOffset.MaxValue
                    ? "有効: 停止ボタン／アプリ・PC終了まで"
                    : $"期限: {Info.ExpiresAt.LocalDateTime:g}";
                var access = StableRegistrationAddress
                    ? "登録コードは固定ホスト名で再利用可"
                    : Info.PasswordRequired ? "6桁PIN保護" : "URLを知っている相手が受け取れます";
                return $"{lifetime} · {access}";
            }
        }
        private string? _peerActivity;
        public string Status => _stopped ?? string.Join(" · ", new[] { _peerActivity, AccessStatus }.Where(value => !string.IsNullOrWhiteSpace(value)));
        public void UpdatePeerStatus(PeerSessionStatus status)
        {
            var received = _receivedFiles.Count == 0 ? "" : $" · 受信済み {_receivedFiles.Count}件";
            _peerActivity = status.State switch
            {
                "waiting" => "相手の接続を待機中" + received,
                "connected" => "相手がP2P接続中" + received,
                "disconnected" => "接続が切れました。再接続を待機中" + received,
                "progress" => $"受信中: {status.FileName ?? "ファイル"} ({status.Percent ?? 0}%)" + received,
                "file-saved" => RecordReceivedFile(status.FileName),
                "failed" => "P2P接続または転送に失敗しました" + received,
                _ => _peerActivity ?? ""
            };
            Changed();
        }
        private string RecordReceivedFile(string? fileName)
        {
            if (!string.IsNullOrWhiteSpace(fileName)) _receivedFiles.Add(fileName);
            var suffix = _receivedFiles.Count == 0 ? "" : $" · 受信済み {_receivedFiles.Count}件";
            return "受信完了" + (string.IsNullOrWhiteSpace(fileName) ? "" : $": {fileName}") + suffix;
        }
        public void Stop(string state) { Active = false; _stopped = state; Changed(); }
    }
    public sealed class TransferRow(TransferProgress data) : NotifyRow
    {
        private TransferProgress _data = data;
        private DateTimeOffset? _finishedAtUtc = TransferHistoryStore.IsTerminal(data.State) ? DateTimeOffset.UtcNow : null;
        private string _historyId = Guid.NewGuid().ToString("N");
        public TransferRow(TransferProgress data, DateTimeOffset? finishedAtUtc, string? historyId) : this(data)
        {
            _finishedAtUtc = finishedAtUtc;
            if (Guid.TryParseExact(historyId, "N", out _)) _historyId = historyId!;
        }
        public string Id => _data.Id;
        public string Direction => _data.Direction;
        public string Title => $"{_data.Direction} · {_data.Name} · {_data.State}";
        public string Detail => $"{TransferFiles.Size(_data.Completed)} / {TransferFiles.Size(_data.Total)}" + (string.IsNullOrWhiteSpace(_data.Detail) ? "" : " · " + _data.Detail);
        public double Percent => _data.Percent;
        public DateTimeOffset? FinishedAtUtc => _finishedAtUtc;
        public string FinishedAtText => _finishedAtUtc?.ToLocalTime().ToString("g") ?? "";
        public bool CanCancel => _data.State is "送信中" or "受信中" or "受信待ち" or "再接続中" or "相手の確認待ち";
        public TransferHistoryRecord ToHistoryRecord()
        {
            var name = (_data.Name ?? "").Replace('\\', '/').Split('/').LastOrDefault() ?? "転送";
            name = new string(name.Where(character => !char.IsControl(character)).Take(128).ToArray());
            if (string.IsNullOrWhiteSpace(name)) name = "転送";
            return new(_historyId, _data.Direction, name, _data.Completed, _data.Total, _data.State, _finishedAtUtc ?? DateTimeOffset.UtcNow);
        }
        public void Update(TransferProgress value)
        {
            _data = value;
            _finishedAtUtc = TransferHistoryStore.IsTerminal(value.State) ? _finishedAtUtc ?? DateTimeOffset.UtcNow : null;
            Changed();
        }
        public void End(string state, string? detail) { _data = _data with { State = state, Detail = detail }; _finishedAtUtc = DateTimeOffset.UtcNow; Changed(); }
    }
}
