using System.CommandLine;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Providers.Graph.Auth;
using Microsoft.Extensions.Configuration;

namespace CloudMigrator.Setup.Cli.Commands;

/// <summary>
/// setup init コマンド。
/// config.json と .env テンプレートを冪等に生成する。
/// </summary>
internal static class InitCommand
{
    public static Command Build()
    {
        var cmd = new Command("init", "設定テンプレート（config.json/.env）を生成します");
        var configPathOpt = new Option<string>("--config-path")
        {
            Description = "config.json の出力先",
            DefaultValueFactory = _ => "configs/config.json",
        };
        var envPathOpt = new Option<string>("--env-path")
        {
            Description = ".env テンプレートの出力先",
            DefaultValueFactory = _ => ".env",
        };
        var forceOpt = new Option<bool>("--force")
        {
            Description = "既存ファイルを上書きします",
        };
        var oneDriveUserIdOpt = new Option<string?>("--onedrive-user-id")
        {
            Description = "OneDrive ユーザーIDまたはUPN（指定時は生成ファイルへ反映）",
        };
        var sharePointSiteIdOpt = new Option<string?>("--sharepoint-site-id")
        {
            Description = "SharePoint サイトID（指定時は生成ファイルへ反映）",
        };
        var sharePointDriveIdOpt = new Option<string?>("--sharepoint-drive-id")
        {
            Description = "SharePoint ドライブID（指定時は生成ファイルへ反映）",
        };
        var resolveGraphIdsOpt = new Option<bool>("--resolve-graph-ids")
        {
            Description = "Graph API から SharePoint サイト/ドライブIDを自動解決します",
        };
        var sharePointSiteUrlOpt = new Option<string?>("--sharepoint-site-url")
        {
            Description = "自動解決に使う SharePoint サイトURL（例: https://contoso.sharepoint.com/sites/migration）",
        };
        var sharePointDriveNameOpt = new Option<string>("--sharepoint-drive-name")
        {
            Description = "自動解決時に選択するドキュメントライブラリ名",
            DefaultValueFactory = _ => "Documents",
        };
        var oneDriveSourceFolderOpt = new Option<string?>("--onedrive-source-folder")
        {
            Description = "転送元フォルダパス（省略時はドライブ全体。例: Documents/Projects）",
        };
        var destinationRootOpt = new Option<string?>("--destination-root")
        {
            Description = "転送先フォルダパス（SharePoint ドライブ上のルート。省略時はドライブルート直下）",
        };
        cmd.Add(configPathOpt);
        cmd.Add(envPathOpt);
        cmd.Add(forceOpt);
        cmd.Add(oneDriveUserIdOpt);
        cmd.Add(sharePointSiteIdOpt);
        cmd.Add(sharePointDriveIdOpt);
        cmd.Add(resolveGraphIdsOpt);
        cmd.Add(sharePointSiteUrlOpt);
        cmd.Add(sharePointDriveNameOpt);
        cmd.Add(oneDriveSourceFolderOpt);
        cmd.Add(destinationRootOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var configPath = parseResult.GetValue(configPathOpt) ?? "configs/config.json";
            var envPath = parseResult.GetValue(envPathOpt) ?? ".env";
            var force = parseResult.GetValue(forceOpt);
            var oneDriveUserId = parseResult.GetValue(oneDriveUserIdOpt);
            var sharePointSiteId = parseResult.GetValue(sharePointSiteIdOpt);
            var sharePointDriveId = parseResult.GetValue(sharePointDriveIdOpt);
            var resolveGraphIds = parseResult.GetValue(resolveGraphIdsOpt);
            var sharePointSiteUrl = parseResult.GetValue(sharePointSiteUrlOpt);
            var sharePointDriveName = parseResult.GetValue(sharePointDriveNameOpt) ?? "Documents";
            var oneDriveSourceFolder = parseResult.GetValue(oneDriveSourceFolderOpt);
            var destinationRoot = parseResult.GetValue(destinationRootOpt);

            await RunAsync(
                configPath,
                envPath,
                force,
                oneDriveUserId,
                sharePointSiteId,
                sharePointDriveId,
                resolveGraphIds,
                sharePointSiteUrl,
                sharePointDriveName,
                oneDriveSourceFolder,
                destinationRoot,
                ct).ConfigureAwait(false);
        });

