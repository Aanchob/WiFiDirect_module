# Hide Chat (`direct_module`)

Windows 10/11 上で近くの端末を BLE と Wi-Fi Direct で見つけ、端末間の TCP 接続でメッセージとファイルを交換する WinUI 3 チャットアプリです。外部のチャットサーバーやクラウドアカウントは使用しません。

## 動作要件

- Windows 10 version 1809 (build 17763) 以降
- BLE と Wi-Fi Direct に対応した Windows PC、および有効な Bluetooth/Wi-Fi アダプター
- ビルドには対象 Windows App SDK をサポートする Visual Studio と Windows アプリ開発 workload、.NET 8 SDK、MSIX 用ツールが必要
- TCP ポート `50001` の端末間通信を許可するファイアウォール設定

ハードウェア、ドライバー、Windows のプライバシー設定や組織ポリシーによっては BLE/Wi-Fi Direct の探索や接続を利用できません。

## ビルドと実行

Visual Studio で `direct_module.slnx` を開き、`x64`（または実機に合う `x86` / `ARM64`）を選択して復元・ビルド・実行します。コマンドラインでは Developer PowerShell から次を実行できます。

```powershell
dotnet restore .\direct_module.csproj
dotnet build .\direct_module.csproj -c Debug -p:Platform=x64
dotnet test .\tests\direct_module.CoreTests\direct_module.CoreTests.csproj -c Release
dotnet publish .\direct_module.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained false
```

NuGet の解決結果はプロジェクトごとの `packages.lock.json` に記録します。依存関係を更新しない検証では `dotnet restore --locked-mode` を使用すると、意図しない推移的依存関係の変更を検出できます。テストプロジェクトはソース共有型かつ x64 専用のため、x86 / ARM64 を含むアプリのソリューション構成からは分離しています。

RID を指定した Release 発行では ReadyToRun を有効にしています。`Platform` と RID は `x86` / `win-x86`、`x64` / `win-x64`、`ARM64` / `win-arm64` の対応で指定してください。通常の Release ビルドは RID 非依存です。WinRT、WinUI、SQLite を含むトリミング互換性は未検証のため、`PublishTrimmed` は無効です。

## 実装済みの機能

- BLE および Wi-Fi Direct による相手探索と接続ロール調整
- IP アドレスを指定した TCP 接続
- 1対1チャットと、ホストが1ホップ中継するグループチャット
- 最大 50 MiB のファイル送受信、進捗表示、受信完了 ACK、Downloads への保存
- 会話ごとの SQLite 履歴（1会話あたり最大 5,000 件、画面表示は直近 500 件）
- 切断検知、PING/PONG、手動再接続、診断ログ

## セキュリティモデル

- 接続ごとに一時 ECDH 鍵を交換し、安定した ECDSA 端末鍵でハンドシェイクを署名します。メッセージフレームは派生したセッション鍵による AES-GCM で暗号化・認証します。
- 初回接続時は暗号 ID（SHA-256 fingerprint）を画面に表示し、利用者の承認後に TOFU pin として保存します。以後、同じ Peer ID で fingerprint が変わった接続は拒否します。
- pin ストアが破損した場合は接続を fail-closed で拒否します。復旧ダイアログで全 pin の喪失を明示的に承認した場合だけ初期化し、以後すべての相手を再照合します。
- ローカル Peer ID または署名秘密鍵が欠損・破損した場合も通信を fail-closed で停止します。明示確認後の復旧操作では旧 ID 一式を隔離するだけで、同じプロセス内に新しい ID は作りません。再起動後は新しい端末 ID となるため、相手側で再確認が必要です。
- 端末秘密鍵、Peer ID、pin ストア、会話履歴の機微な列は現在の Windows ユーザーに紐づくデータ保護を使用します。
- `chat.db` はデータベース全体の暗号化ではありません。本文、相手名、各種 ID、ファイル情報、種別・フラグは保護されますが、保持期限と安定した時系列表示に必要な送信時刻、SQLite の行 ID・件数、および keyed lookup token が同一会話・同一メッセージかどうかという等価関係はローカル DB を読める相手に見えます。lookup token の元 ID と内容は token だけからは復元できません。
- PC の `MachineName` は表示名として BLE/Wi-Fi Direct の近隣端末および接続相手へ広告・送信されます。
- 添付ファイル、Downloads へ保存したコピー、送信用キャッシュのファイル本体はアプリ内暗号化の対象外で、Windows のユーザー ACL に依存します。
- 探索情報だけで相手を完全に本人確認できるわけではありません。初回承認時は相手側画面など別経路で fingerprint を照合してください。
- 受信ファイルは自動実行しませんが、開く操作は関連付けアプリに渡します。信頼できないファイルを開かないでください。

この仕組みは端末間通信を保護する実装であり、第三者監査済みの暗号プロトコルやインターネット越しの匿名化機能を提供するものではありません。

## データ保存先

通常は `%LOCALAPPDATA%\Aanchob\WiFiDirect_module` 以下を使用します。

- `chat.db`: 会話履歴
- `Attachments\`: 受信した添付ファイル（一時・古いファイルは定期整理）
- `identity.dat`, `chat-identity.key`, pin ストア: ローカル端末 ID と暗号 ID
- 送信前に作成するファイルのスナップショット: Windows アプリの LocalCache `outgoing`（利用できない場合はアプリデータ配下）

「保存」を選んだ添付は現在の Windows ユーザーの Downloads フォルダーへコピーされます。実際に解決された DB/添付/Downloads パスは起動時ログにも表示されます。

## 制限事項

- インターネット中継、オフライン配送、アカウント同期はありません。相手が同時に起動し、直接接続できる必要があります。
- グループ通信はメッシュではなく、受信側ホストによる1ホップ中継です。ホスト切断中の配送保証はありません。
- Windows の Bluetooth/Wi-Fi Direct 実装とアダプタードライバーに依存するため、端末の組み合わせによって接続性が異なります。
- 受信添付は 30 日、未完了ファイルと送信用キャッシュは短期間で整理される実装です。送信用スナップショットは最大 256 件 / 合計 1 GiB で、上限時は古い順に削除されるため、必要なファイルは Downloads へ保存してください。
- 役割・transcript・鍵導出を相互に束縛する署名付きハンドシェイク v3 は、旧 v2 以下のビルドと互換性がありません。
- BLE Company ID `0x1234` と Wi-Fi Direct vendor OUI `44-43-48` は試作用です。製品として配布する前に、正式な割り当てまたは製品固有の一意な値へ変更してください。
