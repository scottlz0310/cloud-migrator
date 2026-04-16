using System.Collections.Generic;
using System.Text.Json;
using CloudMigrator.Core.Configuration;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// 検証対象: ConfigurationService
/// 目的: config.json の読み書き・シークレット除外・マージ更新が正しく動作することを確認する
/// </summary>
public sealed class ConfigurationServiceTests : IDisposable
{
    private readonly string _configPath;

    /// <summary>
    /// テスト用 config.json を一時ディレクトリに作成する。
    /// </summary>
    public ConfigurationServiceTests()
    {
        _configPath = Path.Combine(Path.GetTempPath(), $"config-test-{Guid.NewGuid()}.json");
        WriteConfig(new
        {
            migrator = new
            {
                maxParallelTransfers = 10,
                chunkSizeMb = 5,
                largeFileThresholdMb = 4,
                retryCount = 3,
                timeoutSec = 120,
                destinationRoot = "Documents/テスト",
                destinationProvider = "sharepoint",
                // 保持されるべき別フィールド
                maxParallelFolderCreations = 2
            }
        });
    }

    public void Dispose()
    {
        if (File.Exists(_configPath)) File.Delete(_configPath);
        if (File.Exists(_configPath + ".tmp")) File.Delete(_configPath + ".tmp");
    }

    // ── GetConfigAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetConfigAsync_ReturnsCorrectValues()
    {
        // 検証対象: GetConfigAsync  目的: migrator セクションの値を正しく返す
        var svc = new ConfigurationService(_configPath);

        var result = await svc.GetConfigAsync();

        result.MaxParallelTransfers.Should().Be(10);
        result.ChunkSizeMb.Should().Be(5);
        result.LargeFileThresholdMb.Should().Be(4);
        result.RetryCount.Should().Be(3);
        result.TimeoutSec.Should().Be(120);
        result.DestinationRoot.Should().Be("Documents/テスト");
        result.DestinationProvider.Should().Be("sharepoint");
    }

    [Fact]
    public async Task GetConfigAsync_DoesNotReturnSecrets()
    {
        // 検証対象: GetConfigAsync  目的: ConfigDto にシークレット系フィールドを含まない
        WriteConfig(new
        {
            migrator = new
            {
                maxParallelTransfers = 10,
                chunkSizeMb = 5,
                largeFileThresholdMb = 4,
                retryCount = 3,
                timeoutSec = 120,
                destinationRoot = "Documents/test",
                destinationProvider = "sharepoint",
                clientSecret = "supersecret"  // config.json に書かれていても返さない
            }
        });
        var svc = new ConfigurationService(_configPath);

        var result = await svc.GetConfigAsync();

        // ConfigDto の型定義自体にシークレットフィールドがないことを型レベルで確認
        var props = typeof(ConfigDto).GetProperties().Select(p => p.Name.ToLowerInvariant());
        props.Should().NotContain(p => p.Contains("secret") || p.Contains("password") || p.Contains("token"));
    }

    // ── UpdateConfigAsync ────────────────────────────────────────────────

    [Fact]
    public async Task UpdateConfigAsync_MergesSpecifiedFields()
    {
        // 検証対象: UpdateConfigAsync  目的: 指定フィールドのみ上書きされる
        var svc = new ConfigurationService(_configPath);

        await svc.UpdateConfigAsync(new ConfigUpdateDto(
            MaxParallelTransfers: 20,
            RetryCount: 5));

        var result = await svc.GetConfigAsync();
        result.MaxParallelTransfers.Should().Be(20);
        result.RetryCount.Should().Be(5);
        // 未指定フィールドは元の値を保持
        result.ChunkSizeMb.Should().Be(5);
        result.TimeoutSec.Should().Be(120);
        result.DestinationRoot.Should().Be("Documents/テスト");
    }

    [Fact]
    public async Task UpdateConfigAsync_PreservesNonExposedFields()
    {
        // 検証対象: UpdateConfigAsync  目的: ConfigUpdateDto に含まれないフィールドが config.json に残る
        var svc = new ConfigurationService(_configPath);

        await svc.UpdateConfigAsync(new ConfigUpdateDto(MaxParallelTransfers: 15));

        var json = await File.ReadAllTextAsync(_configPath);
        using var doc = JsonDocument.Parse(json);
        // maxParallelFolderCreations は ConfigUpdateDto 外のフィールドだが保持される
        doc.RootElement.GetProperty("migrator")
            .GetProperty("maxParallelFolderCreations")
            .GetInt32()
            .Should().Be(2);
    }

