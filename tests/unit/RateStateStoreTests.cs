using CloudMigrator.Core.Transfer;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// <see cref="RateStateStore"/> のユニットテスト（#163）。
/// <para>
/// v2 形式の書込・読込、v0.5.x 形式（<c>rate</c> キーのみ）の後方互換読込、
/// 壊れた JSON・空ファイル・未存在ファイルでのフォールバック（<c>null</c>）を検証する。
/// </para>
/// </summary>
public sealed class RateStateStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public RateStateStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rate_state_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "rate_state.json");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* テスト後処理失敗は無視 */ }
    }

    [Fact]
    public async Task SaveAsync_Then_Load_ReturnsV2State()
    {
        var store = new RateStateStore(_filePath);
        await store.SaveAsync(rateTokensPerSec: 12.5, maxInflight: 14);

        var loaded = store.Load();
        loaded.Should().NotBeNull();
        loaded!.Format.Should().Be(RateStateFormat.V2);
        loaded.RateTokensPerSec.Should().Be(12.5);
        loaded.MaxInflight.Should().Be(14);
    }

    [Fact]
    public void Load_ReturnsNull_WhenFileDoesNotExist()
    {
        var store = new RateStateStore(_filePath);
        store.Load().Should().BeNull();
    }

    [Fact]
    public void Load_ReturnsNull_WhenFileIsEmpty()
    {
        File.WriteAllText(_filePath, string.Empty);
        var store = new RateStateStore(_filePath);
        store.Load().Should().BeNull();
    }

    [Fact]
    public void Load_ReturnsNull_WhenJsonIsCorrupted()
    {
        File.WriteAllText(_filePath, "{invalid json");
        var store = new RateStateStore(_filePath);
        store.Load().Should().BeNull();
    }

    [Fact]
    public void Load_RestoresLegacyFormat_WhenOnlyRateKeyPresent()
    {
        // v0.5.x 形式: version フィールド無し、rate キーのみ
        File.WriteAllText(_filePath, """{"rate": 7.5, "savedAt": "2026-04-19T00:00:00Z"}""");
        var store = new RateStateStore(_filePath);

        var loaded = store.Load();
        loaded.Should().NotBeNull();
        loaded!.Format.Should().Be(RateStateFormat.Legacy);
        loaded.RateTokensPerSec.Should().Be(7.5);
        loaded.MaxInflight.Should().BeNull();
    }

    [Fact]
    public void Load_ReturnsNull_WhenV2FormatMissesRateField()
    {
        // version=2 だが rate_tokens_per_sec が欠落 → 不正として null
        File.WriteAllText(_filePath, """{"version": 2, "max_inflight": 10}""");
        var store = new RateStateStore(_filePath);
        store.Load().Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_OverwritesExistingFile()
    {
        var store = new RateStateStore(_filePath);
        await store.SaveAsync(5.0, 4);
        await store.SaveAsync(20.0, 16);

        var loaded = store.Load();
        loaded.Should().NotBeNull();
        loaded!.RateTokensPerSec.Should().Be(20.0);
        loaded.MaxInflight.Should().Be(16);
    }

    [Fact]
    public async Task SaveAsync_CreatesParentDirectory()
    {
        var nestedPath = Path.Combine(_tempDir, "nested", "sub", "rate_state.json");
        var store = new RateStateStore(nestedPath);

        await store.SaveAsync(3.0, 8);

        File.Exists(nestedPath).Should().BeTrue();
    }

    [Fact]
    public void Load_IgnoresNonFiniteRateValue_ByReturningNull()
    {
        // NaN・Infinity は不正値として扱う
        File.WriteAllText(_filePath, """{"version": 2, "rate_tokens_per_sec": "NaN"}""");
        var store = new RateStateStore(_filePath);
        store.Load().Should().BeNull();
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyPath()
    {
        var act = () => new RateStateStore(string.Empty);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task SaveAsync_WritesAtomically_NoTempFileLeftBehind()
    {
        var store = new RateStateStore(_filePath);
        await store.SaveAsync(8.0, 5);

        var tmpPath = _filePath + ".tmp";
        File.Exists(tmpPath).Should().BeFalse("atomic write で temp ファイルは残ってはならない");
        File.Exists(_filePath).Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"version": 3, "rate_tokens_per_sec": 12.5, "max_inflight": 14}""")]
    [InlineData("""{"version": 99, "rate_tokens_per_sec": 12.5, "max_inflight": 14}""")]
    public void Load_ReturnsNull_WhenVersionIsUnknown(string json)
    {
        // 未知の version は前方互換を仮定せずコールドスタート扱い
        File.WriteAllText(_filePath, json);
        var store = new RateStateStore(_filePath);
        store.Load().Should().BeNull();
    }

    [Theory]
    [InlineData("""{"version": 2, "rate_tokens_per_sec": 12.5}""")]
    [InlineData("""{"version": 2, "rate_tokens_per_sec": 12.5, "max_inflight": 0}""")]
    [InlineData("""{"version": 2, "rate_tokens_per_sec": 12.5, "max_inflight": -3}""")]
    [InlineData("""{"version": 2, "rate_tokens_per_sec": 12.5, "max_inflight": "abc"}""")]
    public void Load_ReturnsNull_WhenV2MaxInflightIsInvalid(string json)
    {
        // v2 で max_inflight が欠落・非正・非数値の場合は null（無効値 0 を作らない）
        File.WriteAllText(_filePath, json);
        var store = new RateStateStore(_filePath);
        store.Load().Should().BeNull();
    }
}
