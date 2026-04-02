using System.Text.Json;
using CloudMigrator.Core.Migration;
using CloudMigrator.Core.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Dashboard;

/// <summary>
/// 移行進捗 Web ダッシュボードの Minimal API ホスト。
/// SQLite 状態 DB を参照し、Chart.js UI へ JSON を提供する。
/// </summary>
public static class DashboardServer
{
    /// <summary>
    /// ダッシュボードサーバーを起動する。
    /// <paramref name="ct"/> がキャンセルされると Graceful Shutdown する。
    /// </summary>
    public static async Task RunAsync(string dbPath, int port, CancellationToken ct)
    {
        // SqliteTransferStateDb は DI に渡すが、インスタンスを外部から渡す場合は
        // DI コンテナが DisposeAsync を呼ばないため finally で明示的に解放する。
        var db = new SqliteTransferStateDb(dbPath);
        await db.InitializeAsync(ct).ConfigureAwait(false);
        var configService = new ConfigurationService();
        var jobService = new TransferJobService();
        var app = BuildApp(db, configService: configService, jobService: jobService);
        app.Urls.Add($"http://localhost:{port}");
        try
        {
            await app.RunAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await db.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 全エンドポイントをマップした <see cref="WebApplication"/> を生成する。
    /// <paramref name="configureWebHost"/> に <c>wb =&gt; wb.UseTestServer()</c> を渡すと
    /// インプロセスの TestServer として使用できる（単体テスト向け）。
    /// </summary>
    internal static WebApplication BuildApp(
        ITransferStateDb db,
        Action<IWebHostBuilder>? configureWebHost = null,
        IConfigurationService? configService = null,
        ITransferJobService? jobService = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning); // ダッシュボード固有のノイズを抑制
        builder.Services.AddSingleton(db);
        // configService 未指定時は既定実装を生成し登録（/api/config の定常稼備を保証）
        var resolvedConfigService = configService ?? new ConfigurationService();
        builder.Services.AddSingleton<IConfigurationService>(resolvedConfigService);
        // jobService 未指定時は既定実装を生成し登録（/api/transfer/* の定常稼備を保証）
        var resolvedJobService = jobService ?? new TransferJobService();
        builder.Services.AddSingleton<ITransferJobService>(resolvedJobService);
        configureWebHost?.Invoke(builder.WebHost);

        var app = builder.Build();

        // ── API エンドポイント ────────────────────────────────────────────────

        // GET /api/status  → ステータス別件数・完了率・バイト数
        app.MapGet("/api/status", async (
            ITransferStateDb stateDb,
            CancellationToken cancellationToken) =>
        {
            var summary = await stateDb.GetSummaryAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(summary);
        });

        // GET /api/metrics?name=rate_limit_pct&minutes=60  → 時系列メトリクス
        // name 省略時は "rate_limit_pct"（Pipeline が 100 件ごとに記録する唯一の既定メトリクス）
        app.MapGet("/api/metrics", async (
            ITransferStateDb stateDb,
            string? name,
            int? minutes,
            CancellationToken cancellationToken) =>
        {
            var data = await stateDb.GetMetricsAsync(
                name ?? "rate_limit_pct",
                minutes ?? 60,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(data);
        });

        // GET /api/phase  → 現在のパイプラインフェーズ（SharePoint 3フェーズ対応）
        app.MapGet("/api/phase", async (
            ITransferStateDb stateDb,
            CancellationToken cancellationToken) =>
        {
            var crawlComplete = await stateDb.GetCheckpointAsync(SharePointMigrationPipeline.CrawlCompleteKey, cancellationToken).ConfigureAwait(false);
            var folderCreationComplete = await stateDb.GetCheckpointAsync(SharePointMigrationPipeline.FolderCreationCompleteKey, cancellationToken).ConfigureAwait(false);
            var folderTotalStr = await stateDb.GetCheckpointAsync(SharePointMigrationPipeline.FolderTotalKey, cancellationToken).ConfigureAwait(false);

            var folderDoneMetrics = await stateDb.GetMetricsAsync("sp_folder_done", 120, cancellationToken).ConfigureAwait(false);
            var folderDone = folderDoneMetrics.Count > 0 ? (int)folderDoneMetrics[^1].Value : 0;

            string phase;
            if (crawlComplete != "true")
                phase = "crawling";
            else if (folderCreationComplete != "true")
                phase = "folder_creation";
            else
                phase = "transferring";

            int? folderTotal = folderTotalStr is not null && int.TryParse(folderTotalStr, out var ft) ? ft : null;

            return Results.Ok(new { phase, folderTotal, folderDone });
        });

        // GET /api/errors  → 最近の失敗ファイル（最大5件）
        app.MapGet("/api/errors", async (
            ITransferStateDb stateDb,
            CancellationToken cancellationToken) =>
        {
            var summary = await stateDb.GetSummaryAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(summary.RecentFailed);
        });

        // GET /api/config  → 設定取得（シークレット除外）
        app.MapGet("/api/config", async (IConfigurationService svc, CancellationToken cancellationToken) =>
        {
            var config = await svc.GetConfigAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(config);
        });

        // PUT /api/config  → 設定マージ保存（シークレット拒否・バリデーション付き）
        app.MapPut("/api/config", async (HttpContext ctx, IConfigurationService svc) =>
        {
            string body;
            using (var reader = new StreamReader(ctx.Request.Body))
                body = await reader.ReadToEndAsync().ConfigureAwait(false);

            if (ContainsSecretKey(body))
                return Results.BadRequest("シークレットキーを含むフィールドは設定できません。");

            ConfigUpdateDto update;
            try
            {
                update = JsonSerializer.Deserialize<ConfigUpdateDto>(body, ApiJsonOptions)
                    ?? new ConfigUpdateDto();
            }
            catch (JsonException)
            {
                return Results.BadRequest("JSON の形式が正しくありません。");
            }

            if (update.MaxParallelTransfers is < 1 or > 100)
                return Results.BadRequest("maxParallelTransfers は 1〜100 の範囲で指定してください。");
            if (update.ChunkSizeMb is < 1 or > 100)
                return Results.BadRequest("chunkSizeMb は 1〜100 の範囲で指定してください。");
            if (update.RetryCount is < 0 or > 20)
                return Results.BadRequest("retryCount は 0〜20 の範囲で指定してください。");

            await svc.UpdateConfigAsync(update, ctx.RequestAborted).ConfigureAwait(false);
            return Results.Ok();
        });

        // POST /api/transfer/start  → 転送ジョブ開始（202 Accepted or 409 Conflict）
        app.MapPost("/api/transfer/start", async (ITransferJobService jobSvc, CancellationToken cancellationToken) =>
        {
            var job = await jobSvc.TryStartAsync(cancellationToken).ConfigureAwait(false);
            if (job is null)
                return Results.Conflict("転送ジョブがすでに実行中です。");
            return Results.Accepted(
                $"/api/transfer/{job.JobId}",
                new { jobId = job.JobId, status = job.Status.ToString() });
        });

        // GET /api/transfer/{id}  → ジョブ状態取得（200 OK or 404 NotFound）
        app.MapGet("/api/transfer/{id}", (string id, ITransferJobService jobSvc) =>
        {
            var job = jobSvc.GetJob(id);
            if (job is null)
                return Results.NotFound();
            return Results.Ok(new
            {
                jobId = job.JobId,
                status = job.Status.ToString(),
                startedAt = job.StartedAt,
                completedAt = job.CompletedAt,
                errorMessage = job.ErrorMessage,
            });
        });

        // GET /  → ダッシュボード HTML
        app.MapGet("/", () => Results.Content(IndexHtml, "text/html; charset=utf-8"));

        return app;
    }

    // ── API ヘルパー ──────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] SecretKeyPatterns = ["secret", "password", "token", "apikey", "api_key"];

    /// <summary>
    /// リクエスト JSON にシークレット系キーが含まれていれば true を返す。
    /// マッチは大文字小文字を区別しない部分一致。
    /// オブジェクト／配列のネストを再帰的に検査する。
    /// </summary>
    private static bool ContainsSecretKey(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ContainsSecretKeyInElement(doc.RootElement);
        }
        catch
        {
            // 不正 JSON は後続のデシリアライズで 400 として弾く
            return false;
        }
    }

    private static bool ContainsSecretKeyInElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (SecretKeyPatterns.Any(p => prop.Name.ToLowerInvariant().Contains(p)))
                        return true;
                    if (ContainsSecretKeyInElement(prop.Value))
                        return true;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsSecretKeyInElement(item))
                        return true;
                }
                break;
        }
        return false;
    }

    // ── インライン HTML（Chart.js CDN 使用） ──────────────────────────────────

    private static readonly string IndexHtml = """
        <!DOCTYPE html>
        <html lang="ja">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>CloudMigrator ダッシュボード</title>
          <script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.7/dist/chart.umd.min.js"></script>
          <script defer src="https://cdn.jsdelivr.net/npm/alpinejs@3.14.9/dist/cdn.min.js"></script>
          <style>
            *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
            body { font-family: 'Segoe UI', system-ui, sans-serif; background: #0f0f10; color: #e2e2e5; min-height: 100dvh; }
            header { background: #1a1a2e; padding: 14px 24px; display: flex; align-items: center; gap: 12px; border-bottom: 1px solid #2a2a4a; }
            header h1 { font-size: 1.1rem; font-weight: 600; letter-spacing: .02em; }
            .badge { font-size: .7rem; background: #4f46e5; padding: 2px 8px; border-radius: 99px; color: #fff; }
            .phase-badge { font-size: .7rem; padding: 2px 10px; border-radius: 99px; color: #fff; display: none; }
            .phase-badge.crawling { background: #f59e0b; }
            .phase-badge.folder_creation { background: #8b5cf6; }
            .phase-badge.transferring { background: #22c55e; }
            .refresh-indicator { margin-left: auto; font-size: .7rem; color: #888; }
            main { max-width: 1200px; margin: 0 auto; padding: 24px 16px; display: grid; gap: 20px; }
            .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 12px; }
            .card { background: #1a1a2e; border-radius: 12px; padding: 18px; border: 1px solid #2a2a4a; }
            .card .label { font-size: .7rem; text-transform: uppercase; letter-spacing: .08em; color: #888; margin-bottom: 6px; }
            .card .value { font-size: 2rem; font-weight: 700; line-height: 1; }
            .card.done .value { color: #22c55e; }
            .card.pending .value { color: #f59e0b; }
            .card.processing .value { color: #60a5fa; }
            .card.failed .value { color: #ef4444; }
            .progress-wrap { background: #1a1a2e; border-radius: 12px; padding: 18px; border: 1px solid #2a2a4a; }
            .progress-wrap h2 { font-size: .85rem; margin-bottom: 12px; color: #aaa; }
            .bar-bg { background: #2a2a4a; border-radius: 8px; height: 20px; overflow: hidden; }
            .bar-fg { height: 100%; background: linear-gradient(90deg, #4f46e5, #22c55e); border-radius: 8px; transition: width .6s ease; }
            .bar-label { text-align: right; font-size: .75rem; color: #aaa; margin-top: 6px; }
            .charts { display: grid; grid-template-columns: repeat(auto-fit, minmax(400px, 1fr)); gap: 20px; }
            .chart-box { background: #1a1a2e; border-radius: 12px; padding: 18px; border: 1px solid #2a2a4a; }
            .chart-box h2 { font-size: .85rem; color: #aaa; margin-bottom: 12px; }
            canvas { max-height: 260px; }
            .errors { background: #1a1a2e; border-radius: 12px; padding: 18px; border: 1px solid #2a2a4a; }
            .errors h2 { font-size: .85rem; color: #aaa; margin-bottom: 10px; }
            .error-item { font-size: .8rem; color: #ef4444; padding: 6px 0; border-bottom: 1px solid #2a2a4a; word-break: break-all; }
            .error-item:last-child { border-bottom: none; }
            .error-item .path { color: #aaa; }
            .no-errors { font-size: .8rem; color: #555; }
            .card.parallel .value { color: #fb923c; }
            .card.retries .value { color: #f59e0b; }
            .card.text-sm .value { font-size: 1.4rem; }
            /* 完了サマリー */
            .completion-summary { background: linear-gradient(135deg, #0a1f0e 0%, #1a1a2e 100%); border-radius: 12px; padding: 20px 24px; border: 2px solid #22c55e; }
            .completion-header { display: flex; align-items: center; gap: 10px; font-size: 1.15rem; font-weight: 700; color: #22c55e; margin-bottom: 16px; flex-wrap: wrap; }
            .completion-header .cs-time { margin-left: auto; font-size: .75rem; color: #888; font-weight: 400; }
            .cs-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(175px, 1fr)); gap: 12px; }
            .cs-item { background: rgba(0,0,0,.35); border-radius: 8px; padding: 12px 14px; border: 1px solid #2a2a4a; }
            .cs-item .cs-label { font-size: .68rem; text-transform: uppercase; letter-spacing: .08em; color: #888; margin-bottom: 6px; }
            .cs-item .cs-value { font-size: 1.4rem; font-weight: 700; color: #e2e2e5; line-height: 1.2; }
            .cs-item .cs-value.success { color: #22c55e; }
            .cs-item .cs-value.warn    { color: #f59e0b; }
            .cs-item .cs-value.danger  { color: #ef4444; }
            .phase-badge.completed { background: #0d9488; }
            /* タブナビゲーション */
            .tabs { display: flex; gap: 4px; }
            .tab-btn { background: transparent; border: 1px solid #2a2a4a; padding: 5px 14px; border-radius: 8px; color: #aaa; cursor: pointer; font-size: .78rem; transition: all .15s; }
            .tab-btn:hover { background: #2a2a4a; color: #e2e2e5; }
            .tab-btn.active { background: #4f46e5; border-color: #4f46e5; color: #fff; }
            /* 設定フォーム */
            .config-section { background: #1a1a2e; border-radius: 12px; padding: 24px; border: 1px solid #2a2a4a; }
            .config-group { margin-bottom: 24px; }
            .config-group-title { font-size: .78rem; text-transform: uppercase; letter-spacing: .08em; color: #888; margin-bottom: 14px; }
            .config-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 14px; }
            .field { display: flex; flex-direction: column; gap: 5px; }
            .field label { font-size: .72rem; color: #888; letter-spacing: .04em; }
            .field input, .field select { background: #0f0f10; border: 1px solid #3a3a5a; border-radius: 6px; color: #e2e2e5; padding: 7px 10px; font-size: .88rem; width: 100%; transition: border-color .15s; }
            .field input:focus, .field select:focus { outline: none; border-color: #4f46e5; }
            .danger-area { margin-top: 20px; border: 1px solid #7f1d1d; border-radius: 8px; overflow: hidden; }
            .danger-area summary { padding: 10px 16px; cursor: pointer; color: #ef4444; font-size: .82rem; font-weight: 600; background: #1a0505; user-select: none; }
            .danger-area .danger-content { padding: 16px; }
            .danger-area .danger-warning { background: #450a0a; border-radius: 6px; padding: 10px 14px; font-size: .78rem; color: #fca5a5; margin-bottom: 14px; border: 1px solid #7f1d1d; }
            .config-actions { margin-top: 22px; display: flex; align-items: center; gap: 14px; flex-wrap: wrap; }
            .btn-save { background: #4f46e5; border: none; color: #fff; padding: 8px 22px; border-radius: 8px; cursor: pointer; font-size: .88rem; transition: opacity .15s; }
            .btn-save:disabled { opacity: .4; cursor: not-allowed; }
            .dirty-indicator { font-size: .75rem; color: #f59e0b; }
            .toast { position: fixed; bottom: 24px; right: 24px; padding: 12px 20px; border-radius: 10px; font-size: .85rem; z-index: 9999; pointer-events: none; }
            .toast.success { background: #14532d; color: #86efac; border: 1px solid #16a34a; }
            .toast.error { background: #450a0a; color: #fca5a5; border: 1px solid #b91c1c; }
            /* 実行タブ */
            .run-section { background: #1a1a2e; border-radius: 12px; padding: 24px; border: 1px solid #2a2a4a; }
            .btn-start { background: #16a34a; border: none; color: #fff; padding: 10px 28px; border-radius: 8px; cursor: pointer; font-size: .95rem; font-weight: 600; transition: opacity .15s; }
            .btn-start:disabled { opacity: .4; cursor: not-allowed; }
            .btn-start:hover:not(:disabled) { opacity: .85; }
            .status-panel { margin-top: 20px; border-radius: 10px; padding: 16px 20px; }
            .pending-panel { background: #1c1f26; border: 1px solid #374151; color: #9ca3af; }
            .running-panel { background: #0c1a2e; border: 1px solid #1d4ed8; color: #93c5fd; }
            .completed-panel { background: #0a1f0e; border: 1px solid #16a34a; color: #86efac; font-weight: 600; }
            .failed-panel { background: #1a0505; border: 1px solid #b91c1c; color: #fca5a5; }
            .cancelled-panel { background: #1c1006; border: 1px solid #92400e; color: #fbbf24; }
            .stop-guide { margin-top: 16px; background: #161616; border-radius: 8px; padding: 14px 16px; border: 1px solid #374151; }
            .stop-guide p { font-size: .78rem; color: #f59e0b; margin-bottom: 10px; }
            .stop-guide code { display: block; background: #0d0d0d; padding: 8px 12px; border-radius: 6px; color: #86efac; font-size: .85rem; font-family: 'Cascadia Code', Consolas, monospace; letter-spacing: .03em; user-select: all; }
            .run-elapsed { font-size: .78rem; color: #60a5fa; margin-top: 6px; }
            .error-detail { font-size: .78rem; margin-top: 8px; color: #fca5a5; }
            @keyframes spin { to { transform: rotate(360deg); } }
            .spinner { display: inline-block; width: 14px; height: 14px; border: 2px solid #374151; border-top-color: #60a5fa; border-radius: 50%; animation: spin 1s linear infinite; vertical-align: middle; margin-right: 6px; }
            .run-progress { color: #e2e2e5; font-size: .9rem; }
          </style>
        </head>
        <body x-data="{ tab: 'monitor' }">
          <header>
            <h1>&#128640; CloudMigrator</h1>
            <span class="badge">Studio</span>
            <span class="phase-badge" id="phaseBadge" x-show="tab === 'monitor'"></span>
            <nav class="tabs">
              <button class="tab-btn" :class="tab === 'monitor' ? 'active' : ''" @click="tab='monitor'">&#128202; 監視</button>
              <button class="tab-btn" :class="tab === 'run' ? 'active' : ''" @click="tab='run'">&#9654;&#65039; 実行</button>
              <button class="tab-btn" :class="tab === 'config' ? 'active' : ''" @click="tab='config'">&#9881;&#65039; 設定</button>
            </nav>
            <span class="refresh-indicator" id="refreshTxt" x-show="tab === 'monitor'">次回更新まで 5s</span>
          </header>
          <!-- 監視タブ -->
          <div x-show="tab === 'monitor'">
          <main>
            <section class="cards">
              <div class="card done"><div class="label">完了</div><div class="value" id="c-done">—</div></div>
              <div class="card pending"><div class="label">待機中</div><div class="value" id="c-pending">—</div></div>
              <div class="card processing"><div class="label">処理中</div><div class="value" id="c-processing">—</div></div>
              <div class="card failed"><div class="label">失敗</div><div class="value" id="c-failed">—</div></div>
              <div class="card"><div class="label">合計 <span id="crawl-badge" style="display:none;font-size:.65rem;background:#f59e0b;color:#1a1a2e;border-radius:4px;padding:1px 5px;vertical-align:middle;">クロール中</span></div><div class="value" id="c-total">—</div></div>
              <div class="card"><div class="label">転送済みバイト</div><div class="value" id="c-bytes">—</div></div>
            </section>

            <section class="cards">
              <div class="card parallel"><div class="label">現在の並列数</div><div class="value" id="c-parallelism">—</div></div>
              <div class="card text-sm"><div class="label">経過時間</div><div class="value" id="c-elapsed">—</div></div>
              <div class="card text-sm"><div class="label">推定残り時間</div><div class="value" id="c-eta">—</div></div>
              <div class="card"><div class="label">平均ファイルサイズ</div><div class="value" id="c-avgsize">—</div></div>
              <div class="card retries"><div class="label">リトライ総数</div><div class="value" id="c-retries">—</div></div>
              <div class="card"><div class="label">エラー率</div><div class="value" id="c-errrate">—</div></div>
            </section>

            <section class="progress-wrap">
              <h2>完了率</h2>
              <div class="bar-bg"><div class="bar-fg" id="progressBar" style="width:0%"></div></div>
              <div class="bar-label" id="progressLabel">0 / 0 (0.0%)</div>
            </section>

            <section class="progress-wrap" id="folderProgressSection" style="display:none">
              <h2>フォルダ作成進捗 <span id="folderPhaseLabel" style="font-weight:normal;color:#8b5cf6;margin-left:6px;"></span></h2>
              <div class="bar-bg"><div class="bar-fg" id="folderProgressBar" style="width:0%;background:linear-gradient(90deg,#8b5cf6,#60a5fa)"></div></div>
              <div class="bar-label" id="folderProgressLabel">0 / —</div>
            </section>

            <!-- 完了サマリー（移行完了時のみ表示） -->
            <section class="completion-summary" id="completionSummary" style="display:none">
              <div class="completion-header">
                <span>&#10003; 移行完了</span>
                <span class="cs-time" id="cs-completedAt"></span>
              </div>
              <div class="cs-grid">
                <div class="cs-item"><div class="cs-label">完了ファイル数</div><div class="cs-value success" id="cs-done">—</div></div>
                <div class="cs-item"><div class="cs-label">失敗ファイル数</div><div class="cs-value" id="cs-failed">—</div></div>
                <div class="cs-item"><div class="cs-label">転送データ量</div><div class="cs-value" id="cs-bytes">—</div></div>
                <div class="cs-item"><div class="cs-label">作成フォルダ数</div><div class="cs-value" id="cs-folders">—</div></div>
                <div class="cs-item"><div class="cs-label">総所要時間</div><div class="cs-value" id="cs-elapsed">—</div></div>
                <div class="cs-item"><div class="cs-label">平均スループット</div><div class="cs-value" id="cs-avg-fps">—</div></div>
                <div class="cs-item"><div class="cs-label">平均転送速度</div><div class="cs-value" id="cs-avg-bps">—</div></div>
                <div class="cs-item"><div class="cs-label">ピーク スループット</div><div class="cs-value" id="cs-peak-fps">—</div></div>
                <div class="cs-item"><div class="cs-label">ピーク 転送速度</div><div class="cs-value" id="cs-peak-bps">—</div></div>
                <div class="cs-item"><div class="cs-label">リトライ総数</div><div class="cs-value" id="cs-retries">—</div></div>
                <div class="cs-item"><div class="cs-label">エラー率</div><div class="cs-value" id="cs-errrate">—</div></div>
                <div class="cs-item"><div class="cs-label">平均ファイルサイズ</div><div class="cs-value" id="cs-avgsize">—</div></div>
              </div>
            </section>

            <section class="charts">
              <div class="chart-box">
                <h2>完了推移（直近ポーリング履歴）</h2>
                <canvas id="doneChart"></canvas>
              </div>
              <div class="chart-box">
                <h2>429 レートリミット率（%）</h2>
                <canvas id="rlChart"></canvas>
              </div>
              <div class="chart-box">
                <h2>スループット（ファイル/分）</h2>
                <canvas id="filesChart"></canvas>
              </div>
              <div class="chart-box">
                <h2>スループット（バイト/秒）</h2>
                <canvas id="bytesChart"></canvas>
              </div>
              <div class="chart-box">
                <h2>並列数推移</h2>
                <canvas id="parallelChart"></canvas>
              </div>
            </section>

            <section class="errors">
              <h2>最近の失敗ファイル</h2>
              <div id="errorList"><span class="no-errors">現在エラーはありません</span></div>
            </section>
          </main>
          </div>

          <!-- 設定タブ -->
          <div x-show="tab === 'config'" x-data="configTab()" x-init="init()">
            <main style="max-width:900px;margin:0 auto;padding:24px 16px;">
              <section class="config-section">
                <div class="config-group">
                  <h3 class="config-group-title">推奨設定</h3>
                  <div class="config-grid">
                    <div class="field">
                      <label>並列転送数 (maxParallelTransfers)</label>
                      <input type="number" x-model.number="config.maxParallelTransfers" min="1" max="100" />
                    </div>
                    <div class="field">
                      <label>チャンクサイズ MB (chunkSizeMb)</label>
                      <input type="number" x-model.number="config.chunkSizeMb" min="1" max="100" />
                    </div>
                    <div class="field">
                      <label>リトライ回数 (retryCount)</label>
                      <input type="number" x-model.number="config.retryCount" min="0" max="20" />
                    </div>
                    <div class="field">
                      <label>タイムアウト秒 (timeoutSec)</label>
                      <input type="number" x-model.number="config.timeoutSec" min="1" />
                    </div>
                    <div class="field" style="grid-column:1/-1">
                      <label>転送先ルートパス (destinationRoot)</label>
                      <input type="text" x-model="config.destinationRoot" />
                    </div>
                  </div>
                </div>

                <details class="danger-area">
                  <summary>&#9888;&#65039; 上級者向け設定（変更時は注意）</summary>
                  <div class="danger-content">
                    <div class="danger-warning">&#9888;&#65039; 以下の設定は動作に大きく影響します。意味を理解した上で変更してください。</div>
                    <div class="config-grid">
                      <div class="field">
                        <label>大容量ファイル閾値 MB (largeFileThresholdMb)</label>
                        <input type="number" x-model.number="config.largeFileThresholdMb" min="1" max="100" />
                      </div>
                      <div class="field">
                        <label>転送先プロバイダ (destinationProvider)</label>
                        <select x-model="config.destinationProvider">
                          <option value="sharepoint">sharepoint</option>
                          <option value="dropbox">dropbox</option>
                        </select>
                      </div>
                    </div>
                  </div>
                </details>

                <div class="config-actions">
                  <button class="btn-save" @click="save()" :disabled="!isDirty() || saving">
                    <span x-text="saving ? '保存中...' : '保存'"></span>
                  </button>
                  <span class="dirty-indicator" x-show="isDirty()">● 未保存の変更があります</span>
                </div>
              </section>
            </main>
            <div class="toast" x-show="toast !== null" :class="toast &amp;&amp; toast.type" x-text="toast &amp;&amp; toast.msg" style="display:none;"></div>
          </div>

          <!-- 実行タブ -->
          <div x-show="tab === 'run'" x-data="runTab()" x-init="init()">
            <main style="max-width:900px;margin:0 auto;padding:24px 16px;">
              <section class="run-section">
                <h3 class="config-group-title" style="margin-bottom:20px;">転送ジョブ制御</h3>

                <div>
                  <button class="btn-start" @click="start()" :disabled="starting || isRunning">
                    <span x-text="starting ? '開始中...' : '転送を開始'"></span>
                  </button>
                  <span style="font-size:.78rem;color:#888;margin-left:14px;" x-show="isRunning &amp;&amp; !starting">転送中です。5秒ごとに状態を更新しています...</span>
                </div>

                <!-- ステータスパネル -->
                <div x-show="jobId !== null" style="margin-top:22px;">

                  <!-- Pending -->
                  <div x-show="status === 'Pending'" class="status-panel pending-panel">
                    <span class="spinner"></span>開始待機中...
                    <div class="run-elapsed" x-show="startedAt">経過: <span x-text="elapsed"></span></div>
                  </div>

                  <!-- Running -->
                  <div x-show="status === 'Running'" class="status-panel running-panel">
                    <div class="run-progress">
                      &#9654;&#65039; 転送実行中
                      <div class="run-elapsed">経過: <span x-text="elapsed"></span></div>
                    </div>
                    <div class="stop-guide">
                      <p>&#9888;&#65039; 転送を停止するには、別ターミナルで以下のコマンドを実行してください:</p>
                      <code>dotnet run -- transfer --cancel</code>
                    </div>
                  </div>

                  <!-- Completed -->
                  <div x-show="status === 'Completed'" class="status-panel completed-panel">
                    &#10003; 転送が完了しました
                    <div class="run-elapsed" x-show="completedAt" x-text="completedAt ? '完了時刻: ' + new Date(completedAt).toLocaleString('ja-JP') : ''"></div>
                    <div class="run-elapsed" x-show="elapsed !== '—'">所要時間: <span x-text="elapsed"></span></div>
                  </div>

                  <!-- Failed -->
                  <div x-show="status === 'Failed'" class="status-panel failed-panel">
                    &#10007; 転送が失敗しました
                    <div class="error-detail" x-show="errorMessage" x-text="errorMessage"></div>
                  </div>

                  <!-- Cancelled -->
                  <div x-show="status === 'Cancelled'" class="status-panel cancelled-panel">
                    &#9632; 転送がキャンセルされました
                    <div class="run-elapsed" x-show="completedAt" x-text="completedAt ? '停止時刻: ' + new Date(completedAt).toLocaleString('ja-JP') : ''"></div>
                  </div>

                </div>
              </section>
            </main>
            <div class="toast" x-show="toast !== null" :class="toast &amp;&amp; toast.type" x-text="toast &amp;&amp; toast.msg" style="display:none;"></div>
          </div>

          <script>
            const POLL_INTERVAL = 5000; // ms
            const MAX_HISTORY = 60;     // 直近 5 分分（5s × 60）

            let latestFilesPerMin = 0;   // 最新スループット（ETA 計算用）
            let completedAt    = null;   // 完了検出時刻（一度だけセット）
            let lastPhaseData  = null;   // 最新フェーズデータ（サマリー用）
            let peakFilesPerMin = 0;     // ピーク スループット（files/min）
            let peakBytesPerSec = 0;     // ピーク スループット（bytes/sec）
            let pipelineStartedAt = null; // パイプライン開始時刻（全期間カバーのメトリクス取得用）

            function fmtDuration(sec) {
              sec = Math.round(sec);
              if (sec < 60) return sec + '秒';
              if (sec < 3600) return Math.floor(sec / 60) + 'm' + String(sec % 60).padStart(2, '0') + 's';
              const h = Math.floor(sec / 3600);
              const m = Math.floor((sec % 3600) / 60);
              return h + 'h' + String(m).padStart(2, '0') + 'm';
            }

            // Chart.js 共通オプション
            Chart.defaults.color = '#aaa';
            Chart.defaults.borderColor = '#2a2a4a';

            const doneHistory = [];
            const doneLabels = [];

            const doneChart = new Chart(document.getElementById('doneChart'), {
              type: 'line',
              data: {
                labels: doneLabels,
                datasets: [{
                  label: '完了件数',
                  data: doneHistory,
                  borderColor: '#22c55e',
                  backgroundColor: 'rgba(34,197,94,.1)',
                  fill: true,
                  tension: 0.3,
                  pointRadius: 2,
                }]
              },
              options: { animation: false, scales: { x: { ticks: { maxTicksLimit: 8 } }, y: { beginAtZero: false } } }
            });

            const rlChart = new Chart(document.getElementById('rlChart'), {
              type: 'line',
              data: {
                labels: [],
                datasets: [{
                  label: 'rate_limit_pct (%)',
                  data: [],
                  borderColor: '#f59e0b',
                  backgroundColor: 'rgba(245,158,11,.1)',
                  fill: true,
                  tension: 0.3,
                  pointRadius: 2,
                }]
              },
              options: {
                animation: false,
                scales: {
                  x: { ticks: { maxTicksLimit: 8 } },
                  y: { min: 0, max: 100, ticks: { callback: v => v + '%' } }
                }
              }
            });

            const filesChart = new Chart(document.getElementById('filesChart'), {
              type: 'line',
              data: {
                labels: [],
                datasets: [{
                  label: 'files/min',
                  data: [],
                  borderColor: '#60a5fa',
                  backgroundColor: 'rgba(96,165,250,.1)',
                  fill: true,
                  tension: 0.3,
                  pointRadius: 2,
                }]
              },
              options: {
                animation: false,
                scales: {
                  x: { ticks: { maxTicksLimit: 8 } },
                  y: { beginAtZero: true, ticks: { callback: v => v + ' f/m' } }
                }
              }
            });

            function fmtBytesPerSec(v) {
              const n = Number(v);
              if (!Number.isFinite(n)) return '-';
              if (n < 1024) return n.toFixed(0) + ' B/s';
              if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB/s';
              return (n / (1024 * 1024)).toFixed(1) + ' MB/s';
            }

            function showCompletionSummary(s) {
              document.getElementById('completionSummary').style.display = '';

              // 完了日時
              document.getElementById('cs-completedAt').textContent =
                completedAt ? completedAt.toLocaleString('ja-JP') : '';

              // 完了ファイル数
              document.getElementById('cs-done').textContent = fmt(s.done);

              // 失敗ファイル数
              const failedCount = (s.failed ?? 0) + (s.permanentFailed ?? 0);
              const failedEl = document.getElementById('cs-failed');
              failedEl.textContent = fmt(failedCount);
              failedEl.className = 'cs-value ' + (failedCount > 0 ? 'danger' : 'success');

              // 転送データ量
              document.getElementById('cs-bytes').textContent = fmtBytes(s.totalDoneSizeBytes);

              // 作成フォルダ数
              document.getElementById('cs-folders').textContent = fmt(lastPhaseData?.folderDone ?? 0);

              // 総所要時間と平均スループット
              const startedAt = s.pipelineStartedAt ?? s.firstUpdatedAt;
              let elapsedSec = null;
              if (startedAt && completedAt) {
                elapsedSec = (completedAt.getTime() - new Date(startedAt).getTime()) / 1000;
                document.getElementById('cs-elapsed').textContent = fmtDuration(elapsedSec);
              } else {
                document.getElementById('cs-elapsed').textContent = '—';
              }
              if (elapsedSec && elapsedSec > 0 && s.done > 0) {
                document.getElementById('cs-avg-fps').textContent =
                  (s.done / (elapsedSec / 60)).toFixed(1) + ' f/m';
                document.getElementById('cs-avg-bps').textContent =
                  fmtBytesPerSec(s.totalDoneSizeBytes / elapsedSec);
              } else {
                document.getElementById('cs-avg-fps').textContent = '—';
                document.getElementById('cs-avg-bps').textContent = '—';
              }

              // ピークスループット
              document.getElementById('cs-peak-fps').textContent =
                peakFilesPerMin > 0 ? peakFilesPerMin.toFixed(1) + ' f/m' : '—';
              document.getElementById('cs-peak-bps').textContent =
                peakBytesPerSec > 0 ? fmtBytesPerSec(peakBytesPerSec) : '—';

              // リトライ総数
              document.getElementById('cs-retries').textContent = fmt(s.totalRetries ?? 0);

              // エラー率
              const total = s.done + failedCount;
              const errRate = total > 0 ? (failedCount / total * 100) : 0;
              const errEl = document.getElementById('cs-errrate');
              errEl.textContent = errRate.toFixed(2) + '%';
              errEl.className = 'cs-value ' + (errRate > 5 ? 'danger' : errRate > 0 ? 'warn' : 'success');

              // 平均ファイルサイズ
              document.getElementById('cs-avgsize').textContent =
                s.done > 0 ? fmtBytes(Math.round(s.totalDoneSizeBytes / s.done)) : '—';
            }

            const bytesChart = new Chart(document.getElementById('bytesChart'), {
              type: 'line',
              data: {
                labels: [],
                datasets: [{
                  label: 'bytes/sec',
                  data: [],
                  borderColor: '#a78bfa',
                  backgroundColor: 'rgba(167,139,250,.1)',
                  fill: true,
                  tension: 0.3,
                  pointRadius: 2,
                }]
              },
              options: {
                animation: false,
                scales: {
                  x: { ticks: { maxTicksLimit: 8 } },
                  y: { beginAtZero: true, ticks: { callback: v => fmtBytesPerSec(v) } }
                }
              }
            });

            const parallelChart = new Chart(document.getElementById('parallelChart'), {
              type: 'line',
              data: {
                labels: [],
                datasets: [{
                  label: '並列数',
                  data: [],
                  borderColor: '#fb923c',
                  backgroundColor: 'rgba(251,146,60,.1)',
                  fill: true,
                  tension: 0.3,
                  pointRadius: 2,
                }]
              },
              options: {
                animation: false,
                scales: {
                  x: { ticks: { maxTicksLimit: 8 } },
                  y: { beginAtZero: true, ticks: { stepSize: 1, callback: v => Number.isInteger(v) ? v + ' 並列' : '' } }
                }
              }
            });

            function fmtBytes(b) {
              if (b === 0) return '0 B';
              const units = ['B','KB','MB','GB','TB'];
              const i = Math.min(Math.floor(Math.log2(b) / 10), 4);
              return (b / Math.pow(1024, i)).toFixed(1) + ' ' + units[i];
            }
            function fmtTime(iso) {
              return new Date(iso).toLocaleTimeString('ja-JP', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
            }
            function fmt(n) { return n.toLocaleString('ja-JP'); }

            async function refreshStatus() {
              const res = await fetch('/api/status');
              if (!res.ok) return;
              const s = await res.json();
              document.getElementById('c-done').textContent       = fmt(s.done);
              document.getElementById('c-pending').textContent    = fmt(s.pending);
              document.getElementById('c-processing').textContent = fmt(s.processing);
              document.getElementById('c-failed').textContent     = fmt(s.failed + s.permanentFailed);
              document.getElementById('c-bytes').textContent      = fmtBytes(s.totalDoneSizeBytes);

              // クロール完了後は確定総数 (crawlTotal) を分母に使う
              const crawlDone = s.crawlComplete === true;
              const denominator = (crawlDone && s.crawlTotal > 0) ? s.crawlTotal : s.total;
              document.getElementById('c-total').textContent      = fmt(denominator);
              document.getElementById('crawl-badge').style.display = crawlDone ? 'none' : 'inline';

              const pct = denominator > 0 ? (s.done / denominator * 100) : 0;
              document.getElementById('progressBar').style.width  = pct.toFixed(1) + '%';
              document.getElementById('progressLabel').textContent = crawlDone
                ? `${fmt(s.done)} / ${fmt(denominator)} (${pct.toFixed(1)}%)`
                : `${fmt(s.done)} / ${fmt(s.total)} (${pct.toFixed(1)}% ※クロール中のため推定)`;

              // 完了推移グラフ更新
              const now = new Date().toLocaleTimeString('ja-JP', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
              doneHistory.push(s.done);
              doneLabels.push(now);
              if (doneHistory.length > MAX_HISTORY) { doneHistory.shift(); doneLabels.shift(); }
              doneChart.update();

              // リトライ総数
              document.getElementById('c-retries').textContent = fmt(s.totalRetries ?? 0);

              // エラー率
              const errRate = s.total > 0 ? (s.failed + s.permanentFailed) / s.total * 100 : 0;
              const errEl = document.getElementById('c-errrate');
              errEl.textContent = errRate.toFixed(1) + '%';
              errEl.style.color = errRate > 0 ? '#ef4444' : '#22c55e';

              // 平均ファイルサイズ
              document.getElementById('c-avgsize').textContent =
                s.done > 0 ? fmtBytes(Math.round(s.totalDoneSizeBytes / s.done)) : '—';

              // 経過時間（pipeline_started_at checkpoint 優先、なければ firstUpdatedAt を代用）
              const startedAt = s.pipelineStartedAt ?? s.firstUpdatedAt;
              if (startedAt && !pipelineStartedAt) pipelineStartedAt = startedAt;
              if (startedAt) {
                const elapsedSec = (Date.now() - new Date(startedAt).getTime()) / 1000;
                document.getElementById('c-elapsed').textContent = fmtDuration(elapsedSec);
              }

              // 推定残り時間（ETA）
              const remaining = s.pending + s.processing + s.failed;
              if (latestFilesPerMin > 0 && remaining > 0) {
                document.getElementById('c-eta').textContent = fmtDuration(remaining / latestFilesPerMin * 60);
              } else {
                document.getElementById('c-eta').textContent = remaining === 0 ? '完了' : '—';
              }

              // 完了検出：pending/processing がゼロかつ 1 件以上完了
              const allDone = s.pending === 0 && s.processing === 0 && s.done > 0;
              if (allDone && !completedAt) {
                // 完了確定時刻はサーバー由来の lastUpdatedAt を優先し、なければクライアント時刻を使用
                completedAt = s.lastUpdatedAt ? new Date(s.lastUpdatedAt) : new Date();
              }
              if (completedAt) {
                showCompletionSummary(s);
                // フェーズバッジを「移行完了」に固定
                const badge = document.getElementById('phaseBadge');
                badge.className  = 'phase-badge completed';
                badge.textContent = '移行完了';
                badge.style.display = 'inline';
              }
            }

            async function refreshMetrics() {
              // パイプライン開始から全期間をカバーする分数（最低 60 分）
              // 60 分を超える移行でピーク値が欠落するのを防ぐための動的計算
              const minsElapsed = pipelineStartedAt
                ? Math.ceil((Date.now() - new Date(pipelineStartedAt).getTime()) / 60000) + 10 : 60;
              const metricsMinutes = Math.max(60, minsElapsed);
              const [rlRes, filesRes, bytesRes, parallelRes] = await Promise.all([
                fetch(`/api/metrics?name=rate_limit_pct&minutes=${metricsMinutes}`),
                fetch(`/api/metrics?name=throughput_files_per_min&minutes=${metricsMinutes}`),
                fetch(`/api/metrics?name=throughput_bytes_per_sec&minutes=${metricsMinutes}`),
                fetch(`/api/metrics?name=current_parallelism&minutes=${metricsMinutes}`),
              ]);
              if (rlRes.ok) {
                const data = await rlRes.json();
                if (data.length) {
                  rlChart.data.labels = data.map(p => fmtTime(p.timestamp));
                  rlChart.data.datasets[0].data = data.map(p => p.value);
                  rlChart.update();
                }
              }
              if (filesRes.ok) {
                const data = await filesRes.json();
                if (data.length) {
                  filesChart.data.labels = data.map(p => fmtTime(p.timestamp));
                  filesChart.data.datasets[0].data = data.map(p => p.value);
                  filesChart.update();
                  latestFilesPerMin = data[data.length - 1].value;
                  peakFilesPerMin = Math.max(peakFilesPerMin, ...data.map(p => p.value));
                }
              }
              if (bytesRes.ok) {
                const data = await bytesRes.json();
                if (data.length) {
                  bytesChart.data.labels = data.map(p => fmtTime(p.timestamp));
                  bytesChart.data.datasets[0].data = data.map(p => p.value);
                  bytesChart.update();
                  peakBytesPerSec = Math.max(peakBytesPerSec, ...data.map(p => p.value));
                }
              }
              if (parallelRes.ok) {
                const data = await parallelRes.json();
                if (data.length) {
                  document.getElementById('c-parallelism').textContent = Math.round(data[data.length - 1].value);
                  parallelChart.data.labels = data.map(p => fmtTime(p.timestamp));
                  parallelChart.data.datasets[0].data = data.map(p => p.value);
                  parallelChart.update();
                }
              }
            }

            const phaseLabels = {
              'crawling':        'クロール中',
              'folder_creation': 'フォルダ作成中',
              'transferring':    '転送中',
            };

            async function refreshPhase() {
              const res = await fetch('/api/phase');
              if (!res.ok) return;
              const p = await res.json();
              lastPhaseData = p; // サマリー（作成フォルダ数）の参照用にキャッシュ

              // フェーズバッジ更新（完了済みの場合は refreshStatus 側で上書き済みのため skip）
              if (!completedAt) {
                const badge = document.getElementById('phaseBadge');
                badge.className = 'phase-badge ' + p.phase;
                badge.textContent = phaseLabels[p.phase] ?? p.phase;
                badge.style.display = 'inline';
              }

              // フォルダ作成進捗セクション（folder_creation フェーズのみ表示）
              const folderSection = document.getElementById('folderProgressSection');
              if (p.phase === 'folder_creation' || (p.phase === 'transferring' && (p.folderTotal ?? 0) > 0)) {
                folderSection.style.display = '';
                const total = p.folderTotal ?? 0;
                const done = p.folderDone ?? 0;
                const pct = total > 0 ? (done / total * 100) : 0;
                document.getElementById('folderProgressBar').style.width = pct.toFixed(1) + '%';
                document.getElementById('folderProgressLabel').textContent =
                  total > 0 ? `${fmt(done)} / ${fmt(total)} (${pct.toFixed(1)}%)` : `${fmt(done)} / —`;
                document.getElementById('folderPhaseLabel').textContent =
                  p.phase === 'folder_creation' ? '(実行中)' : '(完了)';
              } else {
                folderSection.style.display = 'none';
              }
            }

            async function refreshErrors() {
              const res = await fetch('/api/errors');
              if (!res.ok) return;
              const errs = await res.json();
              const el = document.getElementById('errorList');
              el.textContent = '';
              if (!errs.length) {
                const span = document.createElement('span');
                span.className = 'no-errors';
                span.textContent = '現在エラーはありません';
                el.appendChild(span);
                return;
              }
              errs.forEach(e => {
                const item = document.createElement('div');
                item.className = 'error-item';
                const pathSpan = document.createElement('span');
                pathSpan.className = 'path';
                pathSpan.textContent = `${e.path}/${e.name}`;
                item.appendChild(pathSpan);
                item.appendChild(document.createElement('br'));
                const msgSpan = document.createElement('span');
                msgSpan.textContent = e.error ?? '';
                item.appendChild(msgSpan);
                el.appendChild(item);
              });
            }

            async function refresh() {
              await Promise.allSettled([refreshStatus(), refreshMetrics(), refreshErrors(), refreshPhase()]);
            }

            // カウントダウン表示
            let remaining = POLL_INTERVAL / 1000;
            const refreshTxt = document.getElementById('refreshTxt');
            setInterval(() => {
              remaining--;
              if (remaining <= 0) { refresh(); remaining = POLL_INTERVAL / 1000; }
              refreshTxt.textContent = `次回更新まで ${remaining}s`;
            }, 1000);

            // 初回即時ロード
            refresh();

            // 設定タブ Alpine.js コンポーネント
            function configTab() {
              return {
                config: {},
                original: {},
                saving: false,
                toast: null,
                async init() {
                  try {
                    const res = await fetch('/api/config');
                    if (res.ok) {
                      this.config = await res.json();
                      this.original = JSON.parse(JSON.stringify(this.config));
                    } else {
                      this.showToast('設定の読み込みに失敗しました', 'error');
                    }
                  } catch (e) {
                    this.showToast('設定の読み込みエラー: ' + e.message, 'error');
                  }
                },
                isDirty() {
                  return JSON.stringify(this.config) !== JSON.stringify(this.original);
                },
                async save() {
                  this.saving = true;
                  try {
                    const res = await fetch('/api/config', {
                      method: 'PUT',
                      headers: { 'Content-Type': 'application/json' },
                      body: JSON.stringify(this.config)
                    });
                    if (res.ok) {
                      this.original = JSON.parse(JSON.stringify(this.config));
                      this.showToast('設定を保存しました', 'success');
                    } else {
                      const msg = await res.text();
                      this.showToast('保存失敗: ' + msg, 'error');
                    }
                  } catch (e) {
                    this.showToast('保存エラー: ' + e.message, 'error');
                  } finally {
                    this.saving = false;
                  }
                },
                showToast(msg, type) {
                  this.toast = { msg, type };
                  setTimeout(() => { this.toast = null; }, 3500);
                }
              };
            }

            // 実行タブ Alpine.js コンポーネント
            function runTab() {
              return {
                jobId: null,
                status: null,
                startedAt: null,
                completedAt: null,
                errorMessage: null,
                starting: false,
                pollTimer: null,
                elapsed: '—',
                elapsedTimer: null,
                toast: null,

                get isRunning() {
                  return this.status === 'Pending' || this.status === 'Running';
                },

                async init() {
                  // セッション中の jobId を復元してポーリング再開
                  const saved = sessionStorage.getItem('transferJobId');
                  if (saved) {
                    this.jobId = saved;
                    await this.fetchStatus();
                    if (this.isRunning) this.startPolling();
                  }
                },

                async start() {
                  this.starting = true;
                  try {
                    const res = await fetch('/api/transfer/start', { method: 'POST' });
                    if (res.status === 202) {
                      const data = await res.json();
                      this.jobId = data.jobId;
                      this.status = data.status;
                      this.startedAt = null;
                      this.completedAt = null;
                      this.errorMessage = null;
                      this.elapsed = '—';
                      sessionStorage.setItem('transferJobId', this.jobId);
                      this.startPolling();
                    } else if (res.status === 409) {
                      this.showToast('転送ジョブがすでに実行中です。', 'error');
                    } else {
                      this.showToast('転送の開始に失敗しました。', 'error');
                    }
                  } catch (e) {
                    this.showToast('エラー: ' + e.message, 'error');
                  } finally {
                    this.starting = false;
                  }
                },

                async fetchStatus() {
                  if (!this.jobId) return;
                  try {
                    const res = await fetch('/api/transfer/' + this.jobId);
                    if (res.ok) {
                      const data = await res.json();
                      this.status = data.status;
                      this.startedAt = data.startedAt;
                      this.completedAt = data.completedAt;
                      this.errorMessage = data.errorMessage;
                      if (this.status === 'Completed' || this.status === 'Failed' || this.status === 'Cancelled') {
                        this.stopPolling();
                        this.updateElapsed();
                      }
                    } else if (res.status === 404) {
                      this.stopPolling();
                    }
                  } catch (_) { /* ネットワークエラーは無視 */ }
                },

                startPolling() {
                  this.stopPolling();
                  this.pollTimer = setInterval(() => this.fetchStatus(), 5000);
                  this.startElapsedTimer();
                },

                stopPolling() {
                  if (this.pollTimer) { clearInterval(this.pollTimer); this.pollTimer = null; }
                  this.stopElapsedTimer();
                },

                startElapsedTimer() {
                  this.stopElapsedTimer();
                  this.elapsedTimer = setInterval(() => this.updateElapsed(), 1000);
                },

                stopElapsedTimer() {
                  if (this.elapsedTimer) { clearInterval(this.elapsedTimer); this.elapsedTimer = null; }
                },

                updateElapsed() {
                  if (!this.startedAt) return;
                  const end = this.completedAt ? new Date(this.completedAt) : new Date();
                  const sec = Math.round((end - new Date(this.startedAt)) / 1000);
                  this.elapsed = fmtDuration(sec);
                },

                showToast(msg, type) {
                  this.toast = { msg, type };
                  setTimeout(() => { this.toast = null; }, 3500);
                }
              };
            }
          </script>
        </body>
        </html>
        """;
}
