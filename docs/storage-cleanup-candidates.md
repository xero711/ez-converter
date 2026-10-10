# 不要物・副産物の整理候補

- 対象: `D:\project\ez converter`
- 基準確認日: 2026-10-09
- 追加確認: 2026-10-10
- この文書は不要物の候補一覧です。手動のファイル削除はしていません。
- 先行スナップショット: 2026-10-10 10:22 JST。`dotnet clean`をテスト各プロジェクトに実行した後のD:空き容量は544.25 GiB（584,382,918,656 bytes）。`.vs/` 3.50 GiB（3,762,698,384 bytes、142ファイル）、`bin/` 8.49 GiB（9,116,667,755 bytes、73,179ファイル）、`obj/` 0.09 GiB（92,955,167 bytes、1,227ファイル）、`tests/` 2.68 GiB（2,881,148,908 bytes、24,567ファイル）、`Tools/` 2.71 GiB（2,913,546,296 bytes、24,155ファイル）。`work/`はファイル0件、`outputs/`はファイル0件。

## 2026-10-10の再確認と圧縮結果

圧縮前にD:の空き容量は544.16 GiB（584,283,758,592 bytes）でした。削除可能性の高い生成物とIDEキャッシュとして、以下の6ディレクトリを確認しました。すべて作業フォルダー内にあり、Git無視対象で、配下に再解析ポイントはありません。論理データは合計15,497,776,104 bytes（約14.43 GiB）で、NTFS圧縮後は8,388,347,543 bytes（約7.82 GiB）になりました。

| 確認済みパス | 圧縮前サイズ | 圧縮後の格納量 | 内容 |
|---|---:|---:|---|
| `bin/Debug/` | 2,746,030,386 bytes（2.56 GiB、24,196ファイル） | 1,618,889,438 bytes | Debugビルド出力。`Tools/`の配布物コピーを含む。 |
| `bin/Release/` | 6,417,607,440 bytes（5.98 GiB、49,047ファイル） | 3,973,457,225 bytes | Releaseビルド・win-x64発行出力。GitHub上にv1.0.6の配布ZIPが公開済み。 |
| `tests/Sharing.Preview/bin/Release/` | 2,745,900,699 bytes（2.56 GiB、24,200ファイル） | 1,618,865,369 bytes | Sharing.PreviewのRelease出力。`Tools/`の配布物コピーを含む。 |
| `.vs/EZConverter/CopilotIndices/` | 3,153,758,400 bytes（2.94 GiB、10ファイル） | 1,002,819,584 bytes | Visual Studio/Copilotのコードチャンク・シンボル索引データベース。 |
| `.vs/EZConverter/FileContentIndex/` | 296,436,546 bytes（0.28 GiB、102ファイル） | 141,012,992 bytes | Visual Studioのファイル内容検索索引。 |
| `.vs/ProjectEvaluation/` | 138,042,633 bytes（0.13 GiB、3ファイル） | 33,302,935 bytes | Visual Studioのプロジェクト評価キャッシュ。 |

対象データの格納量は7,109,428,561 bytes（約6.62 GiB）減りました。D:の実測空き容量は591,469,330,432 bytes（550.85 GiB）で、圧縮前の計測より7,185,571,840 bytes（約6.69 GiB）増加しました。差分には同時刻の他データ変動が含まれる可能性があります。

`devenv.exe`と`dotnet.exe`は起動していません。稼働中の`MediaConverter.exe`は作業フォルダー外のインストール先から起動していました。上記6ディレクトリの再帰削除は自動レビューで拒否され、削除コマンドは実行されませんでした。代わりにWindows標準のNTFS圧縮を適用し、ファイルを保持したまま容量を回収しました。圧縮後のDebug出力にある7-Zip 26.04を実行し、`7z.exe i`が終了コード0で完了することを確認しています。全ディレクトリは残っています。
- 直前の測定では`tests/`が6,083,413,470 bytes（5.67 GiB）、D:空き容量が581,138,513,920 bytesだった。`tests`配下の全`.csproj`とUpdateSentinelにRelease Cleanを行い、`tests/`は3,202,264,562 bytes（2.98 GiB）減少、D:空き容量は3,244,404,736 bytes（3.02 GiB）増加した。テストの`bin/`にあった大半の生成物は整理できたが、`tests/Sharing.Preview/bin/Release`はClean後も2,745,900,699 bytes（2.56 GiB）残るため、内容を判断するまで保持する。
- 2026-10-10 09:42 JST時点のD:空き容量は538.43 GiB（578,134,568,960 bytes）。同日それ以前に543.97 GiB／544.02 GiBを記録しており、空き容量の変動要因は未確認です。前回記録の503.96 GiBとの差40.01 GiBについても要因を確認できていません。
- 候補ごとのサイズ・ファイル数は2026-10-09の走査結果です。最新の空き容量と同じ時点の全件再走査ではありません。
- 先に試みた一括整理は自動レビューで止まりました。その後、再生成可能なテスト出力を標準Cleanで一部整理しました。

