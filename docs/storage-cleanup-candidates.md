# 不要物・副産物の整理候補

- 対象: `D:\project\ez converter`
- 基準確認日: 2026-10-09
- 追加確認: 2026-10-10
- この文書は削除候補の一覧です。ファイルやフォルダーは削除していません。
- 2026-10-10最新確認時のD:空き容量: 541.18 GiB。前回記録の503.96 GiBより37.22 GiB増えています。両時点の間にこの作業で削除は行っておらず、増加要因は確認できていません。
- 候補ごとのサイズ・ファイル数は2026-10-09の走査結果です。最新の空き容量と同じ時点の全件再走査ではありません。
- 先に試みた一括整理は自動レビューで止まり、この作業では削除していません。

2026-10-10の共有統合テストで、一度だけ終了時のロックファイルが残りました。場所は `work/sharing-p2p-browser/8bd73f66f742483eb342bc352da42b62/receive/.ez-incoming/2db2d24ae4784b7abc8d2f3b4d1406d1.lock` で、0 bytesです。後続の再実行は正常終了しました。この一時フォルダーは下記 `work/sharing-p2p-browser/` の候補に含めています。削除操作はしていません。

## 削除候補

以下は通常のビルドで再生成されるキャッシュ・中間生成物です。子プロジェクトを含む実測合計は約 **19.86 GiB** です（別記の一時プロジェクト4件を除く）。

| パス | 実測サイズ | 内容・扱い |
|---|---:|---|
| `.vs/` | 5.51 GiB | Visual Studio のキャッシュ・ワークスペース情報。Visual Studio を閉じてから整理候補にできます。 |
| `bin/` | 8.53 GiB | アプリのビルド出力。今回のリリース確認用win-x64発行物も含みます。保持する配布物を確認してから整理候補にできます。 |
| `obj/` | 0.09 GiB | .NET の中間生成物。次回ビルドで再生成されます。 |
| `Sharing/bin/`, `Sharing/obj/` | 約 0.002 GiB | LocalSend/P2P共有ライブラリの生成物。 |
| `Compression/bin/`, `Compression/obj/` | 約 0.002 GiB | 圧縮ライブラリの生成物。 |
| `UpdateAgent/bin/`, `UpdateAgent/obj/` | 約 0.14 GiB | 更新エージェントの生成物。今回発行したwin-x64実行物を含みます。 |
| `tests/<project>/bin/`, `tests/<project>/obj/` と `tests/AppUpdate.Integration/Fixtures/UpdateSentinel/{bin,obj}/` | 約 5.59 GiB | テスト各プロジェクトの生成物。特に `tests/Conversion.Integration/bin/` が2.76 GiB、`tests/Sharing.Preview/bin/` が2.56 GiBです。必要な再利用物を確認してから整理候補にできます。 |
| `MediaConverter_mt233urd_wpftmp.csproj` | 58,300 bytes | WPF ビルド時に生成された一時プロジェクト。 |
| `MediaConverter_owhzpuz1_wpftmp.csproj` | 60,168 bytes | WPF ビルド時に生成された一時プロジェクト。 |
| `MediaConverter_vwbnzs0q_wpftmp.csproj` | 58,368 bytes | WPF ビルド時に生成された一時プロジェクト。 |
| `MediaConverter_xohpnbwx_wpftmp.csproj` | 60,168 bytes | WPF ビルド時に生成された一時プロジェクト。 |

一時プロジェクト 4 件の合計は 237,004 bytes（約 0.23 MiB）です。`.vs/`、各 `bin/`・`obj/`、`*_wpftmp.csproj` は `.gitignore` の対象ですが、無視設定だけではディスク上のファイルは消えません。

## 中身を確認してから判断する候補

`work/` は **99.27 GiB、858,377ファイル、直下129フォルダー**です。ビルド・発行済みアプリの複製や、合成ファイルを使った転送テスト結果が混在しています。以下の上位30フォルダーだけで約88.17 GiBあります。

直下129フォルダーと直下ファイル2件の全一覧・個別サイズは [work-storage-inventory.md](work-storage-inventory.md) に記録しています。走査時の読取エラーと除外した再解析ポイントはありません。

