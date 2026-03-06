using System.CommandLine;
using System.Net.Http.Headers;
using System.Text.Json;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Providers.Graph.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Identity.Client;

namespace CloudMigrator.Setup.Cli.Commands;

/// <summary>
/// setup verify コマンド。
/// Graph トークン取得と主要リソースIDの疎通を検証する。
/// </summary>
internal static class VerifyCommand
{
    public static Command Build()
    {
        var cmd = new Command("verify", "Graph 認証と OneDrive/SharePoint 識別子の疎通を検証します");
        var configPathOpt = new Option<string?>("--config-path")
        {
            Description = "設定ファイルパス（省略時は自動探索）",
        };
        var timeoutSecOpt = new Option<int>("--timeout-sec")
        {
            Description = "Graph API 検証時の HTTP タイムアウト秒",
            DefaultValueFactory = _ => 30,
        };
        var skipOnedriveOpt = new Option<bool>("--skip-onedrive")
        {
            Description = "OneDrive の疎通確認をスキップします",
        };
        var skipSharepointOpt = new Option<bool>("--skip-sharepoint")
        {
            Description = "SharePoint の疎通確認をスキップします",
        };

        cmd.Add(configPathOpt);
        cmd.Add(timeoutSecOpt);
        cmd.Add(skipOnedriveOpt);
        cmd.Add(skipSharepointOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var configPath = parseResult.GetValue(configPathOpt);
            var timeoutSec = parseResult.GetValue(timeoutSecOpt);
            var skipOnedrive = parseResult.GetValue(skipOnedriveOpt);
            var skipSharepoint = parseResult.GetValue(skipSharepointOpt);
            await RunAsync(configPath, timeoutSec, skipOnedrive, skipSharepoint, ct).ConfigureAwait(false);
        });

        return cmd;
    }

    internal static async Task RunAsync(
        string? configPath,
        int timeoutSec,
        bool skipOnedrive,
        bool skipSharepoint,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var config = AppConfiguration.Build(configPath);
        var options = config.GetSection(MigratorOptions.SectionName).Get<MigratorOptions>()
            ?? new MigratorOptions();
        var clientSecret = AppConfiguration.GetGraphClientSecret();

        var errors = BuildPreflightErrors(options, clientSecret, skipOnedrive, skipSharepoint);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
                Console.WriteLine($"[ERR]  preflight: {error}");

            Environment.ExitCode = 1;
            return;
        }

        string token;
        var authenticator = new GraphAuthenticator(
            options.Graph.ClientId,
            options.Graph.TenantId,
            clientSecret);

        try
        {
            token = await authenticator
                .GetAuthorizationTokenAsync(new Uri("https://graph.microsoft.com/v1.0/"), cancellationToken: ct)
                .ConfigureAwait(false);
            Console.WriteLine("[OK]   graph.token: 取得成功");
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"[ERR]  graph.token: {ex.Message}");
            Environment.ExitCode = 1;
            return;
        }
        catch (MsalException ex)
        {
            Console.WriteLine($"[ERR]  graph.token: {ex.Message}");
            Environment.ExitCode = 1;
            return;
        }

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, timeoutSec)),
        };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var probes = new List<VerifyProbeResult>
        {
            await ProbeAsync(
                httpClient,
                "graph.organization",
                "https://graph.microsoft.com/v1.0/organization?$top=1",
                ct).ConfigureAwait(false),
        };

        if (!skipOnedrive)
        {
            probes.Add(await ProbeAsync(
                httpClient,
                "graph.onedrive",
                $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(options.Graph.OneDriveUserId)}/drive?$select=id",
                ct).ConfigureAwait(false));
        }

        if (!skipSharepoint)
        {
            probes.Add(await ProbeAsync(
                httpClient,
                "graph.sharepointSite",
                $"https://graph.microsoft.com/v1.0/sites/{Uri.EscapeDataString(options.Graph.SharePointSiteId)}?$select=id",
                ct).ConfigureAwait(false));
            probes.Add(await ProbeAsync(
                httpClient,
                "graph.sharepointDrive",
                $"https://graph.microsoft.com/v1.0/drives/{Uri.EscapeDataString(options.Graph.SharePointDriveId)}?$select=id",
                ct).ConfigureAwait(false));
        }

        foreach (var probe in probes)
        {
            if (probe.Success)
                Console.WriteLine($"[OK]   {probe.Name}: {probe.Message}");
            else
                Console.WriteLine($"[ERR]  {probe.Name}: {probe.Message}");
        }

        if (probes.Any(p => !p.Success))
            Environment.ExitCode = 1;
    }

    internal static IReadOnlyList<string> BuildPreflightErrors(
        MigratorOptions options,
        string clientSecret,
        bool skipOnedrive,
        bool skipSharepoint)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Graph.ClientId))
            errors.Add("MIGRATOR__GRAPH__CLIENTID が未設定です。");
        if (string.IsNullOrWhiteSpace(options.Graph.TenantId))
            errors.Add("MIGRATOR__GRAPH__TENANTID が未設定です。");
        if (string.IsNullOrWhiteSpace(clientSecret))
            errors.Add("MIGRATOR__GRAPH__CLIENTSECRET が未設定です。");

        if (!skipOnedrive && string.IsNullOrWhiteSpace(options.Graph.OneDriveUserId))
            errors.Add("MIGRATOR__GRAPH__ONEDRIVEUSERID が未設定です。");

        if (!skipSharepoint)
        {
            if (string.IsNullOrWhiteSpace(options.Graph.SharePointSiteId))
                errors.Add("MIGRATOR__GRAPH__SHAREPOINTSITEID が未設定です。");
            if (string.IsNullOrWhiteSpace(options.Graph.SharePointDriveId))
                errors.Add("MIGRATOR__GRAPH__SHAREPOINTDRIVEID が未設定です。");
        }

        return errors;
    }

    private static async Task<VerifyProbeResult> ProbeAsync(
        HttpClient httpClient,
        string name,
        string url,
        CancellationToken ct)
    {
        try
        {
            using var response = await httpClient.GetAsync(url, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var snippet = TrimForLog(body, maxLength: 180);
                return VerifyProbeResult.Fail(
                    name,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {snippet}");
            }

            var id = TryReadId(body);
            return string.IsNullOrWhiteSpace(id)
                ? VerifyProbeResult.Ok(name, "疎通成功")
                : VerifyProbeResult.Ok(name, $"疎通成功 (id={id})");
        }
        catch (HttpRequestException ex)
        {
            return VerifyProbeResult.Fail(name, ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return VerifyProbeResult.Fail(name, "タイムアウトが発生しました。");
        }
    }

    internal static string? TryReadId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("id", out var idProperty))
                return idProperty.GetString();
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string TrimForLog(string message, int maxLength)
    {
        var normalized = message.ReplaceLineEndings(" ").Trim();
        if (normalized.Length <= maxLength)
            return normalized;

        return normalized[..maxLength] + "...";
    }
}

internal sealed record VerifyProbeResult(
    string Name,
    bool Success,
    string Message)
{
    public static VerifyProbeResult Ok(string name, string message)
        => new(name, true, message);

    public static VerifyProbeResult Fail(string name, string message)
        => new(name, false, message);
}
