namespace CloudMigrator.Dashboard;

/// <summary>
/// ダッシュボード表示用の純粋なフォーマット補助メソッド群。
/// Blazor コンポーネントから分離することでユニットテスト可能にする。
/// </summary>
public static class DashboardFormatHelper
{
    public static string FormatDuration(double seconds)
    {
        var sec = (int)Math.Round(Math.Abs(seconds));
        if (sec < 60) return $"{sec}秒";
        if (sec < 3600) return $"{sec / 60}m{sec % 60:D2}s";
        return $"{sec / 3600}h{sec % 3600 / 60:D2}m";
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1_024 => $"{bytes / 1_024.0:F1} KB",
        _ => $"{bytes} B",
    };

    public static string FormatBytesPerSec(double v) => v switch
    {
        >= 1_048_576 => $"{v / 1_048_576:F1} MB/s",
        >= 1_024 => $"{v / 1_024:F1} KB/s",
        _ => $"{v:F0} B/s",
    };
}
