using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EZConverter.Sharing;
using MediaConverter.Services;
using MediaConverter.Views;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

internal static class Program
{
    private sealed record BrowserDownload(string Name, int Length, string Sha256);
    private sealed record BrowserUpload(string Name, int Length);
    private static int _exitCode;

    [STAThread]
    private static int Main(string[] args)
    {
        var publicP2p = args is ["--public-p2p"];
        if (args is ["--public-p2p-manual"])
            return RunPublicP2PManual();
        if (args is ["--public-p2p-manual-share"])
            return RunPublicP2PManualShare();
        if (args.Length > 0 && args[0] == "--public-manual")
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("Usage: Sharing.P2PBrowser --public-manual <synthetic-file>");
                return 2;
            }
            return RunPublicManual(args[1]);
        }

        var root = Path.Combine(Directory.GetCurrentDirectory(), "work", "sharing-p2p-browser", Guid.NewGuid().ToString("N"));
        var sendDirectory = Path.Combine(root, "send");
        var receiveDirectory = Path.Combine(root, "receive");
        Directory.CreateDirectory(sendDirectory);
        Directory.CreateDirectory(receiveDirectory);
        var shareBytes = RandomNumberGenerator.GetBytes(2_097_251);
        var nestedBytes = RandomNumberGenerator.GetBytes(1_048_657);
        const int uploadLength = 12 * 1024 * 1024;
        var sharePath = Path.Combine(sendDirectory, "p2p-download.bin");
        File.WriteAllBytes(sharePath, shareBytes);
        Directory.CreateDirectory(Path.Combine(sendDirectory, "日本語 & (sample)"));
        File.WriteAllBytes(Path.Combine(sendDirectory, "日本語 & (sample)", "p2p-nested.bin"), nestedBytes);
        var localAddresses = TransferServer.LocalAddresses();
        var browserAddress = localAddresses.FirstOrDefault(address => IPAddress.TryParse(address, out var ip) &&
            (ip.GetAddressBytes()[0] == 10 || ip.GetAddressBytes()[0] == 192 && ip.GetAddressBytes()[1] == 168 ||
             ip.GetAddressBytes()[0] == 172 && ip.GetAddressBytes()[1] is >= 16 and <= 31)) ?? localAddresses.FirstOrDefault() ?? "127.0.0.1";

        var server = new TransferServer(new() { DeviceName = "P2P Browser Test", ReceiveDirectory = receiveDirectory, StateDirectory = Path.Combine(root, "state"), BindAddress = System.Net.IPAddress.Any });
        server.Incoming += offer => offer.Accept();
        var received = new TaskCompletionSource<TransferProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeAppReceived = new TaskCompletionSource<TransferProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var partialReceive = new TaskCompletionSource<TransferProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Progress += progress =>
        {
            if (progress.Direction != "受信") return;
            if (progress.State == "受信中" && progress.Completed >= 48 * 1024) partialReceive.TrySetResult(progress);
            if (progress.State == "完了") received.TrySetResult(progress);
            if (progress.State == "完了" && progress.Name == "Native EZ Converter Sender") nativeAppReceived.TrySetResult(progress);
        };

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var status = new TextBlock { Text = "WebView2 P2P検証を準備しています...", Margin = new Thickness(16), Foreground = Brushes.White };
        var guest = new WebView2 { CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = Path.Combine(root, "webview-guest") } };
        var grid = new Grid { Background = new SolidColorBrush(Color.FromRgb(18, 22, 29)) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(status, 0);
        Grid.SetRow(guest, 1);
        grid.Children.Add(status); grid.Children.Add(guest);
        var window = new Window { Title = "EZ Converter · P2P通信確認", Width = 1120, Height = 760, Content = grid, Background = Brushes.Black };
        app.MainWindow = window;
        window.Loaded += async (_, _) =>
        {
            PeerSessionWindow? hostWindow = null;
            Window? queuedGuestWindow = null;
            WebView2? queuedGuest = null;
            CloudflareTunnel? publicTunnel = null;
            Uri? publicOrigin = null;
            try
            {
                await server.StartAsync();
                await guest.EnsureCoreWebView2Async();
                await using (var browserShare = await LocalSendDownloadServer.StartAsync("LocalSend Browser Test",
                    await TransferFiles.CollectAsync([sendDirectory]), "123456", TimeSpan.FromMinutes(5)))
                {
                    guest.Source = new Uri(browserShare.LocalLink("127.0.0.1"));
                    status.Text = "LocalSend v2のブラウザー共有とPINを確認中...";
                    await WaitForJsAsync(guest, "getComputedStyle(document.querySelector('#pin-form')).display !== 'none'", TimeSpan.FromSeconds(40));
                    Console.WriteLine("PASS LocalSend-compatible browser page opened (secureContext=" + await guest.ExecuteScriptAsync("isSecureContext") + ")");
                    await guest.ExecuteScriptAsync("document.querySelector('#pin').value='123456';document.querySelector('#pin-form').dispatchEvent(new Event('submit',{bubbles:true,cancelable:true}));");
                    await WaitForJsAsync(guest, "document.querySelectorAll('#files li a').length===2", TimeSpan.FromSeconds(40));
                    await WaitForJsAsync(guest, "!document.querySelector('#bulk-download').hidden && new URL(document.querySelector('#download-all').href).pathname.endsWith('/download-all')", TimeSpan.FromSeconds(20));
                    Console.WriteLine("PASS browser exposes the folder-preserving bulk ZIP alongside individual LocalSend downloads");
                    await guest.ExecuteScriptAsync("(async()=>{const result=[];for(const link of document.querySelectorAll('#files li a')){const bytes=await(await fetch(link.href)).arrayBuffer(),hash=await crypto.subtle.digest('SHA-256',bytes);result.push({Name:link.download,Length:bytes.byteLength,Sha256:[...new Uint8Array(hash)].map(value=>value.toString(16).padStart(2,'0')).join('').toUpperCase()});}window.__localSendDownloads=result;})()");
                    await WaitForJsAsync(guest, "Boolean(window.__localSendDownloads)", TimeSpan.FromSeconds(60));
                    var downloadedFiles = JsonSerializer.Deserialize<List<BrowserDownload>>(await guest.ExecuteScriptAsync("window.__localSendDownloads"))!;
                    if (downloadedFiles.Count != 2 || !downloadedFiles.Any(file => file.Name == "p2p-download.bin" && file.Length == shareBytes.Length && file.Sha256 == Convert.ToHexString(SHA256.HashData(shareBytes))) ||
                        !downloadedFiles.Any(file => file.Name == "p2p-nested.bin" && file.Length == nestedBytes.Length && file.Sha256 == Convert.ToHexString(SHA256.HashData(nestedBytes))))
                        throw new Exception("LocalSend browser download bytes differ from the sender files.");
                    Console.WriteLine($"PASS LocalSend v2 browser download: {shareBytes.Length + nestedBytes.Length:N0} bytes, PIN accepted, SHA-256 verified");
                }

                var p2pShare = server.CreateShare(await TransferFiles.CollectAsync([sendDirectory]), TimeSpan.FromMinutes(5));
                hostWindow = await OpenHostWindowAsync(server.HostPage(p2pShare), window);
                if (publicP2p)
                {
                    publicTunnel = new CloudflareTunnel();
                    publicTunnel.Status += message => Console.Error.WriteLine("TUNNEL_STATUS: " + message);
                    publicTunnel.Diagnostic += message => Console.Error.WriteLine("TUNNEL: " + Regex.Replace(message,
                        @"https://[a-z0-9-]+\.trycloudflare\.com(?:/[^\s]*)?", "[一時URL]"));
                    publicOrigin = await publicTunnel.StartAsync(server.SignalPort);
                    Console.WriteLine("PASS temporary public share URL reached the sender-PC signaling listener");
                    guest.Source = new Uri(publicOrigin, "/s/" + p2pShare.Token);
                }
                else guest.Source = new Uri($"http://{browserAddress}:{server.SignalPort}/s/{p2pShare.Token}");
                if (!publicP2p)
                {
                    await WaitForJsAsync(guest, "typeof startPeer === 'function'", TimeSpan.FromSeconds(20));
                    var lanContext = await guest.ExecuteScriptAsync("JSON.stringify({secure:isSecureContext,peerConnection:typeof RTCPeerConnection})");
                    if (await guest.ExecuteScriptAsync("isSecureContext") != "false")
                        throw new Exception("LAN-IP regression test must use a non-secure HTTP context: " + lanContext);
                    if (await guest.ExecuteScriptAsync("typeof RTCPeerConnection === 'function'") != "true")
                        throw new Exception("LAN URL does not expose WebRTC in this browser context: " + lanContext);
                    Console.WriteLine("PASS LAN-IP browser context exposes WebRTC (" + lanContext + ")");
                }
                await WaitForJsAsync(guest, "document.querySelectorAll('#files li button').length===2", TimeSpan.FromSeconds(40));
                await WaitForJsAsync(guest, "document.querySelector('#downloadZip') && !document.querySelector('#downloadZip').hidden", TimeSpan.FromSeconds(10));
                await VerifySelectedCandidatePairAsync(guest);
                Console.WriteLine($"PASS {(publicP2p ? "public-signaled " : string.Empty)}selected WebRTC candidate pair is direct and non-relay");
                await guest.ExecuteScriptAsync("""
                    window.__p2pChunks=[];
                    Object.defineProperty(window,'showSaveFilePicker',{configurable:true,value:async()=>({createWritable:async()=>({
                      write:async value=>window.__p2pChunks.push(new Uint8Array(value).slice()),
                      close:async()=>{
                        const length=window.__p2pChunks.reduce((sum,part)=>sum+part.length,0),bytes=new Uint8Array(length);
                        let offset=0;for(const part of window.__p2pChunks){bytes.set(part,offset);offset+=part.length;}
                        const hash=new EZSha256();hash.update(bytes);
                        window.__p2pReceived={Name:'p2p-download.bin',Length:length,Sha256:hash.hex().toUpperCase()};
                      },
                      abort:async()=>{}
                    })})});
                    const row=[...document.querySelectorAll('#files li')].find(item=>item.textContent.includes('p2p-download.bin'));
                    if(!row)throw Error('P2P test file is missing');row.querySelector('button').click();
                    """);
                await WaitForJsAsync(guest, "Boolean(window.__p2pReceived)", TimeSpan.FromSeconds(60));
                var p2pDownload = JsonSerializer.Deserialize<BrowserDownload>(await guest.ExecuteScriptAsync("window.__p2pReceived"));
                if (p2pDownload is null || p2pDownload.Length != shareBytes.Length ||
                    !p2pDownload.Sha256.Equals(Convert.ToHexString(SHA256.HashData(shareBytes)), StringComparison.OrdinalIgnoreCase))
                    throw new Exception("WebRTC share did not deliver the expected file bytes.");
                Console.WriteLine($"PASS {(publicP2p ? "public-signaled " : string.Empty)}WebRTC share delivered {p2pDownload.Length:N0} bytes directly with SHA-256 verified");
                await guest.ExecuteScriptAsync("""
                    (()=>{
                    window.__cancelWriteCount=0;window.__cancelWriteEntered=false;window.__cancelWriteRelease=null;window.__cancelWriteAborted=false;window.__cancelStored=0;
                    window.__cancelChooseDestinationOriginal=chooseDestination;
                    chooseDestination=async()=>({writer:{
                      write:async value=>{window.__cancelWriteCount++;window.__cancelStored+=value.byteLength;if(window.__cancelWriteCount===2){window.__cancelWriteEntered=true;await new Promise(resolve=>window.__cancelWriteRelease=resolve);}},
                      close:async()=>{},abort:async()=>{window.__cancelWriteAborted=true;window.__cancelWriteRelease?.();}
                    },parts:null});
                    window.__cancelDownloadRequests=0;const cancelTestSendData=sendData;sendData=data=>{if(data.type==='download')window.__cancelDownloadRequests++;return cancelTestSendData(data);};
                    const row=[...document.querySelectorAll('#files li')].find(item=>item.textContent.includes('p2p-download.bin'));
                    if(!row)throw Error('P2P cancel test file is missing');row.querySelector('button').dataset.saved='false';row.querySelector('button').disabled=false;
                    window.__cancelSetup={disabled:row.querySelector('button').disabled,picker:typeof window.showSaveFilePicker,channel:channel?.readyState,active:Boolean(activeFile)};row.querySelector('button').click();
                    })();
                    """);
                try { await WaitForJsAsync(guest, "window.__cancelWriteEntered && window.__cancelStored>0 && !document.querySelector('#cancelDownload').hidden", TimeSpan.FromSeconds(30)); }
                catch (TimeoutException error) { throw new TimeoutException("P2P browser cancel did not reach the active writer: " + await guest.ExecuteScriptAsync("JSON.stringify({setup:window.__cancelSetup,requests:window.__cancelDownloadRequests,writeCount:window.__cancelWriteCount,entered:window.__cancelWriteEntered,stored:window.__cancelStored,active:Boolean(activeFile),cancelHidden:document.querySelector('#cancelDownload')?.hidden,channel:channel?.readyState,message:document.querySelector('#message')?.textContent})"), error); }
                await guest.ExecuteScriptAsync("document.querySelector('#cancelDownload').click()");
                await WaitForJsAsync(guest, "window.__cancelWriteAborted && !cancellingReceive && channel?.readyState==='open' && document.querySelector('#message').textContent.includes('P2P接続は維持')", TimeSpan.FromSeconds(20));
                var cancelHostBrowser = hostWindow?.Browser ?? throw new Exception("P2P share host window closed during cancellation verification.");
                await WaitForJsAsync(cancelHostBrowser, "!shareBusy && document.querySelector('#status').textContent.includes('受信をキャンセルしました')", TimeSpan.FromSeconds(20));
                await guest.ExecuteScriptAsync("""
                    (()=>{
                    window.__cancelRetryChunks=[];window.__cancelRetryResult=undefined;
                    chooseDestination=async()=>({writer:{
                      write:async value=>window.__cancelRetryChunks.push(new Uint8Array(value).slice()),
                      close:async()=>{const length=window.__cancelRetryChunks.reduce((sum,part)=>sum+part.length,0),bytes=new Uint8Array(length);let offset=0;for(const part of window.__cancelRetryChunks){bytes.set(part,offset);offset+=part.length;}const hash=new EZSha256();hash.update(bytes);window.__cancelRetryResult={Length:length,Sha256:hash.hex().toUpperCase()};},abort:async()=>{}
                    },parts:null});
                    const row=[...document.querySelectorAll('#files li')].find(item=>item.textContent.includes('p2p-download.bin'));
                    row.querySelector('button').dataset.saved='false';row.querySelector('button').disabled=false;row.querySelector('button').click();
                    })();
                    """);
                await WaitForJsAsync(guest, "Boolean(window.__cancelRetryResult)", TimeSpan.FromSeconds(60));
                await guest.ExecuteScriptAsync("chooseDestination=window.__cancelChooseDestinationOriginal;delete window.__cancelChooseDestinationOriginal;");
                var cancelRetry = JsonSerializer.Deserialize<BrowserDownload>(await guest.ExecuteScriptAsync("window.__cancelRetryResult"));
                if (cancelRetry is null || cancelRetry.Length != shareBytes.Length ||
                    !cancelRetry.Sha256.Equals(Convert.ToHexString(SHA256.HashData(shareBytes)), StringComparison.OrdinalIgnoreCase))
                    throw new Exception("P2P download could not be retried after cancellation with matching SHA-256.");
                Console.WriteLine("PASS browser cancelled an active P2P download, kept the peer connection open, and retried successfully with SHA-256 verified");
                await guest.ExecuteScriptAsync("""
                    (()=>{
                    window.__zipCancelOriginalDestination=chooseDestination;window.__zipCancelWrites=0;window.__zipCancelEntered=false;window.__zipCancelRelease=null;window.__zipCancelAborted=false;window.__zipCancelClosed=false;
                    chooseDestination=async()=>({writer:{
                      write:async value=>{window.__zipCancelWrites++;if(window.__zipCancelWrites===2){window.__zipCancelEntered=true;await new Promise(resolve=>window.__zipCancelRelease=resolve);}},
                      close:async()=>{window.__zipCancelClosed=true;},abort:async()=>{window.__zipCancelAborted=true;window.__zipCancelRelease?.();}
                    },parts:null});
                    document.querySelector('#downloadZip').click();
                    })();
                    """);
                await WaitForJsAsync(guest, "window.__zipCancelEntered && !document.querySelector('#cancelDownload').hidden", TimeSpan.FromSeconds(30));
                await guest.ExecuteScriptAsync("document.querySelector('#cancelDownload').click()");
                await WaitForJsAsync(guest, "window.__zipCancelAborted && !zipBatch && !activeFile && !cancellingReceive && channel?.readyState==='open' && document.querySelector('#message').textContent.includes('P2P接続は維持')", TimeSpan.FromSeconds(20));
                var zipCancelHostBrowser = hostWindow?.Browser ?? throw new Exception("P2P share host window closed during ZIP cancellation verification.");
                await WaitForJsAsync(zipCancelHostBrowser, "!shareBusy && document.querySelector('#status').textContent.includes('受信をキャンセルしました')", TimeSpan.FromSeconds(20));
                if (await guest.ExecuteScriptAsync("window.__zipCancelClosed") != "false")
                    throw new Exception("The cancelled ZIP was finalized despite the cancellation request.");
                await guest.ExecuteScriptAsync("chooseDestination=window.__zipCancelOriginalDestination;delete window.__zipCancelOriginalDestination;");
                Console.WriteLine("PASS browser cancelled a multi-file P2P ZIP, discarded the partial archive, kept the peer connection open, and allowed a clean retry");
                await WaitForJsAsync(guest, "!activeFile && channel?.readyState==='open'", TimeSpan.FromSeconds(10));
                await guest.ExecuteScriptAsync("""
                    window.__zipChunks=[];window.__zipBlob=undefined;window.__zipError=undefined;window.__zipDone=false;
                    try {
                      Object.defineProperty(window,'showSaveFilePicker',{configurable:true,value:async options=>({createWritable:async()=>({
                        write:async value=>window.__zipChunks.push(new Uint8Array(value).slice()),
                        close:async()=>{window.__zipBlob=new Blob(window.__zipChunks,{type:'application/zip'});},abort:async()=>{}
                      })})});
                      window.__zipStatus='';const originalShow=show;show=message=>{window.__zipStatus=String(message);originalShow(message);};
                      document.querySelector('#downloadZip').click();
                    } catch(error) { window.__zipError=String(error.stack||error.message||error);window.__zipDone=true; }
                    """);
                await WaitForJsAsync(guest, "!document.querySelector('#downloadZip').disabled && window.__zipStatus!=='' && !window.__zipStatus.startsWith('ZIPへ')", TimeSpan.FromSeconds(90));
                if (await guest.ExecuteScriptAsync("Boolean(window.__zipBlob)") != "true")
                    throw new Exception("Browser ZIP download failed: " + await guest.ExecuteScriptAsync("window.__zipStatus") + " error=" + await guest.ExecuteScriptAsync("window.__zipError"));
                await guest.ExecuteScriptAsync("window.__zipBase64=undefined;(async()=>{const bytes=new Uint8Array(await window.__zipBlob.arrayBuffer());let binary='';for(let offset=0;offset<bytes.length;offset+=32768)binary+=String.fromCharCode(...bytes.subarray(offset,offset+32768));window.__zipBase64=btoa(binary);})().catch(error=>window.__zipError=String(error.stack||error.message||error));");
                await WaitForJsAsync(guest, "typeof window.__zipBase64==='string' || Boolean(window.__zipError)", TimeSpan.FromSeconds(20));
                if (await guest.ExecuteScriptAsync("typeof window.__zipBase64==='string'") != "true")
                    throw new Exception("Could not read the generated ZIP from the browser test: " + await guest.ExecuteScriptAsync("window.__zipError"));
                var zipBase64 = JsonSerializer.Deserialize<string>(await guest.ExecuteScriptAsync("window.__zipBase64"));
                using (var zipStream = new MemoryStream(Convert.FromBase64String(zipBase64!)))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
                {
                    var expectedZipFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal)
                    {
                        ["send/p2p-download.bin"] = shareBytes,
                        ["send/日本語 & (sample)/p2p-nested.bin"] = nestedBytes
                    };
                    if (archive.Entries.Count != expectedZipFiles.Count) throw new Exception("P2P ZIP contains an unexpected number of entries.");
                    foreach (var entry in archive.Entries)
                    {
                        if (!expectedZipFiles.TryGetValue(entry.FullName, out var expectedBytes)) throw new Exception("P2P ZIP contains an unexpected path: " + entry.FullName);
                        using var entryStream = entry.Open();
                        using var entryBytes = new MemoryStream();
                        await entryStream.CopyToAsync(entryBytes);
                        var bytes = entryBytes.ToArray();
                        if (bytes.Length != expectedBytes.Length || !SHA256.HashData(bytes).AsSpan().SequenceEqual(SHA256.HashData(expectedBytes)))
                            throw new Exception("P2P ZIP entry differs from the source SHA-256: " + entry.FullName);
                    }
                }
                Console.WriteLine("PASS browser can create a folder-preserving P2P ZIP and every extracted entry matches SHA-256");
                await guest.ExecuteScriptAsync("""
                    window.__fallbackDebug = { stage: 'start' };
                    try {
                    Object.defineProperty(window, 'showSaveFilePicker', { configurable: true, value: undefined });
                    window.__fallbackDownload = undefined;
                    window.__fallbackError = undefined;
                    window.__fallbackClickCount = 0;
                    window.__fallbackOpfsWrites = 0;
                    const storedParts = [];
                    const temporaryFile = {
                      createWritable: async () => ({
                        write: async value => { window.__fallbackOpfsWrites++; storedParts.push(new Uint8Array(value).slice()); },
                        close: async () => {}, abort: async () => {}
                      }),
                      getFile: async () => new Blob(storedParts)
                    };
                    const temporaryDirectory = { getFileHandle: async () => temporaryFile, removeEntry: async () => {} };
                    const storageManager = navigator.storage || {};
                    Object.defineProperty(storageManager, 'getDirectory', { configurable: true, value: async () => temporaryDirectory });
                    Object.defineProperty(navigator, 'storage', { configurable: true, value: storageManager });
                    window.__fallbackDebug.stage = 'picker disabled';
                    const createObjectURL = URL.createObjectURL.bind(URL);
                    URL.createObjectURL = blob => { window.__fallbackBlob = blob; return createObjectURL(blob); };
                    const nativeClick = HTMLAnchorElement.prototype.click;
                    HTMLAnchorElement.prototype.click = function() {
                      window.__fallbackClickCount++;
                      if (this.href.startsWith('blob:') && this.download === 'p2p-nested.bin') {
                        const name = this.download;
                        window.__fallbackBlob.arrayBuffer().then(async bytes => {
                          const hash = new EZSha256(); hash.update(new Uint8Array(bytes));
                          window.__fallbackDownload = { Name: name, Length: bytes.byteLength,
                            Sha256: hash.hex().toUpperCase() };
                        }).catch(error => { window.__fallbackError = String(error?.stack || error); });
                        return;
                      }
                      return nativeClick.call(this);
                    };
                    window.__fallbackDebug.stage = 'download hooks installed';
                    const row = [...document.querySelectorAll('#files li')].find(item => item.textContent.includes('p2p-nested.bin'));
                    const button = row?.querySelector('button');
                    window.__fallbackDebug = { stage: 'button found', hasRow: Boolean(row), disabled: button?.disabled,
                      picker: typeof window.showSaveFilePicker, channel: channel?.readyState,
                      file: row?.textContent, urlApi: typeof URL.createObjectURL };
                    if (!button || button.disabled) window.__fallbackError = 'Fallback test button is missing or disabled';
                    else button.click();
                    } catch (error) { window.__fallbackError = String(error?.stack || error); }
                    """);
                await WaitForJsAsync(guest, "Boolean(window.__fallbackDownload) || Boolean(window.__fallbackError) || window.__fallbackClickCount > 0", TimeSpan.FromSeconds(15));
                var fallbackDebug = await guest.ExecuteScriptAsync("JSON.stringify({debug:window.__fallbackDebug,error:window.__fallbackError,clicks:window.__fallbackClickCount,blob:window.__fallbackBlob?.size,download:window.__fallbackDownload})");
                if (await guest.ExecuteScriptAsync("Boolean(window.__fallbackError)") == "true")
                    throw new Exception("WebRTC Blob fallback failed: " + fallbackDebug);
                if (await guest.ExecuteScriptAsync("Boolean(window.__fallbackDownload)") != "true")
                    throw new Exception("WebRTC Blob fallback did not create the expected download: " + fallbackDebug);
                if (await guest.ExecuteScriptAsync("window.__fallbackOpfsWrites > 0") != "true")
                    throw new Exception("Large-file browser fallback did not stream data into temporary storage: " + fallbackDebug);
                var fallbackDownload = JsonSerializer.Deserialize<BrowserDownload>(await guest.ExecuteScriptAsync("window.__fallbackDownload"));
                if (fallbackDownload is null || fallbackDownload.Length != nestedBytes.Length ||
                    !fallbackDownload.Sha256.Equals(Convert.ToHexString(SHA256.HashData(nestedBytes)), StringComparison.OrdinalIgnoreCase))
                    throw new Exception("WebRTC Blob fallback did not produce the expected file bytes.");
                Console.WriteLine("PASS browser without the File System Access API streamed the download through temporary storage and SHA-256 verified it");
                await guest.ExecuteScriptAsync("(async()=>{const getDirectory=navigator.storage.getDirectory;Object.defineProperty(navigator.storage,'getDirectory',{configurable:true,value:async()=>{throw new DOMException('temporary storage unavailable','QuotaExceededError');}});try{const small=await chooseDestination({length:2*1024*1024,relativePath:'small.bin'});let largeError='';try{await chooseDestination({length:513*1024*1024,relativePath:'large.bin'});}catch(error){largeError=error.message;}window.__fallbackQuotaTest=small.writer===null&&Array.isArray(small.parts)&&largeError.includes('一時保存領域を利用できません');}finally{Object.defineProperty(navigator.storage,'getDirectory',{configurable:true,value:getDirectory});}})().catch(error=>window.__fallbackQuotaError=String(error.stack||error.message||error));");
                await WaitForJsAsync(guest, "window.__fallbackQuotaTest!==undefined || Boolean(window.__fallbackQuotaError)", TimeSpan.FromSeconds(5));
                if (await guest.ExecuteScriptAsync("window.__fallbackQuotaTest") != "true")
                    throw new Exception("Browser fallback did not handle temporary-storage quota safely: " + await guest.ExecuteScriptAsync("window.__fallbackQuotaError"));
                Console.WriteLine("PASS temporary-storage denial falls back for bounded files and rejects oversized memory-only downloads");
                await guest.ExecuteScriptAsync("window.__p2pReconnectClosed=false;window.__p2pReconnectReady=0;const reconnectStartPeer=startPeer;startPeer=function(){window.__p2pReconnectReady++;return reconnectStartPeer();};const reconnectClose=ws.onclose;ws.onclose=event=>{window.__p2pReconnectClosed=true;reconnectClose(event);};ws.close();");
                await WaitForJsAsync(guest, "window.__p2pReconnectClosed && window.__p2pReconnectReady>0 && channel?.readyState==='open' && document.querySelectorAll('#files li button').length===2", TimeSpan.FromSeconds(45));
                await VerifySelectedCandidatePairAsync(guest);
                await guest.ExecuteScriptAsync("""
                    window.__p2pReceived=undefined;window.__p2pChunks=[];
                    Object.defineProperty(window,'showSaveFilePicker',{configurable:true,value:async()=>({createWritable:async()=>({
                      write:async value=>window.__p2pChunks.push(new Uint8Array(value).slice()),
                      close:async()=>{
                        const length=window.__p2pChunks.reduce((sum,part)=>sum+part.length,0),bytes=new Uint8Array(length);
                        let offset=0;for(const part of window.__p2pChunks){bytes.set(part,offset);offset+=part.length;}
                        const hash=new EZSha256();hash.update(bytes);
                        window.__p2pReceived={Name:'p2p-download.bin',Length:length,Sha256:hash.hex().toUpperCase()};
                      },
                      abort:async()=>{}
                    })})});
                    const row=[...document.querySelectorAll('#files li')].find(item=>item.textContent.includes('p2p-download.bin'));
                    if(!row)throw Error('P2P reconnect test file is missing');row.querySelector('button').click();
                    """);
                await WaitForJsAsync(guest, "Boolean(window.__p2pReceived)", TimeSpan.FromSeconds(60));
                var reconnectedDownload = JsonSerializer.Deserialize<BrowserDownload>(await guest.ExecuteScriptAsync("window.__p2pReceived"));
                if (reconnectedDownload is null || reconnectedDownload.Length != shareBytes.Length ||
                    !reconnectedDownload.Sha256.Equals(Convert.ToHexString(SHA256.HashData(shareBytes)), StringComparison.OrdinalIgnoreCase))
                    throw new Exception("WebRTC share did not resume after signaling reconnect with matching bytes.");
                Console.WriteLine($"PASS {(publicP2p ? "public-signaled " : string.Empty)}browser share reconnected automatically and transferred {reconnectedDownload.Length:N0} bytes with SHA-256 verified");

                await guest.ExecuteScriptAsync("""
                    window.__zipResumeWriteCount=0;window.__zipResumeWriteEntered=false;window.__zipResumeWriteRelease=null;
                    window.__zipResumeStored=new Uint8Array(0);window.__zipResumeTruncated=-1;window.__zipResumeRequestedOffset=0;
                    window.__zipResumeBlob=undefined;window.__zipResumeFinished=false;window.__zipResumeError=undefined;
                    window.__zipReconnectBaseline=window.__p2pReconnectReady||0;
                    const oldSendData=sendData;
                    sendData=data=>{if(data.type==='download')window.__zipResumeRequestedOffset=Math.max(window.__zipResumeRequestedOffset,data.offset||0);return oldSendData(data);};
                    Object.defineProperty(window,'showSaveFilePicker',{configurable:true,value:async()=>({createWritable:async()=>{
                      let position=0,stored=new Uint8Array(0);
                      return {
                        write:async value=>{
                          const payload=new Uint8Array(value);window.__zipResumeWriteCount++;
                          if(window.__zipResumeWriteCount===3){window.__zipResumeWriteEntered=true;await new Promise(resolve=>window.__zipResumeWriteRelease=resolve);}
                          const next=new Uint8Array(Math.max(stored.length,position+payload.length));next.set(stored);next.set(payload,position);stored=next;position+=payload.length;window.__zipResumeStored=stored.slice();
                        },
                        truncate:async length=>{stored=stored.slice(0,length);position=Math.min(position,length);window.__zipResumeTruncated=length;window.__zipResumeStored=stored.slice();},
                        seek:async offset=>{position=offset;},
                        close:async()=>{window.__zipResumeBlob=new Blob([stored],{type:'application/zip'});},abort:async()=>{}
                      };
                    }})});
                    (async()=>{try{await downloadAllZip();}catch(error){window.__zipResumeError=String(error.stack||error.message||error);}window.__zipResumeFinished=true;})();
                    """);
                await WaitForJsAsync(guest, "window.__zipResumeWriteEntered && window.__zipResumeStored.length>0", TimeSpan.FromSeconds(30));
                var zipInterruptedOffset = JsonSerializer.Deserialize<long>(await guest.ExecuteScriptAsync("window.__zipResumeRequestedOffset"));
                var zipRecoveryHostBrowser = hostWindow?.Browser ?? throw new Exception("P2P share host window closed before ZIP recovery verification.");
                var simulatedZipIceState = JsonSerializer.Deserialize<JsonElement>(await zipRecoveryHostBrowser.ExecuteScriptAsync("(()=>{const failedPeer=pc;clearTimeout(peerRecoveryTimer);peerRecoveryTimer=null;window.__zipShareRecoverySeen=false;const originalShow=show;show=message=>{if(message.includes('受信データを保持して接続を再確立しています'))window.__zipShareRecoverySeen=true;originalShow(message);};const nativeSetTimeout=window.setTimeout;window.setTimeout=(callback,delay,...args)=>nativeSetTimeout(callback,delay===8000?0:delay,...args);Object.defineProperty(failedPeer,'connectionState',{configurable:true,get:()=> 'failed'});schedulePeerRecovery(failedPeer);window.setTimeout=nativeSetTimeout;return {state:failedPeer.connectionState,recoveryScheduled:Boolean(peerRecoveryTimer)};})()"));
                if (simulatedZipIceState.GetProperty("state").GetString() != "failed" || !simulatedZipIceState.GetProperty("recoveryScheduled").GetBoolean())
                    throw new Exception("Could not schedule P2P ZIP recovery after an ICE failure: " + simulatedZipIceState);
                await WaitForJsAsync(zipRecoveryHostBrowser, "window.__zipShareRecoverySeen===true", TimeSpan.FromSeconds(5));
                await guest.ExecuteScriptAsync("setTimeout(()=>window.__zipResumeWriteRelease?.(),200);");
                try
                {
                    await WaitForJsAsync(guest, "window.__p2pReconnectReady>window.__zipReconnectBaseline && channel?.readyState==='open' && window.__zipResumeRequestedOffset>0", TimeSpan.FromSeconds(30));
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException("P2P ZIP did not resume after ICE recovery. guest=" + await guest.ExecuteScriptAsync("JSON.stringify({message:document.querySelector('#message')?.textContent,channel:channel?.readyState,offset:window.__zipResumeRequestedOffset,truncated:window.__zipResumeTruncated,finished:window.__zipResumeFinished,error:window.__zipResumeError})") + " host=" + await zipRecoveryHostBrowser.ExecuteScriptAsync("JSON.stringify({status:document.querySelector('#status')?.textContent,ws:ws?.readyState,channel:channel?.readyState,pc:pc?.connectionState})"));
                }
                await WaitForJsAsync(guest, "window.__zipResumeFinished===true", TimeSpan.FromSeconds(90));
                if (await guest.ExecuteScriptAsync("Boolean(window.__zipResumeBlob)") != "true" || zipInterruptedOffset != 0 ||
                    await guest.ExecuteScriptAsync("window.__zipResumeRequestedOffset>0 && window.__zipResumeTruncated>window.__zipResumeRequestedOffset") != "true")
                    throw new Exception("P2P ZIP recovery did not preserve a non-zero file offset and its ZIP header: " + await guest.ExecuteScriptAsync("JSON.stringify({offset:window.__zipResumeRequestedOffset,truncated:window.__zipResumeTruncated,error:window.__zipResumeError})"));
                await guest.ExecuteScriptAsync("window.__zipResumeBase64=undefined;(async()=>{const bytes=new Uint8Array(await window.__zipResumeBlob.arrayBuffer());let binary='';for(let offset=0;offset<bytes.length;offset+=32768)binary+=String.fromCharCode(...bytes.subarray(offset,offset+32768));window.__zipResumeBase64=btoa(binary);})().catch(error=>window.__zipResumeError=String(error.stack||error.message||error));");
                await WaitForJsAsync(guest, "typeof window.__zipResumeBase64==='string' || Boolean(window.__zipResumeError)", TimeSpan.FromSeconds(20));
                if (await guest.ExecuteScriptAsync("typeof window.__zipResumeBase64==='string'") != "true")
                    throw new Exception("Could not read the resumed ZIP: " + await guest.ExecuteScriptAsync("window.__zipResumeError"));
                using (var resumedZipStream = new MemoryStream(Convert.FromBase64String(JsonSerializer.Deserialize<string>(await guest.ExecuteScriptAsync("window.__zipResumeBase64"))!)))
                using (var resumedArchive = new ZipArchive(resumedZipStream, ZipArchiveMode.Read))
                {
                    var expectedZipFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal)
                    {
                        ["send/p2p-download.bin"] = shareBytes,
                        ["send/日本語 & (sample)/p2p-nested.bin"] = nestedBytes
                    };
                    if (resumedArchive.Entries.Count != expectedZipFiles.Count) throw new Exception("Resumed P2P ZIP contains an unexpected number of entries.");
                    foreach (var entry in resumedArchive.Entries)
                    {
                        if (!expectedZipFiles.TryGetValue(entry.FullName, out var expectedBytes)) throw new Exception("Resumed P2P ZIP contains an unexpected path: " + entry.FullName);
                        using var entryStream = entry.Open();
                        using var entryBytes = new MemoryStream();
                        await entryStream.CopyToAsync(entryBytes);
                        if (!SHA256.HashData(entryBytes.ToArray()).AsSpan().SequenceEqual(SHA256.HashData(expectedBytes)))
                            throw new Exception("Resumed P2P ZIP entry differs from source: " + entry.FullName);
                    }
                }
                Console.WriteLine($"PASS interrupted P2P ZIP resumed from {JsonSerializer.Deserialize<long>(await guest.ExecuteScriptAsync("window.__zipResumeRequestedOffset")):N0} file bytes and extracted entries match source SHA-256");

                await guest.ExecuteScriptAsync("""
                    window.__resumeSetupError = null;
                    try {
                    window.__p2pResumeOffset = 0;
                    window.__p2pResumeResult = undefined;
                    window.__resumeWriterBytes = new Uint8Array(0);
                    window.__resumeTruncatedTo = -1;
                    window.__resumeWriteCount = 0;
                    window.__resumeWriteEntered = false;
                    window.__resumeWriteRelease = null;
                    window.__p2pFailureReasons = [];
                    const originalFailActiveForResume = failActive;
                    failActive = error => { window.__p2pFailureReasons.push(String(error?.stack || error?.message || error)); return originalFailActiveForResume(error); };
                    const originalSendData = sendData;
                    sendData = data => {
                      if (data.type === 'download' && Number.isSafeInteger(data.offset)) window.__p2pResumeOffset = data.offset;
                      return originalSendData(data);
                    };
                    Object.defineProperty(window, 'showSaveFilePicker', { configurable: true, value: async () => ({ createWritable: async () => {
                      let position = 0, stored = new Uint8Array(0);
                      return {
                        write: async value => {
                          const payload = new Uint8Array(value);
                          window.__resumeWriteCount++;
                          if (window.__resumeWriteCount === 2) {
                            window.__resumeWriteEntered = true;
                            await new Promise(resolve => window.__resumeWriteRelease = resolve);
                          }
                          const next = new Uint8Array(Math.max(stored.length, position + payload.length));
                          next.set(stored);
                          next.set(payload, position);
                          stored = next;
                          position += payload.length;
                          window.__resumeWriterBytes = stored.slice();
                        },
                        truncate: async length => { stored = stored.slice(0, length); position = Math.min(position, length); window.__resumeWriterBytes = stored.slice(); window.__resumeTruncatedTo = length; },
                        seek: async offset => { position = offset; },
                        close: async () => {
                          const hash = new EZSha256(); hash.update(window.__resumeWriterBytes);
                          window.__p2pResumeResult = { Name: 'p2p-download.bin', Length: window.__resumeWriterBytes.length,
                            Sha256: hash.hex().toUpperCase() };
                        },
                        abort: async () => {}
                      };
                    }})});
                    window.__p2pReconnectBaseline = window.__p2pReconnectReady || 0;
                    const row = [...document.querySelectorAll('#files li')].find(item => item.textContent.includes('p2p-download.bin'));
                    if (!row) throw Error('P2P interrupted-transfer test file is missing');
                    row.querySelector('button').dataset.saved = 'false';
                    row.querySelector('button').disabled = false;
                    row.querySelector('button').click();
                    } catch (error) { window.__resumeSetupError = String(error.stack || error.message || error); }
                    """);
                await WaitForJsAsync(guest, "window.__resumeSetupError || window.__resumeWriteEntered && window.__resumeWriterBytes.length > 0", TimeSpan.FromSeconds(30));
                var resumeSetupError = await guest.ExecuteScriptAsync("window.__resumeSetupError");
                if (resumeSetupError != "null") throw new Exception("Could not start the interrupted browser download: " + resumeSetupError);
                var interruptedOffset = JsonSerializer.Deserialize<long>(await guest.ExecuteScriptAsync("window.__resumeWriterBytes.length"));
                await guest.ExecuteScriptAsync("window.__p2pReconnectBaseline=window.__p2pReconnectReady||0;");
                var shareHostBrowser = hostWindow?.Browser ?? throw new Exception("P2P share host window closed before ICE recovery verification.");
                var simulatedShareIceState = JsonSerializer.Deserialize<JsonElement>(await shareHostBrowser.ExecuteScriptAsync("(()=>{const failedPeer=pc;clearTimeout(peerRecoveryTimer);peerRecoveryTimer=null;window.__shareRecoverySeen=false;const originalShow=show;show=message=>{if(message.includes('受信データを保持して接続を再確立しています'))window.__shareRecoverySeen=true;originalShow(message);};const nativeSetTimeout=window.setTimeout;window.setTimeout=(callback,delay,...args)=>nativeSetTimeout(callback,delay===8000?0:delay,...args);Object.defineProperty(failedPeer,'connectionState',{configurable:true,get:()=> 'failed'});schedulePeerRecovery(failedPeer);window.setTimeout=nativeSetTimeout;return {state:failedPeer.connectionState,recoveryScheduled:Boolean(peerRecoveryTimer)};})()"));
                if (simulatedShareIceState.GetProperty("state").GetString() != "failed" || !simulatedShareIceState.GetProperty("recoveryScheduled").GetBoolean())
                    throw new Exception("Could not schedule share recovery after an ICE failure in the WebView2 host: " + simulatedShareIceState);
                await WaitForJsAsync(shareHostBrowser, "window.__shareRecoverySeen===true", TimeSpan.FromSeconds(5));
                await guest.ExecuteScriptAsync("setTimeout(()=>window.__resumeWriteRelease?.(),200);");
                try
                {
                    await WaitForJsAsync(guest, "window.__p2pReconnectReady > window.__p2pReconnectBaseline && channel?.readyState==='open' && window.__p2pResumeOffset>0", TimeSpan.FromSeconds(20));
                }
                catch (TimeoutException)
                {
                    var guestState = await guest.ExecuteScriptAsync("JSON.stringify({status:document.querySelector('#message')?.textContent,ws:ws?.readyState,channel:channel?.readyState,pc:pc?.connectionState,reconnectStopped,resumeTransferPending,activeFile:activeFile?.relativePath,received,reconnectReady:window.__p2pReconnectReady,baseline:window.__p2pReconnectBaseline,resumeOffset:window.__p2pResumeOffset,writeCount:window.__resumeWriteCount,failures:window.__p2pFailureReasons})");
                    var hostState = hostWindow?.Browser is { } hostStateBrowser
                        ? await hostStateBrowser.ExecuteScriptAsync("JSON.stringify({status:document.querySelector('#status')?.textContent,ws:ws?.readyState,channel:channel?.readyState,pc:pc?.connectionState,shareBusy,hostReconnectStopped})")
                        : "host window missing";
                    throw new TimeoutException("Interrupted download did not reconnect. guest=" + guestState + " host=" + hostState);
                }
                await VerifySelectedCandidatePairAsync(guest);
                await WaitForJsAsync(guest, "Boolean(window.__p2pResumeResult)", TimeSpan.FromSeconds(60));
                var resumedDownload = JsonSerializer.Deserialize<BrowserDownload>(await guest.ExecuteScriptAsync("window.__p2pResumeResult"));
                var requestedResumeOffset = JsonSerializer.Deserialize<long>(await guest.ExecuteScriptAsync("window.__p2pResumeOffset"));
                var durableResumeOffset = JsonSerializer.Deserialize<long>(await guest.ExecuteScriptAsync("window.__resumeTruncatedTo"));
                if (resumedDownload is null || resumedDownload.Length != shareBytes.Length ||
                    interruptedOffset <= 0 || interruptedOffset >= shareBytes.Length || requestedResumeOffset <= 0 ||
                    requestedResumeOffset != durableResumeOffset || requestedResumeOffset >= shareBytes.Length ||
                    !resumedDownload.Sha256.Equals(Convert.ToHexString(SHA256.HashData(shareBytes)), StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Interrupted WebRTC download did not resume at a non-zero offset with matching bytes.");
                Console.WriteLine($"PASS interrupted browser download resumed at durable offset {requestedResumeOffset:N0} (cut during write after {interruptedOffset:N0} bytes) and SHA-256 verified");

                queuedGuest = new WebView2
                {
                    CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = Path.Combine(root, "webview-queued-guest") }
                };
                queuedGuestWindow = new Window
                {
                    Title = "EZ Converter · P2P待機受信者テスト",
                    Width = 900,
                    Height = 620,
                    Content = queuedGuest,
                    Owner = window
                };
                queuedGuestWindow.Show();
                await queuedGuest.EnsureCoreWebView2Async();
                queuedGuest.Source = publicP2p
                    ? new Uri(publicOrigin!, "/s/" + p2pShare.Token)
                    : new Uri(server.PeerLink(p2pShare, browserAddress));
                await WaitForJsAsync(queuedGuest, "document.querySelector('#message')?.textContent.includes('順番待ちです')", TimeSpan.FromSeconds(30));
                await guest.ExecuteScriptAsync("ws.close();");
                await WaitForJsAsync(queuedGuest,
                    "channel?.readyState==='open' && document.querySelectorAll('#files li button').length===2",
                    TimeSpan.FromSeconds(45));
                await VerifySelectedCandidatePairAsync(queuedGuest);
                await queuedGuest.ExecuteScriptAsync("""
                    window.__queuedChunks=[];window.__queuedDownload=undefined;
                    Object.defineProperty(window,'showSaveFilePicker',{configurable:true,value:async()=>({createWritable:async()=>(
                      {write:async value=>window.__queuedChunks.push(new Uint8Array(value).slice()),
                       close:async()=>{const length=window.__queuedChunks.reduce((sum,part)=>sum+part.length,0),bytes=new Uint8Array(length);let offset=0;for(const part of window.__queuedChunks){bytes.set(part,offset);offset+=part.length;}const hash=new EZSha256();hash.update(bytes);window.__queuedDownload={Name:'p2p-download.bin',Length:length,Sha256:hash.hex().toUpperCase()};},abort:async()=>{}}
                    )})});
                    const row=[...document.querySelectorAll('#files li')].find(item=>item.textContent.includes('p2p-download.bin'));
                    if(!row)throw Error('Promoted P2P queue receiver did not receive the file manifest');row.querySelector('button').click();
                    """);
                await WaitForJsAsync(queuedGuest, "Boolean(window.__queuedDownload)", TimeSpan.FromSeconds(60));
                var queuedDownload = JsonSerializer.Deserialize<BrowserDownload>(await queuedGuest.ExecuteScriptAsync("window.__queuedDownload"));
                if (queuedDownload is null || queuedDownload.Length != shareBytes.Length ||
                    !queuedDownload.Sha256.Equals(Convert.ToHexString(SHA256.HashData(shareBytes)), StringComparison.OrdinalIgnoreCase))
                    throw new Exception("The promoted P2P queue receiver did not receive the expected file bytes.");
                Console.WriteLine($"PASS queued browser receiver was promoted and downloaded {queuedDownload.Length:N0} bytes over direct WebRTC with SHA-256 verified");

                await WaitForJsAsync(guest, "document.querySelector('#message')?.textContent.includes('順番待ちです')", TimeSpan.FromSeconds(30));
                await queuedGuest.ExecuteScriptAsync("reconnectStopped=true;ws?.close();");
                await WaitForJsAsync(guest,
                    "channel?.readyState==='open' && document.querySelectorAll('#files li button').length===2",
                    TimeSpan.FromSeconds(45));
                queuedGuestWindow.Close();
                queuedGuestWindow = null;
                queuedGuest = null;
                Console.WriteLine("PASS closing a completed queue receiver promotes the next waiting browser automatically");

                hostWindow.Close();
                hostWindow = null;
                server.RevokeLink(p2pShare.Token);
                await Task.Delay(250);

                var invite = server.CreateInvitation(TimeSpan.FromMinutes(5));
                hostWindow = await OpenHostWindowAsync(server.HostPage(invite), window);
                Uri invitationUri;
                if (publicP2p)
                {
                    publicOrigin ??= await (publicTunnel ??= new CloudflareTunnel()).StartAsync(server.SignalPort);
                    invitationUri = new Uri(publicOrigin, "/i/" + invite.Token);
                    guest.Source = invitationUri;
                    Console.WriteLine("PASS temporary public invitation URL reached the sender-PC signaling listener");
                }
                else
                {
                    invitationUri = new Uri(server.PeerLink(invite, browserAddress));
                    guest.Source = invitationUri;
                }
                status.Text = "P2P招待: ブラウザーからPCへ送信中...";
                await WaitForJsAsync(guest, "!document.querySelector('#pickerArea').hidden", TimeSpan.FromSeconds(40));
                await guest.ExecuteScriptAsync("window.__relayPolicyTest=undefined;(async()=>{try{const host={candidate:'candidate:1 1 UDP 1 192.0.2.1 5000 typ host',sdpMid:'0',sdpMLineIndex:0},relay={candidate:'candidate:2 1 UDP 1 192.0.2.2 6000 typ relay',sdpMid:'0',sdpMLineIndex:0},sdp='v=0\\r\\na=candidate:3 1 UDP 1 192.0.2.3 7000 typ relay\\r\\na=candidate:4 1 UDP 1 192.0.2.4 8000 typ host\\r\\n',makePeer=(added,descriptions)=>({setRemoteDescription:async description=>descriptions.push(description),addIceCandidate:async candidate=>added.push(candidate)});window.EZAllowRelay=false;const directAdded=[],directDescriptions=[],directQueue=window.EZRemoteIceQueue(makePeer(directAdded,directDescriptions));await directQueue.addCandidate(relay);await directQueue.addCandidate(host);await directQueue.setRemoteDescription({type:'offer',sdp});const filteredSdp=directDescriptions[0].sdp,blocked=!directAdded.includes(relay)&&directAdded.includes(host)&&!filteredSdp.includes('typ relay')&&filteredSdp.includes('typ host')&&window.EZRelayCandidateBlocked;window.EZAllowRelay=true;const relayAdded=[],relayDescriptions=[],relayQueue=window.EZRemoteIceQueue(makePeer(relayAdded,relayDescriptions));await relayQueue.addCandidate(relay);await relayQueue.setRemoteDescription({type:'offer',sdp});window.EZAllowRelay=false;window.__relayPolicyTest=blocked&&relayAdded.includes(relay)&&relayDescriptions[0].sdp.includes('typ relay');}catch{window.__relayPolicyTest=false;}})();");
                await WaitForJsAsync(guest, "window.__relayPolicyTest !== undefined", TimeSpan.FromSeconds(5));
                var relayPolicy = await guest.ExecuteScriptAsync("window.__relayPolicyTest");
                if (relayPolicy != "true") throw new Exception("TURN candidate policy did not enforce explicit relay consent.");
                Console.WriteLine("PASS unconfigured TURN relay candidates are blocked while direct candidates remain usable");
                await guest.ExecuteScriptAsync("window.__testSawDisconnect=false;const testShowOriginal=show;show=function(message){if(String(message).includes('接続が一時的に切れました'))window.__testSawDisconnect=true;testShowOriginal(message);};");
                await guest.ExecuteScriptAsync("window.__uploadExpected=undefined;window.__resumeTestUpload=null;(async()=>{const bytes=new Uint8Array(" + uploadLength + ");for(let i=0;i<bytes.length;i++)bytes[i]=(i*31+17)&255;const file=new File([bytes],'p2p-browser-upload.bin'),slice=file.slice.bind(file);let pauseFirstRecoveryChunk=true;file.slice=(...args)=>{const blob=slice(...args),read=blob.arrayBuffer.bind(blob);blob.arrayBuffer=async()=>{if(args[0]===48*1024&&pauseFirstRecoveryChunk){pauseFirstRecoveryChunk=false;await new Promise(resolve=>window.__resumeTestUpload=resolve);}else await new Promise(resolve=>setTimeout(resolve,100));return read();};return blob;};const transfer=new DataTransfer();transfer.items.add(file);window.__uploadExpected={Name:file.name,Length:bytes.length};const input=document.querySelector('#files');input.files=transfer.files;input.dispatchEvent(new Event('change',{bubbles:true}));document.querySelector('#send').click();})().catch(error=>window.__uploadExpected={Error:error.message});");
                await WaitForJsAsync(guest, "window.__uploadExpected !== undefined", TimeSpan.FromSeconds(20));
                var expectedUploadJson = await guest.ExecuteScriptAsync("window.__uploadExpected");
                var expectedUpload = JsonSerializer.Deserialize<BrowserUpload>(expectedUploadJson);
                if (expectedUpload is null || expectedUpload.Length != uploadLength) throw new Exception("Browser could not prepare the synthetic interruption-test file: " + expectedUploadJson);
                var expectedUploadBytes = Enumerable.Range(0, uploadLength).Select(index => unchecked((byte)(index * 31 + 17))).ToArray();
                var expectedUploadSha256 = Convert.ToHexString(SHA256.HashData(expectedUploadBytes));
                var interruptedAt = await partialReceive.Task.WaitAsync(TimeSpan.FromSeconds(45));
                Console.WriteLine($"PASS received {interruptedAt.Completed:N0} bytes before the deterministic interruption point");
                if (interruptedAt.Completed >= expectedUpload.Length) throw new Exception("The upload reached its end before the recovery test could interrupt it.");
                var hostBrowser = hostWindow?.Browser ?? throw new Exception("P2P receive host window closed before reconnect verification.");
                var simulatedIceState = JsonSerializer.Deserialize<string>(await hostBrowser.ExecuteScriptAsync("(()=>{const failedPeer=pc;Object.defineProperty(failedPeer,'connectionState',{configurable:true,get:()=> 'failed'});failedPeer.onconnectionstatechange();return failedPeer.connectionState;})()"));
                if (simulatedIceState != "failed") throw new Exception("Could not simulate an ICE failure in the WebView2 host: " + simulatedIceState);
                await WaitForJsAsync(hostBrowser, "document.querySelector('#status').textContent.includes('直接接続を確立できませんでした')", TimeSpan.FromSeconds(5));
                await WaitForJsAsync(guest, "window.__testSawDisconnect===true", TimeSpan.FromSeconds(35));
                await guest.ExecuteScriptAsync("window.__resumeTestUpload?.();window.__resumeTestUpload=null;");
                await WaitForJsAsync(hostBrowser, "ws && ws.readyState===WebSocket.OPEN && pc && pc.connectionState==='connected' && channel?.readyState==='open'", TimeSpan.FromSeconds(45));
                var completed = await received.Task.WaitAsync(TimeSpan.FromSeconds(90));
                var stored = await File.ReadAllBytesAsync(Path.Combine(completed.Detail!, "p2p-browser-upload.bin"));
                if (stored.Length != expectedUpload.Length || !Convert.ToHexString(SHA256.HashData(stored)).Equals(expectedUploadSha256, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Resumed P2P browser upload bytes differ from the source SHA-256.");
                Console.WriteLine($"PASS WebRTC invitation recovered from ICE failure at {interruptedAt.Completed:N0} bytes, reconnected both peers, and resumed {stored.Length:N0} bytes with SHA-256 verified");

                await guest.CoreWebView2.ExecuteScriptAsync("location.href='about:blank'");
                await WaitForJsAsync(hostBrowser, "channel?.readyState!=='open'", TimeSpan.FromSeconds(15));
                const string nativeFileName = "native-app-p2p-smoke.bin";
                var nativeBytes = new byte[2_048_321];
                for (var index = 0; index < nativeBytes.Length; index++) nativeBytes[index] = unchecked((byte)(index * 17 + 91));
                var nativeFilePath = Path.Combine(sendDirectory, nativeFileName);
                await File.WriteAllBytesAsync(nativeFilePath, nativeBytes);
                var nativeFiles = await TransferFiles.CollectAsync([nativeFilePath]);
                var directRouteObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var nativeProgress = new Progress<TransferProgress>(value =>
                {
                    if (value.State == "P2P直接接続") directRouteObserved.TrySetResult(true);
                });
                var nativeInvitationUri = invitationUri;
                if (publicP2p)
                {
                    var registrationCode = await InvitationCodeService.EncodeAsync(invitationUri.AbsoluteUri);
                    nativeInvitationUri = await InvitationCodeService.RestoreAndValidateAsync(registrationCode);
                    Console.WriteLine("PASS EZC1 registration code restores and validates an active public invitation URL");
                }
                else
                {
                    nativeInvitationUri = new Uri(server.PeerLink(invite, "127.0.0.1"));
                }
                await P2PInvitationSender.SendAsync(nativeInvitationUri, nativeFiles,
                    "Native EZ Converter Sender", nativeProgress, CancellationToken.None, window);
                await directRouteObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var nativeCompletion = await nativeAppReceived.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var nativeReceivedBytes = await File.ReadAllBytesAsync(Path.Combine(nativeCompletion.Detail!, nativeFileName));
                if (!nativeReceivedBytes.AsSpan().SequenceEqual(nativeBytes) ||
                    !Convert.ToHexString(SHA256.HashData(nativeReceivedBytes)).Equals(nativeFiles[0].File.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Native EZ Converter P2P invitation transfer did not preserve the original bytes.");
                Console.WriteLine($"PASS EZ Converter app-to-app invitation sent {nativeReceivedBytes.Length:N0} bytes over WebRTC DataChannel with direct ICE and SHA-256 verified");
                status.Text = "P2Pの送信・受信が完了しました。ウィンドウはまもなく閉じます。";
            }
            catch (Exception exception)
            {
                _exitCode = 1;
                status.Text = "P2P確認に失敗しました: " + exception.Message;
                try { if (hostWindow?.Browser is { } browser) Console.Error.WriteLine("HOST PAGE: " + await browser.ExecuteScriptAsync("document.body.innerText")); } catch { }
                try { Console.Error.WriteLine("GUEST PAGE: " + await guest.CoreWebView2.ExecuteScriptAsync("document.body.innerText")); } catch { }
                Console.Error.WriteLine(exception);
            }
            finally
            {
                await Task.Delay(800);
                queuedGuestWindow?.Close();
                hostWindow?.Close();
                if (publicTunnel is not null) await publicTunnel.DisposeAsync();
                await server.DisposeAsync();
                window.Close();
            }
        };
        app.Run(window);
        CleanupSuccessfulWorkDirectory(root, "sharing-p2p-browser");
        return _exitCode;
    }

    private static int RunPublicManual(string sourcePath)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine("合成テストファイルが見つかりません: " + sourcePath);
            return 2;
        }

        _exitCode = 0;
        var cloudflareTunnel = new CloudflareTunnel();
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var status = new TextBlock
        {
            Text = "LocalSend互換の一時共有を準備しています...",
            Foreground = Brushes.White,
            FontSize = 18,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18)
        };
        var shareUrl = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56, Margin = new Thickness(0, 0, 0, 12) };
        var passwordText = new TextBlock { Foreground = Brushes.White, FontSize = 16, Margin = new Thickness(0, 0, 0, 12) };
        var stopButton = new Button { Content = "共有を停止して閉じる", Padding = new Thickness(14, 8, 14, 8), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false };
        var panel = new StackPanel { Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(status);
        panel.Children.Add(new TextBlock { Text = "一時共有URL", Foreground = Brushes.LightGray, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(shareUrl);
        panel.Children.Add(passwordText);
        panel.Children.Add(new TextBlock
        {
            Text = "合成ファイル1件のみ · 6桁PIN · 送信側PCから直接配信 · 10分で自動停止。受信後は「共有を停止して閉じる」を押してください。",
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18)
        });
        panel.Children.Add(stopButton);
        var window = new Window
        {
            Title = "EZ Converter · LocalSend互換の一時共有テスト",
            Width = 760,
            Height = 390,
            MinWidth = 560,
            Content = panel,
            Background = new SolidColorBrush(Color.FromRgb(24, 30, 39)),
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        app.MainWindow = window;

        LocalSendDownloadServer? shareServer = null;
        var stopping = false;
        var allowClose = false;
        DateTimeOffset? expiresAt = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        async Task StopAsync()
        {
            if (stopping) return;
            stopping = true;
            timer.Stop();
            stopButton.IsEnabled = false;
            status.Text = "送信側PCの共有サーバーとCloudflare Tunnelを停止しています...";
            if (shareServer is not null) await shareServer.DisposeAsync();
            await cloudflareTunnel.StopAsync();
            Console.WriteLine("PUBLIC_SHARE_STOPPED");
            allowClose = true;
            window.Close();
        }

        window.Closing += (_, e) =>
        {
            if (allowClose) return;
            e.Cancel = true;
            _ = StopAsync();
        };
        stopButton.Click += async (_, _) => await StopAsync();
        timer.Tick += async (_, _) =>
        {
            if (expiresAt is not { } deadline) return;
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) await StopAsync();
            else status.Text = $"公開テスト中 · 残り {Math.Ceiling(remaining.TotalMinutes):0} 分 · 送信元PCとこの画面を開いたままにしてください。";
        };
        window.Loaded += async (_, _) =>
        {
            try
            {
                var files = await TransferFiles.CollectAsync([sourcePath]);
                if (files.Count != 1) throw new InvalidDataException("テストファイルは1件に限ります。");
                status.Text = "送信側PCのLocalSend互換サーバーを起動しています...";
                var password = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
                shareServer = await LocalSendDownloadServer.StartAsync("EZ Converter LocalSend Smoke Test", files, password, TimeSpan.FromMinutes(10));
                status.Text = "Cloudflare Tunnelに接続しています...";
                var origin = await cloudflareTunnel.StartAsync(shareServer.Port);
                var url = new Uri(origin, "/");
                shareUrl.Text = url.AbsoluteUri;
                passwordText.Text = "テスト用PIN: " + password;
                expiresAt = shareServer.ExpiresAt;
                stopButton.IsEnabled = true;
                Console.WriteLine("PUBLIC_SHARE=" + url.AbsoluteUri);
                Console.WriteLine("PUBLIC_SHARE_PIN=" + password);
                Console.WriteLine("PUBLIC_SHARE_SHA256=" + files[0].File.Sha256);
                Console.WriteLine("PUBLIC_SHARE_EXPIRES=" + expiresAt.Value.ToLocalTime().ToString("O"));
                status.Text = "公開URLを発行しました。LocalSend v2のprepare-download / download APIで、このPCから直接配信します。";
                timer.Start();
            }
            catch (Exception exception)
            {
                _exitCode = 1;
                status.Text = "公開テストを開始できませんでした: " + exception.Message;
                Console.Error.WriteLine(exception);
                await StopAsync();
            }
        };

        app.Run(window);
        return _exitCode;
    }

    private static int RunPublicP2PManual()
    {
        const int testLength = 1024 * 1024;
        const string testName = "ez-p2p-smoke-test.bin";
        var expectedBytes = new byte[testLength];
        for (var index = 0; index < expectedBytes.Length; index++)
            expectedBytes[index] = unchecked((byte)(index * 31 + 17));
        var expectedHash = Convert.ToHexString(SHA256.HashData(expectedBytes));
        var root = Path.Combine(Directory.GetCurrentDirectory(), "work", "sharing-public-p2p-manual", Guid.NewGuid().ToString("N"));
        var receiveDirectory = Path.Combine(root, "receive");
        Directory.CreateDirectory(receiveDirectory);

        var server = new TransferServer(new()
        {
            DeviceName = "EZ Converter P2P実機テスト",
            ReceiveDirectory = receiveDirectory,
            StateDirectory = Path.Combine(root, "state"),
            BindAddress = IPAddress.Loopback,
            MaxReceiveBytes = testLength,
            OfferLifetime = TimeSpan.FromMinutes(10)
        });
        var tunnel = new CloudflareTunnel();
        tunnel.Status += message => Console.Error.WriteLine("TUNNEL_STATUS: " + message);
        tunnel.Diagnostic += message => Console.Error.WriteLine("TUNNEL: " + Regex.Replace(message,
            @"https://[a-z0-9-]+\.trycloudflare\.com(?:/[^\s]*)?", "[一時URL]"));
        var status = new TextBlock
        {
            Text = "合成テスト用の一時受信を準備しています...",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        };
        var urlBox = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56, Margin = new Thickness(0, 0, 0, 12) };
        var qr = new Image { Width = 270, Height = 270, Stretch = System.Windows.Media.Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        var stopButton = new Button { Content = "招待URLを停止して終了", IsEnabled = false, Padding = new Thickness(16, 9, 16, 9), HorizontalAlignment = HorizontalAlignment.Right };
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = "別回線からのP2P受信テスト", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8) });
        content.Children.Add(status);
        content.Children.Add(qr);
        content.Children.Add(new TextBlock { Text = "別PC／スマートフォンでQRを読み取り、合成ファイル ez-p2p-smoke-test.bin（1 MiB）だけを送信してください。ファイル名・サイズ・内容のSHA-256が一致する1件以外は受け取りません。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(0, 12, 0, 8) });
        content.Children.Add(urlBox);
        content.Children.Add(stopButton);
        var window = new Window
        {
            Title = "EZ Converter · 別回線P2Pテスト",
            Width = 760,
            Height = 700,
            MinWidth = 620,
            Content = content,
            Background = new SolidColorBrush(Color.FromRgb(24, 30, 39)),
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.MainWindow = window;

        LinkInfo? invitation = null;
        PeerSessionWindow? hostWindow = null;
        DateTimeOffset expiresAt = DateTimeOffset.MaxValue;
        Task? cleanupTask = null;
        var allowClose = false;
        var accepted = 0;
        var completed = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        BitmapImage LoadQr(byte[] png)
        {
            using var stream = new MemoryStream(png);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        async Task StopResourcesCoreAsync()
        {
            timer.Stop();
            stopButton.IsEnabled = false;
            hostWindow?.Close();
            if (invitation is not null) server.RevokeLink(invitation.Token);
            await tunnel.StopAsync();
            await server.DisposeAsync();
        }

        Task StopResourcesAsync() => cleanupTask ??= StopResourcesCoreAsync();

        async Task StopAndCloseAsync()
        {
            try { await StopResourcesAsync(); }
            catch (Exception error) { _exitCode = 1; status.Text = "一時サーバーの停止中にエラーが発生しました: " + error.Message; }
            allowClose = true;
            window.Close();
        }

        async Task VerifyReceivedAsync(string outputDirectory)
        {
            try
            {
                var outputPath = Path.Combine(outputDirectory, testName);
                await using var stream = File.OpenRead(outputPath);
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
                var verified = FileInfoHasExpectedLength(outputPath, testLength) && actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
                await StopResourcesAsync();
                if (!verified)
                {
                    _exitCode = 1;
                    status.Text = "P2P受信後のサイズまたはSHA-256が一致しません。";
                }
                else
                {
                    status.Text = "成功：別端末からP2Pで直接受信し、SHA-256も一致しました。\n受信先: " + outputPath + "\n一時招待URLとTunnelは停止済みです。";
                    Console.WriteLine("PASS manual cross-device P2P upload and SHA-256 verification");
                }
                qr.Visibility = Visibility.Collapsed;
                urlBox.Clear();
                stopButton.Content = "閉じる";
                stopButton.IsEnabled = true;
            }
            catch (Exception error)
            {
                _exitCode = 1;
                status.Text = "受信後の検証に失敗しました: " + error.Message;
                await StopResourcesAsync();
                qr.Visibility = Visibility.Collapsed;
                urlBox.Clear();
                stopButton.Content = "閉じる";
                stopButton.IsEnabled = true;
            }
        }

        server.Incoming += offer =>
        {
            var valid = offer.Files.Count == 1 && offer.Files[0].RelativePath == testName &&
                offer.Files[0].Length == testLength && offer.Files[0].Sha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
            if (!valid || Interlocked.CompareExchange(ref accepted, 1, 0) != 0)
            {
                offer.Reject();
                window.Dispatcher.BeginInvoke(new Action(() => status.Text = "指定された合成ファイル以外は拒否しました。正しいテスト用ファイルを送ってください。"));
                return;
            }
            offer.Accept();
            window.Dispatcher.BeginInvoke(new Action(() => status.Text = "受信を許可しました。1 MiBをP2P転送中です..."));
        };
        server.Progress += progress =>
        {
            if (progress.Direction != "受信") return;
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (progress.State == "受信中") status.Text = $"P2P受信中: {progress.Completed:N0} / {progress.Total:N0} bytes";
                if (progress.State == "完了" && progress.Detail is { } output && Interlocked.Exchange(ref completed, 1) == 0)
                    _ = VerifyReceivedAsync(output);
                else if (progress.State is "エラー" or "キャンセル" or "拒否") status.Text = "転送状態: " + progress.State + (progress.Detail is null ? string.Empty : " · " + progress.Detail);
            }));
        };

        stopButton.Click += (_, _) => _ = StopAndCloseAsync();
        window.Closing += (_, eventArgs) =>
        {
            if (allowClose) return;
            eventArgs.Cancel = true;
            _ = StopAndCloseAsync();
        };
        timer.Tick += (_, _) =>
        {
            if (DateTimeOffset.UtcNow < expiresAt) return;
            _exitCode = 2;
            Console.WriteLine("PUBLIC_P2P_MANUAL_EXPIRED");
            status.Text = "テスト招待の10分間の有効期限が切れました。停止しています...";
            _ = StopAndCloseAsync();
        };
        window.Loaded += async (_, _) =>
        {
            try
            {
                await server.StartAsync();
                invitation = server.CreateInvitation(TimeSpan.FromMinutes(10));
                hostWindow = await OpenHostWindowAsync(server.HostPage(invitation), window);
                var publicOrigin = await tunnel.StartAsync(server.SignalPort);
                var invitationUrl = new Uri(publicOrigin, "/i/" + invitation.Token).AbsoluteUri;
                urlBox.Text = invitationUrl;
                qr.Source = LoadQr(ShareQrCode.CreatePng(invitationUrl));
                expiresAt = invitation.ExpiresAt;
                stopButton.IsEnabled = true;
                status.Text = "10分間だけ有効です。受信側はテスト用の1 MiBファイルだけ受け付けます。";
                timer.Start();
                Console.WriteLine("PUBLIC_P2P_MANUAL_URL=" + invitationUrl);
            }
            catch (Exception error)
            {
                _exitCode = 1;
                status.Text = "一時P2P招待を開始できませんでした: " + error.Message;
                Console.Error.WriteLine(error);
                await StopAndCloseAsync();
            }
        };

        app.Run(window);
        CleanupSuccessfulWorkDirectory(root, "sharing-public-p2p-manual");
        return _exitCode;

        static bool FileInfoHasExpectedLength(string path, long length) => new FileInfo(path).Length == length;
    }

    private static int RunPublicP2PManualShare()
    {
        const int testLength = 1024 * 1024;
        const string testName = "ez-p2p-share-smoke-test.bin";
        var root = Path.Combine(Path.GetTempPath(), "EZConverter-P2P-Share-" + Guid.NewGuid().ToString("N"));
        var sendDirectory = Path.Combine(root, "send");
        Directory.CreateDirectory(sendDirectory);
        var sourcePath = Path.Combine(sendDirectory, testName);
        var bytes = new byte[testLength];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = unchecked((byte)(index * 31 + 17));
        File.WriteAllBytes(sourcePath, bytes);

        var server = new TransferServer(new()
        {
            DeviceName = "EZ Converter P2P共有テスト",
            ReceiveDirectory = Path.Combine(root, "receive"),
            StateDirectory = Path.Combine(root, "state"),
            BindAddress = IPAddress.Loopback
        });
        var tunnel = new CloudflareTunnel();
        tunnel.Status += message => Console.Error.WriteLine("TUNNEL_STATUS: " + message);
        tunnel.Diagnostic += message => Console.Error.WriteLine("TUNNEL: " + Regex.Replace(message,
            @"https://[a-z0-9-]+\.trycloudflare\.com(?:/[^\s]*)?", "[一時URL]"));

        var status = new TextBlock
        {
            Text = "合成ファイルの一時共有を準備しています...",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 10)
        };
        var qr = new Image { Width = 230, Height = 230, Stretch = System.Windows.Media.Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        var urlBox = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 52, Margin = new Thickness(0, 4, 0, 8) };
        var pinText = new TextBlock { Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 8) };
        var hashText = new TextBlock { Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 10) };
        var stopButton = new Button { Content = "共有を停止", IsEnabled = false, Padding = new Thickness(16, 9, 16, 9), HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "別回線へP2P共有するテスト", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(status);
        panel.Children.Add(qr);
        panel.Children.Add(new TextBlock
        {
            Text = $"テスト用の合成ファイル {testName}（1 MiB）だけを共有します。この画面に表示された6桁PINを受信側で入力してください。ファイル本体は端末間のP2P直接通信で送られ、Cloudflare Tunnelは接続案内だけに使います。10分後または停止ボタンで終了します。",
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 8)
        });
        panel.Children.Add(new TextBlock { Text = "共有URL", Foreground = Brushes.LightGray });
        panel.Children.Add(urlBox);
        panel.Children.Add(pinText);
        panel.Children.Add(hashText);
        panel.Children.Add(stopButton);
        var window = new Window
        {
            Title = "EZ Converter · 一時P2P共有テスト",
            Width = 740,
            Height = 760,
            MinWidth = 600,
            Content = panel,
            Background = new SolidColorBrush(Color.FromRgb(24, 30, 39)),
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.MainWindow = window;

        LinkInfo? share = null;
        PeerSessionWindow? hostWindow = null;
        DateTimeOffset expiresAt = DateTimeOffset.MaxValue;
        Task? cleanupTask = null;
        var allowClose = false;
        var checkingHost = 0;
        var finished = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        BitmapImage LoadQr(byte[] png)
        {
            using var stream = new MemoryStream(png);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        void RemoveGeneratedTestFiles()
        {
            try
            {
                var fullRoot = Path.GetFullPath(root);
                var tempPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (fullRoot.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(fullRoot).StartsWith("EZConverter-P2P-Share-", StringComparison.Ordinal))
                    Directory.Delete(fullRoot, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("テスト用一時ファイルを削除できませんでした: " + error.Message);
            }
        }

        async Task StopResourcesCoreAsync()
        {
            timer.Stop();
            stopButton.IsEnabled = false;
            hostWindow?.Close();
            hostWindow = null;
            if (share is not null) server.RevokeLink(share.Token);
            try { await tunnel.StopAsync(); }
            finally
            {
                await server.DisposeAsync();
                RemoveGeneratedTestFiles();
            }
        }

        Task StopResourcesAsync() => cleanupTask ??= StopResourcesCoreAsync();

        void HideConnectionDetails()
        {
            qr.Visibility = Visibility.Collapsed;
            urlBox.Clear();
            pinText.Text = string.Empty;
            allowClose = true;
            stopButton.Content = "閉じる";
            stopButton.IsEnabled = true;
        }

        async Task FinishAfterTransferAsync()
        {
            if (Interlocked.Exchange(ref finished, 1) != 0) return;
            try
            {
                await StopResourcesAsync();
                status.Text = "成功：受信側（ブラウザーまたはEZ Converter）がP2P転送とSHA-256検証を完了しました。共有URL・一時サーバー・Tunnelは停止済みです。\n期待SHA-256: " + hashText.Text;
                Console.WriteLine("PASS manual cross-device P2P share; receiver reported verified file-saved");
            }
            catch (Exception error)
            {
                _exitCode = 1;
                status.Text = "転送後の一時サーバー停止でエラーが発生しました: " + error.Message;
            }
            HideConnectionDetails();
        }

        async Task StopAndCloseAsync()
        {
            Interlocked.Exchange(ref finished, 1);
            try { await StopResourcesAsync(); }
            catch (Exception error) { _exitCode = 1; status.Text = "一時サーバーの停止中にエラーが発生しました: " + error.Message; }
            allowClose = true;
            window.Close();
        }

        stopButton.Click += (_, _) =>
        {
            if (allowClose) window.Close();
            else _ = StopAndCloseAsync();
        };
        window.Closing += (_, eventArgs) =>
        {
            if (allowClose) return;
            eventArgs.Cancel = true;
            _ = StopAndCloseAsync();
        };
        timer.Tick += async (_, _) =>
        {
            if (DateTimeOffset.UtcNow >= expiresAt)
            {
                if (Interlocked.Exchange(ref finished, 1) != 0) return;
                _exitCode = 2;
                try { await StopResourcesAsync(); }
                catch (Exception error) { status.Text = "期限切れ後の停止でエラーが発生しました: " + error.Message; }
                if (_exitCode == 2) status.Text = "10分の有効期限が切れたため、共有URLと一時サーバーを停止しました。";
                HideConnectionDetails();
                return;
            }

            if (hostWindow?.Browser is not { } browser || Interlocked.Exchange(ref checkingHost, 1) != 0) return;
            try
            {
                var result = await browser.ExecuteScriptAsync("document.querySelector('#status')?.textContent || ''");
                var hostStatus = JsonSerializer.Deserialize<string>(result) ?? string.Empty;
                if (hostStatus.StartsWith("送信完了: " + testName, StringComparison.Ordinal))
                    await FinishAfterTransferAsync();
                else if (hostStatus.Length > 0 && !hostStatus.StartsWith("相手がURLを開く", StringComparison.Ordinal))
                    status.Text = $"共有中 · 残り {Math.Ceiling((expiresAt - DateTimeOffset.UtcNow).TotalMinutes):0} 分\n{hostStatus}";
                else
                    status.Text = $"受信側の接続待ち · 残り {Math.Ceiling((expiresAt - DateTimeOffset.UtcNow).TotalMinutes):0} 分";
            }
            catch (Exception error) when (error is InvalidOperationException or JsonException)
            {
                status.Text = "P2P接続画面を確認できません。必要なら停止ボタンで終了してください。";
            }
            finally { Volatile.Write(ref checkingHost, 0); }
        };
        window.Loaded += async (_, _) =>
        {
            try
            {
                await server.StartAsync();
                var files = await TransferFiles.CollectAsync([sourcePath]);
                if (files.Count != 1 || files[0].File.Length != testLength || files[0].File.RelativePath != testName)
                    throw new InvalidDataException("テスト用ファイルの内容を準備できませんでした。");
                var pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
                share = server.CreateShare(files, TimeSpan.FromMinutes(10), pin);
                hostWindow = await OpenHostWindowAsync(server.HostPage(share), window);
                status.Text = "Cloudflare Tunnelは接続案内専用です。URLを発行しています...";
                var publicOrigin = await tunnel.StartAsync(server.SignalPort);
                var shareUrl = new Uri(publicOrigin, "/s/" + share.Token).AbsoluteUri;
                urlBox.Text = shareUrl;
                qr.Source = LoadQr(ShareQrCode.CreatePng(shareUrl));
                pinText.Text = "受信側へ伝える6桁PIN: " + pin;
                hashText.Text = files[0].File.Sha256;
                expiresAt = share.ExpiresAt;
                stopButton.IsEnabled = true;
                status.Text = "一時共有を開始しました。受信側でURLを開き、PINを入力してください。";
                Console.WriteLine("PUBLIC_P2P_MANUAL_SHARE_URL=" + shareUrl);
                Console.WriteLine("PUBLIC_P2P_MANUAL_SHARE_PIN=" + pin);
                Console.WriteLine("PUBLIC_P2P_MANUAL_SHARE_SHA256=" + files[0].File.Sha256);
                timer.Start();
            }
            catch (Exception error)
            {
                _exitCode = 1;
                status.Text = "一時P2P共有を開始できませんでした: " + error.Message;
                Console.Error.WriteLine(error);
                try { await StopResourcesAsync(); }
                catch (Exception stopError) { Console.Error.WriteLine("停止中のエラー: " + stopError.Message); }
                HideConnectionDetails();
            }
        };

        app.Run(window);
        return _exitCode;
    }

    private static async Task<PeerSessionWindow> OpenHostWindowAsync(Uri page, Window owner)
    {
        var window = new PeerSessionWindow(page, "EZ Converter · P2P接続確認") { Owner = owner };
        window.Show();
        await window.Ready.WaitAsync(TimeSpan.FromSeconds(20));
        if (window.InitializationError is not null) throw new InvalidOperationException(window.InitializationError.Message, window.InitializationError);
        return window;
    }

    private static async Task WaitForJsAsync(WebView2 view, string condition, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            try { if (await view.ExecuteScriptAsync(condition) == "true") return; }
            catch (InvalidOperationException) { }
            await Task.Delay(100);
        }
        var state = await view.ExecuteScriptAsync("JSON.stringify({message:document.querySelector('#message')?.textContent||document.readyState,blobFallback:{debug:window.__fallbackDebug,error:window.__fallbackError,clicks:window.__fallbackClickCount,blob:window.__fallbackBlob?.size,download:window.__fallbackDownload}})");
        throw new TimeoutException("Browser condition timed out: " + condition + " ; state=" + state);
    }

    private static void CleanupSuccessfulWorkDirectory(string path, string category)
    {
        if (_exitCode != 0) return;

        var expectedParent = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "work", category));
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(fullPath), expectedParent, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(Path.GetFileName(fullPath), out _))
        {
            Console.Error.WriteLine("Successful test cleanup refused an unexpected path: " + fullPath);
            return;
        }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt == 39)
                    Console.Error.WriteLine($"Successful test artifacts were retained at '{fullPath}' because cleanup failed: {error.Message}");
                else Thread.Sleep(250);
            }
        }
    }

    private static async Task WaitForJsAsync(CoreWebView2 view, string condition, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            try { if (await view.ExecuteScriptAsync(condition) == "true") return; }
            catch (InvalidOperationException) { }
            await Task.Delay(100);
        }
        var state = await view.ExecuteScriptAsync("document.querySelector('#status')?.textContent||document.readyState");
        throw new TimeoutException("Browser condition timed out: " + condition + " ; state=" + state);
    }

    private static async Task VerifySelectedCandidatePairAsync(WebView2 view)
    {
        await view.ExecuteScriptAsync("window.__ezSelectedCandidatePair=undefined;(async()=>{try{for(let attempt=0;attempt<80;attempt++){const stats=await pc.getStats();let pair;for(const item of stats.values())if(item.type==='transport'&&item.selectedCandidatePairId)pair=stats.get(item.selectedCandidatePairId);if(pair){const local=stats.get(pair.localCandidateId),remote=stats.get(pair.remoteCandidateId);if(local?.candidateType&&remote?.candidateType){window.__ezSelectedCandidatePair={Local:local.candidateType,Remote:remote.candidateType,RelayConfigured:window.EZAllowRelay};return;}}await new Promise(resolve=>setTimeout(resolve,250));}window.__ezSelectedCandidatePair={Error:'No selected ICE candidate pair became available.'};}catch(error){window.__ezSelectedCandidatePair={Error:String(error)};}})();");
        await WaitForJsAsync(view, "window.__ezSelectedCandidatePair!==undefined", TimeSpan.FromSeconds(25));
        var serialized = JsonSerializer.Deserialize<string>(await view.ExecuteScriptAsync("JSON.stringify(window.__ezSelectedCandidatePair)"))
            ?? throw new InvalidDataException("The selected WebRTC route was empty.");
        using var result = JsonDocument.Parse(serialized);
        var route = result.RootElement;
        if (route.TryGetProperty("Error", out var error)) throw new InvalidOperationException("Could not inspect the selected WebRTC route: " + error.GetString());
        var localType = route.GetProperty("Local").GetString();
        var remoteType = route.GetProperty("Remote").GetString();
        var relayConfigured = route.GetProperty("RelayConfigured").GetBoolean();
        if (relayConfigured || localType == "relay" || remoteType == "relay")
            throw new InvalidOperationException($"The selected WebRTC route is not direct: local={localType}, remote={remoteType}, relayConfigured={relayConfigured}.");
    }
}
