using System.CommandLine;
using System.Diagnostics;
using CloudMigrator.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Cli.Commands;

/// <summary>
/// watchdog サブコマンド。
/// transfer ログの無更新を検知して transfer プロセスを再起動する（FR-16/FR-17）。
/// </summary>
internal static class WatchdogCommand
{
    public static Command Build()
    {
        var cmd = new Command("watchdog", "転送ログを監視し、フリーズ時に transfer を自動再起動します（FR-16/FR-17）");

        cmd.SetAction(async (parseResult, ct) =>
        {
            await RunAsync(ct).ConfigureAwait(false);
        });

        return cmd;
    }

    internal static async Task RunAsync(CancellationToken ct)
    {
        using var svc = CliServices.Build();
        var logger = svc.LoggerFactory.CreateLogger("watchdog");
        var opts = svc.Options;
        var watchdogOpts = opts.Watchdog;

        logger.LogInformation(
            "watchdog 開始: ログパス={LogPath}, タイムアウト={TimeoutMin}分, ポーリング={PollSec}秒",
            opts.Paths.TransferLog,
            watchdogOpts.TimeoutMinutes,
            watchdogOpts.PollIntervalSeconds);

        int restartCount = 0;
        int exitCode;

        do
        {
            ct.ThrowIfCancellationRequested();

            logger.LogInformation("transfer プロセスを起動します（再起動回数: {Count}）", restartCount);
            exitCode = await RunTransferWithWatchAsync(
                watchdogOpts,
                opts.Paths.TransferLog,
                logger,
                ct).ConfigureAwait(false);

            restartCount++;

            // ExitCode=0: 正常完了。FR-17: 転送残あり判断は watchdog が再実行で確認
            if (exitCode == 0)
            {
                logger.LogInformation("transfer が正常終了しました。watchdog を停止します。");
                break;
            }

            if (exitCode == ExitCodes.FrozenRestart)
            {
                logger.LogWarning("フリーズ検知による再起動 #{Count}", restartCount);
                // 短い待機後に再試行
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                continue;
            }

            // 明示的なエラー終了 → watchdog も終了
            logger.LogError("transfer がエラーコード {Code} で終了しました。watchdog を停止します。", exitCode);
            break;

        } while (!ct.IsCancellationRequested);

        logger.LogInformation("watchdog 終了（再起動合計 {Count} 回）", restartCount);
    }

    /// <summary>
    /// transfer プロセスを起動し、ログ無更新タイムアウトを監視する。
    /// フリーズ検知時はプロセスをキルして <see cref="ExitCodes.FrozenRestart"/> を返す。
    /// </summary>
    internal static async Task<int> RunTransferWithWatchAsync(
        WatchdogOptions watchdogOpts,
        string logPath,
        ILogger logger,
        CancellationToken ct)
    {
        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("実行ファイルパスを取得できません。");

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = exePath,
            ArgumentList = { },
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        foreach (var arg in watchdogOpts.TransferArgs)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();

        logger.LogInformation("transfer PID={Pid}", process.Id);

        var timeout = TimeSpan.FromMinutes(watchdogOpts.TimeoutMinutes);
        var pollInterval = TimeSpan.FromSeconds(watchdogOpts.PollIntervalSeconds);
        var lastModified = GetLogLastModified(logPath);
        // ファイル未存在の場合は現在時刻を基準にしてカウントを開始する
        if (lastModified == DateTime.MinValue)
            lastModified = DateTime.UtcNow;

        using var freezeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // フリーズ監視ループ
        var monitorTask = MonitorFreezeAsync(
            logPath, timeout, pollInterval, lastModified, logger, freezeCts.Token);

        // プロセス完了待機
        var waitTask = process.WaitForExitAsync(ct);

        var completed = await Task.WhenAny(monitorTask, waitTask).ConfigureAwait(false);

        if (completed == monitorTask && await monitorTask.ConfigureAwait(false))
        {
            // フリーズ検知 → プロセスをキル
            logger.LogWarning(
                "フリーズ検知（{Min}分間ログ更新なし）。PID={Pid} をキルします。",
                watchdogOpts.TimeoutMinutes,
                process.Id);
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "プロセスのキルに失敗しました（既に終了済みの可能性があります）");
            }
            return ExitCodes.FrozenRestart;
        }

        // プロセスが先に終了した場合
        await freezeCts.CancelAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>
    /// ログファイルの更新が timeout を超えた場合に true を返す。
    /// キャンセル時は false を返す。
    /// </summary>
    private static async Task<bool> MonitorFreezeAsync(
        string logPath,
        TimeSpan timeout,
        TimeSpan pollInterval,
        DateTime lastModified,
        ILogger logger,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(pollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            var current = GetLogLastModified(logPath);
            if (current > lastModified)
            {
                lastModified = current;
                logger.LogDebug("ログ更新確認: {LastModified:O}", current);
                continue;
            }

            // 最後の更新からの経過時間を確認
            var elapsed = DateTime.UtcNow - lastModified;
            logger.LogDebug(
                "ログ未更新経過: {Elapsed:hh\\:mm\\:ss} / タイムアウト: {Timeout:hh\\:mm\\:ss}",
                elapsed, timeout);

            if (elapsed >= timeout)
                return true; // フリーズ検知
        }

        return false;
    }

    private static DateTime GetLogLastModified(string logPath)
    {
        try
        {
            return File.Exists(logPath)
                ? File.GetLastWriteTimeUtc(logPath)
                : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    internal static class ExitCodes
    {
        /// <summary>フリーズ検知による内部再起動シグナル。</summary>
        public const int FrozenRestart = -999;
    }
}
