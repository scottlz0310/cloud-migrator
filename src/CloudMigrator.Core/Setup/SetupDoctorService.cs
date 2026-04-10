using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Core.Setup;

/// <summary>
/// セットアップ診断サービスのオプション。
/// </summary>
/// <param name="ClientId">Azure AD アプリケーション ID。</param>
/// <param name="TenantId">Azure AD テナント ID。</param>
/// <param name="ClientSecret">クライアントシークレット（環境変数から取得）。</param>
/// <param name="SiteId">SharePoint サイト ID。</param>
/// <param name="DriveId">SharePoint ドキュメントライブラリ（ドライブ）ID。</param>
/// <param name="DestinationRoot">転送先ルートパス（空文字の場合はドライブルートを確認）。</param>
public sealed record DoctorOptions(
    string ClientId,
    string TenantId,
    string ClientSecret,
    string SiteId,
    string DriveId,
    string DestinationRoot);

/// <summary>接続テストサービスの契約。</summary>
public interface ISetupDoctorService
{
    /// <summary>Graph 認証・SharePoint サイト・ドキュメントライブラリの疎通確認を実行する。</summary>
    Task<DoctorResult> RunAsync(CancellationToken ct = default);
}

/// <summary>
/// Graph API への HTTP 呼び出しで接続疎通を確認する <see cref="ISetupDoctorService"/> 実装。
/// 各チェックのタイムアウトは <see cref="CheckTimeoutSeconds"/> 秒。
/// </summary>
public sealed class SetupDoctorService : ISetupDoctorService
{
    private const int CheckTimeoutSeconds = 10;

    private readonly DoctorOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SetupDoctorService>? _logger;

    /// <param name="options">診断に必要な資格情報・設定値。</param>
    /// <param name="httpClientFactory">HttpClient ファクトリー（RunAsync 毎にクライアントを生成しハンドラープールを再利用）。</param>
    /// <param name="logger">ロガー（省略可。null の場合はサーバーログへの出力が行われない）。</param>
    public SetupDoctorService(DoctorOptions options, IHttpClientFactory httpClientFactory, ILogger<SetupDoctorService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _options = options;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<DoctorResult> RunAsync(CancellationToken ct = default)
    {
        using var http = _httpClientFactory.CreateClient("SetupDoctor");

        var (token, authCheck) = await CheckAuthAsync(http, ct).ConfigureAwait(false);

        if (authCheck.Status == DoctorStatus.Fail)
        {
            // 認証失敗時は後続チェックをスキップ
            return BuildResult(
            [
                authCheck,
                new DoctorCheck("SharePoint サイト", DoctorStatus.Fail, "認証失敗のためスキップ"),
                new DoctorCheck("ドキュメントライブラリ", DoctorStatus.Fail, "認証失敗のためスキップ"),
            ]);
        }

        var siteCheck = await CheckSiteAsync(http, token!, ct).ConfigureAwait(false);
        var driveCheck = await CheckDriveAsync(http, token!, ct).ConfigureAwait(false);

        return BuildResult([authCheck, siteCheck, driveCheck]);
    }

    // ── 各チェック ────────────────────────────────────────────────────────

    /// <summary>クライアント資格情報フローでアクセストークンを取得する。</summary>
    private async Task<(string? Token, DoctorCheck Check)> CheckAuthAsync(HttpClient http, CancellationToken ct)
    {
        const string CheckName = "Graph 認証";

        if (string.IsNullOrWhiteSpace(_options.ClientId) ||
            string.IsNullOrWhiteSpace(_options.TenantId) ||
            string.IsNullOrWhiteSpace(_options.ClientSecret))
        {
            return (null, new DoctorCheck(CheckName, DoctorStatus.Fail,
                "ClientId / TenantId / ClientSecret が設定されていません。MIGRATOR__GRAPH__* 環境変数を確認してください。"));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(CheckTimeoutSeconds));

        try
        {
            using var form = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("client_id", _options.ClientId),
                new KeyValuePair<string, string>("client_secret", _options.ClientSecret),
                new KeyValuePair<string, string>("scope", "https://graph.microsoft.com/.default"),
            ]);

            using var res = await http.PostAsync(
                $"https://login.microsoftonline.com/{Uri.EscapeDataString(_options.TenantId)}/oauth2/v2.0/token",
                form, cts.Token).ConfigureAwait(false);

            if (!res.IsSuccessStatusCode)
            {
                return (null, new DoctorCheck(CheckName, DoctorStatus.Fail,
                    $"トークン取得失敗 (HTTP {(int)res.StatusCode})"));
            }

            using var stream = await res.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);

