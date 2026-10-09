# Media Converter

Windows 11向けのWPF製ローカルファイルコンバーターです。Convertioの公開形式一覧にあるカテゴリを参考に、画像・ベクター・動画・音声に加えて、文書、プレゼンテーション、アーカイブ、電子書籍、フォントまで扱えるようにしています。Convertioのサイト自体は300以上の形式と多数の変換パターンを案内していますが、本アプリはクラウドサービスではなく、Windowsにインストールされた変換エンジンを使ってローカル変換します。

## 対応カテゴリとエンジン

- 画像・RAW・ベクター: AVIF、BMP、DDS、EXR、GIF、HDR、HEIC/HEIF、ICO、JPEG/JPG、JPEG 2000、PNG、PSD、SGI、TIFF、TGA、WebP、XBM、SVG、EPS、AIなど
- 動画: 3G2、3GP、ASF、AVI、AV1、DIVX、FLV、HEVC、M2TS、M4V、MKV、MOV、MP4、MPEG、MTS、MXF、OGV、RM/RMVB、TS、VOB、WebM、WMVなど
- 音声: AAC、AC3、AIFF、AMR、AU、CAF、FLAC、M4A、M4R、MP2、MP3、OGA/OGG、Opus、WAV、WMA、WavPackなど
- 文書・表計算: DOC/DOCX/DOCM、DOT/DOTX、ODT、RTF、TXT、HTML、CSV、XLS/XLSX、ODS、PDFなど
- プレゼンテーション: ODP、POT/POTX、PPS/PPSX、PPT/PPTXおよびマクロ有効形式
- アーカイブ: 7Z、ZIP、TAR、GZ、TGZ、BZ2、XZ、JAR、LHA、ARJ、RAR、CAB、RPM、DEBなど（読み込み専用形式を含む）
- 電子書籍: AZW3、EPUB、FB2、LRF、MOBI、PDB、RB、SNB、TCR
- フォント: CFF、DFONT、OTF、PFB、SFD、TTF、WOFF、WOFF2など

画面の出力形式一覧には、その入力形式から実際に書き出せる形式だけを表示します。RAW画像やRARなど、読み込み専用の形式への書き出しは選択できません。動画では「AV1動画（MP4）」を選ぶと、AV1映像とAAC音声のMP4へ変換します。変換中はFFmpegの処理時間に応じた進捗を表示します。文書と表計算の形式は分けて表示し、プレゼンテーションからPDFへの変換にも対応します。PDFからXLSX/TXTへの変換はPDFの文字を抽出する専用処理を使います。

## 変換エンジンの配置

変換エンジンはカテゴリごとに分離しています。起動時と変換前に実行確認を行い、実際に必要なエンジンがない場合は理由をログに表示します。

