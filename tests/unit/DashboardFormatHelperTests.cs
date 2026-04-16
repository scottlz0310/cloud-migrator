using CloudMigrator.Dashboard;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

public class DashboardFormatHelperTests
{
    // ── FormatDuration ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "0秒")]
    [InlineData(1, "1秒")]
    [InlineData(59, "59秒")]
    [InlineData(60, "1m00s")]
    [InlineData(61, "1m01s")]
    [InlineData(90, "1m30s")]
    [InlineData(3599, "59m59s")]
    [InlineData(3600, "1h00m")]
    [InlineData(3661, "1h01m")]
    [InlineData(7322, "2h02m")]
    public void FormatDuration_VariousInputs_ReturnsExpected(double seconds, string expected)
    {
        DashboardFormatHelper.FormatDuration(seconds).Should().Be(expected);
    }

    [Fact]
    public void FormatDuration_NegativeValue_TreatedAsAbsolute()
    {
        // 絶対値で処理されるため負値は正値と同じ結果になる
        DashboardFormatHelper.FormatDuration(-90).Should().Be("1m30s");
    }

    // ── FormatBytes ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1_023L, "1023 B")]
    [InlineData(1_024L, "1.0 KB")]
    [InlineData(1_536L, "1.5 KB")]
    [InlineData(1_048_575L, "1024.0 KB")]
    [InlineData(1_048_576L, "1.0 MB")]
    [InlineData(1_572_864L, "1.5 MB")]
    [InlineData(1_073_741_823L, "1024.0 MB")]
    [InlineData(1_073_741_824L, "1.0 GB")]
    [InlineData(2_147_483_648L, "2.0 GB")]
    public void FormatBytes_VariousInputs_ReturnsExpected(long bytes, string expected)
    {
        DashboardFormatHelper.FormatBytes(bytes).Should().Be(expected);
    }

    // ── FormatBytesPerSec ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0, "0 B/s")]
    [InlineData(512.0, "512 B/s")]
    [InlineData(1_023.0, "1023 B/s")]
    [InlineData(1_024.0, "1.0 KB/s")]
    [InlineData(1_536.0, "1.5 KB/s")]
    [InlineData(1_047_552.0, "1023.0 KB/s")]
    [InlineData(1_048_576.0, "1.0 MB/s")]
    [InlineData(2_097_152.0, "2.0 MB/s")]
    public void FormatBytesPerSec_VariousInputs_ReturnsExpected(double v, string expected)
    {
        DashboardFormatHelper.FormatBytesPerSec(v).Should().Be(expected);
    }
}
