using System.CommandLine;
using CloudMigrator.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace CloudMigrator.Setup.Cli.Commands;

/// <summary>
/// setup doctor コマンド。
/// 設定不足や典型的な入力ミスを事前検出して、実行前の摩擦を下げる。
/// </summary>
internal static class DoctorCommand
{
    public static Command Build()
    {
        var cmd = new Command("doctor", "必須設定と主要パスを診断します");
        var configPathOpt = new Option<string?>("--config-path")
        {
            Description = "設定ファイルパス（省略時は自動探索）",
        };
        var strictDropboxOpt = new Option<bool>("--strict-dropbox")
        {
            Description = "Dropbox トークン不足を警告ではなくエラーとして扱います",
        };
        cmd.Add(configPathOpt);
        cmd.Add(strictDropboxOpt);

        cmd.SetAction((parseResult, ct) =>
        {
            var configPath = parseResult.GetValue(configPathOpt);
            var strictDropbox = parseResult.GetValue(strictDropboxOpt);
            Run(configPath, strictDropbox, ct);
            return Task.CompletedTask;
        });

        return cmd;
    }

    internal static void Run(string? configPath, bool strictDropbox, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // configPath 未指定の場合は自動探索した実際のパスを取得して表示に使う
        var resolvedConfigPath = configPath ?? AppConfiguration.ResolveConfigPath();

        var config = AppConfiguration.Build(resolvedConfigPath);
        var options = config.GetSection(MigratorOptions.SectionName).Get<MigratorOptions>()
            ?? new MigratorOptions();

        var checks = BuildChecks(
            options,
            AppConfiguration.GetGraphClientSecret(),
            AppConfiguration.GetDropboxAccessToken(),
            resolvedConfigPath,
            strictDropbox);

        var errorCount = 0;
        var warningCount = 0;

        foreach (var check in checks)
        {
            switch (check.Status)
            {
                case DoctorCheckStatus.Ok:
                    Console.WriteLine($"[OK]   {check.Name}: {check.Message}");
                    break;
                case DoctorCheckStatus.Warning:
                    warningCount++;
                    Console.WriteLine($"[WARN] {check.Name}: {check.Message}");
                    break;
                case DoctorCheckStatus.Error:
                    errorCount++;
                    Console.WriteLine($"[ERR]  {check.Name}: {check.Message}");
                    break;
            }
        }

        Console.WriteLine($"doctor 結果: error={errorCount}, warning={warningCount}");
        if (errorCount > 0)
            Environment.ExitCode = 1;
    }

    internal static IReadOnlyList<DoctorCheckResult> BuildChecks(
        MigratorOptions options,
        string graphClientSecret,
        string dropboxAccessToken,
        string? resolvedConfigPath,
        bool strictDropbox)
    {
        var isDropboxDest = options.DestinationProvider.Equals("dropbox", StringComparison.OrdinalIgnoreCase);

        // SharePoint 必須フィールドのチェック: Dropbox 転送先の場合は警告扱いに緩和
        static DoctorCheckResult SpCheck(bool isDropbox, string name, string? value, string sourceKey) =>
            isDropbox
                ? (string.IsNullOrWhiteSpace(value)
                    ? new DoctorCheckResult(DoctorCheckStatus.Warning, name, $"{sourceKey} が未設定です（転送先 Dropbox の場合は OneDrive ソースのみ必要）。")
                    : new DoctorCheckResult(DoctorCheckStatus.Ok, name, "設定済み"))
                : (string.IsNullOrWhiteSpace(value)
                    ? new DoctorCheckResult(DoctorCheckStatus.Error, name, $"{sourceKey} が未設定です。")
                    : new DoctorCheckResult(DoctorCheckStatus.Ok, name, "設定済み"));

        var checks = new List<DoctorCheckResult>
        {
            Required("graph.clientId", options.Graph.ClientId, "MIGRATOR__GRAPH__CLIENTID"),
            Required("graph.tenantId", options.Graph.TenantId, "MIGRATOR__GRAPH__TENANTID"),
            Required("graph.oneDriveUserId", options.Graph.OneDriveUserId, "MIGRATOR__GRAPH__ONEDRIVEUSERID"),
            SpCheck(isDropboxDest, "graph.sharePointSiteId", options.Graph.SharePointSiteId, "MIGRATOR__GRAPH__SHAREPOINTSITEID"),
            SpCheck(isDropboxDest, "graph.sharePointDriveId", options.Graph.SharePointDriveId, "MIGRATOR__GRAPH__SHAREPOINTDRIVEID"),
            Required("graph.clientSecret", graphClientSecret, "MIGRATOR__GRAPH__CLIENTSECRET"),
            Required("paths.skipList", options.Paths.SkipList, "migrator.paths.skipList"),
            Required("paths.oneDriveCache", options.Paths.OneDriveCache, "migrator.paths.oneDriveCache"),
            Required("paths.sharePointCache", options.Paths.SharePointCache, "migrator.paths.sharePointCache"),
            Required("paths.dropboxCache", options.Paths.DropboxCache, "migrator.paths.dropboxCache"),
            Required("paths.transferLog", options.Paths.TransferLog, "migrator.paths.transferLog"),
            Required("paths.configHash", options.Paths.ConfigHash, "migrator.paths.configHash"),
        };

        if (string.IsNullOrWhiteSpace(dropboxAccessToken))
        {
            // 転送先が Dropbox か --strict-dropbox 指定の場合は必須エラー
            checks.Add(isDropboxDest || strictDropbox
                ? new DoctorCheckResult(
                    DoctorCheckStatus.Error,
                    "dropbox.accessToken",
                    "MIGRATOR__DROPBOX__ACCESSTOKEN が未設定です。")
                : new DoctorCheckResult(
                    DoctorCheckStatus.Warning,
                    "dropbox.accessToken",
                    "MIGRATOR__DROPBOX__ACCESSTOKEN が未設定です（Dropbox を使わない場合は無視可）。"));
        }
        else
        {
            checks.Add(new DoctorCheckResult(
                DoctorCheckStatus.Ok,
                "dropbox.accessToken",
                "設定済み"));
        }

        if (!string.IsNullOrWhiteSpace(resolvedConfigPath))
        {
            checks.Add(File.Exists(resolvedConfigPath)
                ? new DoctorCheckResult(DoctorCheckStatus.Ok, "config.path", $"検出: {resolvedConfigPath}")
                : new DoctorCheckResult(
                    DoctorCheckStatus.Warning,
                    "config.path",
                    $"設定ファイルが見つかりません: {resolvedConfigPath}（環境変数のみで実行されます）"));
        }

        return checks;
    }

    private static DoctorCheckResult Required(string name, string? value, string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new DoctorCheckResult(
                DoctorCheckStatus.Error,
                name,
                $"{sourceKey} が未設定です。");

        return new DoctorCheckResult(DoctorCheckStatus.Ok, name, "設定済み");
    }
}

internal enum DoctorCheckStatus
{
    Ok,
    Warning,
    Error,
}

internal sealed record DoctorCheckResult(
    DoctorCheckStatus Status,
    string Name,
    string Message);