## 2026-10-10の実在確認と標準Clean

- 基準となる標準Clean直後のD:空き容量は541.22 GiBで、その後の追加Clean後に544.02 GiBを確認しました。Clean実行ごとの容量記録はないため、個別の回収量は断定しません。
- `dotnet clean MediaConverter.csproj -c Release --nologo` とDebug構成のCleanは完了しました。`Sharing.Preview`は依存NuGetパッケージを復元してからCleanしましたが、出力の実測サイズは2.56 GiBのままでした。`Conversion.Integration`のCleanではテスト出力が約2.76 GiBから空になりました。D:の実測空き容量は追加Clean後に約2.80 GiB増えました。
- 2026-10-10 10:22 JSTのClean後は `.vs/` が3.50 GiB、`bin/` が8.49 GiB、`obj/` が0.09 GiB、`tests/` が2.68 GiBでした。`tests/Conversion.Integration/bin/`は0 bytes、`tests/Sharing.Preview/bin/Release/`には2.56 GiBが残っています。ルート`bin/`のRelease/Debug生成物と`.vs/`も残存しています。
- 前回一覧にあった `work/` は2026-10-10 10:22 JST時点でファイル0件です。2026-10-09の `work-storage-inventory.md` と下表は当時のスナップショットとして残します。
- 最初の`Sharing.Preview` Cleanは参照するNuGetパッケージ `ClosedXML 0.105.1` が見つからずNETSDK1064で失敗しました。依存関係を復元後、再度Cleanは成功しました。生成物の手動削除やキャッシュ削除は行っていません。
- `.vs/`の索引とプロジェクト評価キャッシュ、ルート`bin/`、Sharing.PreviewのRelease出力はNTFS圧縮済みです。これらは作業フォルダー内に保持されています。ソースの`Tools/`は変更していません。自動レビューが再帰削除を拒否したため、手動削除は行っていません。

## 削除候補

以下は通常のビルドで再生成されるキャッシュ・中間生成物です。約19.86 GiBという値は2026-10-09時点のスナップショットであり、最新状態の合計ではありません。

| パス | 実測サイズ | 内容・扱い |
|---|---:|---|
| `.vs/` | 3.50 GiB（2026-10-10） | Visual Studio のキャッシュ・ワークスペース情報。Visual Studio を閉じてから整理候補にできます。 |
| `bin/` | 8.49 GiB（2026-10-10） | アプリのビルド出力。今回のリリース確認用win-x64発行物も含みます。保持する配布物を確認してから整理候補にできます。 |
| `obj/` | 0.09 GiB | .NET の中間生成物。次回ビルドで再生成されます。 |
| `Sharing/bin/`, `Sharing/obj/` | 約 0.002 GiB | LocalSend/P2P共有ライブラリの生成物。 |
| `Compression/bin/`, `Compression/obj/` | 約 0.002 GiB | 圧縮ライブラリの生成物。 |
| `UpdateAgent/bin/`, `UpdateAgent/obj/` | 約 0.14 GiB | 更新エージェントの生成物。今回発行したwin-x64実行物を含みます。 |
| `tests/<project>/bin/`, `tests/<project>/obj/` と `tests/AppUpdate.Integration/Fixtures/UpdateSentinel/{bin,obj}/` | 2.68 GiB（2026-10-10 Clean後） | 全テストプロジェクトをClean済み。`tests/Conversion.Integration/bin/`は0 bytesですが、`tests/Sharing.Preview/bin/Release/`の2.56 GiBはClean後も残っています。残存物の内容を確認してから整理候補にできます。 |
| `MediaConverter_mt233urd_wpftmp.csproj` | 58,300 bytes | WPF ビルド時に生成された一時プロジェクト。 |
| `MediaConverter_owhzpuz1_wpftmp.csproj` | 60,168 bytes | WPF ビルド時に生成された一時プロジェクト。 |
| `MediaConverter_vwbnzs0q_wpftmp.csproj` | 58,368 bytes | WPF ビルド時に生成された一時プロジェクト。 |
| `MediaConverter_xohpnbwx_wpftmp.csproj` | 60,168 bytes | WPF ビルド時に生成された一時プロジェクト。 |

一時プロジェクト 4 件の合計は 237,004 bytes（約 0.23 MiB）です。`.vs/`、各 `bin/`・`obj/`、`*_wpftmp.csproj` は `.gitignore` の対象ですが、無視設定だけではディスク上のファイルは消えません。

