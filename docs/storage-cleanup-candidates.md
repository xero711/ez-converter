# 不要物・副産物の整理候補

- 対象: `D:\project\ez converter`
- 基準確認日: 2026-10-09
- 追加確認: 2026-10-10
- この文書は不要物の候補一覧です。手動のファイル削除はしていません。
- 2026-10-10の最新確認時点でD:空き容量は544.02 GiB。標準Clean直後に確認した541.22 GiBから2.80 GiB増えています。前回記録の503.96 GiBとの差40.06 GiBについては、前回記録から標準Clean直前までの容量差がないため、要因を確認できていません。
- 候補ごとのサイズ・ファイル数は2026-10-09の走査結果です。最新の空き容量と同じ時点の全件再走査ではありません。
- 先に試みた一括整理は自動レビューで止まりました。その後、再生成可能なテスト出力を標準Cleanで一部整理しました。

## 2026-10-10の実在確認と標準Clean

- 基準となる標準Clean直後のD:空き容量は541.22 GiBで、その後の追加Clean後に544.02 GiBを確認しました。Clean実行ごとの容量記録はないため、個別の回収量は断定しません。
- `dotnet clean MediaConverter.csproj -c Release --nologo` とDebug構成のCleanは完了しました。`Sharing.Preview`は依存NuGetパッケージを復元してからCleanしましたが、出力の実測サイズは2.56 GiBのままでした。`Conversion.Integration`のCleanではテスト出力が約2.76 GiBから空になりました。D:の実測空き容量は追加Clean後に約2.80 GiB増えました。
- 最新の実在確認では `.vs/` が3.50 GiB、`bin/` が8.49 GiB、`obj/` が0.09 GiB、`tests/` が2.90 GiBでした。`bin/Release/` は5.93 GiB、`bin/Debug/` は2.56 GiBです。Clean後も残る出力は下表の通り保持しています。
- 前回一覧にあった `work/` は現在のチェックアウトには存在しません。この作業では削除しておらず、いつ・何によってなくなったかは確認できていません。2026-10-09の `work-storage-inventory.md` は当時のスナップショットとして残します。
- 最初の`Sharing.Preview` Cleanは参照するNuGetパッケージ `ClosedXML 0.105.1` が見つからずNETSDK1064で失敗しました。依存関係を復元後、再度Cleanは成功しました。生成物の手動削除やキャッシュ削除は行っていません。
- `.vs/`、残る配布・テスト出力、Toolsはそのままです。前回の自動レビューが再帰的な手動削除を拒否したため、これらは個別確認と安全なClean経路が必要です。

## 削除候補

以下は通常のビルドで再生成されるキャッシュ・中間生成物です。約19.86 GiBという値は2026-10-09時点のスナップショットであり、最新状態の合計ではありません。

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
