using CloudMigrator.Core.State;
using CloudMigrator.Providers.Abstractions;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// 検証対象: SqliteTransferStateDb  目的: SQLite 状態 DB の CRUD・チェックポイント・ストリーミング取得
/// </summary>
public class SqliteTransferStateDbTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly SqliteTransferStateDb _db;

    public SqliteTransferStateDbTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        _db = new SqliteTransferStateDb(_dbPath);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static StorageItem MakeItem(string path, string name, string id = "id1", long? size = 1024)
        => new StorageItem { Id = id, Path = path, Name = name, SizeBytes = size, IsFolder = false };

    // ── InitializeAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task InitializeAsync_CreatesTablesSuccessfully()
    {
        // 検証対象: InitializeAsync  目的: スキーマ作成が冪等に成功する
        await _db.InitializeAsync(CancellationToken.None);
        // 2 回呼んでもエラーにならない（CREATE TABLE IF NOT EXISTS）
        var act = () => _db.InitializeAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ── UpsertPendingAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task UpsertPendingAsync_NewItem_InsertsAsPending()
    {
        // 検証対象: UpsertPendingAsync  目的: 未登録アイテムを pending で INSERT する
        await _db.InitializeAsync(CancellationToken.None);
        var item = MakeItem("docs", "file.txt");

        await _db.UpsertPendingAsync(item, CancellationToken.None);

        var status = await _db.GetStatusAsync("docs", "file.txt", CancellationToken.None);
        status.Should().Be(TransferStatus.Pending);
    }

    [Fact]
    public async Task UpsertPendingAsync_ExistingFailedItem_ResetsToZeroRetryCount()
    {
        // 検証対象: UpsertPendingAsync  目的: failed レコードを retry_count=0 でリセットする
        await _db.InitializeAsync(CancellationToken.None);
        var item = MakeItem("docs", "file.txt");
        await _db.UpsertPendingAsync(item, CancellationToken.None);
        await _db.MarkFailedAsync("docs", "file.txt", "error", CancellationToken.None);

        // 再度 UpsertPending → retry_count がリセットされて pending に戻る
        await _db.UpsertPendingAsync(item, CancellationToken.None);

        var status = await _db.GetStatusAsync("docs", "file.txt", CancellationToken.None);
        status.Should().Be(TransferStatus.Pending);
    }

    // ── GetStatusAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_NonExistentItem_ReturnsNull()
    {
        // 検証対象: GetStatusAsync  目的: 未登録アイテムは null を返す
        await _db.InitializeAsync(CancellationToken.None);

        var status = await _db.GetStatusAsync("nonexistent", "file.txt", CancellationToken.None);

        status.Should().BeNull();
    }

    // ── MarkDoneAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task MarkDoneAsync_UpdatesStatusToDone()
    {
        // 検証対象: MarkDoneAsync  目的: done への遷移が正しく行われる
        await _db.InitializeAsync(CancellationToken.None);
        var item = MakeItem("docs", "file.txt");
        await _db.UpsertPendingAsync(item, CancellationToken.None);

        await _db.MarkDoneAsync("docs", "file.txt", CancellationToken.None);

        var status = await _db.GetStatusAsync("docs", "file.txt", CancellationToken.None);
        status.Should().Be(TransferStatus.Done);
    }

    // ── MarkProcessingAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task MarkProcessingAsync_UpdatesStatusToProcessing()
    {
        // 検証対象: MarkProcessingAsync  目的: processing への遷移が正しく行われる
        await _db.InitializeAsync(CancellationToken.None);
        var item = MakeItem("docs", "file.txt");
        await _db.UpsertPendingAsync(item, CancellationToken.None);

        await _db.MarkProcessingAsync("docs", "file.txt", CancellationToken.None);

        var status = await _db.GetStatusAsync("docs", "file.txt", CancellationToken.None);
        status.Should().Be(TransferStatus.Processing);
    }

    // ── MarkFailedAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task MarkFailedAsync_BelowMaxRetry_SetsFailed()
    {
        // 検証対象: MarkFailedAsync  目的: MaxRetry 未満のとき failed に遷移する
        await _db.InitializeAsync(CancellationToken.None);
        var item = MakeItem("docs", "file.txt");
        await _db.UpsertPendingAsync(item, CancellationToken.None);

        // 1 回失敗（MaxRetry=3 なので failed に留まる）
        await _db.MarkFailedAsync("docs", "file.txt", "err", CancellationToken.None);

        var status = await _db.GetStatusAsync("docs", "file.txt", CancellationToken.None);
        status.Should().Be(TransferStatus.Failed);
    }

    [Fact]
    public async Task MarkFailedAsync_ReachesMaxRetry_SetsPermanentFailed()
    {
        // 検証対象: MarkFailedAsync  目的: MaxRetry 回失敗で permanent_failed に遷移する
        await _db.InitializeAsync(CancellationToken.None);
        var item = MakeItem("docs", "file.txt");
        await _db.UpsertPendingAsync(item, CancellationToken.None);

        // MaxRetry 回失敗させる
        for (var i = 0; i < SqliteTransferStateDb.MaxRetry; i++)
            await _db.MarkFailedAsync("docs", "file.txt", "err", CancellationToken.None);

        var status = await _db.GetStatusAsync("docs", "file.txt", CancellationToken.None);
        status.Should().Be(TransferStatus.PermanentFailed);
    }

    // ── ResetPermanentFailedAsync ────────────────────────────────────────────

    [Fact]
    public async Task ResetPermanentFailedAsync_WithPermanentFailedRecords_ResetsToFailedAndReturnsCount()
    {
        // 検証対象: ResetPermanentFailedAsync  目的: permanent_failed が failed に戻り件数を返す
        await _db.InitializeAsync(CancellationToken.None);
        var item = MakeItem("docs", "file.txt");
        await _db.UpsertPendingAsync(item, CancellationToken.None);
        for (var i = 0; i < SqliteTransferStateDb.MaxRetry; i++)
            await _db.MarkFailedAsync("docs", "file.txt", "err", CancellationToken.None);
        (await _db.GetStatusAsync("docs", "file.txt", CancellationToken.None))
            .Should().Be(TransferStatus.PermanentFailed);

        var resetCount = await _db.ResetPermanentFailedAsync(CancellationToken.None);

        resetCount.Should().Be(1);
        (await _db.GetStatusAsync("docs", "file.txt", CancellationToken.None))
            .Should().Be(TransferStatus.Failed);
    }

    [Fact]
    public async Task ResetPermanentFailedAsync_NoPermanentFailedRecords_ReturnsZero()
    {
        // 検証対象: ResetPermanentFailedAsync  目的: 対象がなければ 0 を返す
        await _db.InitializeAsync(CancellationToken.None);

        var resetCount = await _db.ResetPermanentFailedAsync(CancellationToken.None);

        resetCount.Should().Be(0);
    }

    // ── ResetAllAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ResetAllAsync_ClearsAllTablesAndLeavesDbAccessible()
    {
        // 検証対象: ResetAllAsync  目的: transfer_records / checkpoints / metrics の全データが削除され、DB は引き続き使用可能
        await _db.InitializeAsync(CancellationToken.None);
        var item = MakeItem("docs", "file.txt");
        await _db.UpsertPendingAsync(item, CancellationToken.None);
        await _db.SaveCheckpointAsync("cursor", "abc", CancellationToken.None);
        await _db.RecordMetricAsync("rate_limit_pct", 1.5, CancellationToken.None);

        await _db.ResetAllAsync(CancellationToken.None);

        // transfer_records: 削除済み
        (await _db.GetStatusAsync("docs", "file.txt", CancellationToken.None)).Should().BeNull();
        // checkpoints: 削除済み
        (await _db.GetCheckpointAsync("cursor", CancellationToken.None)).Should().BeNull();
        // DB は正常に使用可能（再 INSERT できる）
        await _db.UpsertPendingAsync(item, CancellationToken.None);
        (await _db.GetStatusAsync("docs", "file.txt", CancellationToken.None)).Should().Be(TransferStatus.Pending);
    }

    // ── InsertPendingIfNewAsync ───────────────────────────────────────────────

    [Fact]
    public async Task InsertPendingIfNewAsync_NewItem_InsertsAndReturnsTrue()
    {
        // 検証対象: InsertPendingIfNewAsync  目的: 未登録アイテムが pending で INSERT され true を返す
        await _db.InitializeAsync(CancellationToken.None);
        var item = MakeItem("docs", "new.txt");

        var inserted = await _db.InsertPendingIfNewAsync(item, CancellationToken.None);

        inserted.Should().BeTrue();
        (await _db.GetStatusAsync("docs", "new.txt", CancellationToken.None)).Should().Be(TransferStatus.Pending);
    }

    [Fact]
    public async Task InsertPendingIfNewAsync_ExistingItem_DoesNothingAndReturnsFalse()
    {
        // 検証対象: InsertPendingIfNewAsync  目的: 既存アイテムは変更されず false を返す（ON CONFLICT DO NOTHING）
        await _db.InitializeAsync(CancellationToken.None);
        var item = MakeItem("docs", "existing.txt");
        await _db.UpsertPendingAsync(item, CancellationToken.None);
        // done に変更しておく（既存レコードが上書きされないことを確認）
        await _db.MarkDoneAsync("docs", "existing.txt", CancellationToken.None);

        var inserted = await _db.InsertPendingIfNewAsync(item, CancellationToken.None);

        inserted.Should().BeFalse();
        // done のまま変わっていないことを確認
        (await _db.GetStatusAsync("docs", "existing.txt", CancellationToken.None)).Should().Be(TransferStatus.Done);
    }

    // ── チェックポイント ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetCheckpointAsync_NonExistent_ReturnsNull()
    {
        // 検証対象: GetCheckpointAsync  目的: 未保存キーは null を返す
        await _db.InitializeAsync(CancellationToken.None);

        var value = await _db.GetCheckpointAsync("cursor", CancellationToken.None);

        value.Should().BeNull();
    }

    [Fact]
    public async Task SaveAndGetCheckpointAsync_RoundTrip()
    {
        // 検証対象: SaveCheckpointAsync + GetCheckpointAsync  目的: 保存した値が取得できる
        await _db.InitializeAsync(CancellationToken.None);

        await _db.SaveCheckpointAsync("cursor", "abc123", CancellationToken.None);
        var value = await _db.GetCheckpointAsync("cursor", CancellationToken.None);

        value.Should().Be("abc123");
    }

    [Fact]
    public async Task SaveCheckpointAsync_Overwrite_UpdatesValue()
    {
        // 検証対象: SaveCheckpointAsync  目的: 同じキーへの上書き保存が正しく動作する
        await _db.InitializeAsync(CancellationToken.None);

        await _db.SaveCheckpointAsync("cursor", "first", CancellationToken.None);
        await _db.SaveCheckpointAsync("cursor", "second", CancellationToken.None);
        var value = await _db.GetCheckpointAsync("cursor", CancellationToken.None);

        value.Should().Be("second");
    }

    // ── GetPendingStreamAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetPendingStreamAsync_ReturnsPendingProcessingFailed()
    {
        // 検証対象: GetPendingStreamAsync  目的: pending/processing/failed のみを返す（done/permanent_failed は除外）
        await _db.InitializeAsync(CancellationToken.None);

        await _db.UpsertPendingAsync(MakeItem("p", "pending.txt", "id1"), CancellationToken.None);
        await _db.UpsertPendingAsync(MakeItem("p", "processing.txt", "id2"), CancellationToken.None);
        await _db.UpsertPendingAsync(MakeItem("p", "failed.txt", "id3"), CancellationToken.None);
        await _db.UpsertPendingAsync(MakeItem("p", "done.txt", "id4"), CancellationToken.None);

        await _db.MarkProcessingAsync("p", "processing.txt", CancellationToken.None);
        await _db.MarkFailedAsync("p", "failed.txt", "err", CancellationToken.None);
        await _db.MarkDoneAsync("p", "done.txt", CancellationToken.None);

        var records = new List<TransferRecord>();
        await foreach (var r in _db.GetPendingStreamAsync(CancellationToken.None))
            records.Add(r);

        records.Should().HaveCount(3);
        records.Select(r => r.Name).Should().BeEquivalentTo(
            ["pending.txt", "processing.txt", "failed.txt"]);
    }

    [Fact]
    public async Task GetPendingStreamAsync_IncludesSourceId()
    {
        // 検証対象: GetPendingStreamAsync  目的: source_id が正しく返される（クラッシュリカバリ向け）
        await _db.InitializeAsync(CancellationToken.None);
        await _db.UpsertPendingAsync(MakeItem("docs", "file.txt", "onedrive-id-xyz"), CancellationToken.None);

        TransferRecord? record = null;
        await foreach (var r in _db.GetPendingStreamAsync(CancellationToken.None))
            record = r;

        record.Should().NotBeNull();
        record!.SourceId.Should().Be("onedrive-id-xyz");
    }

    // ── GetSummaryAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetSummaryAsync_EmptyDb_ReturnsAllZeros()
    {
        // 検証対象: GetSummaryAsync  目的: DB が空の場合はすべてゼロのサマリーを返す
        await _db.InitializeAsync(CancellationToken.None);

        var summary = await _db.GetSummaryAsync(CancellationToken.None);

        summary.Pending.Should().Be(0);
        summary.Processing.Should().Be(0);
        summary.Done.Should().Be(0);
        summary.Failed.Should().Be(0);
        summary.PermanentFailed.Should().Be(0);
        summary.TotalDoneSizeBytes.Should().Be(0L);
        summary.Total.Should().Be(0);
        summary.CompletionRate.Should().Be(0.0);
        summary.FirstUpdatedAt.Should().BeNull();
        summary.LastUpdatedAt.Should().BeNull();
        summary.RecentFailed.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSummaryAsync_MixedStatuses_ReturnsCorrectCounts()
    {
        // 検証対象: GetSummaryAsync  目的: 各ステータス件数が正しく集計される
        await _db.InitializeAsync(CancellationToken.None);

        // 2 pending, 1 processing, 3 done, 1 failed, 1 permanent_failed を作成
        await _db.UpsertPendingAsync(MakeItem("p", "a.txt", "id1", 100), CancellationToken.None);
        await _db.UpsertPendingAsync(MakeItem("p", "b.txt", "id2", 200), CancellationToken.None);
        await _db.UpsertPendingAsync(MakeItem("p", "c.txt", "id3", 300), CancellationToken.None);
        await _db.UpsertPendingAsync(MakeItem("p", "d.txt", "id4", 400), CancellationToken.None);
        await _db.UpsertPendingAsync(MakeItem("p", "e.txt", "id5", 500), CancellationToken.None);
        await _db.UpsertPendingAsync(MakeItem("p", "f.txt", "id6", 600), CancellationToken.None);
        await _db.UpsertPendingAsync(MakeItem("p", "g.txt", "id7", 700), CancellationToken.None);
        await _db.UpsertPendingAsync(MakeItem("p", "h.txt", "id8", 800), CancellationToken.None);

        await _db.MarkProcessingAsync("p", "c.txt", CancellationToken.None);
        await _db.MarkDoneAsync("p", "d.txt", CancellationToken.None);
        await _db.MarkDoneAsync("p", "e.txt", CancellationToken.None);
        await _db.MarkDoneAsync("p", "f.txt", CancellationToken.None);
        await _db.MarkFailedAsync("p", "g.txt", "timeout", CancellationToken.None);
        // permanent_failed: MaxRetry 回失敗させる
        for (var i = 0; i < SqliteTransferStateDb.MaxRetry; i++)
            await _db.MarkFailedAsync("p", "h.txt", "fatal", CancellationToken.None);

        var summary = await _db.GetSummaryAsync(CancellationToken.None);

        summary.Pending.Should().Be(2);
        summary.Processing.Should().Be(1);
        summary.Done.Should().Be(3);
        summary.Failed.Should().Be(1);
        summary.PermanentFailed.Should().Be(1);
        summary.Total.Should().Be(8);
    }

    [Fact]
    public async Task GetSummaryAsync_DoneItems_SumsSizeBytes()
    {
        // 検証対象: GetSummaryAsync  目的: done ファイルのバイト数のみが TotalDoneSizeBytes に集計される
        await _db.InitializeAsync(CancellationToken.None);

        await _db.UpsertPendingAsync(MakeItem("p", "a.txt", "id1", 1000), CancellationToken.None);
        await _db.UpsertPendingAsync(MakeItem("p", "b.txt", "id2", 2000), CancellationToken.None);
        await _db.UpsertPendingAsync(MakeItem("p", "c.txt", "id3", 4000), CancellationToken.None);
        await _db.MarkDoneAsync("p", "a.txt", CancellationToken.None);
        await _db.MarkDoneAsync("p", "b.txt", CancellationToken.None);
        // c.txt は pending のまま（集計に含まれないはず）

        var summary = await _db.GetSummaryAsync(CancellationToken.None);

        summary.TotalDoneSizeBytes.Should().Be(3000L); // 1000 + 2000
    }

    [Fact]
    public async Task GetSummaryAsync_CompletionRate_IsCalculatedCorrectly()
    {
        // 検証対象: GetSummaryAsync  目的: CompletionRate が done/total × 100 で計算される
        await _db.InitializeAsync(CancellationToken.None);

        await _db.UpsertPendingAsync(MakeItem("p", "a.txt", "id1"), CancellationToken.None);
        await _db.UpsertPendingAsync(MakeItem("p", "b.txt", "id2"), CancellationToken.None);
        await _db.UpsertPendingAsync(MakeItem("p", "c.txt", "id3"), CancellationToken.None);
        await _db.UpsertPendingAsync(MakeItem("p", "d.txt", "id4"), CancellationToken.None);
        await _db.MarkDoneAsync("p", "a.txt", CancellationToken.None);
        await _db.MarkDoneAsync("p", "b.txt", CancellationToken.None);

        var summary = await _db.GetSummaryAsync(CancellationToken.None);

        summary.CompletionRate.Should().BeApproximately(50.0, precision: 0.01);
    }

    [Fact]
    public async Task GetSummaryAsync_RecentFailed_ReturnsAtMostFive()
    {
        // 検証対象: GetSummaryAsync  目的: RecentFailed は最大5件で新しい順に返される
        await _db.InitializeAsync(CancellationToken.None);

        // 7 件を failed にする
        for (var i = 1; i <= 7; i++)
        {
            await _db.UpsertPendingAsync(MakeItem("p", $"f{i}.txt", $"id{i}"), CancellationToken.None);
            await _db.MarkFailedAsync("p", $"f{i}.txt", $"error{i}", CancellationToken.None);
        }

        var summary = await _db.GetSummaryAsync(CancellationToken.None);

        summary.RecentFailed.Should().HaveCount(5);
        // 新しい順（f7→f3）に並んでいることを検証（f1, f2 は除外される）
        summary.RecentFailed.Select(f => f.Name)
            .Should().ContainInOrder("f7.txt", "f6.txt", "f5.txt", "f4.txt", "f3.txt");
        // エラーメッセージが正しく含まれている
        summary.RecentFailed.All(f => f.Error != null).Should().BeTrue();
    }

    [Fact]
    public async Task GetSummaryAsync_TotalRetries_SumsRetryCount()
    {
        // 検証対象: GetSummaryAsync  目的: 全レコードの retry_count が TotalRetries に合算される
        await _db.InitializeAsync(CancellationToken.None);

        // a.txt: 1 回失敗 → retry_count = 1
        await _db.UpsertPendingAsync(MakeItem("p", "a.txt", "id1"), CancellationToken.None);
        await _db.MarkFailedAsync("p", "a.txt", "err", CancellationToken.None);

        // b.txt: 2 回失敗 → retry_count = 2
        await _db.UpsertPendingAsync(MakeItem("p", "b.txt", "id2"), CancellationToken.None);
        await _db.MarkFailedAsync("p", "b.txt", "err", CancellationToken.None);
        await _db.MarkFailedAsync("p", "b.txt", "err", CancellationToken.None);

        // c.txt: リトライなし → retry_count = 0
        await _db.UpsertPendingAsync(MakeItem("p", "c.txt", "id3"), CancellationToken.None);

        var summary = await _db.GetSummaryAsync(CancellationToken.None);

        summary.TotalRetries.Should().Be(3L); // a.txt(1) + b.txt(2) + c.txt(0)
    }

    // ── RecordMetricAsync / GetMetricsAsync ──────────────────────────────────

    [Fact]
    public async Task RecordMetricAsync_ThenGetMetricsAsync_ReturnsInsertedValues()
    {
        // 検証対象: RecordMetricAsync / GetMetricsAsync  目的: 書き込んだメトリクスが取得できる
        await _db.InitializeAsync(CancellationToken.None);

        await _db.RecordMetricAsync("test_metric", 42.5, CancellationToken.None);
        await _db.RecordMetricAsync("test_metric", 99.0, CancellationToken.None);

        var results = await _db.GetMetricsAsync("test_metric", 60, CancellationToken.None);

        results.Should().HaveCount(2);
        results[0].Name.Should().Be("test_metric");
        results[0].Value.Should().Be(42.5);
        results[1].Value.Should().Be(99.0);
    }

    [Fact]
    public async Task GetMetricsAsync_AscendingOrder_ReturnsOldestFirst()
    {
        // 検証対象: GetMetricsAsync  目的: timestamp ASC で返される
        await _db.InitializeAsync(CancellationToken.None);

        await _db.RecordMetricAsync("order_metric", 1.0, CancellationToken.None);
        await Task.Delay(10); // タイムスタンプに差をつける
        await _db.RecordMetricAsync("order_metric", 2.0, CancellationToken.None);
        await Task.Delay(10);
        await _db.RecordMetricAsync("order_metric", 3.0, CancellationToken.None);

        var results = await _db.GetMetricsAsync("order_metric", 60, CancellationToken.None);

        results.Should().HaveCount(3);
        results.Select(p => p.Value).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task GetMetricsAsync_DifferentName_ReturnsEmptyList()
    {
        // 検証対象: GetMetricsAsync  目的: 別名のメトリクスはフィルタされる
        await _db.InitializeAsync(CancellationToken.None);

        await _db.RecordMetricAsync("metric_a", 10.0, CancellationToken.None);

        var results = await _db.GetMetricsAsync("metric_b", 60, CancellationToken.None);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMetricsAsync_OldRecordsOutsideWindow_AreExcluded()
    {
        // 検証対象: GetMetricsAsync  目的: recentMinutes 時間外のレコードは除外される
        await _db.InitializeAsync(CancellationToken.None);

        // 過去のレコードを直接 INSERT（2 時間前）
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO metrics (timestamp, name, value)
            VALUES (@ts, @name, @value);
            """;
        var oldTs = DateTimeOffset.UtcNow.AddHours(-2).ToString("O");
        cmd.Parameters.AddWithValue("@ts", oldTs);
        cmd.Parameters.AddWithValue("@name", "window_metric");
        cmd.Parameters.AddWithValue("@value", 999.0);
        await cmd.ExecuteNonQueryAsync();

        // 直近60分以内のレコードを追加
        await _db.RecordMetricAsync("window_metric", 1.0, CancellationToken.None);

        var results = await _db.GetMetricsAsync("window_metric", 60, CancellationToken.None);

        // 2時間前のレコードは除外され、直近のみ返る
        results.Should().HaveCount(1);
        results[0].Value.Should().Be(1.0);
    }

    [Fact]
    public async Task RecordMetricAsync_Timestamp_IsStoredAsRoundtripFormat()
    {
        // 検証対象: RecordMetricAsync  目的: Timestamp が "O" フォーマットで往復変換できる
        await _db.InitializeAsync(CancellationToken.None);

        var before = DateTimeOffset.UtcNow;
        await _db.RecordMetricAsync("ts_check", 1.0, CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        var results = await _db.GetMetricsAsync("ts_check", 60, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Timestamp.Should().BeOnOrAfter(before.AddSeconds(-1));
        results[0].Timestamp.Should().BeOnOrBefore(after.AddSeconds(1));
    }
}