## 中身を確認してから判断する候補

2026-10-09時点のスナップショットでは `work/` は **99.27 GiB、858,377ファイル、直下129フォルダー**でした。ビルド・発行済みアプリの複製や、合成ファイルを使った転送テスト結果が混在していました。以下の上位30フォルダーだけで約88.17 GiBあります。2026-10-10 09:42 JSTの現況確認では `work/sharing-tests/` だけが存在し、その中は空です。

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
| `%LOCALAPPDATA%\Temp\EZConverter-Sharing-UI-f45ad4de64f245ca9d474cc18d5ff1b3\` | 135,244 bytes（4ファイル） | 2026-10-10のWPF統合試験がLocalSendアプリの53317番ポート競合で失敗した際に診断用に保持した合成UIデータ。動的テストポートへ変更した後の再実行は成功。 |
| `%LOCALAPPDATA%\Temp\EZConverter-Sharing-UI-6eea94415f264f4f8b2a9e84b0c4d876\` | 135,239 bytes（4ファイル） | 同上。 |
| `%LOCALAPPDATA%\Temp\EZConverter-Sharing-UI-e1bac89fbe224fa1ad2d0fd9751ac3df\` | 135,254 bytes（4ファイル） | 同上。 |

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

## 2026-10-10の追加テスト残留物

公式LocalSendアプリが起動している環境で `Sharing.Integration --live-localsend-discovery` を実行しましたが、30秒以内にLAN上の別ホストを検出できず、テストはタイムアウトしました。今回の失敗実行が生成した次のテスト用証明書が残っています。

| パス | サイズ | 内容・状態 |
|---|---:|---|
| `work/live-localsend-discovery/3c835ad89a3047e9932a20124905d4de/state/localsend-identity.dpapi` | 2,804 bytes | 失敗した実機発見テスト用に生成された証明書。`state/`にはこのファイルのみ、隣接する`received/`は空です。削除は自動レビューで拒否されたため保持しています。 |

同じPC上のLocalSend HTTPS `/api/localsend/v2/info` は応答しましたが、これは別端末での発見やファイル転送の合格証拠には数えていません。

## 2026-10-10のアップデート統合試験残留物

`AppUpdate.Integration` の隔離インストール試験が生成した更新起動用 `MediaConverter.exe` が、試験終了後もTempに残っています。各ファイルは151,552 bytes、3個合計454,656 bytesです。確認時に関連するテストプロセスは動いていません。削除は行っていません。

| パス | サイズ | 内容・状態 |
|---|---:|---|
| `%LOCALAPPDATA%\Temp\EZConverter-AppUpdate-Integration-23a4110cc428441dbee092a88c7d6eb3\agent-install\MediaConverter.exe` | 151,552 bytes | 隔離アップデート後のアプリ起動確認用センチネル。 |
| `%LOCALAPPDATA%\Temp\EZConverter-AppUpdate-Integration-52ece02eafde4597893b71d0ccc2c640\agent-install\MediaConverter.exe` | 151,552 bytes | 隔離アップデート後のアプリ起動確認用センチネル。 |
| `%LOCALAPPDATA%\Temp\EZConverter-AppUpdate-Integration-b49f0cb7e3234ff6b460db18904f3402\agent-install\MediaConverter.exe` | 151,552 bytes | 隔離アップデート後のアプリ起動確認用センチネル。 |

## 2026-10-10のLocalSend CLI相互運用試験残留物

以下は公式CLIとの転送試験に使った一時フォルダーです。試験生成物のサイズは合計66,222,924 bytes（約63.1 MiB）です。送信元と受信先を再走査し、空ファイル、日本語名2 MiB、9 MiBチャンク転送、および2 MiBの双方向ペイロードのサイズ・SHA-256一致を確認しました。外部受入れ完了まで証跡を保持し、削除していません。

| パス | サイズ | 内容・状態 |
|---|---:|---|
| `%LOCALAPPDATA%\Temp\EZConverter-LocalSendInterop-20261009\` | 56,496,972 bytes（101ファイル） | 公式CLI、合成ファイル、送受信結果、`ReceiverHarness`と`ZipMeasure`の一時ビルド出力。受信ファイルは送信元と同じSHA-256。 |
| `%LOCALAPPDATA%\Temp\EZConverter-LocalSend-b445786c6a694d8bb42320e446c7c66e\LocalSend-CLI-1.18.2-windows-x86-64.exe` | 9,725,952 bytes | 上記フォルダー内CLIの重複。両方のSHA-256は公式v1.18.2配布資産と一致（`CA0B267E7457324B3664A935DE4E4956DA701A7FA1045F6F99C72602F7F8DC38`）。 |