    [Fact]
    public async Task UpdateConfigAsync_UpdatesDestinationRoot()
    {
        // 検証対象: UpdateConfigAsync  目的: 文字列フィールドも正しくマージ保存できる
        var svc = new ConfigurationService(_configPath);

        await svc.UpdateConfigAsync(new ConfigUpdateDto(DestinationRoot: "Documents/新フォルダ"));

        var result = await svc.GetConfigAsync();
        result.DestinationRoot.Should().Be("Documents/新フォルダ");
    }

    [Fact]
    public async Task UpdateConfigAsync_NullFieldsAreSkipped()
    {
        // 検証対象: UpdateConfigAsync  目的: null フィールドを渡しても既存値が変わらない
        var svc = new ConfigurationService(_configPath);

        await svc.UpdateConfigAsync(new ConfigUpdateDto()); // 全フィールド null

        var result = await svc.GetConfigAsync();
        result.MaxParallelTransfers.Should().Be(10);
        result.ChunkSizeMb.Should().Be(5);
        result.DestinationRoot.Should().Be("Documents/テスト");
    }

    // ── NormalizeProvider: GetConfigAsync 経由での正規化検証 ─────────────

    [Theory]
    [InlineData("graph", "sharepoint")]
    [InlineData("Graph", "sharepoint")]
    [InlineData("GRAPH", "sharepoint")]
    public async Task GetConfigAsync_WhenDestinationProviderIsGraphAlias_ReturnsSharepoint(
        string rawValue, string expected)
    {
        // 検証対象: GetConfigAsync（NormalizeProvider）
        // 目的: "graph" / "Graph" 等の旧エイリアスが "sharepoint" に正規化されること
        WriteConfig(new
        {
            migrator = new
            {
                maxParallelTransfers = 4,
                chunkSizeMb = 5,
                largeFileThresholdMb = 4,
                retryCount = 3,
                timeoutSec = 300,
                destinationRoot = string.Empty,
                destinationProvider = rawValue
            }
        });
        var svc = new ConfigurationService(_configPath);

        var result = await svc.GetConfigAsync();

        result.DestinationProvider.Should().Be(expected);
    }

    [Theory]
    [InlineData("DROPBOX", "dropbox")]
    [InlineData("Dropbox", "dropbox")]
    public async Task GetConfigAsync_WhenDestinationProviderIsDropboxVariant_ReturnsDropbox(
        string rawValue, string expected)
    {
        // 検証対象: GetConfigAsync（NormalizeProvider）
        // 目的: 大文字バリアントの "DROPBOX" / "Dropbox" が "dropbox" に正規化されること
        WriteConfig(new
        {
            migrator = new
            {
                maxParallelTransfers = 4,
                chunkSizeMb = 5,
                largeFileThresholdMb = 4,
                retryCount = 3,
                timeoutSec = 300,
                destinationRoot = string.Empty,
                destinationProvider = rawValue
            }
        });
        var svc = new ConfigurationService(_configPath);

        var result = await svc.GetConfigAsync();

        result.DestinationProvider.Should().Be(expected);
    }

    [Fact]
    public async Task GetConfigAsync_WhenDestinationProviderIsUnknown_ReturnsLowerCased()
    {
        // 検証対象: GetConfigAsync（NormalizeProvider）
        // 目的: 未知のプロバイダー値は小文字化されて返されること
        WriteConfig(new
        {
            migrator = new
            {
                maxParallelTransfers = 4,
                chunkSizeMb = 5,
                largeFileThresholdMb = 4,
                retryCount = 3,
                timeoutSec = 300,
                destinationRoot = string.Empty,
                destinationProvider = "OneDrive"
            }
        });
        var svc = new ConfigurationService(_configPath);

        var result = await svc.GetConfigAsync();

        result.DestinationProvider.Should().Be("onedrive");
    }

    // ── AdaptiveConcurrency（GetConfigAsync 経由）────────────────────────

    [Fact]
    public async Task GetConfigAsync_WhenAdaptiveConcurrencySectionAbsent_ReturnsDefaults()
    {
        // 検証対象: GetConfigAsync（adaptiveConcurrency なし）
        // 目的: adaptiveConcurrency セクションが存在しないとき、デフォルト値を返すこと
        // コンストラクタで書き込まれた設定は adaptiveConcurrency を含まない
        var svc = new ConfigurationService(_configPath);

        var result = await svc.GetConfigAsync();

        result.AdaptiveConcurrencyEnabled.Should().BeFalse();
        result.AdaptiveConcurrencyInitialDegree.Should().Be(0);
        result.AdaptiveConcurrencyDecreasePercent.Should().Be(50);
        result.AdaptiveConcurrencyIncreaseIntervalSec.Should().Be(60);
    }

