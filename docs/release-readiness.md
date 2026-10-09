# リリース準備状況

- 確認日: 2026-10-09
- 判定: **v1.0.5を公開済み**。Release workflowは全ステップ成功し、公開ZIPを自動アップデーターがダウンロードしてSHA-256を検証しました。別PC・別回線での受け入れ確認と不要物の削除は残っています。

## 要件別の現在地

| 要件 | 現在の証拠 | 状態 |
|---|---|---|
| LocalSend互換LAN転送 | [LocalSend Protocol v2.2](https://github.com/localsend/protocol/blob/main/README.md)の発見・登録・送受信形式を実装。[公式CLI 1.18.2](https://github.com/localsend/localsend/releases/tag/v1.18.2)（配布SHA-256照合済み: `ca0b267e7457324b3664a935de4e4956da701a7fa1045f6f99c72602f7f8dc38`）と同一PCで双方向の実転送を確認。両方向で空ファイル、日本語名の2 MiBファイル、9 MiBファイルの合計3件を送り、全6ファイルのサイズ・SHA-256が一致。CLIのchunked送信を妨げるKestrel本文上限と、CLIが失敗扱いする204応答を修正。 | 共有統合テスト217件、公式CLIとの同一PC・別プロセス双方向転送済み。公式GUIとの別端末転送、別PC・別OSでの受け入れは未確認。 |
| 登録コードとインターネット送信 | EZC1コードで招待URLを持ち運び、連絡先をDPAPI保護して保存。Quick TunnelとNamed Tunnelの選択、固定シグナリングポート、DPAPI保護トークン、`TUNNEL_TOKEN`環境変数での起動、公開HTTPSヘルスチェックを追加。共有統合テストでトークン非露出・ホスト名・ポート検証と固定ポート待受を確認。 | 共有統合テスト217件、WPF UI統合試験15項目、Quick Tunnel公開試験25項目に成功。公開招待をEZC1コードで別の送信側ビューに登録し、4,195,037バイトの承認付き送信とSHA-256を確認。実Cloudflare Named Tunnelと別回線PC／スマートフォンの実転送は未確認。 |
| ファイル変換 | 254個の一意な入力形式×170出力候補（43,180組合せ）を走査し、8,003件の経路判定と形式選択UIが一致。これは同じresolverの整合性検査で、全経路の実変換ではない。Conversion.Matrixは終了コード0。アプリ変換器による合成データの成功19件（PNG→JPG/WebP/TIFF、HTML→DOCX→PDF→TXT、XLSX→CSV、ZIP→7z/TAR.GZ/JAR、TAR.GZ→ZIP、EPUB↔AZW3、SVG→EPS/PS、MP4→3GP/Xvid、TTF→OTF、画像だけのHTML→PDF）、FontForgeによるOTF→TTF再読込1件を確認。別テストのMP4→AV1と動画→MP3も成功。不正ZIPとOCRなしの画像PDF→TXTは、期待通り失敗し部分出力を残さない。 | ImageMagick、LibreOffice/PDF、7-Zip、Calibre、FontForge、FFmpegを使用。EPS/PS検査はGhostscriptを要求せず、PostScriptヘッダー・BoundingBox・終端を確認。RAW、残りのベクター形式、プレゼン、その他大半の映像・音声拡張子などは未検査で、全形式の成功保証ではない。 |
| 動画URL保存 | YouTube／VimeoのURL形状、HLS・DASH、署名付きURLの引数保持を検証。署名付きHTTP MP4と合成HLSをyt-dlpで取得し、FFmpegの再読込に成功。 | 41件の統合確認に成功。実サイトの仕様変更やログイン必須ページは保証対象外。 |
| 自動更新 | GitHub Release応答の検査、SHA-256、破損ZIP拒否、部分取得ファイルの削除、隔離されたUpdateAgentの置換・バックアップ・再起動を確認。Release workflowはタグからアプリ版を埋め込み、公開後にアプリ自身の更新処理を実行。 | ローカル統合11件に加え、[v1.0.5 Release run](https://github.com/xero711/ez-converter/actions/runs/37930137055)の12件目で公開ZIP 1,343,542,324 bytesを実ダウンロードし、SHA-256 `0e541b8d056e830b033bcc4c266305cd216adeae5db713b75f38dd7389a89df2`を照合。成功。 |
| Releaseビルド | 変更後のWPFアプリ本体と共有ライブラリをRelease構成でビルド。 | 0警告・0エラー。 |
| ストレージ整理 | [候補一覧](storage-cleanup-candidates.md)と[work全件一覧](work-storage-inventory.md)に容量・パスを記録。 | 削除は未実施。自動レビューが一括削除操作を拒否したため、空き容量は増えていない。2026-10-09再確認時のD:空き容量は441.32 GiB。 |
| 発行サイズ | 発行フォルダーは3,215,746,709 bytes。 | v1.0.5のRelease ZIPは1,343,542,324 bytes（約1.25 GiB）で、GitHubの2 GiB上限未満。SHA-256 sidecarとGitHub asset digestを公開し、自動更新で実データを照合済み。 |
| GitHubリポジトリ | `xero711/ez-converter`を公開リポジトリとして作成し、公開用no-replyアドレスの単一スナップショットを`main`へpush。秘密トークン・秘密鍵のスキャンでは検出なし。 | [共有CI](https://github.com/xero711/ez-converter/actions/runs/37930136662)と[Release workflow](https://github.com/xero711/ez-converter/actions/runs/37930137055)が成功。v1.0.5の公開と自動更新検証まで完了。 |

## 公開共有の制約

Quick Tunnelは初期設定で使う一時URLです。設定でNamed Tunnelを選ぶと、固定ホスト名のHTTPS公開URLを使えます。Cloudflare Zero Trustの公開ホスト名ルートを `http://127.0.0.1:<ポート>` に作成し、アプリにも同じホスト名・ポート・トークンを設定してください。遠隔管理トンネルはCloudflare公式の `TUNNEL_TOKEN` 環境変数を利用して起動します（[run parameters](https://developers.cloudflare.com/tunnel/reference/run-parameters/)）。実アカウント資格情報がないため、Cloudflare上のルートから別ネットワーク端末までの通し試験は残っています。

2026-10-09に `Sharing.UIIntegration --public-p2p` を実行し、Quick Tunnelで公開したURL経由のブラウザー受信、再開受信、EZC1招待コード登録後の別送信側ビューからの招待受信を確認しました。最後の送信は受信側で承認し、4,195,037バイトを保存して送受信双方のSHA-256が一致しました。受信側は同一PC上の別WPFビューのため、別回線の第三者端末での実測は未実施です。試験完了後に成功時の一時フォルダー、トンネル、待受ポートは残りません。

## 次の受け入れ確認

WPF共有UI統合試験 (`dotnet run --project tests\Sharing.UIIntegration\Sharing.UIIntegration.csproj -c Release --no-restore`) は2026-10-09に実行し、登録コードの保存、DPAPIで保護したPIN、PIN必須LocalSend API、転送データのSHA-256、停止後の失効、同一PCの2受信者への送信、WebRTC招待送信など15項目すべて成功しました。インターネット越しのQuick Tunnel通し試験は `--public-p2p` の別実行が必要です。

1. 公式LocalSend GUIを別端末で同じLANに接続し、発見・登録・実ファイル送受信を確認する。
2. 別回線のPC／スマートフォンから、登録コードを使った双方の送受信とSHA-256を確認する。
3. CloudflareアカウントでNamed Tunnelを作成し、公開ホスト名ルート、起動、停止、再接続、別回線のファイル転送を確認。
4. ストレージ候補一覧の内容をレビュー後に整理し、整理前後の空き容量を計測する。自動レビューが拒否した一括削除操作は再実行せず、安全に対象を絞った方法を確定する。