        return cmd;
    }

    internal static Task RunAsync(
        string configPath,
        string envPath,
        bool force,
        CancellationToken ct)
        => RunAsync(
            configPath,
            envPath,
            force,
            oneDriveUserId: null,
            sharePointSiteId: null,
            sharePointDriveId: null,
            resolveGraphIds: false,
            sharePointSiteUrl: null,
            sharePointDriveName: "Documents",
            oneDriveSourceFolder: null,
            destinationRoot: null,
            ct: ct);

    internal static async Task RunAsync(
        string configPath,
        string envPath,
        bool force,
        string? oneDriveUserId,
        string? sharePointSiteId,
        string? sharePointDriveId,
        bool resolveGraphIds,
        string? sharePointSiteUrl,
        string sharePointDriveName,
        string? oneDriveSourceFolder,
        string? destinationRoot,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var configTemplate = await LoadTemplateAsync(
            relativePath: Path.Combine("configs", "config.json"),
            fallbackContent: BuildDefaultConfigTemplate(),
            ct: ct).ConfigureAwait(false);
        var envTemplate = await LoadTemplateAsync(
            relativePath: "sample.env",
            fallbackContent: DefaultEnvTemplate,
            ct: ct).ConfigureAwait(false);

        string? graphClientId = null;
        string? graphTenantId = null;
        if (resolveGraphIds)
        {
            var resolved = await ResolveGraphIdentifiersAsync(
                configPath,
                oneDriveUserId,
                sharePointSiteUrl,
                sharePointDriveName,
                ct).ConfigureAwait(false);

            oneDriveUserId = resolved.OneDriveUserId;
            sharePointSiteId = resolved.SharePointSiteId;
            sharePointDriveId = resolved.SharePointDriveId;
            graphClientId = resolved.GraphClientId;
            graphTenantId = resolved.GraphTenantId;

            Console.WriteLine(
                $"[OK]   graph.resolve: siteId={sharePointSiteId}, driveId={sharePointDriveId} (name={resolved.SharePointDriveName})");
        }

        configTemplate = ApplyGraphValuesToConfigTemplate(
            configTemplate,
            oneDriveUserId,
            sharePointSiteId,
            sharePointDriveId,
            oneDriveSourceFolder,
            destinationRoot);

        envTemplate = ApplyGraphValuesToEnvTemplate(
            envTemplate,
            oneDriveUserId,
            sharePointSiteId,
            sharePointDriveId,
            graphClientId,
            graphTenantId);

        var results = new List<InitFileResult>(capacity: 2);
        await WriteTemplateAsync(configPath, configTemplate, force, results, ct).ConfigureAwait(false);
        await WriteTemplateAsync(envPath, envTemplate, force, results, ct).ConfigureAwait(false);

        foreach (var result in results)
        {
            switch (result.Status)
            {
                case InitFileStatus.Written:
                    Console.WriteLine($"[OK]   {result.Path}: 生成しました");
                    break;
                case InitFileStatus.Skipped:
                    Console.WriteLine($"[SKIP] {result.Path}: 既存のため未変更（--force で上書き可）");
                    break;
                case InitFileStatus.Error:
                    Console.WriteLine($"[ERR]  {result.Path}: {result.Message}");
                    break;
            }
        }

        if (results.Any(r => r.Status == InitFileStatus.Error))
            Environment.ExitCode = 1;
    }

    internal static async Task WriteTemplateAsync(
        string outputPath,
        string content,
        bool force,
        IList<InitFileResult> results,
        CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(outputPath);
        if (File.Exists(fullPath) && !force)
        {
            results.Add(new InitFileResult(InitFileStatus.Skipped, fullPath, "既存ファイルを維持"));
            return;
        }

        var directoryPath = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
            Directory.CreateDirectory(directoryPath);

        try
        {
            await File.WriteAllTextAsync(fullPath, content, ct).ConfigureAwait(false);
            results.Add(new InitFileResult(InitFileStatus.Written, fullPath, "生成完了"));
        }
        catch (UnauthorizedAccessException ex)
        {
            results.Add(new InitFileResult(InitFileStatus.Error, fullPath, ex.Message));
        }
        catch (IOException ex)
        {
            results.Add(new InitFileResult(InitFileStatus.Error, fullPath, ex.Message));
        }
    }

    internal static string ApplyGraphValuesToConfigTemplate(
        string configTemplate,
        string? oneDriveUserId,
        string? sharePointSiteId,
        string? sharePointDriveId,
        string? oneDriveSourceFolder = null,
        string? destinationRoot = null)
    {
        // oneDriveUserId/sharePointSiteId/sharePointDriveId: null または空文字 = 変更しない
        // oneDriveSourceFolder / destinationRoot: null = 変更しない, "" = 明示クリア, "値" = その値に更新（Trim 適用）
        if (string.IsNullOrWhiteSpace(oneDriveUserId) &&
            string.IsNullOrWhiteSpace(sharePointSiteId) &&
            string.IsNullOrWhiteSpace(sharePointDriveId) &&
            oneDriveSourceFolder is null &&
            destinationRoot is null)
        {
            return configTemplate;
        }

        var root = JsonSerializer.Deserialize<MigratorConfigRoot>(
            configTemplate,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("config テンプレートの解析に失敗しました。");

        if (!string.IsNullOrWhiteSpace(oneDriveUserId))
            root.Migrator.Graph.OneDriveUserId = oneDriveUserId;
        if (!string.IsNullOrWhiteSpace(sharePointSiteId))
            root.Migrator.Graph.SharePointSiteId = sharePointSiteId;
        if (!string.IsNullOrWhiteSpace(sharePointDriveId))
            root.Migrator.Graph.SharePointDriveId = sharePointDriveId;
        if (oneDriveSourceFolder is not null)
            root.Migrator.Graph.OneDriveSourceFolder = oneDriveSourceFolder.Trim();
        if (destinationRoot is not null)
            root.Migrator.DestinationRoot = destinationRoot.Trim();

        return JsonSerializer.Serialize(
            root,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
    }

    internal static string ApplyGraphValuesToEnvTemplate(
        string envTemplate,
        string? oneDriveUserId,
        string? sharePointSiteId,
        string? sharePointDriveId,
        string? graphClientId,
        string? graphTenantId)
    {
        var updated = envTemplate;

        if (!string.IsNullOrWhiteSpace(graphClientId))
            updated = UpsertEnvVariable(updated, "MIGRATOR__GRAPH__CLIENTID", graphClientId);
        if (!string.IsNullOrWhiteSpace(graphTenantId))
            updated = UpsertEnvVariable(updated, "MIGRATOR__GRAPH__TENANTID", graphTenantId);
        if (!string.IsNullOrWhiteSpace(oneDriveUserId))
            updated = UpsertEnvVariable(updated, "MIGRATOR__GRAPH__ONEDRIVEUSERID", oneDriveUserId);
        if (!string.IsNullOrWhiteSpace(sharePointSiteId))
            updated = UpsertEnvVariable(updated, "MIGRATOR__GRAPH__SHAREPOINTSITEID", sharePointSiteId);
        if (!string.IsNullOrWhiteSpace(sharePointDriveId))
            updated = UpsertEnvVariable(updated, "MIGRATOR__GRAPH__SHAREPOINTDRIVEID", sharePointDriveId);

        return updated;
    }

    internal static string UpsertEnvVariable(string envTemplate, string key, string value)
    {
        var normalized = envTemplate.ReplaceLineEndings("\n");
        var pattern = $"^{Regex.Escape(key)}=.*$";
        if (Regex.IsMatch(normalized, pattern, RegexOptions.Multiline))
        {
            var replaced = Regex.Replace(
                normalized,
                pattern,
                $"{key}={value}",
                RegexOptions.Multiline);
            return replaced.Replace("\n", Environment.NewLine);
        }

        var appended = normalized.TrimEnd('\n') + $"\n{key}={value}\n";
        return appended.Replace("\n", Environment.NewLine);
    }

    internal static SharePointSiteAddress ParseSharePointSiteUrl(string siteUrl)
    {
        if (!Uri.TryCreate(siteUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("--sharepoint-site-url は絶対URLで指定してください。");
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("--sharepoint-site-url は https:// で指定してください。");

        var path = uri.AbsolutePath.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(path) || path == "/")
            throw new InvalidOperationException("--sharepoint-site-url はサイトパスを含めて指定してください。");

        return new SharePointSiteAddress(uri.Host, path);
    }

    internal static string BuildSiteLookupUrl(string hostName, string sitePath)
    {
        var trimmedPath = sitePath.Trim('/');
        if (string.IsNullOrWhiteSpace(trimmedPath))
            throw new InvalidOperationException("サイトパスが空です。");

        var encodedPath = string.Join('/', trimmedPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));

        return $"https://graph.microsoft.com/v1.0/sites/{Uri.EscapeDataString(hostName)}:/{encodedPath}?$select=id";
    }

    internal static string FindDriveIdByName(string drivesJson, string driveName)
    {
        using var doc = JsonDocument.Parse(drivesJson);
        if (!doc.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Graph drives 応答の形式が不正です。");

        var availableNames = new List<string>();
        foreach (var driveElement in values.EnumerateArray())
        {
            var name = driveElement.TryGetProperty("name", out var nameProperty)
                ? nameProperty.GetString()
                : null;
            var id = driveElement.TryGetProperty("id", out var idProperty)
                ? idProperty.GetString()
                : null;

            if (!string.IsNullOrWhiteSpace(name))
                availableNames.Add(name);

            if (!string.IsNullOrWhiteSpace(name) &&
                !string.IsNullOrWhiteSpace(id) &&
                string.Equals(name, driveName, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        var available = availableNames.Count == 0
            ? "(なし)"
            : string.Join(", ", availableNames);
        throw new InvalidOperationException(
            $"ドキュメントライブラリ '{driveName}' が見つかりません。利用可能: {available}");
    }

    private static async Task<ResolvedGraphIdentifiers> ResolveGraphIdentifiersAsync(
        string configPath,
        string? oneDriveUserId,
        string? sharePointSiteUrl,
        string sharePointDriveName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(oneDriveUserId))
            throw new InvalidOperationException("--resolve-graph-ids 使用時は --onedrive-user-id が必須です。");
        if (string.IsNullOrWhiteSpace(sharePointSiteUrl))
            throw new InvalidOperationException("--resolve-graph-ids 使用時は --sharepoint-site-url が必須です。");

        var config = AppConfiguration.Build(configPath);
        var options = config.GetSection(MigratorOptions.SectionName).Get<MigratorOptions>()
            ?? new MigratorOptions();
        var clientSecret = AppConfiguration.GetGraphClientSecret();

        if (string.IsNullOrWhiteSpace(options.Graph.ClientId))
            throw new InvalidOperationException("MIGRATOR__GRAPH__CLIENTID が未設定です。");
        if (string.IsNullOrWhiteSpace(options.Graph.TenantId))
            throw new InvalidOperationException("MIGRATOR__GRAPH__TENANTID が未設定です。");
        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("MIGRATOR__GRAPH__CLIENTSECRET が未設定です。");

        var authenticator = new GraphAuthenticator(
            options.Graph.ClientId,
            options.Graph.TenantId,
            clientSecret);
        var token = await authenticator
            .GetAuthorizationTokenAsync(new Uri("https://graph.microsoft.com/v1.0/"), cancellationToken: ct)
            .ConfigureAwait(false);

        using var httpClient = new HttpClient
        {
            // VerifyCommand と同様に、Graph 呼び出しに対する明示的なタイムアウトを設定する。
            Timeout = TimeSpan.FromSeconds(30),
        };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var oneDriveUserBody = await GetGraphJsonAsync(
                httpClient,
                $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(oneDriveUserId)}?$select=id,userPrincipalName",
                "onedrive.user",
                ct).ConfigureAwait(false);
            var effectiveOneDriveUser = TryReadProperty(oneDriveUserBody, "userPrincipalName") ?? oneDriveUserId;

            var address = ParseSharePointSiteUrl(sharePointSiteUrl);
            var siteBody = await GetGraphJsonAsync(
                httpClient,
                BuildSiteLookupUrl(address.HostName, address.SitePath),
                "sharepoint.site",
                ct).ConfigureAwait(false);
            var siteId = TryReadProperty(siteBody, "id")
                ?? throw new InvalidOperationException("SharePoint サイトIDの抽出に失敗しました。");

            var drivesBody = await GetGraphJsonAsync(
                httpClient,
                $"https://graph.microsoft.com/v1.0/sites/{Uri.EscapeDataString(siteId)}/drives?$select=id,name,webUrl",
                "sharepoint.drives",
                ct).ConfigureAwait(false);
            var driveId = FindDriveIdByName(drivesBody, sharePointDriveName);

            return new ResolvedGraphIdentifiers(
                effectiveOneDriveUser,
                siteId,
                driveId,
                sharePointDriveName,
                options.Graph.ClientId,
                options.Graph.TenantId);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient.Timeout によるタイムアウトなど、ユーザー起因ではないキャンセルを分かりやすいエラーに変換する。
            throw new InvalidOperationException("Microsoft Graph への接続がタイムアウトしました。ネットワーク状態や Graph の応答状況を確認してください。");
        }
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

        throw new InvalidOperationException(
            $"{probeName} の取得に失敗しました: HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {TrimForLog(body, 180)}");
    }

    private static string? TryReadProperty(string json, string propertyName)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(propertyName, out var property)
            ? property.GetString()
            : null;
    }

    private static string TrimForLog(string message, int maxLength)
    {
        var normalized = message.ReplaceLineEndings(" ").Trim();
        if (normalized.Length <= maxLength)
            return normalized;

        return normalized[..maxLength] + "...";
    }

    internal static async Task<string> LoadTemplateAsync(
        string relativePath,
        string fallbackContent,
        CancellationToken ct)
    {
        var templatePath = ResolveTemplatePath(relativePath);
        if (templatePath is null)
            return fallbackContent;

        return await File.ReadAllTextAsync(templatePath, ct).ConfigureAwait(false);
    }

    internal static string? ResolveTemplatePath(string relativePath)
    {
        var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);

        var cwdCandidate = Path.GetFullPath(normalizedRelativePath, Environment.CurrentDirectory);
        if (File.Exists(cwdCandidate))
            return cwdCandidate;

        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, normalizedRelativePath);
            if (File.Exists(candidate))
                return candidate;

            var parent = Directory.GetParent(dir);
            if (parent is null)
                break;

            dir = parent.FullName;
        }

        return null;
    }

    internal static string BuildDefaultConfigTemplate()
    {
        var model = new { Migrator = new MigratorOptions() };
        return JsonSerializer.Serialize(
            model,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
    }

    internal const string DefaultEnvTemplate =
        """
        # Microsoft Graph API 認証情報（必須）
        MIGRATOR__GRAPH__CLIENTID=your-client-id-here
        MIGRATOR__GRAPH__CLIENTSECRET=your-client-secret-here
        MIGRATOR__GRAPH__TENANTID=your-tenant-id-here

        # OneDrive / SharePoint 対象リソース
        MIGRATOR__GRAPH__ONEDRIVEUSERID=user@example.com
        # 転送元 OneDrive のルートフォルダパス（省略時はドライブ全体。例: Documents/Projects）
        MIGRATOR__GRAPH__ONEDRIVESOURCEFOLDER=
        MIGRATOR__GRAPH__SHAREPOINTSITEID=your-site-id-here
        MIGRATOR__GRAPH__SHAREPOINTDRIVEID=your-drive-id-here

        # Dropbox（任意）
        MIGRATOR__DROPBOX__ACCESSTOKEN=your-dropbox-access-token-here
        """;
}

internal enum InitFileStatus
{
    Written,
    Skipped,
    Error,
}

internal sealed record InitFileResult(
    InitFileStatus Status,
    string Path,
    string Message);

internal sealed class MigratorConfigRoot
{
    public MigratorOptions Migrator { get; init; } = new();
}

internal sealed record SharePointSiteAddress(
    string HostName,
    string SitePath);

internal sealed record ResolvedGraphIdentifiers(
    string OneDriveUserId,
    string SharePointSiteId,
    string SharePointDriveId,
    string SharePointDriveName,
    string GraphClientId,
    string GraphTenantId);
