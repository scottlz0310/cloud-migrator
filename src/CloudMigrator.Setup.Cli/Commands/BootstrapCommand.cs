using System.CommandLine;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Providers.Graph.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;

namespace CloudMigrator.Setup.Cli.Commands;

/// <summary>
/// setup bootstrap コマンド。
/// 対話形式でセットアップを完了する、初回利用者向けの入口コマンド。
/// </summary>
internal static class BootstrapCommand
{
    public static Command Build()
    {
        var cmd = new Command("bootstrap", "対話形式でセットアップを完了します（初回利用者向け）");
        var configPathOpt = new Option<string>("--config-path")
        {
            Description = "config.json の出力先",
            DefaultValueFactory = _ => "configs/config.json",
        };
        var envPathOpt = new Option<string>("--env-path")
        {
            Description = ".env の出力先",
            DefaultValueFactory = _ => ".env",
        };
        var forceOpt = new Option<bool>("--force")
        {
            Description = "既存ファイルを上書きします",
        };
        var noVerifyOpt = new Option<bool>("--no-verify")
        {
            Description = "完了後の verify（Graph疎通確認）をスキップします",
        };
        var destinationOpt = new Option<string>("--destination")
        {
            Description = "転送先プロバイダー (sharepoint または dropbox)",
            DefaultValueFactory = _ => "sharepoint",
        };

        cmd.Add(configPathOpt);
        cmd.Add(envPathOpt);
        cmd.Add(forceOpt);
        cmd.Add(noVerifyOpt);
        cmd.Add(destinationOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var configPath = parseResult.GetValue(configPathOpt) ?? "configs/config.json";
            var envPath = parseResult.GetValue(envPathOpt) ?? ".env";
            var force = parseResult.GetValue(forceOpt);
            var noVerify = parseResult.GetValue(noVerifyOpt);
            var destination = parseResult.GetValue(destinationOpt) ?? "sharepoint";
            await RunAsync(configPath, envPath, force, noVerify, destination, new DefaultBootstrapConsole(), ct).ConfigureAwait(false);
        });

