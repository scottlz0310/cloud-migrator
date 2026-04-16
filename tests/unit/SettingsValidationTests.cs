using CloudMigrator.Dashboard;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

public class SettingsValidationTests
{
    // ── ValidateMaxParallelTransfers ──────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(256)]
    public void ValidateMaxParallelTransfers_ValidRange_ReturnsNull(int v) =>
        SettingsValidation.ValidateMaxParallelTransfers(v).Should().BeNull();

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public void ValidateMaxParallelTransfers_OutOfRange_ReturnsError(int v) =>
        SettingsValidation.ValidateMaxParallelTransfers(v).Should().NotBeNull();

    // ── ValidateMaxParallelFolderCreations ────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(32)]
    public void ValidateMaxParallelFolderCreations_ValidRange_ReturnsNull(int v) =>
        SettingsValidation.ValidateMaxParallelFolderCreations(v).Should().BeNull();

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public void ValidateMaxParallelFolderCreations_OutOfRange_ReturnsError(int v) =>
        SettingsValidation.ValidateMaxParallelFolderCreations(v).Should().NotBeNull();

    // ── ValidateChunkSizeMb ───────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(100)]
    public void ValidateChunkSizeMb_ValidRange_ReturnsNull(int v) =>
        SettingsValidation.ValidateChunkSizeMb(v).Should().BeNull();

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void ValidateChunkSizeMb_OutOfRange_ReturnsError(int v) =>
        SettingsValidation.ValidateChunkSizeMb(v).Should().NotBeNull();

    // ── ValidateLargeFileThresholdMb ──────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void ValidateLargeFileThresholdMb_ValidRange_ReturnsNull(int v) =>
        SettingsValidation.ValidateLargeFileThresholdMb(v).Should().BeNull();

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void ValidateLargeFileThresholdMb_OutOfRange_ReturnsError(int v) =>
        SettingsValidation.ValidateLargeFileThresholdMb(v).Should().NotBeNull();

    // ── ValidateRetryCount ────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(10)]
    public void ValidateRetryCount_ValidRange_ReturnsNull(int v) =>
        SettingsValidation.ValidateRetryCount(v).Should().BeNull();

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void ValidateRetryCount_OutOfRange_ReturnsError(int v) =>
        SettingsValidation.ValidateRetryCount(v).Should().NotBeNull();

    // ── ValidateTimeoutSec ────────────────────────────────────────────────────

    [Theory]
    [InlineData(30)]
    [InlineData(1800)]
    [InlineData(3600)]
    public void ValidateTimeoutSec_ValidRange_ReturnsNull(int v) =>
        SettingsValidation.ValidateTimeoutSec(v).Should().BeNull();

    [Theory]
    [InlineData(29)]
    [InlineData(3601)]
    public void ValidateTimeoutSec_OutOfRange_ReturnsError(int v) =>
        SettingsValidation.ValidateTimeoutSec(v).Should().NotBeNull();

    // ── ValidateRcWindowSecs ──────────────────────────────────────────────────

    [Fact]
    public void ValidateRcWindowSecs_UseRateControlFalse_ReturnsNull() =>
        SettingsValidation.ValidateRcWindowSecs(false, 30, 5).Should().BeNull();

    [Fact]
    public void ValidateRcWindowSecs_ShortLessThanLong_ReturnsNull() =>
        SettingsValidation.ValidateRcWindowSecs(true, 5, 30).Should().BeNull();

    [Theory]
    [InlineData(30, 30)]
    [InlineData(31, 30)]
    public void ValidateRcWindowSecs_ShortGeqLong_ReturnsError(int shortSec, int longSec) =>
        SettingsValidation.ValidateRcWindowSecs(true, shortSec, longSec).Should().NotBeNull();

    // ── ValidateRcDecayFactors ────────────────────────────────────────────────

    [Fact]
    public void ValidateRcDecayFactors_UseRateControlFalse_ReturnsNull() =>
        SettingsValidation.ValidateRcDecayFactors(false, 0.9, 0.3).Should().BeNull();

    [Fact]
    public void ValidateRcDecayFactors_MinLessThanMax_ReturnsNull() =>
        SettingsValidation.ValidateRcDecayFactors(true, 0.3, 0.9).Should().BeNull();

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(0.9, 0.3)]
    public void ValidateRcDecayFactors_MinGeqMax_ReturnsError(double min, double max) =>
        SettingsValidation.ValidateRcDecayFactors(true, min, max).Should().NotBeNull();

    // ── ValidateAdaptiveDecreasePercent ───────────────────────────────────────

    [Fact]
    public void ValidateAdaptiveDecreasePercent_UseRateControlTrue_ReturnsNull() =>
        SettingsValidation.ValidateAdaptiveDecreasePercent(true, true, 0).Should().BeNull();

    [Fact]
    public void ValidateAdaptiveDecreasePercent_AdaptiveDisabled_ReturnsNull() =>
        SettingsValidation.ValidateAdaptiveDecreasePercent(false, false, 0).Should().BeNull();

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(99)]
    public void ValidateAdaptiveDecreasePercent_ValidRange_ReturnsNull(int v) =>
        SettingsValidation.ValidateAdaptiveDecreasePercent(false, true, v).Should().BeNull();

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void ValidateAdaptiveDecreasePercent_OutOfRange_ReturnsError(int v) =>
        SettingsValidation.ValidateAdaptiveDecreasePercent(false, true, v).Should().NotBeNull();

    // ── ValidateAdaptiveIncreaseIntervalSec ───────────────────────────────────

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(120)]
    public void ValidateAdaptiveIncreaseIntervalSec_ValidValues_ReturnsNull(int v) =>
        SettingsValidation.ValidateAdaptiveIncreaseIntervalSec(false, true, v).Should().BeNull();

    [Theory]
    [InlineData(15)]
    [InlineData(45)]
    [InlineData(180)]
    public void ValidateAdaptiveIncreaseIntervalSec_InvalidValues_ReturnsError(int v) =>
        SettingsValidation.ValidateAdaptiveIncreaseIntervalSec(false, true, v).Should().NotBeNull();

    [Fact]
    public void ValidateAdaptiveIncreaseIntervalSec_UseRateControlTrue_ReturnsNull() =>
        SettingsValidation.ValidateAdaptiveIncreaseIntervalSec(true, true, 999).Should().BeNull();

    // ── ValidateAdaptiveInitialDegree ─────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(256)]
    public void ValidateAdaptiveInitialDegree_ValidRange_ReturnsNull(int v) =>
        SettingsValidation.ValidateAdaptiveInitialDegree(false, true, v).Should().BeNull();

    [Theory]
    [InlineData(-1)]
    [InlineData(257)]
    public void ValidateAdaptiveInitialDegree_OutOfRange_ReturnsError(int v) =>
        SettingsValidation.ValidateAdaptiveInitialDegree(false, true, v).Should().NotBeNull();

    [Fact]
    public void ValidateAdaptiveInitialDegree_UseRateControlTrue_ReturnsNull() =>
        SettingsValidation.ValidateAdaptiveInitialDegree(true, true, -999).Should().BeNull();
}
