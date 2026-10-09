using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MediaConverter;
using MediaConverter.Services;
using MediaConverter.Views;
using EZConverter.Sharing;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

internal static class Program
{
    private static int _exitCode = 1;

    [STAThread]
    private static int Main(string[] args)
    {
        var testPublicOnlineShare = args is ["--public-p2p"];
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var fixtureDirectory = Path.Combine(Path.GetTempPath(), "EZConverter-Sharing-UI-" + Guid.NewGuid().ToString("N"));
        var stateDirectory = Path.Combine(fixtureDirectory, "app-state");
        var sharing = new SharingView(stateDirectory);
        var window = new Window
        {
            Title = "EZ Converter · 共有UI統合テスト",
            Width = 1180,
            Height = 780,
            Left = -1600,
            Top = 80,
            ShowInTaskbar = false,
            Content = sharing
        };

        window.Loaded += async (_, _) =>
        {
            try
            {
                VerifyOnlineTunnelRetentionPolicy();
                VerifyPeerDiscoveryStatus();
                VerifyMediaUrlHandling(fixtureDirectory);
                await VerifyVideoPageProbeAsync(window);
                Directory.CreateDirectory(fixtureDirectory);
                var fixture = RandomNumberGenerator.GetBytes(131_173);
                var fixturePath = Path.Combine(fixtureDirectory, "ui-share-smoke.bin");
                await File.WriteAllBytesAsync(fixturePath, fixture);

                const string receiverPin = "482917";
                foreach (var controlName in new[]
                {
                    "RegisteredContactNameBox", "RegistrationCodeBox", "AddRegisteredContactButton",
                    "RemoveRegisteredContactButton", "RegisteredContactsBox", "SendRegisteredContactButton"
                })
                {
                    if (sharing.FindName(controlName) is null)
                        throw new InvalidOperationException($"The registered internet contact control is missing: {controlName}");
                }
                Console.WriteLine("PASS sharing UI exposes internet contact registration, saved contacts, and send controls");
                var contactNameBox = sharing.FindName("RegisteredContactNameBox") as TextBox
                    ?? throw new InvalidOperationException("The registered contact name field is missing.");
                var registrationCodeBox = sharing.FindName("RegistrationCodeBox") as TextBox
                    ?? throw new InvalidOperationException("The registration code field is missing.");
                contactNameBox.Text = "UI registration fixture";
                registrationCodeBox.Text = InvitationCodeService.CreateStructuralCode(
                    "https://example.com/i/" + new string('c', 64));
                RaiseClick(sharing, "AddRegisteredContactButton");
                await WaitUntilAsync(() => sharing.RegisteredContacts.Count == 1, TimeSpan.FromSeconds(20));
                if (sharing.RegisteredContacts.Single() is not { DisplayName: "UI registration fixture", RegistrationCode: var savedCode } ||
                    savedCode != registrationCodeBox.Text)
                    throw new InvalidDataException("The sharing screen did not save the registered contact name.");
                Console.WriteLine("PASS sharing UI validates and saves an internet registration code");
                if (sharing.FindName("LocalSendReceivePinBox") is not PasswordBox receivePinBox)
                    throw new InvalidOperationException("The LocalSend receiver PIN control is missing.");
                receivePinBox.Password = receiverPin;
                var peerReceiveDirectory = Path.Combine(fixtureDirectory, "registered-peer-received");
                Directory.CreateDirectory(peerReceiveDirectory);
                if (sharing.FindName("ReceiveDirectoryBox") is not TextBox initialReceiveDirectoryBox)
                    throw new InvalidOperationException("The configured receive directory control is missing.");
                initialReceiveDirectoryBox.Text = peerReceiveDirectory;
                RaiseClick(sharing, "StartButton");
                await WaitUntilAsync(() => !receivePinBox.IsEnabled &&
                    sharing.FindName("ConnectionUrlBox") is TextBox connection && connection.Text.StartsWith("https://", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(20));
                sharing.AddPaths([fixturePath]);
                RaiseClick(sharing, "LinksPageButton");
                if (sharing.FindName("LinkAddFilesButton") is not Button ||
                    sharing.FindName("LinkAddFolderButton") is not Button ||
                    sharing.FindName("LinkClearFilesButton") is not Button)
                    throw new InvalidOperationException("The URL sharing page must provide direct file/folder selection and clearing actions.");
                Console.WriteLine("PASS URL sharing page supports the simple choose-files-then-create-link workflow");
                RaiseClick(sharing, "CreateShareButton");
                await WaitUntilAsync(() => sharing.Links.Count == 1, TimeSpan.FromSeconds(20));
                var link = sharing.Links[0];
                if (!link.Active || link.Online || link.Info.IsInvitation || link.Info.FileCount != 1)
                    throw new InvalidOperationException("The sharing screen did not create an active LAN file share.");
                Console.WriteLine("PASS sharing screen creates an active LAN LocalSend URL");

                using (var preferences = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(stateDirectory, "preferences.json"))))
                {
                    var protectedPin = preferences.RootElement.GetProperty("ProtectedReceivePin").GetString();
                    if (string.IsNullOrEmpty(protectedPin) || protectedPin.Contains(receiverPin, StringComparison.Ordinal) ||
                        DpapiStringProtector.Unprotect(protectedPin) != receiverPin)
                        throw new InvalidDataException("The LocalSend receiver PIN was not saved as a DPAPI-protected setting.");
                }
                Console.WriteLine("PASS receiver PIN is stored using current-user DPAPI, not as plaintext");

                var connectionBox = sharing.FindName("ConnectionUrlBox") as TextBox
                    ?? throw new InvalidOperationException("The LocalSend connection URL control is missing.");
                var connectionUri = new Uri(connectionBox.Text);
                var fingerprint = connectionUri.Fragment.TrimStart('#');
                var uploadEndpoint = new UriBuilder(connectionUri) { Path = "/api/localsend/v2/prepare-upload", Query = "", Fragment = "" }.Uri;
                using var localSendHandler = new HttpClientHandler
                {
                    UseProxy = false,
                    ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
                        Convert.ToHexString(SHA256.HashData(certificate.RawData)).Equals(fingerprint, StringComparison.OrdinalIgnoreCase)
                };
                using var localSendHttp = new HttpClient(localSendHandler) { Timeout = TimeSpan.FromSeconds(10) };
                var fileId = Guid.NewGuid().ToString("N");
                var emptyFile = new LocalSendFileMetadata(fileId, "ui-pin-fixture.bin", 0, "application/octet-stream",
                    Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())));
                var uploadRequest = new LocalSendPrepareUploadRequest(
                    new LocalSendDeviceInfo("UI PIN fixture", Port: 53317, Protocol: "https", Fingerprint: fingerprint),
                    new Dictionary<string, LocalSendFileMetadata> { [fileId] = emptyFile });
                using (var missingPin = await localSendHttp.PostAsJsonAsync(uploadEndpoint, uploadRequest))
                    if (missingPin.StatusCode != System.Net.HttpStatusCode.Unauthorized)
                        throw new InvalidOperationException("The WPF receiver did not require its configured LocalSend PIN.");
                using var pinRequestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var acceptedPinTask = localSendHttp.PostAsJsonAsync(uploadEndpoint + "?pin=" + receiverPin, uploadRequest, pinRequestCancellation.Token);
                await WaitUntilAsync(() => sharing.IncomingOffers.Count == 1, TimeSpan.FromSeconds(10));
                sharing.IncomingOffers.Single().Transfer.Accept();
                using (var acceptedPin = await acceptedPinTask)
                {
                    acceptedPin.EnsureSuccessStatusCode();
                    using var receipt = await acceptedPin.Content.ReadAsStreamAsync();
                    using var pinPreparation = await JsonDocument.ParseAsync(receipt);
                    var pinSessionId = pinPreparation.RootElement.GetProperty("sessionId").GetString()
                        ?? throw new InvalidDataException("The PIN-authenticated LocalSend session ID was empty.");
                    using var cancelled = await localSendHttp.PostAsync(new UriBuilder(uploadEndpoint)
                    {
                        Path = "/api/localsend/v2/cancel",
                        Query = "sessionId=" + Uri.EscapeDataString(pinSessionId)
                    }.Uri, null);
                    if (cancelled.StatusCode != System.Net.HttpStatusCode.NoContent)
                        throw new InvalidOperationException("The PIN-authenticated LocalSend session could not be cancelled.");
                }
                Console.WriteLine("PASS WPF receiver requires the configured PIN and accepts it through the LocalSend API");