        return cmd;
    }

    internal static Task RunAsync(
        string configPath,
        string envPath,
        bool force,
        bool noVerify,
        CancellationToken ct)
        => RunAsync(configPath, envPath, force, noVerify, "sharepoint", new DefaultBootstrapConsole(), ct);

    internal static async Task RunAsync(
        string configPath,
        string envPath,
        bool force,
        bool noVerify,
        string destination,
        IBootstrapConsole console,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var isDropboxDest = destination.Equals("dropbox", StringComparison.OrdinalIgnoreCase);

        console.WriteLine("=================================================================");
        console.WriteLine("  CloudMigrator セットアップウィザード");
        console.WriteLine("=================================================================");
        console.WriteLine("各項目を入力してください。Ctrl+C で中断できます。");
        console.WriteLine();

        // config.json から既存設定を読み込む（JSON のみ、env変数を含まない）
        // 再実行時のデフォルト値として使用する
        var cfgOptions = LoadConfigJsonOptions(configPath);
        var cfgClientId = NullIfWhiteSpace(cfgOptions.Graph.ClientId);
        var cfgTenantId = NullIfWhiteSpace(cfgOptions.Graph.TenantId);
        var cfgOneDriveUpn = NullIfWhiteSpace(cfgOptions.Graph.OneDriveUserId);
        var cfgSourceFolder = NullIfWhiteSpace(cfgOptions.Graph.OneDriveSourceFolder);
        var cfgDestinationRoot = NullIfWhiteSpace(cfgOptions.DestinationRoot);
        var cfgSiteId = NullIfWhiteSpace(cfgOptions.Graph.SharePointSiteId);
        var cfgDriveId = NullIfWhiteSpace(cfgOptions.Graph.SharePointDriveId);

        // 環境変数から設定を読み込む（Bitwarden+dsx 等で管理している場合に活用）
        // 空白のみの値は未設定として扱う
        var envClientId = NullIfWhiteSpace(Environment.GetEnvironmentVariable("MIGRATOR__GRAPH__CLIENTID"));
        var envTenantId = NullIfWhiteSpace(Environment.GetEnvironmentVariable("MIGRATOR__GRAPH__TENANTID"));
        var envClientSecret = NullIfWhiteSpace(Environment.GetEnvironmentVariable("MIGRATOR__GRAPH__CLIENTSECRET"));
        var envOneDriveUpn = NullIfWhiteSpace(Environment.GetEnvironmentVariable("MIGRATOR__GRAPH__ONEDRIVEUSERID"));
        var envSourceFolder = NullIfWhiteSpace(Environment.GetEnvironmentVariable("MIGRATOR__GRAPH__ONEDRIVESOURCEFOLDER"));
        var envSiteId = NullIfWhiteSpace(Environment.GetEnvironmentVariable("MIGRATOR__GRAPH__SHAREPOINTSITEID"));
        var envDriveId = NullIfWhiteSpace(Environment.GetEnvironmentVariable("MIGRATOR__GRAPH__SHAREPOINTDRIVEID"));

        // 有効なデフォルト値: 環境変数 > config.json
        var defaultClientId = envClientId ?? cfgClientId;
        var defaultTenantId = envTenantId ?? cfgTenantId;
        var defaultOneDriveUpn = envOneDriveUpn ?? cfgOneDriveUpn;
        var defaultSourceFolder = envSourceFolder ?? cfgSourceFolder;
        // 既存 SharePoint 設定（再利用候補）: 環境変数 > config.json
        var existingSiteId = envSiteId ?? cfgSiteId;
        var existingDriveId = envDriveId ?? cfgDriveId;

        // 検出済み設定の通知（環境変数由来）
        var envPresets = new (string Name, bool HasValue)[]
        {
            ("MIGRATOR__GRAPH__CLIENTID", envClientId != null),
            ("MIGRATOR__GRAPH__TENANTID", envTenantId != null),
            ("MIGRATOR__GRAPH__CLIENTSECRET（シークレット）", envClientSecret != null),
            ("MIGRATOR__GRAPH__ONEDRIVEUSERID", envOneDriveUpn != null),
            ("MIGRATOR__GRAPH__ONEDRIVESOURCEFOLDER", envSourceFolder != null),
        };
        var envPresetCount = envPresets.Count(x => x.HasValue);
        if (envPresetCount > 0)
        {
            console.WriteLine($"ℹ️  環境変数から {envPresetCount} 件の設定を検出しました。Enter で現在値を使用できます。");
            foreach (var (name, _) in envPresets.Where(x => x.HasValue))
                console.WriteLine($"     ✓ {name}");
            console.WriteLine();
        }

        // 検出済み設定の通知（config.json 由来、env未設定の項目のみ）
        var cfgPresets = new (string Name, bool HasValue)[]
        {
            ("MIGRATOR__GRAPH__CLIENTID", cfgClientId != null && envClientId == null),
            ("MIGRATOR__GRAPH__TENANTID", cfgTenantId != null && envTenantId == null),
            ("MIGRATOR__GRAPH__ONEDRIVEUSERID", cfgOneDriveUpn != null && envOneDriveUpn == null),
            ("MIGRATOR__GRAPH__ONEDRIVESOURCEFOLDER", cfgSourceFolder != null && envSourceFolder == null),
            ("destinationRoot（転送先フォルダ）", cfgDestinationRoot != null),
            ("SharePointSiteId + DriveId", cfgSiteId != null && cfgDriveId != null),
        };
        var cfgPresetCount = cfgPresets.Count(x => x.HasValue);
        if (cfgPresetCount > 0)
        {
            console.WriteLine($"ℹ️  configs/config.json から {cfgPresetCount} 件の設定を読み込みました。Enter で前回値を使用できます。");
            foreach (var (name, _) in cfgPresets.Where(x => x.HasValue))
                console.WriteLine($"     ✓ {name}");
            console.WriteLine();
        }

        // ステップ 1: Azure AD アプリ登録情報
        console.WriteLine("--- ステップ 1/3: Azure AD アプリ登録情報 ---");
        var clientId = console.Prompt("ClientId（アプリケーション ID）", defaultClientId);
        var tenantId = console.Prompt("TenantId（ディレクトリ ID）", defaultTenantId);
        var hasEnvSecret = envClientSecret != null;
        var inputSecret = console.PromptMasked("ClientSecret（入力は画面に表示されません）", hasExistingValue: hasEnvSecret);
        var clientSecret = string.IsNullOrEmpty(inputSecret) && hasEnvSecret ? envClientSecret! : inputSecret;
        console.WriteLine();

        // ステップ 2: OneDrive / SharePoint 情報
        console.WriteLine("--- ステップ 2/3: OneDrive / SharePoint 設定 ---");
        var oneDriveUserUpn = console.Prompt("OneDrive ユーザーのUPN（例: user@contoso.com）", defaultOneDriveUpn);
        console.WriteLine("  ヒント: フォルダパスを指定しない場合は Enter でドライブ全体を転送対象とします。前回値が表示されている場合でも \"-\" を入力するとクリアしてドライブ全体を指定できます。");
        var oneDriveSourceFolderInput = console.Prompt("転送元フォルダパス（省略可。例: Documents/Projects）", defaultSourceFolder);
        var oneDriveSourceFolder = oneDriveSourceFolderInput == "-" ? string.Empty : oneDriveSourceFolderInput;
        console.WriteLine("  ヒント: SharePoint ドライブ上の転送先フォルダを指定します。省略するとドライブルート直下に格納されます。");
        var destinationRootInput = console.Prompt("転送先フォルダパス（省略可。例: 移行データ/OneDrive）", cfgDestinationRoot);
        var destinationRoot = destinationRootInput == "-" ? string.Empty : destinationRootInput;

        // 並列転送設定
        console.WriteLine("  ヒント: 最大並列転送数を増やすと速度が上がりますが、レート制限にかかりやすくなります（推奨: 4〜8）。");
        var maxParallelTransfersInput = console.Prompt("最大並列転送数", cfgOptions.MaxParallelTransfers.ToString());
        var maxParallelTransfers = int.TryParse(maxParallelTransfersInput, out var mpt) && mpt >= 1
            ? mpt
            : Math.Max(1, cfgOptions.MaxParallelTransfers);
        var adaptiveConcurrencyEnabled = console.PromptBool(
            "レート制限に応じた動的並列度制御（AdaptiveConcurrency）を有効にしますか？",
            defaultValue: cfgOptions.AdaptiveConcurrency.Enabled);
        console.WriteLine();

        // Dropbox 転送先設定（--destination dropbox の場合のみ）
        string dropboxAccessToken = string.Empty;
        string dropboxRootPath = string.Empty;
        bool dropboxTokenFromEnv = false;
        if (isDropboxDest)
        {
            console.WriteLine("--- Dropbox 転送先設定 ---");
            var envDropboxToken = NullIfWhiteSpace(Environment.GetEnvironmentVariable("MIGRATOR__DROPBOX__ACCESSTOKEN"));
            dropboxTokenFromEnv = envDropboxToken != null;
            var inputDropboxToken = console.PromptMasked(
                "Dropbox AccessToken（入力は画面に表示されません）",
                hasExistingValue: dropboxTokenFromEnv);
            dropboxAccessToken = string.IsNullOrEmpty(inputDropboxToken) && dropboxTokenFromEnv
                ? envDropboxToken!
                : inputDropboxToken;
            console.WriteLine("  ヒント: Dropbox 内の転送先フォルダを指定します。省略するとルート直下に格納されます。");
            var cfgDropboxRootPath = NullIfWhiteSpace(cfgOptions.Dropbox.RootPath);
            dropboxRootPath = console.Prompt("Dropbox 転送先フォルダパス（省略可。例: /移行データ）", cfgDropboxRootPath) ?? string.Empty;
            console.WriteLine();
        }

        // SharePoint 再利用チェック: SiteId + DriveId が既存の場合は URL 入力を省略できる
        // Dropbox 転送先の場合は SharePoint の設定を求めないためスキップ
        var reuseSharePoint = false;
        string sharePointSiteUrl = string.Empty;
        if (!isDropboxDest)
        {
            if (!string.IsNullOrWhiteSpace(existingSiteId) && !string.IsNullOrWhiteSpace(existingDriveId))
            {
                console.WriteLine();
                console.WriteLine("  前回の SharePoint 設定を検出しました:");
                console.WriteLine($"    SiteId : {existingSiteId}");
                console.WriteLine($"    DriveId: {existingDriveId}");
                reuseSharePoint = !console.PromptBool(
                    "  SharePoint サイトURL を入力して再解決しますか？ (N = 前回設定を使用)",
                    defaultValue: false);
            }
            if (!reuseSharePoint)
                sharePointSiteUrl = console.Prompt("SharePoint サイトURL（例: https://contoso.sharepoint.com/sites/migration）");
            console.WriteLine();
        }

        // env変数由来フラグ（全て env変数から取得した場合は .env 生成をスキップ）
        var secretFromEnv = hasEnvSecret && string.IsNullOrEmpty(inputSecret);
        var clientIdFromEnv = envClientId != null && clientId == envClientId;
        var tenantIdFromEnv = envTenantId != null && tenantId == envTenantId;
        var upnFromEnv = envOneDriveUpn != null && oneDriveUserUpn == envOneDriveUpn;
        var sourceFolderFromEnv = envSourceFolder != null && oneDriveSourceFolder == envSourceFolder;
        bool allAuthFromEnv = clientIdFromEnv && tenantIdFromEnv && secretFromEnv && upnFromEnv;
        // Dropbox 転送先の場合: Dropbox トークンも env から取得済みであることが必要
        if (isDropboxDest)
            allAuthFromEnv = allAuthFromEnv && dropboxTokenFromEnv;

        // ステップ 3: Graph API でID解決
        string effectiveOneDriveUser;
        string siteId;
        DriveEntry selectedDrive;

        if (isDropboxDest)
        {
            // Dropbox 転送先: OneDrive 認証のみ確認（SharePoint ID 解決は不要）
            console.WriteLine("--- ステップ 3/3: OneDrive 認証確認 ---");
            console.WriteLine("OneDrive への接続を確認しています...");

            try
            {
                effectiveOneDriveUser = await ResolveOneDriveUserAsync(
                    clientId, tenantId, clientSecret, oneDriveUserUpn, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                console.WriteLine($"[ERR]  {ex.Message}");
                console.WriteLine("セットアップを中断しました。設定値を確認してもう一度お試しください。");
                Environment.ExitCode = 1;
                return;
            }
            catch (HttpRequestException ex)
            {
                console.WriteLine($"[ERR]  ネットワーク接続に失敗しました: {ex.Message}");
                console.WriteLine("インターネット接続やプロキシ設定を確認してください。");
                Environment.ExitCode = 1;
                return;
            }
            catch (MsalException ex)
            {
                console.WriteLine($"[ERR]  トークン取得に失敗しました: {ex.Message}");
                console.WriteLine("ClientId / TenantId / ClientSecret を確認してください。");
                Environment.ExitCode = 1;
                return;
            }

            siteId = string.Empty;
            selectedDrive = new DriveEntry(string.Empty, "（Dropbox 転送先）");
            console.WriteLine($"[OK]   OneDrive ユーザー: {effectiveOneDriveUser}");
            console.WriteLine($"[OK]   Dropbox 転送先: {(string.IsNullOrWhiteSpace(dropboxRootPath) ? "ルート" : dropboxRootPath)}");
            console.WriteLine();
        }
        else if (reuseSharePoint)
        {
            // SharePoint は既存 ID を再利用し、OneDrive 認証のみ確認する
            console.WriteLine("--- ステップ 3/3: OneDrive 認証確認 ---");
            console.WriteLine("OneDrive への接続を確認しています...");

            try
            {
                effectiveOneDriveUser = await ResolveOneDriveUserAsync(
                    clientId, tenantId, clientSecret, oneDriveUserUpn, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                console.WriteLine($"[ERR]  {ex.Message}");
                console.WriteLine("セットアップを中断しました。設定値を確認してもう一度お試しください。");
                Environment.ExitCode = 1;
                return;
            }
            catch (HttpRequestException ex)
            {
                console.WriteLine($"[ERR]  ネットワーク接続に失敗しました: {ex.Message}");
                console.WriteLine("インターネット接続やプロキシ設定を確認してください。");
                Environment.ExitCode = 1;
                return;
            }
            catch (MsalException ex)
            {
                console.WriteLine($"[ERR]  トークン取得に失敗しました: {ex.Message}");
                console.WriteLine("ClientId / TenantId / ClientSecret を確認してください。");
                Environment.ExitCode = 1;
                return;
            }

            siteId = existingSiteId!;
            selectedDrive = new DriveEntry(existingDriveId!, "（前回設定）");
            console.WriteLine($"[OK]   OneDrive ユーザー: {effectiveOneDriveUser}");
            console.WriteLine($"[OK]   SharePoint SiteId: {siteId}（前回設定を使用）");
            console.WriteLine($"[OK]   DriveId: {selectedDrive.Id}（前回設定を使用）");
            console.WriteLine();
        }
        else
        {
            console.WriteLine("--- ステップ 3/3: Graph API 接続 ---");
            console.WriteLine("Graph API に接続してIDを解決しています...");

            IReadOnlyList<DriveEntry> drives;
            try
            {
                (effectiveOneDriveUser, siteId, drives) = await ResolveGraphInfoAsync(
                    clientId, tenantId, clientSecret, oneDriveUserUpn, sharePointSiteUrl, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                console.WriteLine($"[ERR]  {ex.Message}");
                console.WriteLine("セットアップを中断しました。設定値を確認してもう一度お試しください。");
                Environment.ExitCode = 1;
                return;
            }
            catch (HttpRequestException ex)
            {
                console.WriteLine($"[ERR]  ネットワーク接続に失敗しました: {ex.Message}");
                console.WriteLine("インターネット接続やプロキシ設定を確認してください。");
                Environment.ExitCode = 1;
                return;
            }
            catch (MsalException ex)
            {
                console.WriteLine($"[ERR]  トークン取得に失敗しました: {ex.Message}");
                console.WriteLine("ClientId / TenantId / ClientSecret を確認してください。");
                Environment.ExitCode = 1;
                return;
            }

            console.WriteLine($"[OK]   OneDrive ユーザー: {effectiveOneDriveUser}");
            console.WriteLine($"[OK]   SharePoint サイトID: {siteId}");
            console.WriteLine();

            try
            {
                selectedDrive = SelectDrive(drives, console);
            }
            catch (InvalidOperationException ex)
            {
                console.WriteLine($"[ERR]  {ex.Message}");
                Environment.ExitCode = 1;
                return;
            }

            console.WriteLine($"[OK]   ドキュメントライブラリ: {selectedDrive.Name}");
            console.WriteLine();
        }

        // テンプレート生成・ファイル書き込み
        var configTemplate = await InitCommand.LoadTemplateAsync(
            Path.Combine("configs", "config.json"),
            InitCommand.BuildDefaultConfigTemplate(),
            ct).ConfigureAwait(false);

        configTemplate = InitCommand.ApplyGraphValuesToConfigTemplate(
            configTemplate,
            effectiveOneDriveUser,
            siteId,
            selectedDrive.Id,
            oneDriveSourceFolder,
            destinationRoot);

        configTemplate = InitCommand.ApplyPerformanceValuesToConfigTemplate(
            configTemplate,
            maxParallelTransfers,
            adaptiveConcurrencyEnabled);

        // Dropbox 転送先の場合: destinationProvider と dropbox.rootPath を config.json に反映
        if (isDropboxDest)
            configTemplate = InitCommand.ApplyDropboxValuesToConfigTemplate(configTemplate, dropboxRootPath);

        // 既存ファイルがある場合は対話的に上書き確認する（--force 指定時はスキップ）
        bool EffectiveForce(string path) =>
            force || (File.Exists(path) && console.PromptBool($"  {path} は既に存在します。上書きしますか？"));

        var configForce = EffectiveForce(configPath);
        var results = new List<InitFileResult>(capacity: 2);
        await InitCommand.WriteTemplateAsync(configPath, configTemplate, configForce, results, ct).ConfigureAwait(false);

        // .env 生成: 全認証情報が env変数由来の場合はスキップ（平文保存を避ける）
        if (!allAuthFromEnv)
        {
            var envTemplate = await InitCommand.LoadTemplateAsync(
                "sample.env",
                InitCommand.DefaultEnvTemplate,
                ct).ConfigureAwait(false);

            envTemplate = ApplyBootstrapEnvTemplate(
                envTemplate,
                effectiveOneDriveUser, upnFromEnv,
                oneDriveSourceFolder, sourceFolderFromEnv,
                siteId, selectedDrive.Id,
                clientId, clientIdFromEnv,
                tenantId, tenantIdFromEnv,
                secretFromEnv);

            // Dropbox 転送先の場合: SP フィールドをコメントアウトし Dropbox トークンを追記
            if (isDropboxDest)
            {
                envTemplate = CommentOutEnvKey(envTemplate, "MIGRATOR__GRAPH__SHAREPOINTSITEID");
                envTemplate = CommentOutEnvKey(envTemplate, "MIGRATOR__GRAPH__SHAREPOINTDRIVEID");
                if (!dropboxTokenFromEnv && !string.IsNullOrWhiteSpace(dropboxAccessToken))
                    envTemplate = InitCommand.UpsertEnvVariable(envTemplate, "MIGRATOR__DROPBOX__ACCESSTOKEN", dropboxAccessToken);
            }

            var envForce = EffectiveForce(envPath);
            await InitCommand.WriteTemplateAsync(envPath, envTemplate, envForce, results, ct).ConfigureAwait(false);
        }

        foreach (var result in results)
        {
            var msg = result.Status switch
            {
                InitFileStatus.Written => $"[OK]   {result.Path}: 生成しました",
                InitFileStatus.Skipped => $"[SKIP] {result.Path}: 既存のため未変更（--force で上書き可）",
                _ => $"[ERR]  {result.Path}: {result.Message}",
            };
            console.WriteLine(msg);
        }

        if (allAuthFromEnv)
        {
            console.WriteLine($"ℹ️  すべての認証情報が環境変数から取得済みのため .env の生成をスキップしました。");
            console.WriteLine($"   以下の識別子を環境変数マネージャーに追加してください：");
            if (!isDropboxDest)
            {
                console.WriteLine($"   MIGRATOR__GRAPH__SHAREPOINTSITEID={siteId}");
                console.WriteLine($"   MIGRATOR__GRAPH__SHAREPOINTDRIVEID={selectedDrive.Id}");
            }
            if (!string.IsNullOrWhiteSpace(oneDriveSourceFolder))
                console.WriteLine($"   MIGRATOR__GRAPH__ONEDRIVESOURCEFOLDER={oneDriveSourceFolder}");
        }

        if (results.Any(r => r.Status == InitFileStatus.Error))
        {
            Environment.ExitCode = 1;
            return;
        }

        console.WriteLine();
        // ClientSecret の手動設定案内（env変数から取得済みの場合はスキップ）
        if (!secretFromEnv)
        {
            console.WriteLine("ℹ️  MIGRATOR__GRAPH__CLIENTSECRET はセキュリティのため設定ファイルに保存されていません。");
            console.WriteLine("   シェル環境に手動で設定してください:");
            console.WriteLine("     PowerShell: $env:MIGRATOR__GRAPH__CLIENTSECRET = \"<your-secret>\"");
            console.WriteLine("     bash/zsh  : export MIGRATOR__GRAPH__CLIENTSECRET=\"<your-secret>\"");
            console.WriteLine();
        }

        // doctor で設定診断
        console.WriteLine("設定を診断しています（doctor）...");
        SetEnvForSession(clientId, tenantId, clientSecret, effectiveOneDriveUser, oneDriveSourceFolder, siteId, selectedDrive.Id);
        // Dropbox 転送先の場合: AccessToken を現セッションの環境変数に設定（doctor チェック用）
        if (isDropboxDest && !string.IsNullOrWhiteSpace(dropboxAccessToken))
            Environment.SetEnvironmentVariable("MIGRATOR__DROPBOX__ACCESSTOKEN", dropboxAccessToken);
        DoctorCommand.Run(configPath, strictDropbox: isDropboxDest, ct);
        console.WriteLine();

        // verify（任意）
        if (!noVerify)
        {
            console.WriteLine("Graph API の疎通確認（verify）を実行します...");
            // Dropbox 転送先の場合は SharePoint 疎通確認をスキップ
            await VerifyCommand.RunAsync(configPath, timeoutSec: 30, skipOnedrive: false, skipSharepoint: isDropboxDest, ct).ConfigureAwait(false);
            console.WriteLine();
        }

        console.WriteLine("=================================================================");
        console.WriteLine("  セットアップ完了！");
        console.WriteLine("=================================================================");
        console.WriteLine($"  config.json : {Path.GetFullPath(configPath)}");
        if (!allAuthFromEnv)
            console.WriteLine($"  .env        : {Path.GetFullPath(envPath)}");
        console.WriteLine();
        console.WriteLine("  次のステップ: transfer コマンドを実行してください。");
    }

    /// <summary>
    /// ドライブ候補一覧からユーザーに選択させ、選択されたエントリを返す。
    /// </summary>
    internal static DriveEntry SelectDrive(IReadOnlyList<DriveEntry> drives, IBootstrapConsole console)
    {
        if (drives.Count == 0)
            throw new InvalidOperationException("SharePoint サイトにドキュメントライブラリが見つかりませんでした。");

        if (drives.Count == 1)
        {
            console.WriteLine($"ドキュメントライブラリを自動選択しました: {drives[0].Name}");
            return drives[0];
        }

        console.WriteLine("利用可能なドキュメントライブラリ:");
        for (var i = 0; i < drives.Count; i++)
            console.WriteLine($"  {i + 1}. {drives[i].Name}");

        // "Documents" が存在する場合はそのインデックスをデフォルト候補にする
        var defaultIdx = 1;
        for (var i = 0; i < drives.Count; i++)
        {
            if (string.Equals(drives[i].Name, "Documents", StringComparison.OrdinalIgnoreCase))
            {
                defaultIdx = i + 1;
                break;
            }
        }

        var selected = console.PromptInt($"番号を入力してください (1-{drives.Count})", min: 1, max: drives.Count, defaultValue: defaultIdx);
        return drives[selected - 1];
    }

    /// <summary>
    /// Graph API の drives 応答 JSON から DriveEntry のリストを解析して返す。
    /// </summary>
    internal static IReadOnlyList<DriveEntry> ParseDrives(string drivesJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(drivesJson);
            if (!doc.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Graph drives 応答の形式が不正です。");

            var drives = new List<DriveEntry>();
            foreach (var elem in values.EnumerateArray())
            {
                var name = elem.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                var id = elem.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                    drives.Add(new DriveEntry(id, name));
            }

            return drives.AsReadOnly();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Graph drives 応答の JSON 解析に失敗しました。応答形式が不正です。", ex);
        }
    }

    private static async Task<(string OneDriveUser, string SiteId, IReadOnlyList<DriveEntry> Drives)> ResolveGraphInfoAsync(
        string clientId,
        string tenantId,
        string clientSecret,
        string oneDriveUserUpn,
        string sharePointSiteUrl,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("ClientId が入力されていません。");
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("TenantId が入力されていません。");
        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("ClientSecret が入力されていません。");
        if (string.IsNullOrWhiteSpace(oneDriveUserUpn))
            throw new InvalidOperationException("OneDrive ユーザーのUPN が入力されていません。");
        if (string.IsNullOrWhiteSpace(sharePointSiteUrl))
            throw new InvalidOperationException("SharePoint サイトURL が入力されていません。");

        var authenticator = new GraphAuthenticator(clientId, tenantId, clientSecret);
        var token = await authenticator
            .GetAuthorizationTokenAsync(new Uri("https://graph.microsoft.com/v1.0/"), cancellationToken: ct)
            .ConfigureAwait(false);

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            // ユーザー情報取得（User.Read.All が付与されていない場合は入力UPNをそのまま使用）
            var userBody = await TryGetGraphJsonAsync(
                httpClient,
                $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(oneDriveUserUpn)}?$select=id,userPrincipalName",
                "onedrive.user",
                ct).ConfigureAwait(false);
            var effectiveUser = userBody is not null
                ? (TryReadProperty(userBody, "userPrincipalName") ?? oneDriveUserUpn)
                : oneDriveUserUpn;

            var address = InitCommand.ParseSharePointSiteUrl(sharePointSiteUrl);
            var siteBody = await GetGraphJsonAsync(
                httpClient,
                InitCommand.BuildSiteLookupUrl(address.HostName, address.SitePath),
                "sharepoint.site",
                ct).ConfigureAwait(false);
            var siteId = TryReadProperty(siteBody, "id")
                ?? throw new InvalidOperationException("SharePoint サイトIDの抽出に失敗しました。");

            var drivesBody = await GetGraphJsonAsync(
                httpClient,
                $"https://graph.microsoft.com/v1.0/sites/{Uri.EscapeDataString(siteId)}/drives?$select=id,name",
                "sharepoint.drives",
                ct).ConfigureAwait(false);
            var drives = ParseDrives(drivesBody);

            return (effectiveUser, siteId, drives);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Microsoft Graph への接続がタイムアウトしました。ネットワーク状態や Graph の応答状況を確認してください。");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Microsoft Graph への接続に失敗しました: {ex.Message}", ex);
        }
    }

    private static void SetEnvForSession(
        string clientId, string tenantId, string clientSecret,
        string oneDriveUserId, string? sourceFolder, string siteId, string driveId)
    {
        Environment.SetEnvironmentVariable("MIGRATOR__GRAPH__CLIENTID", clientId);
        Environment.SetEnvironmentVariable("MIGRATOR__GRAPH__TENANTID", tenantId);
        Environment.SetEnvironmentVariable("MIGRATOR__GRAPH__CLIENTSECRET", clientSecret);
        Environment.SetEnvironmentVariable("MIGRATOR__GRAPH__ONEDRIVEUSERID", oneDriveUserId);
        Environment.SetEnvironmentVariable("MIGRATOR__GRAPH__SHAREPOINTSITEID", siteId);
        Environment.SetEnvironmentVariable("MIGRATOR__GRAPH__SHAREPOINTDRIVEID", driveId);
        // 空/未指定の場合は既存の環境変数を明示的にクリアする（古い値が残って誤動作するのを防ぐ）
        Environment.SetEnvironmentVariable(
            "MIGRATOR__GRAPH__ONEDRIVESOURCEFOLDER",
            string.IsNullOrWhiteSpace(sourceFolder) ? null : sourceFolder);
    }

    internal static string ApplyBootstrapEnvTemplate(
        string template,
        string effectiveOneDriveUser, bool upnFromEnv,
        string oneDriveSourceFolder, bool sourceFolderFromEnv,
        string siteId, string driveId,
        string clientId, bool clientIdFromEnv,
        string tenantId, bool tenantIdFromEnv,
        bool secretFromEnv)
    {
        var updated = template;

        // auth値: env変数由来 → コメントアウト（.envローダーによる空値上書きを防ぐ）
        //         手動入力   → 値を反映
        updated = clientIdFromEnv
            ? CommentOutEnvKey(updated, "MIGRATOR__GRAPH__CLIENTID")
            : InitCommand.UpsertEnvVariable(updated, "MIGRATOR__GRAPH__CLIENTID", clientId);

        updated = tenantIdFromEnv
            ? CommentOutEnvKey(updated, "MIGRATOR__GRAPH__TENANTID")
            : InitCommand.UpsertEnvVariable(updated, "MIGRATOR__GRAPH__TENANTID", tenantId);

        // ClientSecret は常にプレースホルダーのまま（セキュリティ上 .env に保存しない）
        // env変数から取得済みの場合はコメントアウトして誤設定を防ぐ
        if (secretFromEnv)
            updated = CommentOutEnvKey(updated, "MIGRATOR__GRAPH__CLIENTSECRET");

        updated = upnFromEnv
            ? CommentOutEnvKey(updated, "MIGRATOR__GRAPH__ONEDRIVEUSERID")
            : InitCommand.UpsertEnvVariable(updated, "MIGRATOR__GRAPH__ONEDRIVEUSERID", effectiveOneDriveUser);

        // 転送元フォルダパス（空文字はドライブ全体なのでプレースホルダーのまま）
        if (!string.IsNullOrWhiteSpace(oneDriveSourceFolder))
        {
            updated = sourceFolderFromEnv
                ? CommentOutEnvKey(updated, "MIGRATOR__GRAPH__ONEDRIVESOURCEFOLDER")
                : InitCommand.UpsertEnvVariable(updated, "MIGRATOR__GRAPH__ONEDRIVESOURCEFOLDER", oneDriveSourceFolder);
        }

        // Graph解決済みID は常に反映（新規取得値のため）
        updated = InitCommand.UpsertEnvVariable(updated, "MIGRATOR__GRAPH__SHAREPOINTSITEID", siteId);
        updated = InitCommand.UpsertEnvVariable(updated, "MIGRATOR__GRAPH__SHAREPOINTDRIVEID", driveId);

        return updated;
    }

    internal static string CommentOutEnvKey(string template, string key)
    {
        var normalized = template.ReplaceLineEndings("\n");
        var pattern = $@"^{Regex.Escape(key)}=.*$";
        if (!Regex.IsMatch(normalized, pattern, RegexOptions.Multiline))
            return template;

        var replaced = Regex.Replace(
            normalized,
            pattern,
            $"# {key}= # シェル環境変数から取得済み（.env への保存をスキップ）",
            RegexOptions.Multiline);
        return replaced.Replace("\n", Environment.NewLine);
    }

    internal static async Task<string?> TryGetGraphJsonAsync(
        HttpClient httpClient,
        string url,
        string probeName,
        CancellationToken ct)
    {
        using var response = await httpClient.GetAsync(url, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // 403 Forbidden のみ graceful fallback（User.Read.All が未付与の場合）
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            return null;

        // それ以外（404/401/429 等）は通常エラーとして例外をスロー
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var snippet = body.ReplaceLineEndings(" ").Trim();
        if (snippet.Length > 180) snippet = snippet[..180] + "...";
        throw new InvalidOperationException(
            $"{probeName} の取得に失敗しました: HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {snippet}");
    }

    private static async Task<string> GetGraphJsonAsync(
        HttpClient httpClient,
        string url,
        string probeName,
        CancellationToken ct)
    {
        using var response = await httpClient.GetAsync(url, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return body;

        var snippet = body.ReplaceLineEndings(" ").Trim();
        if (snippet.Length > 180) snippet = snippet[..180] + "...";
        throw new InvalidOperationException(
            $"{probeName} の取得に失敗しました: HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {snippet}");
    }

    private static string? TryReadProperty(string json, string propertyName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(propertyName, out var property)
                ? property.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// config.json のみ（env変数を含まない）から設定を読み込む。
    /// ファイルが存在しない場合やJSONが壊れている場合はデフォルト値を返す。
    /// </summary>
    internal static MigratorOptions LoadConfigJsonOptions(string configPath)
    {
        if (!File.Exists(configPath))
            return new MigratorOptions();

        try
        {
            var cfg = new ConfigurationBuilder()
                .AddJsonFile(configPath, optional: true, reloadOnChange: false)
                .Build();

            return cfg.GetSection(MigratorOptions.SectionName).Get<MigratorOptions>() ?? new MigratorOptions();
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or System.IO.IOException)
        {
            // 壊れた config.json はウィザードを止めず、前回値なしとして続行する
            Console.Error.WriteLine(
                $"[WARN] configs/config.json の解析に失敗しました（前回値は使用しません）: {ex.Message}");
            return new MigratorOptions();
        }
    }

    /// <summary>
    /// OneDrive ユーザーの認証のみ確認し、有効な UPN を返す。
    /// SharePoint 解決はスキップする（前回設定を再利用する場合に使用）。
    /// </summary>
    private static async Task<string> ResolveOneDriveUserAsync(
        string clientId,
        string tenantId,
        string clientSecret,
        string oneDriveUserUpn,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("ClientId が入力されていません。");
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("TenantId が入力されていません。");
        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("ClientSecret が入力されていません。");
        if (string.IsNullOrWhiteSpace(oneDriveUserUpn))
            throw new InvalidOperationException("OneDrive ユーザーのUPN が入力されていません。");

        var authenticator = new GraphAuthenticator(clientId, tenantId, clientSecret);
        var token = await authenticator
            .GetAuthorizationTokenAsync(new Uri("https://graph.microsoft.com/v1.0/"), cancellationToken: ct)
            .ConfigureAwait(false);

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var userBody = await TryGetGraphJsonAsync(
                httpClient,
                $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(oneDriveUserUpn)}?$select=id,userPrincipalName",
                "onedrive.user",
                ct).ConfigureAwait(false);

            return userBody is not null
                ? (TryReadProperty(userBody, "userPrincipalName") ?? oneDriveUserUpn)
                : oneDriveUserUpn;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Microsoft Graph への接続がタイムアウトしました。");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Microsoft Graph への接続に失敗しました: {ex.Message}", ex);
        }
    }

    /// <summary>空白のみの文字列を null に正規化する。</summary>
    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>BootstrapCommand が使用するドライブ情報。</summary>
