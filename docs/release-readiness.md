# リリース準備状況

- 確認日: 2026-10-10
- 判定: **v1.0.6を公開済み**。Release workflowは全ステップ成功し、公開ZIPを自動アップデーターがダウンロードしてSHA-256を検証しました。追加変更はPR #1でレビュー中です。別PC・別回線での受け入れ確認と不要物の削除は残っています。

## 要件別の現在地

| 要件 | 現在の証拠 | 状態 |
|---|---|---|
| LocalSend互換LAN転送 | [LocalSend Protocol v2.2](https://github.com/localsend/protocol/blob/main/README.md)の発見・登録・送受信形式を実装。[公式CLI 1.18.2](https://github.com/localsend/localsend/releases/tag/v1.18.2)（配布SHA-256照合済み: `ca0b267e7457324b3664a935de4e4956da701a7fa1045f6f99c72602f7f8dc38`）と同一PCで双方向の実転送を確認。両方向で空ファイル、日本語名の2 MiBファイル、9 MiBファイルの合計3件を送り、全6ファイルのサイズ・SHA-256が一致。CLIのchunked送信を妨げるKestrel本文上限と、CLIが失敗扱いする204応答を修正。 | 共有統合テスト217件、公式CLIとの同一PC・別プロセス双方向転送済み。公式GUIとの別端末転送、別PC・別OSでの受け入れは未確認。 |
| 登録コードとインターネット送信 | EZC1コードで招待URLを持ち運び、連絡先をDPAPI保護して保存。Quick TunnelとNamed Tunnelの選択、固定シグナリングポート、DPAPI保護トークン、`TUNNEL_TOKEN`環境変数での起動、公開HTTPSヘルスチェックを追加。共有統合テストでトークン非露出・ホスト名・ポート検証と固定ポート待受を確認。 | 共有統合テスト217件、WPF UI統合試験16項目、Quick Tunnel公開試験25項目に成功。EZC1招待の4,195,037バイト転送を確認。`Sharing.P2PBrowser --public-p2p`でもQuick Tunnel URL、直接ICE、WebRTCの2,097,251バイト転送、再接続・再開、EZC1登録後の2,048,321バイトのアプリ間転送が成功。PR #1でこの公開経路検査を共有CIと次回Release CIに追加中。実Named Tunnelと、Wi-Fiを切った別回線端末の確認は未完了。 |
| ファイル変換 | 254個の一意な入力形式×170出力候補（43,180組合せ）を走査し、8,003件の経路判定と形式選択UIが一致。これは同じresolverの整合性検査で、全経路の実変換ではない。Conversion.Matrixは終了コード0。アプリ変換器による合成データの成功24件（PNG→JPG/WebP/TIFF、HTML→DOCX→PDF→TXT、画像だけのHTML→PDF、XLSX→CSV、ODP→PPTX→PDF→TXT、ZIP→7z/TAR.GZ/JAR、TAR.GZ→ZIP、EPUB↔AZW3、SVG→EPS/PS、MP4→3GP/Xvid、WAV→MP3/FLAC、TTF→OTF）を確認。Flat ODFからLibreOfficeで作成したODPを入力にしています。FontForgeによるOTF→TTF再読込1件、別テストのMP4→AV1と動画→MP3も成功。不正ZIPとOCRなしの画像PDF→TXTは、期待通り失敗し部分出力を残さない。 | ImageMagick、LibreOffice/PDF、7-Zip、Calibre、FontForge、FFmpegを使用。EPS/PS検査はGhostscriptを要求せず、PostScriptヘッダー・BoundingBox・終端を確認。RAW、残りのベクター形式、古いPowerPoint形式・スライドショー形式など他のプレゼン経路、その他大半の映像・音声拡張子は未検査で、全形式の成功保証ではない。 |
| 動画URL保存 | YouTube／Vimeoページ、埋め込み、HLS・DASH、署名付きURLを検証。スキーム省略・`//`・前後空白も安全に正規化。署名付きHTTP MP4と合成HLSをyt-dlpで取得し、FFmpegの再読込に成功。Deno公式Windows資産のサイズ・SHA-256・実行版を照合し、12時間キャッシュも確認。通常解析で候補が見つからない場合に備え、隔離された一時WebView2で公開HTTP(S)動画を検出し、Referer／User-Agentを付けて再試行する画面も追加。ブラウザーCookieはダウンロード側へ渡さない。 | URL・実取得50件の統合確認に成功。WPF UI統合試験で合成ページ上の署名付きMP4、WebM、HLS、DASH候補を検出し、重複しない表示名、署名付きURL、ページURL、User-Agentの引き渡しを確認。変更後のアプリReleaseビルドも警告0・エラー0。実サイト受け入れは未検証。最新Denoは実行時に[公式Release](https://github.com/denoland/deno/releases/latest)から取得。実サイトの仕様変更、ログイン必須、DRM保護動画は保証対象外。 |
| 自動更新 | GitHub Release応答の検査、SHA-256、破損ZIP拒否、部分取得ファイルの削除、隔離されたUpdateAgentの置換・バックアップ・再起動を確認。Release workflowはタグからアプリ版を埋め込み、公開後にアプリ自身の更新処理を実行。 | ローカル統合11件に加え、[v1.0.6 Release run](https://github.com/xero711/ez-converter/actions/runs/37936030969)の12件目で公開ZIP 1,343,542,303 bytesを実ダウンロードし、SHA-256 `173ea61efa32c818a578183db895872580ca75435c8caf771ec3f1f57089fb1e`を照合。成功。 |
| Releaseビルド | 変更後のWPFアプリ本体と共有ライブラリをRelease構成でビルド。 | 0警告・0エラー。 |
| ストレージ整理 | [候補一覧](storage-cleanup-candidates.md)と[work全件一覧](work-storage-inventory.md)に容量・パスを記録。 | 自動レビューが無視設定済みビルド／作業フォルダーの再帰削除を拒否したため、この作業では削除していない。2026-10-10最新確認時のD:空き容量は541.18 GiB。前回記録の503.96 GiBから37.22 GiB増えた要因は未確認。候補内訳は2026-10-09時点の走査値。 |
| 発行サイズ | 発行フォルダーは3,215,746,709 bytes。 | v1.0.6のRelease ZIPは1,343,542,303 bytes（約1.25 GiB）で、GitHubの2 GiB上限未満。SHA-256 sidecarとGitHub asset digestを公開し、自動更新で実データを照合済み。 |
| GitHubリポジトリ | `xero711/ez-converter`を公開リポジトリとして作成し、公開用no-replyアドレスの単一スナップショットを`main`へpush。秘密トークン・秘密鍵のスキャンでは検出なし。 | [共有CI](https://github.com/xero711/ez-converter/actions/runs/37936018958)と[Release workflow](https://github.com/xero711/ez-converter/actions/runs/37936030969)が成功。v1.0.6の公開と自動更新検証まで完了。 |

## 公開共有の制約

Quick Tunnelは初期設定で使う一時URLです。設定でNamed Tunnelを選ぶと、固定ホスト名のHTTPS公開URLを使えます。Cloudflare Zero Trustの公開ホスト名ルートを `http://127.0.0.1:<ポート>` に作成し、アプリにも同じホスト名・ポート・トークンを設定してください。遠隔管理トンネルはCloudflare公式の `TUNNEL_TOKEN` 環境変数を利用して起動します（[run parameters](https://developers.cloudflare.com/tunnel/reference/run-parameters/)）。実アカウント資格情報がないため、Cloudflare上のルートから別ネットワーク端末までの通し試験は残っています。

2026-10-09に `Sharing.UIIntegration --public-p2p` を実行し、Quick Tunnelで公開したURL経由のブラウザー受信、再開受信、EZC1招待コード登録後の別送信側ビューからの招待受信を確認しました。最後の送信は受信側で承認し、4,195,037バイトを保存して送受信双方のSHA-256が一致しました。受信側は同一PC上の別WPFビューのため、別回線の第三者端末での実測は未実施です。試験完了後に成功時の一時フォルダー、トンネル、待受ポートは残りません。

同日、`Sharing.P2PBrowser --public-p2p` もローカル実行しました。Quick Tunnel公開URLへの到達、直接ICE候補、2,097,251バイトのWebRTC受信、再接続・中断後の再開、EZC1コード復元後のアプリ間2,048,321バイト送信をSHA-256一致まで確認しました。この自動試験のコマンドを共有CIと次回Release workflowに追加しています。これも同一PC内の試験なので、別回線端末の受け入れ証拠には数えません。

## 次の受け入れ確認

WPF共有UI統合試験 (`dotnet run --project tests\Sharing.UIIntegration\Sharing.UIIntegration.csproj -c Release --no-restore`) は2026-10-09に実行し、登録コードの保存、DPAPIで保護したPIN、PIN必須LocalSend API、転送データのSHA-256、停止後の失効、同一PCの2受信者への送信、WebRTC招待送信など15項目すべて成功しました。Quick Tunnel経由の公開経路は `Sharing.P2PBrowser --public-p2p` の別実行で成功しましたが、別回線端末の通し確認は残っています。

1. 公式LocalSend GUIを別端末で同じLANに接続し、発見・登録・実ファイル送受信を確認する。
2. 別回線のPC／スマートフォンから、登録コードを使った双方の送受信とSHA-256を確認する。
3. CloudflareアカウントでNamed Tunnelを作成し、公開ホスト名ルート、起動、停止、再接続、別回線のファイル転送を確認。
4. ストレージ候補一覧の内容をレビュー後に整理し、整理前後の空き容量を計測する。自動レビューが拒否した一括削除操作は再実行せず、安全に対象を絞った方法を確定する。