                using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
                var baseUri = new Uri(link.Url);
                var page = await http.GetStringAsync(baseUri);
                if (!page.Contains("/localsend.js", StringComparison.Ordinal))
                    throw new InvalidDataException("The sharing screen URL did not serve the LocalSend-compatible browser page.");

                var prepareUri = new Uri(baseUri, "/api/localsend/v2/prepare-download");
                using var prepared = await http.PostAsync(prepareUri, content: null);
                prepared.EnsureSuccessStatusCode();
                using var metadata = JsonDocument.Parse(await prepared.Content.ReadAsStringAsync());
                var sessionId = metadata.RootElement.GetProperty("sessionId").GetString()
                    ?? throw new InvalidDataException("The LocalSend download session ID was empty.");
                var fileEntry = metadata.RootElement.GetProperty("files").EnumerateObject().Single();
                if (fileEntry.Value.GetProperty("fileName").GetString() != "ui-share-smoke.bin")
                    throw new InvalidDataException("The LocalSend browser page exposed unexpected file metadata.");

                var query = "sessionId=" + Uri.EscapeDataString(sessionId) + "&fileId=" + Uri.EscapeDataString(fileEntry.Name);
                var downloadUri = new UriBuilder(baseUri) { Path = "/api/localsend/v2/download", Query = query }.Uri;
                var received = await http.GetByteArrayAsync(downloadUri);
                if (!received.AsSpan().SequenceEqual(fixture) ||
                    !Convert.ToHexString(SHA256.HashData(received)).Equals(fileEntry.Value.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The WPF-created LocalSend share returned different file bytes.");
                Console.WriteLine($"PASS WPF-created URL serves {received.Length:N0} bytes with matching SHA-256");

                RaiseClick(sharing, "StopButton");
                await WaitUntilAsync(() => !link.Active && !sharing.IsRunning, TimeSpan.FromSeconds(15));
                var listenerClosed = false;
                try
                {
                    using var afterStop = await http.GetAsync(baseUri);
                    listenerClosed = !afterStop.IsSuccessStatusCode;
                }
                catch (HttpRequestException) { listenerClosed = true; }
                if (!listenerClosed) throw new InvalidOperationException("The sender-PC share listener stayed reachable after pressing Stop.");
                Console.WriteLine("PASS Stop button invalidates the WPF share and closes the sender-PC listener");

                var nativePeerReceiveDirectory = Path.Combine(fixtureDirectory, "native-peer-received");
                var nativePeerReceiver = new TransferServer(new()
                {
                    DeviceName = "UI EZ peer receiver",
                    ReceiveDirectory = nativePeerReceiveDirectory,
                    StateDirectory = Path.Combine(fixtureDirectory, "native-peer-state"),
                    BindAddress = System.Net.IPAddress.Loopback
                });
                var secondNativePeerReceiveDirectory = Path.Combine(fixtureDirectory, "second-native-peer-received");
                var secondNativePeerReceiver = new TransferServer(new()
                {
                    DeviceName = "UI EZ peer receiver 2",
                    ReceiveDirectory = secondNativePeerReceiveDirectory,
                    StateDirectory = Path.Combine(fixtureDirectory, "second-native-peer-state"),
                    BindAddress = System.Net.IPAddress.Loopback
                });
                nativePeerReceiver.Incoming += offer => offer.Accept();
                secondNativePeerReceiver.Incoming += offer => offer.Accept();
                var nativePeerCompleted = new TaskCompletionSource<TransferProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
                var secondNativePeerCompleted = new TaskCompletionSource<TransferProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
                nativePeerReceiver.Progress += progress =>
                {
                    if (progress.Direction == "受信" && progress.State == "完了") nativePeerCompleted.TrySetResult(progress);
                };
                secondNativePeerReceiver.Progress += progress =>
                {
                    if (progress.Direction == "受信" && progress.State == "完了") secondNativePeerCompleted.TrySetResult(progress);
                };
                try
                {
                    await nativePeerReceiver.StartAsync();
                    await secondNativePeerReceiver.StartAsync();
                    RaiseClick(sharing, "StartButton");
                    await WaitUntilAsync(() => sharing.FindName("StartButton") is Button startButton && !startButton.IsEnabled &&
                        sharing.FindName("ConnectionUrlBox") is TextBox activeConnection && activeConnection.Text.StartsWith("https://", StringComparison.Ordinal),
                        TimeSpan.FromSeconds(20));

                    var secondNativeFixture = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 19);
                    var secondNativePath = Path.Combine(fixtureDirectory, "ui-native-peer-extra.bin");
                    await File.WriteAllBytesAsync(secondNativePath, secondNativeFixture);
                    sharing.SelectedFiles.Clear();
                    sharing.AddPaths([fixturePath, secondNativePath]);
                    var nativePeer = new PeerDevice(nativePeerReceiver.DeviceName, "127.0.0.1", nativePeerReceiver.HttpsPort,
                        nativePeerReceiver.Fingerprint, DateTimeOffset.UtcNow);
                    var secondNativePeer = new PeerDevice(secondNativePeerReceiver.DeviceName, "127.0.0.1", secondNativePeerReceiver.HttpsPort,
                        secondNativePeerReceiver.Fingerprint, DateTimeOffset.UtcNow);
                    sharing.Peers.Add(nativePeer);
                    sharing.Peers.Add(secondNativePeer);
                    if (sharing.FindName("PeersList") is not ListBox peersList ||
                        sharing.FindName("StatusText") is not TextBlock nativeSendStatus)
                        throw new InvalidOperationException("The LocalSend-compatible peer list controls are missing.");
                    peersList.SelectedItems.Clear();
                    peersList.SelectedItems.Add(nativePeer);
                    peersList.SelectedItems.Add(secondNativePeer);
                    RaiseClick(sharing, "SendPeerButton");

                    var nativeResults = await Task.WhenAll(nativePeerCompleted.Task, secondNativePeerCompleted.Task).WaitAsync(TimeSpan.FromSeconds(90));
                    await WaitUntilAsync(() => nativeSendStatus.Text.Contains("2台すべてに送信が完了", StringComparison.Ordinal), TimeSpan.FromSeconds(15));
                    foreach (var receiveDirectory in new[] { nativeResults[0].Detail!, nativeResults[1].Detail! })
                    {
                        var receivedMain = await File.ReadAllBytesAsync(Path.Combine(receiveDirectory, Path.GetFileName(fixturePath)));
                        var receivedSecond = await File.ReadAllBytesAsync(Path.Combine(receiveDirectory, Path.GetFileName(secondNativePath)));
                        if (!receivedMain.AsSpan().SequenceEqual(fixture) || !receivedSecond.AsSpan().SequenceEqual(secondNativeFixture) ||
                            Convert.ToHexString(SHA256.HashData(receivedMain)) != Convert.ToHexString(SHA256.HashData(fixture)) ||
                            Convert.ToHexString(SHA256.HashData(receivedSecond)) != Convert.ToHexString(SHA256.HashData(secondNativeFixture)))
                            throw new InvalidDataException($"The WPF multi-peer transfer changed file bytes in {receiveDirectory}.");
                    }
                    Console.WriteLine("PASS WPF peer list broadcasts a multi-file batch to two EZ Converter receivers with matching SHA-256 hashes");
                }
                finally
                {
                    await nativePeerReceiver.DisposeAsync();
                    await secondNativePeerReceiver.DisposeAsync();
                }

                var p2pReceiver = new TransferServer(new()
                {
                    DeviceName = "UI P2P receiver",
                    ReceiveDirectory = Path.Combine(fixtureDirectory, "p2p-received"),
                    StateDirectory = Path.Combine(fixtureDirectory, "p2p-state"),
                    BindAddress = System.Net.IPAddress.Loopback
                });
                p2pReceiver.Incoming += offer => offer.Accept();
                var p2pCompleted = new TaskCompletionSource<TransferProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
                p2pReceiver.Progress += progress =>
                {
                    if (progress.Direction == "受信" && progress.State == "完了") p2pCompleted.TrySetResult(progress);
                };
                PeerSessionWindow? p2pReceiverWindow = null;
                try
                {
                    await p2pReceiver.StartAsync();
                    var invite = p2pReceiver.CreateInvitation(TimeSpan.FromMinutes(2));
                    p2pReceiverWindow = new PeerSessionWindow(p2pReceiver.HostPage(invite), "P2P受信側UIテスト") { Owner = window };
                    p2pReceiverWindow.Show();
                    await p2pReceiverWindow.Ready.WaitAsync(TimeSpan.FromSeconds(20));
                    sharing.AddPaths([fixturePath]);
                    if (sharing.FindName("TargetUrlBox") is not TextBox targetUrlBox ||
                        sharing.FindName("StatusText") is not TextBlock sendStatus)
                        throw new InvalidOperationException("The P2P send URL controls are missing.");
                    targetUrlBox.Text = p2pReceiver.PeerLink(invite, "127.0.0.1");
                    RaiseClick(sharing, "SendUrlButton");
                    var p2pResult = await p2pCompleted.Task.WaitAsync(TimeSpan.FromSeconds(60));
                    var p2pReceivedFile = Path.Combine(p2pResult.Detail!, "ui-share-smoke.bin");
                    var p2pBytes = await File.ReadAllBytesAsync(p2pReceivedFile);
                    if (!p2pBytes.AsSpan().SequenceEqual(fixture) ||
                        !Convert.ToHexString(SHA256.HashData(p2pBytes)).Equals(Convert.ToHexString(SHA256.HashData(fixture)), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The WPF P2P invitation path changed the source file bytes.");
                    await WaitUntilAsync(() => sendStatus.Text.Contains("P2P直接送信が完了", StringComparison.Ordinal), TimeSpan.FromSeconds(15));
                    Console.WriteLine("PASS WPF Send URL uses WebRTC invitation transfer and receiver SHA-256 verification");
                }
                finally
                {
                    p2pReceiverWindow?.Close();
                    await p2pReceiver.DisposeAsync();
                }

                if (testPublicOnlineShare)
                {
                    var onlineFixture = RandomNumberGenerator.GetBytes(4 * 1024 * 1024 + 321);
                    var onlineFixturePath = Path.Combine(fixtureDirectory, "p2p-native-resume.bin");
                    await File.WriteAllBytesAsync(onlineFixturePath, onlineFixture);
                    sharing.SelectedFiles.Clear();
                    sharing.AddPaths([onlineFixturePath]);
                    if (sharing.FindName("OnlineCheckBox") is not CheckBox onlineCheckBox)
                        throw new InvalidOperationException("The online sharing option is missing.");
                    onlineCheckBox.IsChecked = true;
                    RaiseClick(sharing, "CreateShareButton");
                    await WaitUntilAsync(() => sharing.Links.Count == 2 && sharing.Links.Any(link => link.Active && link.Online && !link.Info.IsInvitation),
                        TimeSpan.FromSeconds(120));
                    var onlineLink = sharing.Links.Single(link => link.Active && link.Online && !link.Info.IsInvitation);
                    var onlineUri = new Uri(onlineLink.Url);
                    if (onlineUri.Scheme != Uri.UriSchemeHttps || !onlineUri.Host.EndsWith(".trycloudflare.com", StringComparison.OrdinalIgnoreCase) ||
                        onlineUri.AbsolutePath != "/s/" + onlineLink.Info.Token)
                        throw new InvalidDataException("The WPF sharing UI did not publish the expected temporary HTTPS URL.");

                    using var onlineHttp = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
                    var onlinePage = await onlineHttp.GetStringAsync(onlineUri);
                    if (!onlinePage.Contains("/share.js", StringComparison.Ordinal) || !onlinePage.Contains("ファイル本体は端末間で直接通信", StringComparison.Ordinal))
                        throw new InvalidDataException("The WPF-created public URL did not serve the browser P2P share page.");
                    Console.WriteLine("PASS WPF sharing UI publishes a temporary HTTPS P2P URL through its sender-PC tunnel");

                    var remoteBrowser = new WebView2
                    {
                        CreationProperties = new CoreWebView2CreationProperties
                        {
                            UserDataFolder = Path.Combine(fixtureDirectory, "public-share-browser")
                        }
                    };
                    var remoteWindow = new Window
                    {
                        Title = "EZ Converter · 公開URL受信テスト",
                        Width = 1000,
                        Height = 720,
                        Left = -1400,
                        Top = 100,
                        ShowInTaskbar = false,
                        Content = remoteBrowser
                    };
                    var publicBrowserExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    remoteWindow.Show();
                    try
                    {
                        await remoteBrowser.EnsureCoreWebView2Async();
                        remoteBrowser.CoreWebView2.Environment.BrowserProcessExited += (_, _) => publicBrowserExited.TrySetResult();
                        remoteBrowser.Source = onlineUri;
                        await WaitForBrowserScriptAsync(remoteBrowser.CoreWebView2,
                            "document.querySelectorAll('#files li button').length===1 && channel?.readyState==='open'", TimeSpan.FromSeconds(90));
                        await WaitUntilAsync(() => onlineLink.Status.Contains("相手がP2P接続中", StringComparison.Ordinal), TimeSpan.FromSeconds(15));
                        Console.WriteLine("PASS WPF share list reports the app-free browser's live P2P connection state");
                        var browserOnly = await remoteBrowser.ExecuteScriptAsync("typeof window.EZSetNativeFiles !== 'function'");
                        if (browserOnly != "true") throw new InvalidOperationException("The public browser test unexpectedly depends on the EZ Converter app bridge.");
                        var expectedLength = onlineFixture.Length;
                        await remoteBrowser.ExecuteScriptAsync($$"""
                            window.__onlineReceiveBytes = new Uint8Array({{expectedLength}});
                            window.__onlineReceiveOffset = 0;
                            window.__onlineReceiveSha256 = null;
                            Object.defineProperty(window, 'showSaveFilePicker', {
                              configurable: true,
                              value: async () => ({ createWritable: async () => ({
                                write: async value => {
                                  const chunk = value instanceof ArrayBuffer ? new Uint8Array(value) : new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
                                  window.__onlineReceiveBytes.set(chunk, window.__onlineReceiveOffset);
                                  window.__onlineReceiveOffset += chunk.byteLength;
                                },
                                close: async () => {
                                  const digest = await crypto.subtle.digest('SHA-256', window.__onlineReceiveBytes);
                                  window.__onlineReceiveSha256 = [...new Uint8Array(digest)].map(value => value.toString(16).padStart(2, '0')).join('').toUpperCase();
                                },
                                abort: async () => {}
                              }) })
                            });
                            document.querySelector('#files li button').click();
                            """);
                        await WaitForBrowserScriptAsync(remoteBrowser.CoreWebView2,
                            "window.__onlineReceiveSha256 && document.querySelector('#message')?.textContent.includes('保存して整合性')", TimeSpan.FromSeconds(90));
                        using var encodedResult = JsonDocument.Parse(await remoteBrowser.ExecuteScriptAsync("JSON.stringify({length:window.__onlineReceiveOffset,sha256:window.__onlineReceiveSha256})"));
                        using var transferResult = JsonDocument.Parse(encodedResult.RootElement.GetString()
                            ?? throw new InvalidDataException("The browser did not return its transfer result."));
                        if (transferResult.RootElement.GetProperty("length").GetInt32() != onlineFixture.Length ||
                            !transferResult.RootElement.GetProperty("sha256").GetString()!.Equals(Convert.ToHexString(SHA256.HashData(onlineFixture)), StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("The WPF-created public P2P URL transferred different file bytes.");
                        await WaitUntilAsync(() => onlineLink.Status.Contains("受信済み 1件", StringComparison.Ordinal), TimeSpan.FromSeconds(15));
                        Console.WriteLine("PASS WPF share list reports browser receipt after file save and SHA-256 verification");
                        Console.WriteLine("PASS browser without the EZ Converter app received the WPF-shared synthetic file over P2P with matching SHA-256");
                    }
                    finally
                    {
                        remoteBrowser.CoreWebView2?.Stop();
                        remoteWindow.Content = null;
                        remoteWindow.Close();
                        ((IDisposable)remoteBrowser).Dispose();
                        try { await publicBrowserExited.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                        catch (TimeoutException) { }
                    }

                    if (sharing.FindName("ReceiveUrlBox") is not TextBox receiveUrlBox ||
                        sharing.FindName("StatusText") is not TextBlock receiveStatus ||
                        sharing.FindName("ReceiveDirectoryBox") is not TextBox receiveDirectoryBox)
                        throw new InvalidOperationException("The in-app P2P receiving controls are missing.");
                    receiveUrlBox.Text = onlineLink.Url;
                    var appReceiveDirectory = Path.Combine(fixtureDirectory, "app-p2p-received");
                    Directory.CreateDirectory(appReceiveDirectory);
                    var preexistingPath = Path.Combine(appReceiveDirectory, "p2p-native-resume.bin");
                    var preexistingBytes = RandomNumberGenerator.GetBytes(37);
                    await File.WriteAllBytesAsync(preexistingPath, preexistingBytes);
                    receiveDirectoryBox.Text = appReceiveDirectory;
                    PeerSessionWindow? inAppReceiverWindow = null;
                    RaiseClick(sharing, "ReceiveUrlButton");
                    await WaitUntilAsync(() =>
                    {
                        inAppReceiverWindow = app.Windows.OfType<PeerSessionWindow>()
                            .FirstOrDefault(candidate => candidate.Title == "EZ Converter · P2P共有受信");
                        return inAppReceiverWindow?.Browser is not null;
                    }, TimeSpan.FromSeconds(30));
                    var inAppBrowser = inAppReceiverWindow!.Browser!;
                    await WaitForBrowserScriptAsync(inAppBrowser,
                        "document.querySelectorAll('#files li button').length===1 && channel?.readyState==='open'", TimeSpan.FromSeconds(90));
                    if (await inAppBrowser.ExecuteScriptAsync("window.EZNativeReceiveEnabled === true") != "true")
                        throw new InvalidOperationException("The WPF receiver did not enable its native, configured-folder save bridge.");
                    await inAppBrowser.ExecuteScriptAsync("""
                        window.__nativeIntegrityCheck = null;
                        (async () => {
                          const started = await nativeSaveRequest('begin', {
                            fileId: 'a'.repeat(32), fileName: 'tampered.bin', size: 3, sha256: '0'.repeat(64)
                          });
                          await nativeSaveRequest('write', { token: started.token, data: btoa(String.fromCharCode(1, 2, 3)) });
                          try {
                            await nativeSaveRequest('complete', { token: started.token });
                            window.__nativeIntegrityCheck = 'unexpectedly accepted';
                          } catch (error) {
                            window.__nativeIntegrityCheck = String(error?.message || error).includes('SHA-256') ? 'rejected' : String(error?.message || error);
                          }
                        })().catch(error => window.__nativeIntegrityCheck = String(error?.message || error));
                        """);
                    await WaitForBrowserScriptAsync(inAppBrowser,
                        "window.__nativeIntegrityCheck !== null", TimeSpan.FromSeconds(15));
                    if (await inAppBrowser.ExecuteScriptAsync("window.__nativeIntegrityCheck === 'rejected'") != "true" ||
                        File.Exists(Path.Combine(appReceiveDirectory, "tampered.bin")))
                        throw new InvalidDataException("The native receive bridge accepted a file whose SHA-256 did not match.");
                    Console.WriteLine("PASS native P2P receiver rejects a mismatched SHA-256 without creating the destination file");
                    await inAppBrowser.ExecuteScriptAsync("""
                        window.__nativeResumeTriggered = false;
                        const originalNativeReceiveSend = sendData;
                        sendData = data => {
                          originalNativeReceiveSend(data);
                          if (!window.__nativeResumeTriggered && data?.type === 'ack' && data.offset >= 1024 * 1024) {
                            window.__nativeResumeTriggered = true;
                            window.__nativeResumeOffset = data.offset;
                            setTimeout(() => ws?.close(), 0);
                          }
                        };
                        """);
                    await inAppBrowser.ExecuteScriptAsync("document.querySelector('#files li button').click()");
                    await WaitForBrowserScriptAsync(inAppBrowser,
                        "window.__nativeResumeTriggered && document.querySelector('#message')?.textContent.includes('保存して整合性')", TimeSpan.FromSeconds(120));
                    var nativeReceivedFile = Path.Combine(appReceiveDirectory, "p2p-native-resume (1).bin");
                    await WaitUntilAsync(() => File.Exists(nativeReceivedFile), TimeSpan.FromSeconds(15));
                    var nativeReceivedBytes = await File.ReadAllBytesAsync(nativeReceivedFile);
                    var preexistingAfter = await File.ReadAllBytesAsync(preexistingPath);
                    if (!nativeReceivedBytes.AsSpan().SequenceEqual(onlineFixture) ||
                        !Convert.ToHexString(SHA256.HashData(nativeReceivedBytes)).Equals(Convert.ToHexString(SHA256.HashData(onlineFixture)), StringComparison.OrdinalIgnoreCase) ||
                        !preexistingAfter.AsSpan().SequenceEqual(preexistingBytes) ||
                        Directory.EnumerateDirectories(appReceiveDirectory, ".EZConverter-P2P-*", SearchOption.TopDirectoryOnly).Any())
                        throw new InvalidDataException("The in-app P2P save changed the source bytes or overwrote the existing file.");
                    await WaitUntilAsync(() => receiveStatus.Text.Contains("P2P受信完了", StringComparison.Ordinal), TimeSpan.FromSeconds(15));
                    var resumeOffset = await inAppBrowser.ExecuteScriptAsync("window.__nativeResumeOffset");
                    Console.WriteLine($"PASS in-app P2P receive resumed after {resumeOffset} bytes, verified SHA-256 natively, and preserved the existing same-name file");

                    RaiseClick(sharing, "StopButton");
                    await WaitUntilAsync(() => !onlineLink.Active && !sharing.IsRunning, TimeSpan.FromSeconds(20));
                    Console.WriteLine("PASS WPF Stop button revokes its public URL and stops the sender-PC server and tunnel");

                    if (sharing.FindName("ReceiveDirectoryBox") is not TextBox invitationReceiveDirectoryBox)
                        throw new InvalidOperationException("The invitation receive directory control is missing.");
                    invitationReceiveDirectoryBox.Text = peerReceiveDirectory;
                    RaiseClick(sharing, "CreateInviteButton");
                    await WaitUntilAsync(() => sharing.Links.Any(candidate => candidate.Active && candidate.Online && candidate.Info.IsInvitation),
                        TimeSpan.FromSeconds(120));
                    var registeredInvitation = sharing.Links.Single(candidate => candidate.Active && candidate.Online && candidate.Info.IsInvitation);
                    var registrationCode = await InvitationCodeService.EncodeAsync(registeredInvitation.Url);
                    if (!InvitationCodeService.TryValidate(registrationCode) ||
                        !InvitationCodeService.TryRestore(registrationCode, out var restoredInvitation) ||
                        restoredInvitation != registeredInvitation.Url)
                        throw new InvalidDataException("The online invitation did not round-trip through its portable registration code.");
                    Console.WriteLine("PASS public receive invitation converts to a portable EZC1 registration code");

                    var registeredSender = new SharingView(Path.Combine(fixtureDirectory, "registered-sender-state"));
                    try
                    {
                        var registeredNameBox = registeredSender.FindName("RegisteredContactNameBox") as TextBox
                            ?? throw new InvalidOperationException("The sender registered-contact name field is missing.");
                        var registeredCodeBox = registeredSender.FindName("RegistrationCodeBox") as TextBox
                            ?? throw new InvalidOperationException("The sender registration-code field is missing.");
                        registeredNameBox.Text = "Remote EZ Converter";
                        registeredCodeBox.Text = registrationCode;
                        RaiseClick(registeredSender, "AddRegisteredContactButton");
                        await WaitUntilAsync(() => registeredSender.RegisteredContacts.Count == 1, TimeSpan.FromSeconds(30));
                        if (registeredSender.RegisteredContacts.Single().RegistrationCode != registrationCode)
                            throw new InvalidDataException("The public invite code was not saved as the sender's registered contact.");

                        var registeredPayload = RandomNumberGenerator.GetBytes(4 * 1024 * 1024 + 733);
                        var registeredPayloadPath = Path.Combine(fixtureDirectory, "registered-contact-transfer.bin");
                        await File.WriteAllBytesAsync(registeredPayloadPath, registeredPayload);
                        registeredSender.AddPaths([registeredPayloadPath]);
                        var previousOfferCount = sharing.IncomingOffers.Count;
                        RaiseClick(registeredSender, "SendRegisteredContactButton");
                        await WaitUntilAsync(() => sharing.IncomingOffers.Count > previousOfferCount, TimeSpan.FromSeconds(60));
                        var registeredOffer = sharing.IncomingOffers.Last();
                        if (registeredOffer.Transfer.Files.Single().RelativePath != Path.GetFileName(registeredPayloadPath))
                            throw new InvalidDataException("The registered-contact invitation offered an unexpected file.");
                        registeredOffer.Transfer.Accept();

                        var registeredFileName = Path.GetFileName(registeredPayloadPath);
                        await WaitUntilAsync(() => Directory.EnumerateFiles(peerReceiveDirectory, registeredFileName, SearchOption.AllDirectories).Any(),
                            TimeSpan.FromSeconds(120));
                        var registeredReceivedPath = Directory.EnumerateFiles(peerReceiveDirectory, registeredFileName, SearchOption.AllDirectories).Single();
                        var registeredReceivedBytes = await File.ReadAllBytesAsync(registeredReceivedPath);
                        if (!registeredReceivedBytes.AsSpan().SequenceEqual(registeredPayload) ||
                            !Convert.ToHexString(SHA256.HashData(registeredReceivedBytes)).Equals(
                                Convert.ToHexString(SHA256.HashData(registeredPayload)), StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("The registered-contact P2P transfer changed the source bytes.");
                        await WaitUntilAsync(() => registeredSender.FindName("StatusText") is TextBlock senderStatus &&
                            senderStatus.Text.Contains("P2P直接送信が完了", StringComparison.Ordinal), TimeSpan.FromSeconds(120));
                        Console.WriteLine("PASS registered-contact send used a public EZC1 invitation and delivered 4 MiB with matching SHA-256");
                    }
                    finally { await registeredSender.DisposeAsync(); }

                    RaiseClick(sharing, "StopButton");
                    await WaitUntilAsync(() => !registeredInvitation.Active && !sharing.IsRunning, TimeSpan.FromSeconds(20));
                    Console.WriteLine("PASS stopping the registered-contact invitation revokes its public route and tunnel");
                }

                await sharing.DisposeAsync();
                var restoredView = new SharingView(stateDirectory);
                try
                {
                    if (restoredView.FindName("LocalSendReceivePinBox") is not PasswordBox restoredPinBox || restoredPinBox.Password != receiverPin)
                        throw new InvalidDataException("The LocalSend receiver PIN did not survive an app restart.");
                }
                finally { await restoredView.DisposeAsync(); }
                Console.WriteLine("PASS receiver PIN is restored from the protected setting after reopening the sharing UI");
                _exitCode = 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
            }
            finally
            {
                try { await sharing.DisposeAsync(); }
                catch (Exception exception) { Console.Error.WriteLine("Cleanup failed: " + exception); _exitCode = 1; }
                window.Close();
                app.Shutdown(_exitCode);
                if (_exitCode == 0 && Directory.Exists(fixtureDirectory) &&
                    Path.GetFileName(fixtureDirectory).StartsWith("EZConverter-Sharing-UI-", StringComparison.Ordinal))
                {
                    try { Directory.Delete(fixtureDirectory, recursive: true); }
                    catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
                    {
                        Console.Error.WriteLine("Temporary UI test data could not be deleted after WebView2 shutdown: " + cleanupError.Message);
                        _exitCode = 1;
                    }
                }
                else if (_exitCode != 0 && Directory.Exists(fixtureDirectory))
                {
                    Console.Error.WriteLine("Sharing UI test artifacts retained for failure diagnosis: " + fixtureDirectory);
                }
            }
        };

        app.Run(window);
        return _exitCode;
    }

    private static void RaiseClick(FrameworkElement root, string name)
    {
        if (root.FindName(name) is not Button button) throw new InvalidOperationException($"WPF control '{name}' was not found.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }

    private static void VerifyOnlineTunnelRetentionPolicy()
    {
        var existingShare = new SharingView.LinkRow(
            new LinkInfo(new string('a', 64), DateTimeOffset.MaxValue, "existing share", 1, 1, false, false),
            "https://example.invalid/s/" + new string('a', 64), true, SharingView.OnlineTunnelProvider.Cloudflare);
        if (!SharingView.ContainsActiveOnlinePeerLink([existingShare]))
            throw new InvalidOperationException("An active online share must keep the shared tunnel alive when another link creation fails.");

        existingShare.Stop("stopped");
        if (SharingView.ContainsActiveOnlinePeerLink([existingShare]))
            throw new InvalidOperationException("A stopped online share must not keep the shared tunnel alive.");

        var localShare = new SharingView.LinkRow(
            new LinkInfo(new string('b', 64), DateTimeOffset.MaxValue, "LAN share", 1, 1, false, false),
            "http://192.0.2.10/s/" + new string('b', 64), false, SharingView.OnlineTunnelProvider.Cloudflare);
        if (SharingView.ContainsActiveOnlinePeerLink([localShare]))
            throw new InvalidOperationException("A LAN-only share must not keep the public tunnel alive.");

        Console.WriteLine("PASS failed online-link cleanup preserves the tunnel for other active online links");
    }

    private static void VerifyPeerDiscoveryStatus()
    {
        var searching = SharingView.FormatPeerDiscoveryStatus(0, TimeSpan.FromSeconds(14), usesAlternatePort: false);
        if (!searching.Contains("検索中", StringComparison.Ordinal) || searching.Contains("手動接続", StringComparison.Ordinal))
            throw new InvalidOperationException("Peer discovery should remain in its normal searching state before the guidance delay.");

        var delayed = SharingView.FormatPeerDiscoveryStatus(0, TimeSpan.FromSeconds(15), usesAlternatePort: false);
        if (!delayed.Contains("手動接続", StringComparison.Ordinal) || !delayed.Contains("ファイアウォール", StringComparison.Ordinal))
            throw new InvalidOperationException("Peer discovery should give actionable firewall and manual-URL guidance after the delay.");

        var found = SharingView.FormatPeerDiscoveryStatus(2, TimeSpan.FromMinutes(1), usesAlternatePort: true);
        if (!found.Contains("2台が見つかりました", StringComparison.Ordinal) || !found.Contains("マルチキャスト検出", StringComparison.Ordinal))
            throw new InvalidOperationException("Found peers and alternate-port discovery status should remain visible.");

        Console.WriteLine("PASS peer discovery status changes to actionable guidance after 15 seconds and keeps peer/alternate-port status");
    }

    private static void VerifyMediaUrlHandling(string outputDirectory)
    {
        var validator = typeof(MainWindow).GetMethod("IsSupportedMediaUrl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("The video URL validator is missing.");
        bool Accepts(string url) => validator.Invoke(null, [url]) is true;
        if (!Accepts("https://video.example/watch/123?signature=abc&source=mobile") ||
            !Accepts("http://media.example:8080/clip.mp4?token=def"))
            throw new InvalidOperationException("HTTP and HTTPS video URLs with query parameters must remain supported.");
        if (Accepts("ftp://video.example/clip") || Accepts("https://user:password@video.example/clip"))
            throw new InvalidOperationException("Unsupported schemes and embedded URL credentials must be rejected.");

        var builder = typeof(MainWindow).GetMethod("BuildMediaArguments", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("The video download argument builder is missing.");
        var signedUrl = "https://cdn.example/video/123?signature=abc&expires=321";
        const string referer = "https://video.example/watch/123";
        const string userAgent = "EZConverter UI integration test";
        var arguments = builder.Invoke(null, [signedUrl, outputDirectory, "mp4", "720p", false, "ffmpeg.exe", referer, userAgent]) as IReadOnlyList<string>
            ?? throw new InvalidOperationException("The video download argument builder returned an unexpected result.");
        if (!arguments.Contains("--ignore-config", StringComparer.Ordinal) || !arguments.Contains("--no-playlist", StringComparer.Ordinal) ||
            !arguments.Contains(signedUrl, StringComparer.Ordinal) ||
            !arguments.Contains("--referer", StringComparer.Ordinal) || !arguments.Contains(referer, StringComparer.Ordinal) ||
            !arguments.Contains("--user-agent", StringComparer.Ordinal) || !arguments.Contains(userAgent, StringComparer.Ordinal))
            throw new InvalidOperationException("yt-dlp arguments must preserve the signed media URL and safely pass its page referer and browser user agent.");
        Console.WriteLine("PASS video URL input accepts varied HTTP/HTTPS hosts and signed query URLs while rejecting credentials and unsupported schemes");
        Console.WriteLine("PASS yt-dlp arguments preserve signed URLs, isolate external configuration, and pass page referer/user agent");
    }

    private static async Task VerifyVideoPageProbeAsync(Window owner)
    {
        using var portReservation = new TcpListener(System.Net.IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((System.Net.IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();

        var origin = new Uri($"http://127.0.0.1:{port}/");
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add(origin.AbsoluteUri);
        listener.Start();
        using var stopServer = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            while (!stopServer.IsCancellationRequested)
            {
                System.Net.HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (System.Net.HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }

                var isPage = context.Request.Url?.AbsolutePath == "/watch";
                var bytes = isPage
                    ? Encoding.UTF8.GetBytes("<!doctype html><html><body><video controls src='/clip.mp4?signature=fixture'></video></body></html>")
                    : [0, 0, 0, 0];
                context.Response.StatusCode = isPage || context.Request.Url?.AbsolutePath == "/clip.mp4"
                    ? (int)System.Net.HttpStatusCode.OK
                    : (int)System.Net.HttpStatusCode.NotFound;
                context.Response.ContentType = isPage ? "text/html; charset=utf-8" : "video/mp4";
                context.Response.ContentLength64 = bytes.Length;
                try { await context.Response.OutputStream.WriteAsync(bytes); }
                catch (System.Net.HttpListenerException) { }
                finally { context.Response.Close(); }
            }
        });

        var pageUrl = new Uri(origin, "watch").AbsoluteUri;
        var expectedMediaUrl = new Uri(origin, "clip.mp4?signature=fixture").AbsoluteUri;
        var probe = new VideoPageProbeWindow(pageUrl)
        {
            Owner = owner,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = owner.Left + 32,
            Top = owner.Top + 32
        };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        DateTime? detectedAt = null;
        var timedOut = false;
        var selectionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        selectionTimer.Tick += (_, _) =>
        {
            var candidate = probe.MediaCandidates.FirstOrDefault(media => media.Url == expectedMediaUrl);
            if (candidate is not null)
            {
                detectedAt ??= DateTime.UtcNow;
                if (DateTime.UtcNow - detectedAt.Value >= TimeSpan.FromMilliseconds(750) &&
                    probe.FindName("MediaList") is ListBox list &&
                    probe.FindName("SaveMediaButton") is Button saveButton)
                {
                    list.SelectedItem = candidate;
                    saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    selectionTimer.Stop();
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                timedOut = true;
                selectionTimer.Stop();
                probe.Close();
            }
        };

        selectionTimer.Start();
        try
        {
            _ = probe.ShowDialog();
            if (timedOut)
                throw new TimeoutException("The isolated WebView2 did not detect and select the public synthetic MP4 URL.");

            var selected = probe.SelectedMedia
                ?? throw new InvalidOperationException("The WebView2 candidate selection did not return a media URL.");
            if (selected.Url != expectedMediaUrl || selected.Kind != "動画ファイル" || selected.PageUrl != pageUrl ||
                string.IsNullOrWhiteSpace(selected.UserAgent))
                throw new InvalidDataException("The WebView2 candidate did not preserve its signed URL, page referer, media kind, and browser user agent.");
            Console.WriteLine("PASS isolated WebView2 discovers and selects a signed synthetic MP4 URL with its page and user agent");
        }
        finally
        {
            selectionTimer.Stop();
            stopServer.Cancel();
            listener.Stop();
            try { await serverTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Timed out waiting for the sharing UI operation.");
    }

    private static async Task WaitForBrowserScriptAsync(CoreWebView2 browser, string expression, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await browser.ExecuteScriptAsync(expression) == "true") return;
            }
            catch (InvalidOperationException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("Timed out waiting for the public browser share page.");
    }
}
