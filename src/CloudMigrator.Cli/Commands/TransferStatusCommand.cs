using System.CommandLine;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.State;
using Microsoft.Extensions.Configuration;

namespace CloudMigrator.Cli.Commands;

/// <summary>
/// status サブコマンド - Dropbox 転送の現在の進捗をダッシュボード形式で表示する。
/// </summary>
internal static class TransferStatusCommand
{
    public static Command Build()
    {
        var dbOpt = new Option<string?>("--db")
        {
            Description = "転送状態 DB ファイルパス（省略時: 設定ファイルの値を使用）",
        };
        var cmd = new Command("status", "Dropbox 転送状態ダッシュボードを表示します");
        cmd.Add(dbOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var dbPath = parseResult.GetValue(dbOpt) ?? ResolveDefaultDbPath();
            await RunAsync(dbPath, ct).ConfigureAwait(false);
        });
        return cmd;
    }

    private static string ResolveDefaultDbPath()
    {
        var config = AppConfiguration.Build();
        var opts = config.GetSection(MigratorOptions.SectionName).Get<MigratorOptions>() ?? new MigratorOptions();
        return opts.Paths.DropboxStateDb;
    }

    private static async Task RunAsync(string dbPath, CancellationToken ct)
    {
        if (!File.Exists(dbPath))
        {
            Console.WriteLine("転送状態 DB が見つかりません。transfer コマンドを先に実行してください。");
            Console.WriteLine($"  想定パス: {dbPath}");
            return;
        }

        await using var stateDb = new SqliteTransferStateDb(dbPath);
        await stateDb.InitializeAsync(ct).ConfigureAwait(false);

        var summary = await stateDb.GetSummaryAsync(ct).ConfigureAwait(false);
        PrintDashboard(summary, dbPath);
    }

    internal static void PrintDashboard(TransferDbSummary s, string dbPath)
    {
        var bar = BuildProgressBar(s.Done, s.Total, 40);

        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════╗");
        Console.WriteLine("║      Dropbox 転送ステータス ダッシュボード           ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine($"  DB パス  : {dbPath}");
        if (s.FirstUpdatedAt.HasValue)
            Console.WriteLine($"  開始時刻 : {s.FirstUpdatedAt.Value:yyyy-MM-dd HH:mm:ss} UTC");
        if (s.LastUpdatedAt.HasValue)
            Console.WriteLine($"  最終更新 : {s.LastUpdatedAt.Value:yyyy-MM-dd HH:mm:ss} UTC");

        Console.WriteLine();
        Console.WriteLine($"  [{bar}] {s.CompletionRate:F1}%");
        Console.WriteLine();
        Console.WriteLine($"  完了      : {s.Done,6:N0} 件   ({FormatBytes(s.TotalDoneSizeBytes)})");
        Console.WriteLine($"  待機中    : {s.Pending,6:N0} 件");
        Console.WriteLine($"  処理中    : {s.Processing,6:N0} 件");
        Console.WriteLine($"  失敗      : {s.Failed,6:N0} 件");
        Console.WriteLine($"  永久失敗  : {s.PermanentFailed,6:N0} 件");
        Console.WriteLine($"  ─────────────────────────────────────────────────────");
        Console.WriteLine($"  合計      : {s.Total,6:N0} 件");

        if (s.RecentFailed.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  ── 最近の失敗 (最大5件) ─────────────────────────────");
            foreach (var f in s.RecentFailed)
            {
                var key = string.IsNullOrEmpty(f.Path) ? f.Name : $"{f.Path}/{f.Name}";
                Console.WriteLine($"  ✗ {key}");
                if (!string.IsNullOrEmpty(f.Error))
                {
                    var truncated = f.Error.Length > 100 ? f.Error[..100] + "…" : f.Error;
                    Console.WriteLine($"    {truncated}");
                }
            }
        }

        Console.WriteLine();
    }

    private static string BuildProgressBar(int done, int total, int width)
    {
        if (total == 0) return new string('░', width);
        var filled = (int)Math.Round((double)done / total * width);
        filled = Math.Clamp(filled, 0, width);
        return new string('█', filled) + new string('░', width - filled);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1_024 => $"{bytes} B",
        < 1_048_576 => $"{bytes / 1_024.0:F1} KB",
        < 1_073_741_824 => $"{bytes / 1_048_576.0:F1} MB",
        _ => $"{bytes / 1_073_741_824.0:F2} GB",
    };
}