| パス | 実測サイズ | 確認内容 |
|---|---:|---|
| `work/sharing-tests/` | 6.10 GiB | 3,380ファイル。複数回の統合テスト用ディレクトリと合成転送データ。 |
| `work/live-localsend-discovery/` | 約16.4 KiB | 9個のGUID付き試行フォルダー。6ファイル（各2,804 bytes）と3個の空フォルダー。LocalSend実機発見テストの状態ファイルを含むため、内容を確認してから整理してください。 |
| `work/temp-sharing-preview/` | 5.12 GiB | 直下は`bin/`、`obj/`のみ。 |
| `work/p2p-browser-build/` | 5.12 GiB | 直下は`bin/`、`obj/`のみ。 |
| `work/sharing-preview-build/` | 5.12 GiB | 直下は`bin/`、`obj/`のみ。 |
| `work/p2p-win-x64-full/` | 2.73 GiB | アプリEXE、変換ツール、ライセンスを含む発行済みアプリ。 |
| `work/publish-build/` | 2.65 GiB | 変換ツールを含む過去の発行出力。 |
| `work/p2p-window-solution-build/` | 2.56 GiB | 過去のビルド出力候補。 |
| `work/lan-broadcast-release/` | 2.56 GiB | 過去のビルド出力候補。 |
| `work/p2p-window-final-build/` | 2.56 GiB | 過去のビルド出力候補。 |
| `work/verified-solution-build/` | 2.56 GiB | 過去のビルド出力候補。 |
| `work/persistent-history-build/` | 2.56 GiB | 過去のビルド出力候補。 |
| `work/health-probe-final-build/` | 2.56 GiB | 過去のビルド出力候補。 |
| `work/ice-diagnostics-build/` | 2.56 GiB | 過去のビルド出力候補。 |
| `work/temp-share-build/` | 2.56 GiB | 過去のビルド出力候補。 |
| `work/turn-build-final/` | 2.56 GiB | 過去のビルド出力候補。 |
| `work/pinggy-provider-build/` | 2.56 GiB | 過去のビルド出力候補。 |
| `work/multi-peer-nonhost-build/` | 2.56 GiB | 過去のビルド出力候補。 |
| `work/health-check-build/` | 2.56 GiB | 過去のビルド出力候補。 |
| `work/move-retry-build/` | 2.56 GiB | 過去のビルド出力候補。 |
| `work/dual-stun-build/` | 2.56 GiB | 過去のビルド出力候補。 |
| `work/turn-build/` | 2.56 GiB | 過去のビルド出力候補。 |
| `work/app-build-audit/` | 2.56 GiB | 過去のビルド出力候補。 |
| `work/p2p-main-v2/` | 2.56 GiB | 過去のビルド出力候補。 |
| `work/p2p-qr-final/` | 2.56 GiB | 過去のビルド出力候補。 |
| `work/p2p-qr-build/` | 2.56 GiB | 過去のビルド出力候補。 |
| `work/p2p-icequeue-solution/` | 2.56 GiB | 過去のビルド出力候補。 |
| `work/p2p-multi-interface-audit/` | 2.56 GiB | 過去のビルド出力候補。 |
| `work/sharing-p2p-browser/` | 2.53 GiB | 過去のビルド出力候補。 |
| `work/verify-buttons/` | 2.50 GiB | 過去の検証用ビルド候補。 |
| `work/verify-build/` | 2.50 GiB | 過去の検証用ビルド候補。 |
| `%LOCALAPPDATA%\Temp\EZConverter-Sharing-UI-609306c018194a499cd08a192392fad6\` | 36,334,128 bytes（約34.7 MiB、182ファイル） | 失敗した公開招待UI試験が診断用に保持した一時フォルダー。WebView2プロファイルと合成テストデータを含みます。今回の再試験は成功し、別の新規一時フォルダーは終了時に残らないことを確認。 |

`work/` の全フォルダーは削除前に個別確認が必要です。特にテスト証跡、再現用ファイル、過去の発行物を保持するか判断してください。サイズは上位フォルダーごとの実測値です。

## 保持するもの

| パス | 理由 |
|---|---|
| `outputs/` | 変換結果やユーザーデータが含まれる可能性があるため。 |
| `Tools/` | 変換エンジンなど、実行・検証で使うツールとライセンス資料があるため。 |
| `docs/Online-P2P-Manual-Test.md` | 手動受け入れ確認の手順書。 |
| ソース、設定、テスト、`.git/` | プロジェクト本体と履歴。 |

## 整理前の確認

ビルドや IDE を終了し、候補の中に残す必要のある実行物・ログ・成果物がないか確認してください。`work/` は特に個別確認が必要です。この文書は一覧作成のみを記録しており、削除の実施を示すものではありません。
