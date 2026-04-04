using CloudMigrator.Core.State;
using CloudMigrator.Providers.Abstractions;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// 検証対象: NullTransferStateDb
/// 目的: Null Object パターン実装がすべてのメソッドを安全に（例外なく）実行し、
///       空/デフォルト値を返すことを確認する
/// </summary>
public sealed class NullTransferStateDbTests
{
    private readonly NullTransferStateDb _db = NullTransferStateDb.Instance;
    private readonly CancellationToken _ct = CancellationToken.None;
    private readonly StorageItem _item = new()
    {
        Id = "id1",
        Name = "file.txt",
        Path = "/folder",
        SizeBytes = 100,
        LastModifiedUtc = DateTimeOffset.UtcNow,
        IsFolder = false,
    };

    [Fact]
    public async Task InitializeAsync_DoesNotThrow()
    {
        // 検証対象: InitializeAsync  目的: 例外なく完了する
        var act = async () => await _db.InitializeAsync(_ct);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsNull()
    {
        // 検証対象: GetStatusAsync  目的: 常に null を返す
        var result = await _db.GetStatusAsync("/path", "file.txt", _ct);
        result.Should().BeNull();
    }

    [Fact]
    public async Task UpsertPendingAsync_DoesNotThrow()
    {
        // 検証対象: UpsertPendingAsync  目的: 例外なく完了する（書き込みは無視）
        var act = async () => await _db.UpsertPendingAsync(_item, _ct);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetSummaryAsync_ReturnsEmptySummary()
    {
        // 検証対象: GetSummaryAsync  目的: 全ゼロのサマリーを返す
        var summary = await _db.GetSummaryAsync(_ct);
        summary.Total.Should().Be(0);
        summary.Done.Should().Be(0);
        summary.Pending.Should().Be(0);
        summary.Failed.Should().Be(0);
        summary.CompletionRate.Should().Be(0.0);
    }

    [Fact]
    public async Task GetMetricsAsync_ReturnsEmptyList()
    {
        // 検証対象: GetMetricsAsync  目的: 空リストを返す
        var metrics = await _db.GetMetricsAsync("rate_limit_pct", 60, _ct);
        metrics.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCheckpointAsync_ReturnsNull()
    {
        // 検証対象: GetCheckpointAsync  目的: 常に null を返す
        var result = await _db.GetCheckpointAsync("some_key", _ct);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetPendingStreamAsync_ReturnsEmptySequence()
    {
        // 検証対象: GetPendingStreamAsync  目的: 要素なしのシーケンスを返す
        var items = new List<TransferRecord>();
        await foreach (var record in _db.GetPendingStreamAsync(_ct))
            items.Add(record);
        items.Should().BeEmpty();
    }

    [Fact]
    public async Task InsertPendingIfNewAsync_ReturnsFalse()
    {
        // 検証対象: InsertPendingIfNewAsync  目的: 常に false を返す（挿入なし）
        var result = await _db.InsertPendingIfNewAsync(_item, _ct);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetDistinctFolderPathsAsync_ReturnsEmptyList()
    {
        // 検証対象: GetDistinctFolderPathsAsync  目的: 空リストを返す
        var paths = await _db.GetDistinctFolderPathsAsync(_ct);
        paths.Should().BeEmpty();
    }

    [Fact]
    public async Task ResetPermanentFailedAsync_ReturnsZero()
    {
        // 検証対象: ResetPermanentFailedAsync  目的: 0 を返す（操作なし）
        var result = await _db.ResetPermanentFailedAsync(_ct);
        result.Should().Be(0);
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        // 検証対象: DisposeAsync  目的: 例外なく完了する
        var act = async () => await _db.DisposeAsync();
        await act.Should().NotThrowAsync();
    }
}
