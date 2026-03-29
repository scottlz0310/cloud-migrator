# データフロー

## 全体フロー

```text
config.json + env vars
  -> AppConfiguration
  -> CliServices
  -> source/destination provider 決定
  -> migration pipeline 実行
  -> SQLite state DB 更新
  -> logs / metrics 出力
  -> status/dashboard/quality-gate から参照
```

## 1. 起動時のデータフロー

1. `Program.cs` が `transfer`, `status`, `dashboard`, `quality-metrics` などのコマンドを公開します。
2. `TransferCommand` が `CliServices.Build()` を呼び、設定、ロガー、Graph/Dropbox クライアント、状態 DB 周辺を組み立てます。
3. `MigratorOptions.DestinationProvider` に応じて、SharePoint パイプラインか Dropbox パイプラインを選択します。
4. `ConfigHashChecker` が設定差分を検知した場合は、キャッシュや skip list をクリアして実行をやり直せる状態に戻します。

## 2. 設定と資格情報の流れ

```text
configs/config.json
  + MIGRATOR__* env vars
    -> AppConfiguration.Build()
      -> MigratorOptions
      -> Graph client secret / Dropbox token 系を直接取得
        -> GraphAuthenticator / DropboxStorageProvider
```

- 非機密設定は `MigratorOptions` にバインドされます。
- シークレットは `AppConfiguration` の専用メソッドで取得し、`config.json` には保持しません。
- ログ出力先、状態 DB、転送先ルート、並列度、リトライ設定もこの段階で確定します。

## 3. SharePoint 移行フロー

```text
OneDrive (Graph)
  -> Phase A: processing reset
  -> Phase B: ListPagedAsync でクロール
  -> SqliteTransferStateDb.transfer_records/checkpoints
  -> Phase C: distinct path からフォルダ作成
  -> Phase D: pending/failed を Channel に投入
  -> Graph Upload (SharePoint)
  -> done / failed / permanent_failed 更新
  -> metrics 記録
```

### Phase A

- 前回クラッシュで残った `processing` を `pending` へ戻します。
- `permanent_failed` も再試行対象に戻せるよう `failed` へ復帰します。

### Phase B

- `GraphStorageProvider.ListPagedAsync` が OneDrive をページ単位で取得します。
- `@odata.nextLink` / `@odata.deltaLink` 相当のカーソルを `checkpoints` に保存し、中断再開と差分クロールを成立させます。
- ファイル単位の情報は SQLite の `transfer_records` に保存されます。

### Phase C

- `transfer_records.path` から祖先フォルダを展開し、深さ順に `EnsureFolderAsync` を実行します。
- 完了件数は `sp_folder_done` メトリクスとして蓄積され、ダッシュボードが参照します。

### Phase D

- `GetPendingStreamAsync()` が `pending`, `processing`, `failed` を読み出します。
- `Channel` と `Parallel.ForEachAsync` がジョブをワーカーへ配布します。
- `AdaptiveConcurrencyController` が有効なら、429/503 や成功数を見て並列度を増減します。
- 転送結果は `done` / `failed` / `permanent_failed` に反映されます。

## 4. Dropbox 移行フロー

```text
OneDrive (Graph)
  -> Phase A: reset + permanent_failed reopen
  -> Phase B: 新規ファイルのみ SQLite へ insert
  -> Phase D: SQLite をストリーム読み出し
  -> DownloadStreamAsync / UploadFromStreamAsync
  -> Dropbox upload session or simple upload
  -> metrics / checkpoints 更新
```

- SharePoint フローと違い、通常はフォルダ先行作成を行いません。
- Dropbox 側はアップロード時の親フォルダ自動作成を前提にし、API 呼び出し数を抑えます。
- ダウンロードとアップロードの所要時間、リトライ回数、レート制限率がログと SQLite メトリクスへ反映されます。

## 5. 状態 DB とダッシュボードの流れ

```text
migration pipeline
  -> transfer_records
  -> checkpoints
  -> metrics
    -> status command
    -> dashboard command
      -> DashboardServer Minimal API
        -> browser UI
```

- `status` は SQLite を直接読んでテキストダッシュボードを表示します。
- `dashboard` は `DashboardServer` を起動し、SQLite の集計結果を JSON API として公開します。
- Web UI は `rate_limit_pct`, `throughput_files_per_min`, `throughput_bytes_per_sec`, `current_parallelism` を定期ポーリングします。

## 6. ログと障害回復

- すべての主要コマンドは `LoggingSetup` を通して UTC の構造化ログを出力します。
- 転送の途中状態は SQLite に保存されるため、プロセス再起動後も `processing -> pending` リセットで復旧できます。
- `watchdog` はログ更新停止を検知して `transfer` を再起動する設計です。

## 7. CI のカバレッジフロー

```text
dotnet test --collect:\"XPlat Code Coverage\"
  -> TestResults/<guid>/coverage.cobertura.xml
  -> GitHub Actions artifact
  -> Codecov upload
  -> quality-metrics command
```

- unit テスト実行時に Cobertura XML が生成されます。
- CI は XML の実ファイル位置を検索してから、Codecov と `quality-metrics` に同じレポートを渡します。
- これにより、`results-directory` 配下で GUID サブディレクトリが付く coverlet の実際の出力形式に追従できます。