    [Fact]
    public async Task GetConfigAsync_WhenAdaptiveConcurrencySharepointProfilePresent_ReturnsValues()
    {
        // 検証対象: GetConfigAsync（adaptiveConcurrency.sharepoint プロファイル）
        // 目的: sharepoint プロファイルの全フィールドを正しく読み取ること
        WriteConfig(new
        {
            migrator = new
            {
                maxParallelTransfers = 4,
                chunkSizeMb = 5,
                largeFileThresholdMb = 4,
                retryCount = 3,
                timeoutSec = 300,
                destinationRoot = string.Empty,
                destinationProvider = "sharepoint",
                adaptiveConcurrency = new
                {
                    sharepoint = new
                    {
                        enabled = true,
                        initialDegree = 8,
                        decreaseMultiplier = 0.75,
                        increaseIntervalSec = 90
                    }
                }
            }
        });
        var svc = new ConfigurationService(_configPath);

        var result = await svc.GetConfigAsync();

        result.AdaptiveConcurrencyEnabled.Should().BeTrue();
        result.AdaptiveConcurrencyInitialDegree.Should().Be(8);
        result.AdaptiveConcurrencyDecreasePercent.Should().Be(75); // Floor(0.75 * 100) = 75
        result.AdaptiveConcurrencyIncreaseIntervalSec.Should().Be(90);
    }

    [Fact]
    public async Task GetConfigAsync_WhenAdaptiveConcurrencyDefaultProfilePresent_ReturnsValues()
    {
        // 検証対象: GetConfigAsync（adaptiveConcurrency.default プロファイル）
        // 目的: sharepoint キーがなく default キーがある場合もフォールバックで読み取ること
        WriteConfig(new
        {
            migrator = new
            {
                maxParallelTransfers = 4,
                chunkSizeMb = 5,
                largeFileThresholdMb = 4,
                retryCount = 3,
                timeoutSec = 300,
                destinationRoot = string.Empty,
                destinationProvider = "sharepoint",
                adaptiveConcurrency = new Dictionary<string, object>
                {
                    ["default"] = new { enabled = true, initialDegree = 4, decreaseMultiplier = 0.5, increaseIntervalSec = 60 }
                }
            }
        });
        var svc = new ConfigurationService(_configPath);

        var result = await svc.GetConfigAsync();

        result.AdaptiveConcurrencyEnabled.Should().BeTrue();
        result.AdaptiveConcurrencyInitialDegree.Should().Be(4);
        result.AdaptiveConcurrencyDecreasePercent.Should().Be(50);
        result.AdaptiveConcurrencyIncreaseIntervalSec.Should().Be(60);
    }

    [Theory]
    [InlineData(0.99, 99)]   // Clamp(Floor(99.0), 1, 99) = 99
    [InlineData(0.01, 1)]    // Clamp(Floor(1.0), 1, 99) = 1
    [InlineData(0.75, 75)]   // Floor(75.0) = 75
    [InlineData(0.509, 50)]  // Floor(50.9) = 50
    public async Task GetConfigAsync_DecreaseMultiplierIsConvertedToPercent(double multiplier, int expectedPercent)
    {
        // 検証対象: GetConfigAsync（decreaseMultiplier → adaptiveDecreasePercent 変換）
        // 目的: Math.Clamp(Floor(dm * 100), 1, 99) で % 変換されること
        WriteConfig(new
        {
            migrator = new
            {
                maxParallelTransfers = 4,
                chunkSizeMb = 5,
                largeFileThresholdMb = 4,
                retryCount = 3,
                timeoutSec = 300,
                destinationRoot = string.Empty,
                destinationProvider = "sharepoint",
                adaptiveConcurrency = new
                {
                    sharepoint = new
                    {
                        enabled = true,
                        initialDegree = 4,
                        decreaseMultiplier = multiplier,
                        increaseIntervalSec = 60
                    }
                }
            }
        });
        var svc = new ConfigurationService(_configPath);

        var result = await svc.GetConfigAsync();

        result.AdaptiveConcurrencyDecreasePercent.Should().Be(expectedPercent);
    }

    // ── RateControl（GetConfigAsync 経由）────────────────────────────────

