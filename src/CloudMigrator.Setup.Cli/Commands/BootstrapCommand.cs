using System.CommandLine;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudMigrator.Providers.Graph.Auth;
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

        cmd.Add(configPathOpt);
        cmd.Add(envPathOpt);
        cmd.Add(forceOpt);
        cmd.Add(noVerifyOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var configPath = parseResult.GetValue(configPathOpt) ?? "configs/config.json";
            var envPath = parseResult.GetValue(envPathOpt) ?? ".env";
            var force = parseResult.GetValue(forceOpt);
            var noVerify = parseResult.GetValue(noVerifyOpt);
            await RunAsync(configPath, envPath, force, noVerify, new DefaultBootstrapConsole(), ct).ConfigureAwait(false);
        });

        return cmd;
    }

    internal static Task RunAsync(
        string configPath,
        string envPath,
        bool force,
        bool noVerify,
        CancellationToken ct)
        => RunAsync(configPath, envPath, force, noVerify, new DefaultBootstrapConsole(), ct);

    internal static async Task RunAsync(
        string configPath,
        string envPath,
        bool force,
        bool noVerify,
        IBootstrapConsole console,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        console.WriteLine("=================================================================");
        console.WriteLine("  CloudMigrator セットアップウィザード");
        console.WriteLine("=================================================================");
        console.WriteLine("各項目を入力してください。Ctrl+C で中断できます。");
        console.WriteLine();

        // ステップ 1: Azure AD アプリ登録情報
        console.WriteLine("--- ステップ 1/3: Azure AD アプリ登録情報 ---");
        var clientId = console.Prompt("ClientId（アプリケーション ID）");
        var tenantId = console.Prompt("TenantId（ディレクトリ ID）");
        var clientSecret = console.PromptMasked("ClientSecret（入力は画面に表示されません）");
        console.WriteLine();

        // ステップ 2: OneDrive / SharePoint 情報
        console.WriteLine("--- ステップ 2/3: OneDrive / SharePoint 設定 ---");
        var oneDriveUserId = console.Prompt("OneDrive ユーザーのUPN（例: user@contoso.com）");
        var sharePointSiteUrl = console.Prompt("SharePoint サイトURL（例: https://contoso.sharepoint.com/sites/migration）");
        console.WriteLine();

        // ステップ 3: Graph API でID解決
        console.WriteLine("--- ステップ 3/3: Graph API 接続 ---");
        console.WriteLine("Graph API に接続してIDを解決しています...");

        string effectiveOneDriveUser;
        string siteId;
        IReadOnlyList<DriveEntry> drives;

        try
        {
            (effectiveOneDriveUser, siteId, drives) = await ResolveGraphInfoAsync(
                clientId, tenantId, clientSecret, oneDriveUserId, sharePointSiteUrl, ct).ConfigureAwait(false);
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

        // ドライブ選択
        DriveEntry selectedDrive;
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

        // テンプレート生成・ファイル書き込み
        var configTemplate = await InitCommand.LoadTemplateAsync(
            Path.Combine("configs", "config.json"),
            InitCommand.BuildDefaultConfigTemplate(),
            ct).ConfigureAwait(false);
        var envTemplate = await InitCommand.LoadTemplateAsync(
            "sample.env",
            InitCommand.DefaultEnvTemplate,
            ct).ConfigureAwait(false);

        configTemplate = InitCommand.ApplyGraphValuesToConfigTemplate(
            configTemplate,
            effectiveOneDriveUser,
            siteId,
            selectedDrive.Id);

        envTemplate = InitCommand.ApplyGraphValuesToEnvTemplate(
            envTemplate,
            effectiveOneDriveUser,
            siteId,
            selectedDrive.Id,
            clientId,
            tenantId);

        // 既存ファイルがある場合は対話的に上書き確認する（--force 指定時はスキップ）
        bool EffectiveForce(string path) =>
            force || (File.Exists(path) && console.PromptBool($"  {path} は既に存在します。上書きしますか？"));

        var configForce = EffectiveForce(configPath);
        var envForce = EffectiveForce(envPath);

        var results = new List<InitFileResult>(capacity: 2);
        await InitCommand.WriteTemplateAsync(configPath, configTemplate, configForce, results, ct).ConfigureAwait(false);
        await InitCommand.WriteTemplateAsync(envPath, envTemplate, envForce, results, ct).ConfigureAwait(false);

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

        if (results.Any(r => r.Status == InitFileStatus.Error))
        {
            Environment.ExitCode = 1;
            return;
        }

        console.WriteLine();
        console.WriteLine("ℹ️  MIGRATOR__GRAPH__CLIENTSECRET はセキュリティのため設定ファイルに保存されていません。");
        console.WriteLine("   シェル環境に手動で設定してください:");
        console.WriteLine("     PowerShell: $env:MIGRATOR__GRAPH__CLIENTSECRET = \"<your-secret>\"");
        console.WriteLine("     bash/zsh  : export MIGRATOR__GRAPH__CLIENTSECRET=\"<your-secret>\"");
        console.WriteLine();

        // doctor で設定診断
        console.WriteLine("設定を診断しています（doctor）...");
        SetEnvForSession(clientId, tenantId, clientSecret, effectiveOneDriveUser, siteId, selectedDrive.Id);
        DoctorCommand.Run(configPath, strictDropbox: false, ct);
        console.WriteLine();

        // verify（任意）
        if (!noVerify)
        {
            console.WriteLine("Graph API の疎通確認（verify）を実行します...");
            await VerifyCommand.RunAsync(configPath, timeoutSec: 30, skipOnedrive: false, skipSharepoint: false, ct).ConfigureAwait(false);
            console.WriteLine();
        }

        console.WriteLine("=================================================================");
        console.WriteLine("  セットアップ完了！");
        console.WriteLine("=================================================================");
        console.WriteLine($"  config.json : {Path.GetFullPath(configPath)}");
        console.WriteLine($"  .env        : {Path.GetFullPath(envPath)}");
        console.WriteLine();
        console.WriteLine("  次のステップ: .env を読み込んで transfer コマンドを実行してください。");
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
        string oneDriveUserId,
        string sharePointSiteUrl,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("ClientId が入力されていません。");
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("TenantId が入力されていません。");
        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("ClientSecret が入力されていません。");
        if (string.IsNullOrWhiteSpace(oneDriveUserId))
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
            var userBody = await GetGraphJsonAsync(
                httpClient,
                $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(oneDriveUserId)}?$select=id,userPrincipalName",
                "onedrive.user",
                ct).ConfigureAwait(false);
            var effectiveUser = TryReadProperty(userBody, "userPrincipalName") ?? oneDriveUserId;

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
        string oneDriveUserId, string siteId, string driveId)
    {
        Environment.SetEnvironmentVariable("MIGRATOR__GRAPH__CLIENTID", clientId);
        Environment.SetEnvironmentVariable("MIGRATOR__GRAPH__TENANTID", tenantId);
        Environment.SetEnvironmentVariable("MIGRATOR__GRAPH__CLIENTSECRET", clientSecret);
        Environment.SetEnvironmentVariable("MIGRATOR__GRAPH__ONEDRIVEUSERID", oneDriveUserId);
        Environment.SetEnvironmentVariable("MIGRATOR__GRAPH__SHAREPOINTSITEID", siteId);
        Environment.SetEnvironmentVariable("MIGRATOR__GRAPH__SHAREPOINTDRIVEID", driveId);
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
}

/// <summary>BootstrapCommand が使用するドライブ情報。</summary>
internal sealed record DriveEntry(string Id, string Name);

/// <summary>テスト差し替えを可能にするコンソール抽象。</summary>
internal interface IBootstrapConsole
{
    void WriteLine(string message = "");
    string Prompt(string label, string? defaultValue = null);
    string PromptMasked(string label);
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

    public string PromptMasked(string label)
    {
        Console.Write($"{label}: ");
        var builder = new StringBuilder();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace && builder.Length > 0)
            {
                builder.Remove(builder.Length - 1, 1);
                Console.Write("\b \b");
            }
            else if (key.Key != ConsoleKey.Backspace)
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
