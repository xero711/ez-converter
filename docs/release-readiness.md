# リリース準備状況

- 確認日: 2026-10-10
- 判定: **v1.0.6を公開済み**。Release workflowは全ステップ成功し、公開ZIPを自動アップデーターがダウンロードしてSHA-256を検証しました。追加変更はPR #1でレビュー中です。PR #1の`b6b085a`に対する[Sharing CI](https://github.com/xero711/ez-converter/actions/runs/38022453669)と[Conversion CI](https://github.com/xero711/ez-converter/actions/runs/38022453642)が両方成功しました。別PC・別回線での受け入れ確認と実Named Tunnel試験は残っています。残存するビルド出力・IDE索引は可逆NTFS圧縮で格納量を約6.62 GiB減らし、D:空き容量は約550.85 GiBになりました。再帰削除は自動レビューに拒否されたため、ファイルは保持しています。

## 要件別の現在地

| 要件 | 現在の証拠 | 状態 |
|---|---|---|
| LocalSend互換LAN転送 | [LocalSend Protocol v2.2](https://github.com/localsend/protocol/blob/main/README.md)の発見・登録・送受信形式を実装。[公式CLI 1.18.2](https://github.com/localsend/localsend/releases/tag/v1.18.2)（配布SHA-256照合済み: `ca0b267e7457324b3664a935de4e4956da701a7fa1045f6f99c72602f7f8dc38`）と同一PCで双方向の実転送を確認。両方向で空ファイル、日本語名の2 MiBファイル、9 MiBファイルの合計3件を送り、全6ファイルのサイズ・SHA-256が一致。CLIのchunked送信を妨げるKestrel本文上限と、CLIが失敗扱いする204応答を修正。更新日時・アクセス日時の送受信も統合試験で確認。 | 共有統合テスト227件、公式CLIとの同一PC・別プロセス双方向転送済み。公式GUIとの別端末転送、別PC・別OSでの受け入れは未確認。 |
| 登録コードとインターネット送信 | EZC1コードで招待URLを持ち運び、連絡先をDPAPI保護して保存。Quick TunnelとNamed Tunnelの選択、固定シグナリングポート、DPAPI保護トークン、`TUNNEL_TOKEN`環境変数での起動、公開HTTPSヘルスチェックを追加。Named Tunnel用の招待識別子をDPAPI保護して保存し、同じ固定ホスト名で招待を再作成した場合にコードを再利用できるようにした。コード更新操作で古い招待識別子をローテーションする。 | 共有統合テスト227件、WPF UI統合試験16項目、Quick Tunnel公開試験25項目に成功。EZC1招待の4,195,037バイト転送を確認。`Sharing.P2PBrowser --public-p2p`でもQuick Tunnel URL、直接ICE、WebRTCの2,097,251バイト転送、再接続・再開、EZC1登録後の2,048,321バイトのアプリ間転送が成功。Named Tunnel識別子の再起動・更新テストと実装コミットのSharing CIは成功。実Named Tunnelと、Wi-Fiを切った別回線端末の確認も未完了。 |
| ファイル変換 | 254個の一意な入力形式×170出力候補（43,180組合せ）を走査し、8,003件の経路判定と形式選択UIが一致。これは同じresolverの整合性検査で、全経路の実変換ではない。Conversion.Matrixは終了コード0。アプリ変換器による合成データの成功24件（PNG→JPG/WebP/TIFF、HTML→DOCX→PDF→TXT、画像だけのHTML→PDF、XLSX→CSV、ODP→PPTX→PDF→TXT、ZIP→7z/TAR.GZ/JAR、TAR.GZ→ZIP、EPUB↔AZW3、SVG→EPS/PS、MP4→3GP/Xvid、WAV→MP3/FLAC、TTF→OTF）を確認。Flat ODFからLibreOfficeで作成したODPを入力にしています。FontForgeによるOTF→TTF再読込1件、別テストのMP4→AV1と動画→MP3も成功。不正ZIPとOCRなしの画像PDF→TXTは、期待通り失敗し部分出力を残さない。FFmpeg配布アーカイブの途中切断を合成し、部分ファイルの破棄後に再取得できることを確認。実AV1変換ではAAC音声保持と進捗順序も確認。追加で、複数色・半透明を含むPNGと257色PPMのMAP出力を含む境界検査、SVG、MP4、WAV、DOCX、XLSX、PPTX、ZIP、EPUB、TTFの代表入力から186種類の一意な出力経路を実変換し、出力を再読込・構造検査する全出力スイープが終了コード0で成功。 | ImageMagick、LibreOffice/PDF、7-Zip、Calibre、FontForge、FFmpegを使用。EPS/PS検査はGhostscriptを要求せず、PostScriptヘッダー・BoundingBox・終端を確認。全書込可能出力を対象にした検証は上記10種類の代表入力からの経路。254入力形式すべてから各出力への実変換や、異なる内容・コーデック・破損データを含む全ケースを網羅したものではない。最新のConversion CIで全出力スイープ、AV1/FFmpeg取得・再試行、動画→音声、動画URL・Deno更新まで成功。変換検証workflowとRelease workflowの双方で同じ全出力スイープを実行する設定に更新。 |
| 動画URL保存 | YouTube／Vimeoページ、埋め込み、HLS・DASH、署名付きURLを検証。スキーム省略・`//`・前後空白も安全に正規化。署名付きHTTP MP4と合成HLSをyt-dlpで取得し、FFmpegの再読込に成功。Deno公式Windows資産のサイズ・SHA-256・実行版を照合し、12時間キャッシュも確認。通常解析で候補が見つからない場合に備え、隔離された一時WebView2で公開HTTP(S)動画を検出し、Referer／User-Agentを付けて再試行する画面も追加。ブラウザーCookieはダウンロード側へ渡さない。GitHub APIのレート制限対策として、任意の`EZCONVERTER_GITHUB_TOKEN`を公式リリースメタデータ照会だけに使用する。 | URL・実取得50件の統合確認に成功。WPF UI統合試験で合成ページ上の署名付きMP4、WebM、HLS、DASH候補を検出し、重複しない表示名、署名付きURL、ページURL、User-Agentの引き渡しを確認。2026-10-10に任意の`--live-site-smoke`で公開YouTube公式動画（`aqz-KE-bpKQ`）のメタデータ取得を確認し、メディアを保存せずに統合確認53件が成功。さらに任意の`--live-direct-download-smoke`で[Test Videos](https://test-videos.co.uk/home)のH.264 MP4直リンク（HEAD 991,017 bytes）をアプリ同一のyt-dlp引数で保存。実ファイル991,017 bytesが4 MiB上限内であること、FFmpeg再生、SHA-256 `77145C94C11F3754207499158DF22406E1FE7635553C1C86DC5E881DFEB32016`を確認し、VideoDownload.Integrationは57件成功。ログイン必須だったVimeoの例は対象外。ライブページのメディア保存、ログイン必須やDRM保護URLは未確認。最新Denoは実行時に[公式Release](https://github.com/denoland/deno/releases/latest)から取得。実サイトの仕様変更、ログイン必須、DRM保護動画は保証対象外。 |
| 自動更新 | GitHub Release応答の検査、SHA-256、破損ZIP拒否、部分取得ファイルの削除、隔離されたUpdateAgentの置換・バックアップ・再起動を確認。Release workflowはタグからアプリ版を埋め込み、公開後にアプリ自身の更新処理を実行。 | ローカル統合11件に加え、[v1.0.6 Release run](https://github.com/xero711/ez-converter/actions/runs/37936030969)の12件目で公開ZIP 1,343,542,303 bytesを実ダウンロードし、SHA-256 `173ea61efa32c818a578183db895872580ca75435c8caf771ec3f1f57089fb1e`を照合。成功。 |
| Releaseビルド | 変更後のWPFアプリ本体と共有ライブラリをRelease構成でビルド。 | 0警告・0エラー。 |
| ストレージ整理 | [候補一覧](storage-cleanup-candidates.md)と[work全件一覧](work-storage-inventory.md)に容量・パスを記録。 | テスト各プロジェクトのRelease Cleanで約3.02 GiB、さらに`bin/Debug`、`bin/Release`、`tests/Sharing.Preview/bin/Release`とVisual Studioの3索引／評価ディレクトリをNTFS圧縮し、対象データの格納量を15,497,776,104 bytesから8,388,347,543 bytesへ削減（約6.62 GiB回収）。D:空き容量は実測550.85 GiB。6ディレクトリは保持し、再帰削除拒否後に手動削除は行っていない。 |
| 発行サイズ | 発行フォルダーは3,215,746,709 bytes。 | v1.0.6のRelease ZIPは1,343,542,303 bytes（約1.25 GiB）で、GitHubの2 GiB上限未満。SHA-256 sidecarとGitHub asset digestを公開し、自動更新で実データを照合済み。 |
| GitHubリポジトリ | `xero711/ez-converter`を公開リポジトリとして作成し、公開用no-replyアドレスの単一スナップショットを`main`へpush。秘密トークン・秘密鍵のスキャンでは検出なし。 | [共有CI](https://github.com/xero711/ez-converter/actions/runs/37936018958)と[Release workflow](https://github.com/xero711/ez-converter/actions/runs/37936030969)が成功。v1.0.6の公開と自動更新検証まで完了。 |

2026-10-10に現在の作業ツリーでConversion.MatrixをReleaseビルド（警告0・エラー0）し、追加検証を含む全体を終了コード0で実行。従来1種類ずつだった動画・音声入力に、MPEG-2/AC-3のMKV、VP9/OpusのWebM、MPEG-4/MP3のAVI、AAC/M4A、Vorbis/OGG、Opus、MP3、WMA、FLACを加え、異なるコーデックからAV1 MP4、MP3、FLAC、WAV、AACへの実変換28経路を再読込検査。不正MP4/MP3の失敗と部分出力が残らないことも確認。従来の全出力スイープ186経路と8,003件の経路整合性検査も同じ実行で成功。網羅対象は記載した合成入力と経路であり、全ての実ファイル・コーデック・破損状態を保証するものではありません。

## 公開共有の制約

Quick Tunnelは初期設定で使う一時URLです。設定でNamed Tunnelを選ぶと、固定ホスト名のHTTPS公開URLを使えます。Cloudflare Zero Trustの公開ホスト名ルートを `http://127.0.0.1:<ポート>` に作成し、アプリにも同じホスト名・ポート・トークンを設定してください。遠隔管理トンネルはCloudflare公式の `TUNNEL_TOKEN` 環境変数を利用して起動します（[run parameters](https://developers.cloudflare.com/tunnel/reference/run-parameters/)）。実アカウント資格情報がないため、Cloudflare上のルートから別ネットワーク端末までの通し試験は残っています。

2026-10-09に `Sharing.UIIntegration --public-p2p` を実行し、Quick Tunnelで公開したURL経由のブラウザー受信、再開受信、EZC1招待コード登録後の別送信側ビューからの招待受信を確認しました。最後の送信は受信側で承認し、4,195,037バイトを保存して送受信双方のSHA-256が一致しました。受信側は同一PC上の別WPFビューのため、別回線の第三者端末での実測は未実施です。試験完了後に成功時の一時フォルダー、トンネル、待受ポートは残りません。

同日、`Sharing.P2PBrowser --public-p2p` もローカル実行しました。Quick Tunnel公開URLへの到達、直接ICE候補、2,097,251バイトのWebRTC受信、再接続・中断後の再開、EZC1コード復元後のアプリ間2,048,321バイト送信をSHA-256一致まで確認しました。この自動試験のコマンドを共有CIと次回Release workflowに追加しています。これも同一PC内の試験なので、別回線端末の受け入れ証拠には数えません。

## 次の受け入れ確認

動画サイトの任意のライブ確認は、`tests/VideoDownload.Integration`へ`--live-site-smoke <公開HTTP(S)動画ページURL>`を渡すと実行できます。複数URLを繰り返し指定でき、アプリと同じyt-dlp引数でメタデータだけを解析します（`--simulate`を使い、動画は保存しません）。例: `dotnet run --project tests\VideoDownload.Integration\VideoDownload.Integration.csproj -c Release --no-restore -- --live-site-smoke https://www.youtube.com/watch?v=aqz-KE-bpKQ`。ログイン、ブラウザーCookie、DRMが必要なURLは試験対象にできません。

公開直リンクの実保存確認は`dotnet run --project tests\VideoDownload.Integration\VideoDownload.Integration.csproj -c Release --no-restore -- --live-direct-download-smoke`で再実行できます。約1 MBのH.264 MP4をダウンロードし、4 MiB上限、FFmpeg再生、SHA-256を確認して一時ファイルを削除します。

WPF共有UI統合試験 (`dotnet run --project tests\Sharing.UIIntegration\Sharing.UIIntegration.csproj -c Release --no-restore`) は2026-10-10に再実行し、登録コードの保存、DPAPIで保護したPIN、PIN必須LocalSend API、転送データのSHA-256、停止後の失効、同一PCの2受信者への送信、Named Tunnel再利用表示、WebRTC招待送信など16項目すべて成功しました。Quick Tunnel経由の公開経路は `Sharing.P2PBrowser --public-p2p` の別実行で成功しましたが、別回線端末の通し確認は残っています。

1. 公式LocalSend GUIを別端末で同じLANに接続し、発見・登録・実ファイル送受信を確認する。
2. 別回線のPC／スマートフォンから、登録コードを使った双方の送受信とSHA-256を確認する。Named Tunnelでは受信PCを再起動して同じ招待を再作成し、以前登録したコードで再送信する。コード更新操作後は古いコードが使えず、新コードで再登録できることも確認する。Quick Tunnelでは終了後に新コードを登録する。
3. CloudflareアカウントでNamed Tunnelを作成し、公開ホスト名ルート、起動、停止、再接続、別回線のファイル転送を確認。
4. ストレージ候補一覧の内容をレビュー後に整理し、整理前後の空き容量を計測する。自動レビューが拒否した一括削除操作は再実行せず、安全に対象を絞った方法を確定する。