    [Fact]
    public async Task GetConfigAsync_WhenRateControlSectionAbsent_ReturnsDefaults()
    {
        // 検証対象: GetConfigAsync（rateControl なし）
        // 目的: rateControl セクションが存在しないとき、デフォルト値を返すこと
        var svc = new ConfigurationService(_configPath);

        var result = await svc.GetConfigAsync();

        result.UseRateControl.Should().BeFalse();
        result.RcShortWindowSec.Should().Be(5);
        result.RcLongWindowSec.Should().Be(30);
        result.RcEmergencyThresholdPct.Should().Be(10);
        result.RcSlowdownThresholdPct.Should().Be(3);
        result.RcMinDecayFactor.Should().Be(0.3);
        result.RcMaxDecayFactor.Should().Be(0.9);
        result.RcInFlightThreshold.Should().Be(32);
        result.RcMaxConcurrency.Should().Be(16);
    }

    [Theory]
    [InlineData(0.10, 10)]
    [InlineData(0.03, 3)]
    [InlineData(0.01, 1)]
    public async Task GetConfigAsync_RateControl_EmergencyThresholdConvertedToPct(double jsonValue, int expectedPct)
    {
        // 検証対象: GetConfigAsync（emergencyThreshold 変換）
        // 目的: JSON 0–1 の値が UI 用 % (0–100) に変換されること
        WriteConfig(new
        {
            migrator = new
            {
                maxParallelTransfers = 4,
                chunkSizeMb = 5,
                largeFileThresholdMb = 4,
                retryCount = 3,
                timeoutSec = 300,
                destinationRoot = string.Empty,
                destinationProvider = "sharepoint",
                rateControl = new { emergencyThreshold = jsonValue }
            }
        });
        var svc = new ConfigurationService(_configPath);

        var result = await svc.GetConfigAsync();

        result.RcEmergencyThresholdPct.Should().Be(expectedPct);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(1.0, 100)]
    public async Task GetConfigAsync_RateControl_EmergencyThresholdBoundary(double jsonValue, int expectedPct)
    {
        // 検証対象: GetConfigAsync（emergencyThreshold 境界値）
        // 目的: 0.0 → 0%、1.0 → 100% に変換されること
        WriteConfig(new
        {
            migrator = new
            {
                maxParallelTransfers = 4,
                chunkSizeMb = 5,
                largeFileThresholdMb = 4,
                retryCount = 3,
                timeoutSec = 300,
                destinationRoot = string.Empty,
                destinationProvider = "sharepoint",
                rateControl = new { emergencyThreshold = jsonValue }
            }
        });
        var svc = new ConfigurationService(_configPath);

        var result = await svc.GetConfigAsync();

        result.RcEmergencyThresholdPct.Should().Be(expectedPct);
    }

    [Fact]
    public async Task GetConfigAsync_RateControl_WhenEmergencyThresholdMissing_ReturnsDefault()
    {
        // 検証対象: GetConfigAsync（emergencyThreshold 欠損）
        // 目的: rateControl セクションはあるが emergencyThreshold が欠損している場合、デフォルト値を返すこと
        WriteConfig(new
        {
            migrator = new
            {
                maxParallelTransfers = 4,
                chunkSizeMb = 5,
                largeFileThresholdMb = 4,
                retryCount = 3,
                timeoutSec = 300,
                destinationRoot = string.Empty,
                destinationProvider = "sharepoint",
                rateControl = new { useRateControl = true }
            }
        });
        var svc = new ConfigurationService(_configPath);

        var result = await svc.GetConfigAsync();

        result.RcEmergencyThresholdPct.Should().Be(10);
    }

    [Theory]
    [InlineData(0.10, 10)]
    [InlineData(0.03, 3)]
    public async Task GetConfigAsync_RateControl_SlowdownThresholdConvertedToPct(double jsonValue, int expectedPct)
    {
        // 検証対象: GetConfigAsync（slowdownThreshold 変換）
        // 目的: JSON 0–1 の値が UI 用 % に変換されること
        WriteConfig(new
        {
            migrator = new
            {
                maxParallelTransfers = 4,
                chunkSizeMb = 5,
                largeFileThresholdMb = 4,
                retryCount = 3,
                timeoutSec = 300,
                destinationRoot = string.Empty,
                destinationProvider = "sharepoint",
                rateControl = new { slowdownThreshold = jsonValue }
            }
        });
        var svc = new ConfigurationService(_configPath);

        var result = await svc.GetConfigAsync();

        result.RcSlowdownThresholdPct.Should().Be(expectedPct);
    }

    // ── RateControl（UpdateConfigAsync 経由）────────────────────────────

    [Fact]
    public async Task UpdateConfigAsync_RateControl_EmergencyThresholdPctSavedAsRatio()
    {
        // 検証対象: UpdateConfigAsync（emergencyThreshold 変換）
        // 目的: UI の % 値 (10) が config.json には 0–1 (0.10) で保存されること
        var svc = new ConfigurationService(_configPath);

        await svc.UpdateConfigAsync(new ConfigUpdateDto(RcEmergencyThresholdPct: 10));

        var json = await File.ReadAllTextAsync(_configPath);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("migrator")
            .GetProperty("rateControl")
            .GetProperty("emergencyThreshold")
            .GetDouble()
            .Should().BeApproximately(0.10, 0.0001);
    }

