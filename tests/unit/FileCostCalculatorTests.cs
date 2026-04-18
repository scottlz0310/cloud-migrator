using CloudMigrator.Core.Transfer;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// FileCostCalculator のユニットテスト（#160）。
/// </summary>
public sealed class FileCostCalculatorTests
{
    // ─── 離散モード ────────────────────────────────────────────────

    [Theory]
    [InlineData(0L, 1)]
    [InlineData(512L, 1)]
    [InlineData(FileCostCalculator.SmallFileThresholdBytes - 1, 1)]
    public void Calculate_Discrete_ReturnsSmallCost_ForSmallFiles(long size, int expected)
    {
        // 検証対象: Calculate（離散・小）  目的: 1 MiB 未満は smallFileCost
        var sut = new FileCostCalculator();
        sut.Calculate(size).Should().Be(expected);
    }

    [Theory]
    [InlineData(FileCostCalculator.SmallFileThresholdBytes)]
    [InlineData(10L * 1024 * 1024)]
    [InlineData(FileCostCalculator.MediumFileThresholdBytes - 1)]
    public void Calculate_Discrete_ReturnsMediumCost_ForMediumFiles(long size)
    {
        // 検証対象: Calculate（離散・中）  目的: 1 MiB〜100 MiB 未満は mediumFileCost
        var sut = new FileCostCalculator();
        sut.Calculate(size).Should().Be(5);
    }

    [Theory]
    [InlineData(FileCostCalculator.MediumFileThresholdBytes)]
    [InlineData(500L * 1024 * 1024)]
    [InlineData(10L * 1024 * 1024 * 1024)]
    public void Calculate_Discrete_ReturnsLargeCost_ForLargeFiles(long size)
    {
        // 検証対象: Calculate（離散・大）  目的: 100 MiB 以上は largeFileCost
        var sut = new FileCostCalculator();
        sut.Calculate(size).Should().Be(20);
    }

    [Fact]
    public void Calculate_Discrete_TreatsNegativeSizeAsZero()
    {
        // 検証対象: Calculate  目的: 負値サイズは 0 として扱い small に分類
        var sut = new FileCostCalculator();
        sut.Calculate(-1).Should().Be(1);
    }

    [Fact]
    public void Calculate_Discrete_UsesCustomCosts()
    {
        // 検証対象: Calculate（離散・カスタム）  目的: コンストラクタで指定したコストが反映される
        var sut = new FileCostCalculator(
            mode: FileCostMode.Discrete,
            smallFileCost: 2,
            mediumFileCost: 10,
            largeFileCost: 40);

        sut.Calculate(0).Should().Be(2);
        sut.Calculate(10L * 1024 * 1024).Should().Be(10);
        sut.Calculate(500L * 1024 * 1024).Should().Be(40);
    }

    // ─── 連続モード ────────────────────────────────────────────────

    [Fact]
    public void Calculate_Continuous_ScalesBySize()
    {
        // 検証対象: Calculate（連続）  目的: cost = ceil(size / scaleBytes) でスケール
        var sut = new FileCostCalculator(
            mode: FileCostMode.Continuous,
            costScaleBytes: 10_000_000L,
            minCost: 1,
            maxCost: 50);

        sut.Calculate(10_000_000L).Should().Be(1);
        sut.Calculate(25_000_000L).Should().Be(3);   // ceil(2.5) = 3
        sut.Calculate(100_000_000L).Should().Be(10);
    }

    [Fact]
    public void Calculate_Continuous_ClampsToMinCost()
    {
        // 検証対象: Calculate（連続）  目的: サイズが小さくても minCost 未満にはならない
        var sut = new FileCostCalculator(
            mode: FileCostMode.Continuous,
            costScaleBytes: 10_000_000L,
            minCost: 3,
            maxCost: 50);

        sut.Calculate(0).Should().Be(3);
        sut.Calculate(100).Should().Be(3);
    }

    [Fact]
    public void Calculate_Continuous_ClampsToMaxCost()
    {
        // 検証対象: Calculate（連続）  目的: 巨大ファイルでも maxCost を超えない
        var sut = new FileCostCalculator(
            mode: FileCostMode.Continuous,
            costScaleBytes: 10_000_000L,
            minCost: 1,
            maxCost: 50);

        sut.Calculate(10L * 1024 * 1024 * 1024).Should().Be(50);
    }

    // ─── バリデーション ───────────────────────────────────────────

    [Fact]
    public void Constructor_Throws_WhenSmallFileCostLessThanOne()
    {
        // 検証対象: コンストラクタバリデーション  目的: smallFileCost < 1 で例外
        Action act = () => new FileCostCalculator(smallFileCost: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_Throws_WhenMediumLessThanSmall()
    {
        // 検証対象: コンストラクタバリデーション  目的: mediumFileCost < smallFileCost で例外
        Action act = () => new FileCostCalculator(smallFileCost: 5, mediumFileCost: 3);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_Throws_WhenLargeLessThanMedium()
    {
        // 検証対象: コンストラクタバリデーション  目的: largeFileCost < mediumFileCost で例外
        Action act = () => new FileCostCalculator(mediumFileCost: 10, largeFileCost: 5);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_Throws_WhenCostScaleBytesLessThanOne()
    {
        // 検証対象: コンストラクタバリデーション  目的: costScaleBytes < 1 で例外
        Action act = () => new FileCostCalculator(costScaleBytes: 0L);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_Throws_WhenMaxCostLessThanMinCost()
    {
        // 検証対象: コンストラクタバリデーション  目的: maxCost < minCost で例外
        Action act = () => new FileCostCalculator(minCost: 10, maxCost: 5);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Mode_ReturnsConfiguredMode()
    {
        // 検証対象: Mode  目的: コンストラクタで指定したモードが Mode プロパティに反映される
        new FileCostCalculator(FileCostMode.Discrete).Mode.Should().Be(FileCostMode.Discrete);
        new FileCostCalculator(FileCostMode.Continuous).Mode.Should().Be(FileCostMode.Continuous);
    }
}
