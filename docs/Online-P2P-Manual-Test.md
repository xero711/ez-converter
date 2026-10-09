# URL共有・別回線P2Pの手動テスト

この手順では、同一LANのURL共有でLocalSend Protocol v2.2を、別回線のURL共有でWebRTCのP2P直接転送を確認します。どちらも個人データではない合成ファイルを使ってください。

同一LANの共有URLは送信側PCの一時HTTPサーバーから配信します。別回線のP2P共有URLではCloudflare Tunnelはページと接続案内だけを届け、Google／CloudflareのSTUNは接続候補を調べます（各STUNサービスに外部IPアドレスが見える場合があります）。ファイル本体はWebRTC DataChannelで端末間を直接転送し、STUNやトンネルを通しません。常設EZ Converterサーバーや第三者のファイル保存先は使いません。直接接続できない場合、ファイルを中継せず失敗します。

固定URLで確認する場合は、[CloudflareのRemote Tunnel作成手順](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/get-started/create-remote-tunnel-api/)またはDashboardのNetworking > Tunnelsから遠隔管理トンネルを作成し、[公開アプリケーションルート](https://developers.cloudflare.com/tunnel/concepts/routing/)のService URLを `http://127.0.0.1:53318` に設定します。EZ Converterの「設定」でNamed Tunnelを有効にし、公開ホスト名と実行トークンを入力して保存し、共有画面の受信を停止して再開してください。ポートを変える場合はCloudflare側とアプリ側を一致させます。トークンはCloudflareのトンネル実行用トークンであり、アカウントAPIトークンを入力しないでください。トークンはこのPCの設定欄へ直接貼り付け、チャットやソースコードへ送らないでください。

Named Tunnelでは、同じ固定ホスト名を使う限り、受信側アプリを再起動して受け取り招待を作り直しても同じEZC1登録コードを利用できます。Quick Tunnelはホスト名と登録コードが一時的です。受信PCはどちらの場合もオンラインで招待を起動している必要があります。コードを無効にする場合はURL共有画面の「固定ホスト名の登録コードを更新」を使い、登録済みの相手には更新後のコードを伝えます。この項目は新機能を含むビルドで確認してください。

## 1. 合成ファイルを作る

送信側PCのPowerShellで、個人データではない1 MiBの合成ファイルを作成します。

```powershell
$testDir = Join-Path $env:TEMP ("EZConverter-LocalSend-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Path $testDir | Out-Null
$testFile = Join-Path $testDir "localsend-smoke-test.bin"
$bytes = New-Object byte[] 1048576
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $rng.GetBytes($bytes)
    [System.IO.File]::WriteAllBytes($testFile, $bytes)
} finally {
    $rng.Dispose()
}
Get-FileHash -Algorithm SHA256 -LiteralPath $testFile
```

ハッシュ値は受信後の照合に使います。

## 2. 同じネットワークで直接共有

1. EZ Converterの「送る・受け取る」から「URL共有」を開き、「ファイルを追加」または「フォルダーを追加」で合成ファイルを選びます。
2. 有効期限を「10分」にし、PIN欄に6桁の数字を入力します。
3. 「インターネットの相手と共有」はオフのまま、「選択したファイルの共有URLを作る」を押します。
4. 送信側PCと同じLANにつながった別PCまたはスマートフォンでURLを開き、別途伝えたPINを入力します。
5. 個別にダウンロードするか、「すべてをまとめてダウンロード（ZIP・フォルダー構造を保持）」でまとめて受け取ります。ZIPの場合は展開後の合成ファイルのSHA-256が手順1の値と一致することを確認します。

ブラウザーにはLocalSend互換ページが表示されます。ページは `POST /api/localsend/v2/prepare-download` で一覧・セッションIDを取得し、選択したファイルを `GET /api/localsend/v2/download` で受信します。

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath "<受信したファイルのフルパス>"
```

## 3. 別回線へP2P共有URLを送る

実機確認を始める前に、任意ファイルを使わない安全な送信側テストを実行できます。送信側PCで次を起動すると、アプリが固定内容の1 MiB合成ファイル1件だけを作成し、6桁PIN付きの共有URLを10分間だけ発行します。別回線のブラウザーまたはEZ Converterで受信を完了すると、共有URL・送信側サーバー・Cloudflare Tunnelは自動停止します。停止ボタンやウィンドウ終了でも止められます。

```powershell
dotnet run --project .\tests\Sharing.P2PBrowser\Sharing.P2PBrowser.csproj -c Release -- --public-p2p-manual-share
```

別回線の実機テストは後日で構いません。ブラウザーで試す場合は、送信側テスト画面のURLと6桁PINを別PC／スマートフォンへ伝えます。受信側にもEZ Converterがある場合は、「送る・受け取る」画面の「共有URLから受け取る」にURLを貼って「受け取る」を押し、開いたP2P画面で送信側テスト画面に表示されたPINを入力してファイルの「保存」を押します。どちらの場合も、受信後のSHA-256が送信側テスト画面に表示された値と一致すれば合格です。受信完了後は共有URL・サーバー・Tunnelが自動停止します。このテストでもCloudflare Tunnelは接続案内だけに使い、ファイル本体はWebRTC DataChannelで端末間を直接転送します。

通常のアプリ画面から実ファイルを共有する場合は、まず同じ合成ファイルで上記のLANテストを行ってから、以下の手順へ進みます。
2. EZ Converterの「URL共有」で「有効期限・パスワードなど」を開き、「インターネットの相手とP2P共有（Cloudflare Tunnelは接続案内のみ）」をオンにします。必要ならPINを設定し、「選択したファイルの共有URLを作る」を押します。
3. 別回線のPCまたはスマートフォンでURLを開き、設定したPINを入力します。受信ページがP2P接続した後、対象ファイルの「保存」を押します。
4. 保存したファイルのSHA-256が手順1の値と一致することを確認します。
5. 試験が終わったら送信側でそのURLの「停止」を押します。

複数ファイルを共有した場合は、「すべてをZIPにまとめて保存」も表示されます。ZIPは受信側ブラウザーで作成し、各ファイルのSHA-256検証に成功したものだけを含めます。保存先フォルダー選択APIがないブラウザーでも一括受信できます。

Cloudflareの接続開始に失敗した場合、ネットワークで外向きTCP/UDP 7844番ポートが許可されているか確認してください。Cloudflare Tunnelは共有ページとWebRTCの接続案内だけを運び、ファイル本体は通しません。公開URLはテスト相手だけに渡し、個人ファイルは使わないでください。

## 4. 別回線から受信側PCへP2P送信

この確認では、別回線のPCまたはスマートフォンのブラウザーから、受信側PCへ合成ファイルを送ります。受信側PCが一時シグナリングサーバーを起動し、Cloudflare Tunnelは招待URLと接続情報を届けるためだけに使います。Google／CloudflareのSTUNには外部IPアドレスが見える場合がありますが、ファイル本体はWebRTC DataChannelで端末間を直接転送します。直接接続できない場合、既定では中継へ切り替えず失敗します。

1. 受信側PCでこのリポジトリのフォルダーから、次のテストホストを起動します。公開招待は10分で自動停止し、条件に合う合成ファイル1件だけを受け付けます。

```powershell
dotnet run --project .\tests\Sharing.P2PBrowser\Sharing.P2PBrowser.csproj -c Release -- --public-p2p-manual
```

2. 表示されたQRコードを別回線のPC／スマートフォンで読み取るか、招待URLをテスト相手に渡します。受信側PCの停止ボタン、ウィンドウ終了、10分経過で招待と一時サーバーを停止します。
3. 送信側の別PCで、次のPowerShellを実行します。作成したファイルは既知の1 MiB合成データです。

```powershell
$testDirectory = Join-Path $env:TEMP ("EZConverter-P2P-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$testFile = Join-Path $testDirectory "ez-p2p-smoke-test.bin"
$bytes = New-Object byte[] 1048576
for ($index = 0; $index -lt $bytes.Length; $index++) {
    $bytes[$index] = [byte](($index * 31 + 17) -band 255)
}
[System.IO.File]::WriteAllBytes($testFile, $bytes)
Get-FileHash -Algorithm SHA256 -LiteralPath $testFile
```

4. 招待ページで `ez-p2p-smoke-test.bin` を選び、「送信する」を押します。テストホストは正確なファイル名・容量・内容ハッシュを照合し、合成データ1件以外は拒否します。
5. 受信側PCにP2P受信成功とSHA-256一致が表示されれば合格です。成功時は公開URLとトンネルを自動停止します。

EZ Converterアプリ同士の送信を確認する場合は、上記で作った同じ合成ファイルを送信側PCのEZ Converterで選び、「送る」画面の接続URL欄に招待URLを貼って送信します。送信画面は受信側の許可後にWebRTC DataChannelで直接転送し、公開HTTP経由のファイル送信には切り替えません。受信ホストがファイル名・容量・SHA-256を検証し、完了後に一時共有を停止します。

受信側の画面に成功とSHA-256一致が表示されれば合格です。接続できない場合は、表示されるICE診断を記録してください。TURNなど別のファイル中継サーバーは自動で使いません。外向きの通信が制限されたネットワークでは、直接接続できないことがあります。

## 5. 停止・期限切れの確認

- 共有一覧の「停止」を押すと、そのリンクと対応する一時サーバー／Cloudflare Tunnelが停止します。
- 設定した期限を過ぎると、該当リンクと一時サーバーが自動停止します。
- アプリ終了またはPC電源断でも送信側サーバーは停止します。再起動後に以前のURLは自動復元されません。Named Tunnelなら同じ受け取り招待を再作成して、以前登録したEZC1コードが再び使えることを確認します。Quick Tunnelは新コードを再登録します。
- 複数ファイルは個別ダウンロードと、フォルダー構造を保持した一括ZIPのどちらでも受け取れます。一括ZIPはLocalSend標準APIを変更しない独自の追加機能です。

## 合格条件と注意

- 同一LANではCloudflareを使わずにブラウザー共有できる。
- APIの`prepare-download`応答がLocalSend v2形式で、間違ったPINが拒否される。
- `download`から取得したデータと送信元ファイルのSHA-256が一致する。
- 停止または期限切れ後に送信側の一時HTTPリスナーが終了する。
- Cloudflare経由のP2P試験も合成ファイルだけを使い、テストURL・PIN・QRをテスト相手以外へ渡さない。
- Named Tunnelでは同じ公開ホスト名のまま、受信停止・再開とアプリ再起動後に以前登録した同じ招待コードが動作することを確認する。登録コード更新後は古いコードが無効になり、新コードが使えることを確認する。CloudflareのDNS・Access設定とローカルポートの不一致はヘルスチェックで失敗します。