    [Fact]
    public async Task UpdateConfigAsync_RateControl_SlowdownThresholdPctSavedAsRatio()
    {
        // 検証対象: UpdateConfigAsync（slowdownThreshold 変換）
        // 目的: UI の % 値 (3) が config.json には 0–1 (0.03) で保存されること
        var svc = new ConfigurationService(_configPath);

        await svc.UpdateConfigAsync(new ConfigUpdateDto(RcSlowdownThresholdPct: 3));

        var json = await File.ReadAllTextAsync(_configPath);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("migrator")
            .GetProperty("rateControl")
            .GetProperty("slowdownThreshold")
            .GetDouble()
            .Should().BeApproximately(0.03, 0.0001);
    }

    [Fact]
    public async Task UpdateConfigAsync_RateControl_RoundTrip_EmergencyThreshold()
    {
        // 検証対象: GetConfigAsync + UpdateConfigAsync（往復変換）
        // 目的: % で保存した値を読み直すと同じ % 値が返ること
        var svc = new ConfigurationService(_configPath);

        await svc.UpdateConfigAsync(new ConfigUpdateDto(RcEmergencyThresholdPct: 15));
        var result = await svc.GetConfigAsync();

        result.RcEmergencyThresholdPct.Should().Be(15);
    }

    // ── UpdateConfigAsync バリデーション ────────────────────────────────

    [Fact]
    public async Task UpdateConfigAsync_RateControl_ThrowsWhenMinDecayFactorGeqMaxDecayFactor()
    {
        // 検証対象: UpdateConfigAsync バリデーション
        // 目的: RcMinDecayFactor >= RcMaxDecayFactor の場合に ArgumentException が発生すること
        var svc = new ConfigurationService(_configPath);

        var act = async () => await svc.UpdateConfigAsync(new ConfigUpdateDto(
            RcMinDecayFactor: 0.9, RcMaxDecayFactor: 0.5));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*RcMinDecayFactor*RcMaxDecayFactor*");
    }

    [Fact]
    public async Task UpdateConfigAsync_RateControl_ThrowsWhenShortWindowGeqLongWindow()
    {
        // 検証対象: UpdateConfigAsync バリデーション
        // 目的: RcShortWindowSec >= RcLongWindowSec の場合に ArgumentException が発生すること
        var svc = new ConfigurationService(_configPath);

        var act = async () => await svc.UpdateConfigAsync(new ConfigUpdateDto(
            RcShortWindowSec: 30, RcLongWindowSec: 30));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*RcShortWindowSec*");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task UpdateConfigAsync_RateControl_ThrowsWhenEmergencyThresholdOutOfRange(int pct)
    {
        // 検証対象: UpdateConfigAsync バリデーション
        // 目的: RcEmergencyThresholdPct が 0–100 外の場合に ArgumentException が発生すること
        var svc = new ConfigurationService(_configPath);

        var act = async () => await svc.UpdateConfigAsync(new ConfigUpdateDto(RcEmergencyThresholdPct: pct));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task UpdateConfigAsync_RateControl_ThrowsWhenSlowdownThresholdOutOfRange(int pct)
    {
        // 検証対象: UpdateConfigAsync バリデーション
        // 目的: RcSlowdownThresholdPct が 0–100 外の場合に ArgumentException が発生すること
        var svc = new ConfigurationService(_configPath);

        var act = async () => await svc.UpdateConfigAsync(new ConfigUpdateDto(RcSlowdownThresholdPct: pct));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task UpdateConfigAsync_RateControl_ValidValuesDoNotThrow()
    {
        // 検証対象: UpdateConfigAsync バリデーション
        // 目的: 有効な組み合わせでは例外が発生しないこと
        var svc = new ConfigurationService(_configPath);

        var act = async () => await svc.UpdateConfigAsync(new ConfigUpdateDto(
            RcShortWindowSec: 5,
            RcLongWindowSec: 30,
            RcEmergencyThresholdPct: 10,
            RcSlowdownThresholdPct: 3,
            RcMinDecayFactor: 0.3,
            RcMaxDecayFactor: 0.9));

        await act.Should().NotThrowAsync();
    }

    // ── ヘルパー ────────────────────────────────────────────────────────

    private void WriteConfig(object data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_configPath, json);
    }
}