internal sealed record DriveEntry(string Id, string Name);

/// <summary>テスト差し替えを可能にするコンソール抽象。</summary>
internal interface IBootstrapConsole
{
    void WriteLine(string message = "");
    string Prompt(string label, string? defaultValue = null);
    string PromptMasked(string label, bool hasExistingValue = false);
    bool PromptBool(string label, bool defaultValue = false);
    int PromptInt(string label, int min, int max, int? defaultValue = null);
}

/// <summary>実際の <see cref="Console"/> に委譲する本番用実装。</summary>
internal sealed class DefaultBootstrapConsole : IBootstrapConsole
{
    public void WriteLine(string message = "") => Console.WriteLine(message);

    public string Prompt(string label, string? defaultValue = null)
    {
        Console.Write(defaultValue is not null ? $"{label} [{defaultValue}]: " : $"{label}: ");
        var input = Console.ReadLine()?.Trim();
        return string.IsNullOrWhiteSpace(input) && defaultValue is not null ? defaultValue : input ?? "";
    }

    public string PromptMasked(string label, bool hasExistingValue = false)
    {
        Console.Write(hasExistingValue ? $"{label}（設定済み — Enter でスキップ）: " : $"{label}: ");
        var builder = new StringBuilder();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace && builder.Length > 0)
            {
                builder.Remove(builder.Length - 1, 1);
                Console.Write("\b \b");
            }
            else if (key.Key != ConsoleKey.Backspace && !char.IsControl(key.KeyChar) && key.KeyChar != '\0')
            {
                builder.Append(key.KeyChar);
                Console.Write('*');
            }
        }
        Console.WriteLine();
        return builder.ToString();
    }

    public bool PromptBool(string label, bool defaultValue = false)
    {
        Console.Write($"{label} ({(defaultValue ? "Y/n" : "y/N")}): ");
        var input = Console.ReadLine()?.Trim().ToUpperInvariant();
        return input switch
        {
            "Y" or "YES" => true,
            "N" or "NO" => false,
            _ => defaultValue,
        };
    }

    public int PromptInt(string label, int min, int max, int? defaultValue = null)
    {
        while (true)
        {
            Console.Write(defaultValue.HasValue ? $"{label} (既定: {defaultValue}): " : $"{label}: ");
            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(input) && defaultValue.HasValue)
                return defaultValue.Value;
            if (int.TryParse(input, out var result) && result >= min && result <= max)
                return result;
            Console.WriteLine($"  {min} から {max} の数値を入力してください。");
        }
    }
}