            var token = doc.RootElement.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(token))
            {
                return (null, new DoctorCheck(CheckName, DoctorStatus.Fail, "access_token が取得できませんでした。"));
            }

            return (token, new DoctorCheck(CheckName, DoctorStatus.Pass, null));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, new DoctorCheck(CheckName, DoctorStatus.Fail, $"タイムアウト ({CheckTimeoutSeconds}秒)"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Graph 認証中に例外が発生しました。ClientId: {ClientId}, TenantId: {TenantId}",
                _options.ClientId, _options.TenantId);
            return (null, new DoctorCheck(CheckName, DoctorStatus.Fail, "Graph 認証中に予期しないエラーが発生しました。"));
        }
    }

    /// <summary>GET /sites/{siteId} で SharePoint サイトの存在・権限を確認する。</summary>
    private async Task<DoctorCheck> CheckSiteAsync(HttpClient http, string token, CancellationToken ct)
    {
        const string CheckName = "SharePoint サイト";

        if (string.IsNullOrWhiteSpace(_options.SiteId))
            return new DoctorCheck(CheckName, DoctorStatus.Fail, "SharePointSiteId が設定されていません。");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(CheckTimeoutSeconds));

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://graph.microsoft.com/v1.0/sites/{Uri.EscapeDataString(_options.SiteId)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var res = await http.SendAsync(req, cts.Token).ConfigureAwait(false);

            if (!res.IsSuccessStatusCode)
            {
                return new DoctorCheck(CheckName, DoctorStatus.Fail,
                    $"サイトへのアクセス失敗 (HTTP {(int)res.StatusCode})");
            }

            using var stream = await res.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);

            var siteId = doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            return new DoctorCheck(CheckName, DoctorStatus.Pass, siteId);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DoctorCheck(CheckName, DoctorStatus.Fail, $"タイムアウト ({CheckTimeoutSeconds}秒)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "SharePoint サイト確認中に例外が発生しました。SiteId: {SiteId}", _options.SiteId);
            return new DoctorCheck(CheckName, DoctorStatus.Fail, "サイト確認中に予期しないエラーが発生しました。");
        }
    }

    /// <summary>
    /// ドライブ（ドキュメントライブラリ）の存在確認。
    /// DestinationRoot が設定されている場合はそのパスの存在も確認する。
    /// </summary>
    private async Task<DoctorCheck> CheckDriveAsync(HttpClient http, string token, CancellationToken ct)
    {
        const string CheckName = "ドキュメントライブラリ";

        if (string.IsNullOrWhiteSpace(_options.DriveId))
            return new DoctorCheck(CheckName, DoctorStatus.Fail, "SharePointDriveId が設定されていません。");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(CheckTimeoutSeconds));

        try
        {
            // 前後空白・バックスラッシュ・余分なスラッシュを除去してパス表記を正規化する
            var root = string.Join('/', _options.DestinationRoot
                .Trim()
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries));
            var url = string.IsNullOrWhiteSpace(root)
                ? $"https://graph.microsoft.com/v1.0/drives/{Uri.EscapeDataString(_options.DriveId)}"
                : $"https://graph.microsoft.com/v1.0/drives/{Uri.EscapeDataString(_options.DriveId)}/root:/{BuildEncodedPath(root)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var res = await http.SendAsync(req, cts.Token).ConfigureAwait(false);

            if (res.StatusCode == System.Net.HttpStatusCode.NotFound && !string.IsNullOrWhiteSpace(root))
            {
                return new DoctorCheck(CheckName, DoctorStatus.Fail,
                    $"destinationRoot が見つかりません: /{root}");
            }

            if (!res.IsSuccessStatusCode)
            {
                return new DoctorCheck(CheckName, DoctorStatus.Fail,
                    $"ライブラリへのアクセス失敗 (HTTP {(int)res.StatusCode})");
            }

            return new DoctorCheck(CheckName, DoctorStatus.Pass, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DoctorCheck(CheckName, DoctorStatus.Fail, $"タイムアウト ({CheckTimeoutSeconds}秒)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "ドキュメントライブラリ確認中に例外が発生しました。DriveId: {DriveId}, DestinationRoot: {DestinationRoot}",
                _options.DriveId, _options.DestinationRoot);
            return new DoctorCheck(CheckName, DoctorStatus.Fail, "ライブラリ確認中に予期しないエラーが発生しました。");
        }
    }

    // ── ヘルパー ─────────────────────────────────────────────────────────

    /// <summary>パスの各セグメントを個別にパーセントエンコードし / で結合する。</summary>
    private static string BuildEncodedPath(string path) =>
        string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

    private static DoctorResult BuildResult(IReadOnlyList<DoctorCheck> checks)
    {
        var hasAnyFail = checks.Any(c => c.Status == DoctorStatus.Fail);
        var hasAnyWarn = checks.Any(c => c.Status == DoctorStatus.Warning);

        var overall = hasAnyFail ? OverallStatus.Unhealthy
            : hasAnyWarn ? OverallStatus.Degraded
            : OverallStatus.Healthy;

        return new DoctorResult(overall, checks);
    }
}