1. 画像・ベクター: [ImageMagick](https://imagemagick.org/)
2. 動画・音声: [FFmpeg](https://ffmpeg.org/)（WindowsビルドはFFmpeg公式が案内する配布元を利用）
3. 文書・表計算・プレゼン: [LibreOffice](https://www.libreoffice.org/)
4. アーカイブ: [7-Zip](https://www.7-zip.org/)
5. 電子書籍: [Calibre](https://calibre-ebook.com/)
6. フォント: [FontForge](https://fontforge.org/)
7. 動画・音声の公開HTTP(S) URL取得: [yt-dlp](https://github.com/yt-dlp/yt-dlp)

エンジンの実行ファイルを個別に参照する操作は不要です。ImageMagick、LibreOffice、7-Zip、Calibre、FontForgeはアプリの `Tools` フォルダーへ同梱済みで、アプリが自動検出します。出力フォルダーの場所も利用者が選ぶ必要はありません。`Tools` 内のファイルはビルド／発行先へ自動コピーされます。外部ツールを別途インストール済みの場合も、同梱版を優先して使います。

yt-dlpとFFmpegはアプリ本体から分離し、`%LOCALAPPDATA%\\EZConverter\\Tools` に配置します。起動時と12時間ごとに自動確認し、機能の実行前にも必要な更新確認を行います。yt-dlpは公式Nightly APIのSHA-256とサイズ、FFmpegは配布元が公開するSHA-256を照合してから置き換えます。ネットワークに接続できない場合は、既にある実行ファイルを残して使用し、更新エラーをログに記録します。初回のツール取得だけはインターネット接続が必要ですが、利用者が配布元や実行ファイルを選ぶ操作はありません。

安定系のエンジンは `Tools` に同梱しています。頻繁な更新が必要なyt-dlpとFFmpegだけ別配置で自動更新します。各ツールの版、配布元、ライセンス資料は [Tools/THIRD-PARTY-NOTICES.md](Tools/THIRD-PARTY-NOTICES.md) を参照してください。ネットワークに接続できない初回起動では、同梱エンジンが必要なファイル変換は利用できますが、FFmpegが必要な動画・音声変換やURL取得は、FFmpegとyt-dlpが取得されるまで実行できません。

## 形式ごとの注意

ImageMagick本体が対応していても、SVG、AI、EPS、PSD、HEIC/HEIF、RAW、CDRなどはdelegate、OS側コーデック、対象形式の読み込み条件に左右されます。

PDFからXLSXへ変換すると、ページ番号と抽出した文字を行ごとに保存します。PDF内の図や画像、元のページレイアウトは再現しません。画像だけのPDFは文字を抽出できないため、変換せず理由を表示します。

LibreOfficeはOffice/OpenDocument形式を中心に変換します。レイアウト、マクロ、フォント、埋め込みオブジェクトは元アプリケーションと完全一致しない場合があります。

7-Zipによるアーカイブ変換は、一度一時フォルダに展開してから別形式へ再圧縮します。パスワード付きアーカイブ、特殊なファイル属性、シンボリックリンクなどは変換できないことがあります。RAR、CAB、DEB、RPMなどは主に読み込み用です。

CalibreとFontForgeは対象形式やバージョンにより書き出し可能な形式が異なります。外部ツールがエラーを返した場合は、一覧から形式を削除せず、画面のエラーとconversion.logを確認できるようにしています。

ma4は入力時に通常のm4aとして扱います。

## UIとテーマ

- 「ファイル」「URLから保存」「圧縮・展開」「送る・受け取る」「設定」のタブ構成です。
- 変換一覧の各行に出力形式ComboBoxを配置し、画像には画像、動画には動画など、元ファイルと同じカテゴリの書き出し可能形式だけを表示します。推奨形式を初期選択し、一覧上部の検索でファイル名・拡張子・カテゴリを絞り込めます。
- ComboBox本体とドロップダウン項目はライト/ダークそれぞれの配色を使い、選択時の文字と背景にも十分なコントラストを確保します。タブ見出しの背景は右端までつながる全幅の帯にしています。
- 複数ファイルの出力形式を個別に選べるため、異なる形式やカテゴリのファイルも一括キューで処理できます。
- Windowsの個人用設定にあるアプリのライト/ダーク設定を起動時に読み取り、配色変更イベントにも追従します。タブ、一覧ヘッダー、選択状態、無効ボタン、進捗バー、独自デザインのウィンドウフレームもテーマに合わせます。
- ウィンドウ上部にはEZロゴ、アクセントライン、専用の最小化・最大化／復元・閉じるボタンを配置しています。タイトル領域のドラッグ移動とウィンドウのサイズ変更に対応します。
- `Assets/media-converter.ico`をウィンドウとEXEアイコンに使用します。

## URLから保存

「URLから保存」タブにURLを貼り付け、映像/音声、形式、品質、保存先を選択して保存します。yt-dlpを使うため、YouTube、X（Twitter）、Discordの公開添付URLや対応サイトのURLを扱い、HTTP(S)署名付きURLのクエリも取得引数へ保持します。ユーザー側のyt-dlp設定は読み込まず、URL内の認証情報は拒否します。プレイリストは既定で取得しません。対応サイトは変わるため、公開URLでもサイト側の仕様変更後は取得できない場合があります（[yt-dlp対応サイト一覧](https://github.com/yt-dlp/yt-dlp/blob/master/supportedsites.md)）。音声抽出と映像の再エンコードにはFFmpegが必要です。

著作権、利用規約、公開範囲などを確認し、利用する権利または許可があるコンテンツだけを取得してください。Discordの認証が必要な非公開動画、期限切れ添付URL、DRM、ログイン必須のコンテンツは対象外です。DRM回避、ログイン情報の入力・保存、アクセス制限の回避は実装していません。URLに含まれる署名付きクエリはログへそのまま保存しないようにしています。

## GitHub Releasesによるアプリ更新

アプリ本体はGitHub Releasesの`MediaConverter-win-x64.zip`を更新単位にします。起動時に設定されたリポジトリの最新リリースと現在のバージョンを比較し、新しい版だけ日本語で確認を表示します。同意した場合は、ZIPとSHA-256をダウンロードして検証し、アプリとは別の`MediaConverter.UpdateAgent.exe`がアプリ終了後に更新します。実行中のEXEを上書きせず、更新前フォルダーをバックアップして、コピーに失敗した場合は復元します。

`appsettings.json`のGitHub設定はソースでは空欄ですが、`.github/workflows/release.yml`がGitHub Actions上のリポジトリ名を公開用ファイルへ設定します。リリースは`v1.0.1`のような3要素以上のバージョンタグをpushして作成します。更新を有効にするには、リリースZIP、`.sha256`、更新ヘルパーを同じリリースへ含める必要があります。

更新処理の統合テストは、GitHub API応答を模擬して新旧バージョン判定、GitHub配布URLの固定、ZIPとSHA-256／チェックサム別ファイルの検証、破損ファイルの削除を確認し、隔離した一時インストール先で更新ヘルパーの置換・バックアップ・再起動まで検証します。実行方法は`dotnet run --project .\tests\AppUpdate.Integration\AppUpdate.Integration.csproj -c Release`です。

GitHubへ配置する際は、ユーザーのGitHubアカウントでこのフォルダーをリポジトリへ登録し、公開/非公開の範囲を決めてください。公開リポジトリなら認証なしで更新確認できます。非公開リポジトリではGitHub APIの認証方式を別途追加する必要があるため、現実装は公開リポジトリを前提にしています。アクセストークンや資格情報をソースへ保存しないでください。

## P2Pファイル送受信

「送る・受け取る」タブで、LocalSend対応端末へのLAN直接送信、ブラウザー共有URL、ブラウザーからPCへ送る受信招待を使えます。ファイルとフォルダーの複数選択、受信前の許可／拒否、進捗表示、キャンセル、停止まで有効な一時リンク（任意で10分～24時間の期限も設定可能）、6桁PIN、共有リンクのローカルQR表示に対応しています。転送履歴は再起動後も最大100件を確認でき、消去もできます。履歴には表示名・方向・サイズ・状態・日時だけを保存し、元パス、共有URL、認証情報、ファイル内容は保存しません。

URL共有は「URL共有」ページでファイルまたはフォルダーを追加し、「選択したファイルの共有URLを作る」を押すだけです。通常は同じLAN内で送信側PCから直接配信し、受信側にこのアプリは不要です。インターネットの相手へ送る場合だけ、折りたたまれた詳細設定からP2P共有を有効にします。

- LAN内の通常ファイル送受信はLocalSend Protocol v2.2互換です。`224.0.0.167:53317/UDP`のマルチキャストで端末を発見し、`/api/localsend/v2/register`で相互登録します。アプリはLocalSend標準のTCP 53317番をHTTPS受信ポートに優先使用するため、マルチキャストが使えない相手の標準ポート探索にも対応します。ポートが既に使われている場合は空きポートへ退避し、マルチキャスト探索を継続します。ファイル送信は受信側の確認後に`prepare-upload`と`upload`を使い、最大4ファイルを並列送信します。フォルダーはLocalSendと同じく相対パス付きのファイル群として転送し、受信側で階層を復元してSHA-256を検証します。HTTPSでは相手の証明書フィンガープリントを固定し、送信側証明書を提示します。端末証明書はWindowsユーザーごとにDPAPI保護して保存するため、受信サーバーを再起動してもLocalSendの端末フィンガープリントを維持します。HTTPS時はJSONの自己申告フィンガープリントとの一致を必須にしません。HTTP接続はLAN内に限定します。送信先PINの入力に加え、このPCの受信にも任意の6桁PINを設定できます。受信PINはDPAPIで保護して保存し、誤入力は送信元アドレスごとに制限します。転送ファイルは中継サービスを通りません。
- IPv4とIPv6の両方で一時サーバーが待ち受け、IPv6 ULAアドレスからも手動接続できます。自動LocalSend端末検出は標準のIPv4マルチキャスト／IPv4サブネット探索に加え、LocalSendのIPv6マルチキャスト拡張も使います。IPv6はインターフェースごとに参加し、IPv4・IPv6の両方で見つかった同一端末はフィンガープリントで1台にまとめます。
- 同じLANのブラウザー共有はLocalSend Protocol v2.2のReverse File Transfer形式です。選択ファイルの共有ごとに送信側PCで一時HTTPサーバーを起動し、`POST /api/localsend/v2/prepare-download`でメタデータとセッションIDを返し、`GET /api/localsend/v2/download`で元ファイルをストリーミングします。複数階層のフォルダー名、6桁PIN、LocalSendと同じ`pin`クエリパラメーターに対応します。LocalSend v2.2の[公式プロトコル仕様](https://github.com/localsend/protocol/blob/main/README.md)に合わせています。独自の追加機能として、同じPIN認証済みセッションから全ファイルを元のフォルダー構造のままZIP一括ダウンロードできます。
- P2Pのブラウザー共有では、保存先フォルダーを選べるブラウザーは元のフォルダー階層を保って一括保存できます。それ以外のブラウザーでも、受信端末側で複数ファイルを標準ZIPにまとめて1回で保存できます。ファイル本体は各ファイルとも送信元PCからWebRTCで直接受け取り、ZIP生成は受信端末上で行います。
- 公開HTTPSの共有URLをEZ Converterの「共有URLから受け取る」に貼り付けると、WebView2のP2P受信画面で開きます。画面上で保存を選ぶと、設定した受信フォルダーへ直接書き込み、アプリ側でもサイズとSHA-256を検証してから確定します。同名ファイルは上書きしません。ファイル本体はWebRTCで直接受信し、Tunnel経由のHTTPダウンロードへ切り替えることはありません。LAN内のHTTP共有URLは従来どおり直接ダウンロードします。
- EZ Converterアプリ同士で別回線の受信招待URLへ送る場合も、WebView2上のWebRTC DataChannelでファイル本体を端末間へ直接送ります。アプリで選んだファイルは受信側の許可後に最大48 KiBずつローカルからWebView2へ読み出し、SHA-256照合を通して受信します。招待URLを汎用HTTP送信APIへ誤って渡さないよう、公開ホストへのHTTP招待アップロードは拒否します。
- インターネットの受信招待には「登録コード」を発行できます。送信側は「接続URLを使って送る」にEZC1コードを入力して保存し、後から登録済みの相手を選んで送信できます。コードは招待URLを含む持ち運び可能なベアラー情報で、共有相手はコードを知っていれば接続を試せます。連絡先ファイルはWindows DPAPIで現在のWindowsユーザーに結び付けて保護します。コード自体にアカウント登録や恒久的な宛先は含まず、受信側PCとアプリが起動し、受信招待と一時Tunnelが有効な間だけ使えます。期限切れ・停止後は新しい招待コードを登録し直してください。受信側は転送ごとにファイルを許可または拒否できます。
- P2Pに使うMicrosoft WebView2 Evergreen Runtimeのオフラインセットアップも配布物に同梱します。PCにランタイムがない場合だけ、P2P機能の初回利用時にファイルのSHA-256を照合して自動セットアップします。インストール後のランタイムはMicrosoft Edge Updateで自動更新されます。約204 MB分、配布サイズが増えます。
- インターネット共有を選んだ場合、送信側PCの一時シグナリングサーバーへCloudflare Tunnelを接続し、ブラウザー共有ページとWebRTC接続情報だけを届けます。Google／CloudflareのSTUNはNAT越え用の接続候補を調べるために使い、相手の外部IPアドレスが各サービスに見える場合がありますが、ファイル本体はWebRTC DataChannelで端末間を直接転送します。トンネルやSTUNへファイル本体を送りません。共有ページ側も接続案内が切れた際に同じURLへ自動再接続します。転送中に接続が切れても同じページを開いたままなら、保存先へ書き込み済みの位置から自動再開します。ページを閉じた場合は再開できません。ブラウザーの保存先選択に対応しない場合も、一時保存領域（OPFS）に対応していれば大きなファイルをメモリーへ蓄積せず保存できます。保存先選択・一時保存領域のどちらにも対応しないブラウザーでは、メモリー保護のため512 MiBを超えるファイルを拒否します。直接接続できない場合は自動中継せず失敗します。Cloudflare接続には外向きTCPまたはUDPの7844番ポートが必要です（[Cloudflare公式の必要ポートと方式選択](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/configure-tunnels/tunnel-with-firewall/)）。ほかのトンネル／中継サービスは使いません。

オンライン共有は初期設定ではCloudflare Quick Tunnelを使い、一時URLを発行します。固定URLを使う場合は「設定」からCloudflare Named Tunnelを有効にし、公開ホスト名、実行トークン、ローカルポートを保存します。Cloudflare Zero Trust側では公開ホスト名のサービス先を `http://127.0.0.1:<設定したポート>` に設定してください。初期ポートは53318です。トークンはWindows DPAPIで保護し、`cloudflared`のコマンドラインではなく子プロセス環境変数 `TUNNEL_TOKEN` から渡します。Cloudflare公式仕様では遠隔管理トンネルはトークンで実行でき、`TUNNEL_TOKEN`環境変数をサポートしています（[Tunnel token](https://developers.cloudflare.com/tunnel/reference/tunnel-tokens/)、[run parameters](https://developers.cloudflare.com/tunnel/reference/run-parameters/)）。Named TunnelにはCloudflareアカウント、管理対象ドメイン、公開ホスト名のルート設定が必要です。設定を変更した後は共有画面で受信を停止し、再開してください。実アカウントでの接続確認は未完了です。
- 同じP2P共有リンク／受信招待には1端末ずつ直接接続し、後続の受信者は送信元PCの一時サーバー上で先着順に最大16接続まで待機できます。接続が空くと次の受信者へ自動で切り替わり、待機中にページを閉じた端末は列から外れます。待機キューは接続案内だけを管理し、ファイル本体は引き続き端末間のWebRTC DataChannelで直接転送します。
- 各リンクの「停止」、全体停止、期限切れ、アプリ／PC終了で一時サーバーと該当トンネルを停止します。WindowsのJob ObjectでTunnel子プロセスもアプリの寿命に結び付けているため、アプリが異常終了した場合も残留しません。PCの電源が切れれば共有URLは再起動後に復元されません。ブラウザー共有と受信招待のどちらもWebRTC DataChannelを使います。TURN中継は既定で無効で、利用者が明示設定しない限りSDP内・追加ICE候補の中継経路を拒否し、直接接続できない場合はファイルを転送しません。
- WebRTC共有のTURN中継を有効にした場合だけ、期限付き資格情報で利用者が設定したサーバーを使います。設定しない限りファイル中継はありません。ブラウザーからPCへ送る受信招待では、送信側・受信側どちらかのシグナリング接続が切れても自動再接続し、同じタブが開いていれば、受信側PCの一時保存位置から10分以内に再開します。ブラウザーを閉じると選択ファイルは保持されないため、同じタブで再接続してください。

ブラウザー共有を別回線から手動確認する場合は、[オンライン共有のテスト手順](docs/Online-P2P-Manual-Test.md)を参照してください。LocalSendの通常転送とLocalSend互換HTTP共有はLAN内の直接通信専用で、Cloudflare Tunnelを使いません。

LocalSend v2のマルチキャスト発見、相互登録、HTTP/HTTPSアップロード、複数端末への同時送信、受信後のSHA-256照合を合成ファイルの統合テストで確認しています。GitHub配布物のSHA-256を照合した公式LocalSend CLI 1.18.2と同一PCで相互に実転送し、両方向で空ファイル、日本語名の2 MiBファイル、9 MiBファイルを送り、全6ファイルのサイズ・SHA-256が一致しました。この試験で見つかったchunked送信の本文上限と、CLIが失敗扱いする204応答は修正しました。このPCにインストールされている公式GUI 1.15.4は同一PCのUDP 53317発見テストで30秒以内に検出できず、別の物理PC／スマートフォン上の公式GUIとの実ファイル送受信は未確認です。実機ではファイアウォールでUDP 53317と、端末発見時に通知されるTCPポートを許可してください。

## ZIP圧縮・展開とGPU

「GPU圧縮・解凍」タブからファイルやフォルダを複数選び、標準ZIPまたはEZ Converter専用の`.ziper`を作成・展開できます。エクスプローラーの右クリックからGPU圧縮を開始でき、ZIP/`.ziper`は右クリックから展開できます。ZIPはWindowsや7-Zipと互換です。`.ziper`はファイルを独立した小さなチャンクへ分けるため、大きなファイルや多数の小さなファイルをNVIDIA GPUで並列処理できます。標準ZIPも、形式とサイズが安全に適合するエントリをGPUで処理します。GPUがない場合、適合するデータがない場合、またはGPUで問題が起きた場合はCPUへ切り替わります。

圧縮ではGPU出力をCPUで復元して照合し、展開ではGPUへ渡す前にCPUでDEFLATEを検証し、GPU出力をCPU結果と照合します。ZIP CRC32、チャンクごとのCRC32と復元サイズ、件数・容量上限、Windowsで危険なパス、リンク・特殊ファイル、既存の展開先を確認します。圧縮は一時ファイルへ完成させてから公開し、展開は隔離フォルダで全件検証後に移動します。キャンセルや破損で元ファイルを上書きしません。暗号化ZIP、破損データ、リンクを含むアーカイブは拒否します。

GPUアクセラレーションには64bit Windows、対応するNVIDIA GPUとドライバーが必要です。CUDA Toolkitの開発用インストールは不要です。初回ビルドは`pwsh -File .\scripts\Prepare-GpuCompression.ps1`を実行してください。公式NVIDIA配布ファイルのSHA-256を検証してCUDA Runtime、nvCOMP、再配布に必要なライセンスとVC++ DLLを配置し、ネイティブGPUブリッジをビルドします。GPUがないPCではこの手順を省略でき、CPUモードはそのまま動作します。GPUエンジンは`Tools\GpuCompression`へ同梱され、GitHub Actionsでも同じ手順で発行物へ追加されます。

動作確認（実GPUを必須にする場合は`--require-gpu`も指定）:

    dotnet run --project .\tests\GpuCompression.Integration\GpuCompression.Integration.csproj -c Release -- --require-gpu

このテストはZIP/`.ziper`のCPU・GPU間往復、SHA-256、Windows標準ZIPとの互換性、GPUがない場合のCPU切替、危険な展開名、既存ファイル保護、キャンセル、破損検出を確認します。参照した[Ziper](https://github.com/xero711/Ziper)はMITライセンスで、元の著作権・ライセンスを[Compression/LICENSE-Ziper.txt](Compression/LICENSE-Ziper.txt)に残しています。CUDA RuntimeとnvCOMPはNVIDIAの公式再配布パッケージを使い、各ライセンスを配布物へ含めます。検証済みのNVIDIA配布物はスクリプトに固定のSHA-256とURLを記録しています。

## ビルド

Visual Studio 2022または.NET 8 SDK以降が入ったWindows 11環境で利用できます。Visual Studioでは `EZConverter.sln` を開いてください。ソリューションにはWPFプロジェクト `MediaConverter.csproj` と更新ヘルパー `UpdateAgent/MediaConverter.UpdateAgent.csproj` を登録しています。

新しいPCへソースだけをコピーして初回ビルドする場合は、先に `pwsh -File .\scripts\Prepare-BundledTools.ps1` を実行してください。配布エンジンをSHA-256検証して `Tools` へ準備します。このフォルダーには現在のWindows向けバンドルが既にあります。GitHub Actionsのリリースも同じスクリプトを自動実行します。

コマンドラインからビルドする場合:

    dotnet restore .\EZConverter.sln
    dotnet build .\EZConverter.sln -c Release

通常のRelease出力:

    bin\Release\net8.0-windows10.0.19041.0\MediaConverter.exe

自己完結型win-x64公開:

    dotnet publish .\MediaConverter.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

公開出力先:

    bin\Release\net8.0-windows10.0.19041.0\win-x64\publish

発行時に安定系エンジンを含む `Tools` フォルダーが発行物へコピーされます。FFmpegとyt-dlpは `Tools` に含めず、ユーザー用フォルダーで独立更新します。大容量の同梱エンジンを含むため、発行先の空き容量を確保してください。再配布時は第三者ライセンス、必要な対応ソース、コーデック／delegate条件を確認してください。

## GitHubへの配置とリリース

1. 公開リポジトリ [xero711/ez-converter](https://github.com/xero711/ez-converter) を利用します。アプリの自動更新は未認証のGitHub Releases APIを使うため、公開リポジトリが必要です。
2. リポジトリをcloneし、GitHub CLIまたはGit Credential Managerで認証して変更をpushします。認証トークンをファイルへ書きません。
3. `MediaConverter.csproj`の`Version`を更新してコミットし、`v`を付けたタグ（例: `v1.0.2`）をpushします。
4. GitHub Actionsが配布エンジンをチェックサム検証して準備し、win-x64自己完結発行、更新ヘルパー同梱、ZIP作成、SHA-256作成、GitHub Release公開まで行います。
5. リリース後にアプリを起動すると更新通知が表示され、同意後に検証済みZIPで更新されます。

アプリのバイナリや外部ツールをGitへ含める場合は、各配布元のライセンスと容量を確認してください。リリースZIPには必要な実行エンジンを同梱しますが、それらの大容量ファイルはソースリポジトリには含めません。

## 使い方

1. 「ファイルを追加」またはドラッグ＆ドロップでファイルを追加します。
2. 各ファイル行の「変換後の形式」から形式を選択します。初期値はカテゴリごとの推奨形式です。
3. 保存先を確認して「変換を開始」を押します。必要な機能の確認はアプリが自動で行います。

動画ファイルでは、MP3、AAC、FLAC、WAVなどの音声形式も選べます。音声形式を選ぶと、映像ストリームを除いて音声トラックのみを抽出・変換します。音声トラックを含まない動画は変換できません。

動画取得を使う場合:

1. 「動画取得」タブを開き、YouTube、X、DiscordなどのURLを貼り付けます。
2. 映像/音声、形式、品質、保存先を選択して「取得を開始」を押します。
3. 保存できない場合は表示される案内を確認してください。非公開、期限切れ、認証必須、DRMは対象外です。

P2Pで送受信する場合:

1. 「送る・受け取る」を開き、受信フォルダーを確認して「受信を開始」します。
2. 同じLANのLocalSend対応アプリへは「LocalSend対応端末」一覧から送信します。PINを要求された場合は6桁PINを入力してください。ブラウザーで受け取る相手には「ファイルの共有URLを作る」、相手からこのPCへ送る場合は「受信招待URLを作る」を選びます。
3. インターネット越しなら「インターネットの相手とP2P接続する」を選びます。相手へURLを渡し、表示される受信内容を確認してから許可します。通常はSTUNを使った端末間の直接接続です。直接接続できないネットワーク向けに、設定画面で利用者自身のcoturn等を登録し、TURN中継を明示的に有効化できます。

TURN中継は既定で無効です。設定にはTURN REST共有シークレット方式のサーバーが必要で、サーバーURLと共有シークレットを登録します。シークレットはWindows DPAPIで現在のWindowsユーザーに結び付けて保護し、共有ページには期限付きの一時資格情報だけを渡します。手動停止リンクでは一時資格情報は7日で更新期限を迎えるため、接続前にページを再読み込みしてください。TURN経由の場合、ファイルの暗号化された通信は設定したTURNサーバーを通ります。サーバーの設置・公開・利用料金は利用者側で用意してください。

同名ファイルがある場合は上書きせず、_converted1などの名前で保存します。ログはアプリと同じフォルダのconversion.logに追記します。

## 構成

- MainWindow.xaml(.cs): WPF UI、ファイル／URLの追加、進捗、内部ログ
- Models/MediaFormatCatalog.cs: 入力/出力形式と読み込み専用形式のカタログ
- Services/ToolLocator.cs: アプリ同梱・ユーザー用・PATH・Program Filesからのエンジン検出
- Services/YtDlpUpdateService.cs: 公式Nightlyの取得、SHA-256検証、12時間間隔の自動更新
- Services/FfmpegUpdateService.cs: FFmpegの取得、SHA-256検証、実行ファイル検証、12時間間隔の自動更新
- Services/AppUpdateService.cs: GitHub Releasesのバージョン比較、ZIP/SHA-256検証、更新ヘルパー起動
- UpdateAgent/Program.cs: アプリ終了待機、バックアップ、更新、失敗時復元、再起動
- Services/DependencyChecker.cs: 外部プロセスの実行可能性・バージョン確認
- Services/ConversionService.cs: バックエンド選択、Office変換、一時展開/再圧縮、エラー処理
- Services/ThemeManager.cs: Windows配色設定に追従するテーマ反映
- Sharing/EZConverter.Sharing.csproj: LAN転送、LocalSend v2.2ブラウザー共有サーバー、受信招待、Cloudflare Tunnel、ピア検出、期限付き共有、受信API
- Sharing/Web/: LocalSend互換ダウンロード画面、ブラウザー受信招待画面
- Views/SharingView.xaml(.cs): ファイル送受信UI、共有リンク、受信許可、転送状況

## ファイル送受信の検証

テストプロジェクトを直接実行する場合はWindowsのWebView2 Runtimeが必要です。通常のアプリ利用では、未導入なら配布物に同梱したセットアップをP2P初回利用時に自動実行します。

Windows 11で本体の統合テスト、LocalSend v2.2 APIの実配信、WebView2上のブラウザー転送テストを実行できます:

dotnet run --project .\tests\Sharing.Integration\Sharing.Integration.csproj -c Release
dotnet run --project .\tests\Sharing.UIIntegration\Sharing.UIIntegration.csproj -c Release
node .\scripts\Test-SharingBrowser.mjs
dotnet run --project .\tests\Sharing.P2PBrowser\Sharing.P2PBrowser.csproj -c Release
dotnet run --project .\tests\Sharing.P2PBrowser\Sharing.P2PBrowser.csproj -c Release -- --public-p2p
dotnet run --project .\tests\Sharing.P2PBrowser\Sharing.P2PBrowser.csproj -c Release -- --public-p2p-manual
dotnet run --project .\tests\Sharing.P2PBrowser\Sharing.P2PBrowser.csproj -c Release -- --public-p2p-manual-share
dotnet run --project .\tests\Conversion.Matrix\Conversion.Matrix.csproj -c Release
dotnet run --project .\tests\AppUpdate.Integration\AppUpdate.Integration.csproj -c Release
dotnet run --project .\tests\VideoDownload.Integration\VideoDownload.Integration.csproj -c Release

LocalSend互換は公式v2.2の端末検出、HTTPS証明書フィンガープリント、標準TCPポート優先／競合時の退避、prepare-upload/upload/cancel、ブラウザー向けprepare-download/downloadを実装しています。統合テストでは独立したJSONクライアントからの階層付きファイル受信、EZ Converterからの9 MiB超を含む階層付き複数ファイル送信、SHA-256照合、一部ファイルだけ受理する応答と転送不要のHTTP 204を検証します。WebView2テストはLocalSend互換HTTP共有、非セキュアなLAN-IP HTTPページからのWebRTC直接転送（共有URLでPCからブラウザーへ、受信招待でブラウザーからPCへの双方向）、複数受信者キューの昇格後における実ファイル転送、保存先選択APIがないブラウザーのBlobダウンロード、共有URLの受信途中に切断した場合の同じページ内での再開、受信招待でのICE接続失敗後の両端再接続・12 MiB再開を確認します。LAN-IP試験は同一PCのWebView2から行っており、別の物理端末との実転送とは区別してください。WindowsのWebView2 Runtimeが必要です。

これらの自動検証は公式プロトコル仕様と独立クライアントを使っています。GitHub配布物のSHA-256を照合した公式LocalSend CLI 1.18.2と同一PCで双方向に実転送しました。両方向で空ファイル、日本語名の2 MiBファイル、9 MiBファイルを送り、全6ファイルのサイズ・SHA-256が一致しています。CLI送信のchunked本文上限と成功コードの差異を修正し、共有統合テスト211件が通過しています。公式LocalSend Windows GUI v1.15.4は同一PCの発見試験で検出できず、GUIを別の物理端末に置いた最終相互接続と、別ネットワーク間の実転送確認は未完了です。

P2Pブラウザー試験では、複数ファイルを受信側でZIP化した結果を.NET標準のZIP読取で再度開き、フォルダー相対パスと各エントリーのSHA-256が元ファイルに一致することも確認します。

## ファイル変換の検証

`Conversion.Matrix` は形式カタログとルート解決の全組み合わせを照合した後、画像、HTML／DOCX／PDF／TXT、XLSX／CSV、ZIP／7z／tar.gz／JAR、EPUB／AZW3、TTF／OTFの合成データ変換と再読込を実行します。出力は一時領域で検証してから確定され、無効なZIPの変換失敗後に中途半端な出力を残さないことも確認します。実行には `Tools` に含まれる変換エンジンが必要です。

```powershell
dotnet run --project .\tests\Conversion.Matrix\Conversion.Matrix.csproj -c Release -p:SkipBundledTools=true
dotnet run --project .\tests\Conversion.Integration\Conversion.Integration.csproj -c Release
dotnet run --project .\tests\Conversion.AudioIntegration\Conversion.AudioIntegration.csproj -c Release
```

`VideoDownload.Integration` はYouTubeの通常・短縮・埋め込み・Shorts URL、Vimeoの通常・埋め込みURL、HLS、DASH、署名付きHTTP(S)メディアURLが取得引数として保たれることと、FTP・file・javascriptスキームおよびURL内の認証情報を拒否することを確認します。署名付きの合成MP4と合成HLSストリームをローカルHTTPサーバーからyt-dlpで実際に取得し、FFmpegで再生可能性を検証します。第三者サイトのページURLは形式と引数保持の検証であり、ライブ取得テストではありません。サイト側の仕様変更やアクセス制限まで保証するものではありません。

`Sharing.UIIntegration` は本体のWPF `SharingView` を起動し、画面操作でLAN共有URLを作成、LocalSend形式で合成ファイルを取得、停止後に送信元リスナーが閉じることを確認します。

    dotnet run --project .\tests\Sharing.UIIntegration\Sharing.UIIntegration.csproj -c Release -- --public-p2p

このUI統合テストの `--public-p2p` は、WPF画面でオンライン共有を有効にして一時HTTPS URLを発行し、アプリを使わないWebView2ブラウザーとEZ Converter内の「共有URLから受け取る」の両方で合成ファイルをP2P受信してSHA-256を照合します。アプリ内受信は設定済みフォルダーへの保存、既存ファイル保護、不一致SHA-256の拒否、シグナリング切断後の途中再開も検証します。停止操作で送信側サーバーとTunnelが閉じることも確認します。同一PC上の自動試験であり、別回線の実機確認の代用ではありません。

`--public-p2p` はCloudflare Tunnelを送信側PCの一時シグナリング専用ポートへつなぎ、公開共有URLと受信招待URLの両方でWebRTC P2P転送を合成ファイルで確認します。公開招待をEZC1登録コードに変換し、DNS検証後にURLを復元して、そのコードからアプリ間送信を実行します。選択されたICE候補ペアも実際のブラウザー統計から読み取り、relay以外の直接経路であることを検証します。受信招待ではICE失敗を発生させ、両端の再接続後に12 MiBファイルが続きから復旧することも確認します。ファイル本体はトンネルを流さず、テスト終了時にトンネルと受信サーバーを停止します。別の回線の端末を使ったNAT越え確認の代用ではありません。

`--public-p2p-manual` は別回線のPC／スマートフォンで実機確認するための10分限定テストホストです。1 MiBの既知の合成データ、指定ファイル名、SHA-256がすべて一致する場合だけ1件を受信し、成功時は公開招待とトンネルを自動停止します。実機で試す場合は[手動テスト手順](docs/Online-P2P-Manual-Test.md)を参照してください。

`--public-p2p-manual-share` は送信側PCから別回線へ共有する方向の10分限定テストです。アプリは1 MiBの固定合成ファイルだけを生成・共有し、6桁PINを発行します。受信側はブラウザーまたはEZ Converterを使用でき、P2P転送とSHA-256検証を完了すると一時URL・サーバー・Tunnelを自動停止します。任意のユーザーファイルは選択・公開されません。アプリで受け取る場合の操作も含め、[手動テスト手順](docs/Online-P2P-Manual-Test.md)を参照してください。

公式LocalSendアプリを別PC／スマートフォンで同じLANに接続して起動した状態なら、実機の端末発見とHTTPS登録を次のコマンドで確認できます（ファイルは送受信しません）。ファイアウォールやWi-Fiの端末間通信制限があると検出できない場合があります。

    dotnet run --project .\tests\Sharing.Integration\Sharing.Integration.csproj -c Release -- --live-localsend-discovery
