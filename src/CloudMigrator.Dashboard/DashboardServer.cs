using CloudMigrator.Core.Migration;
using CloudMigrator.Core.State;
using Microsoft.AspNetCore.Builder;
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
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning); // ダッシュボード固有のノイズを抑制

        // ITransferStateDb を DI コンテナに登録する。
        // テストや将来の DB 差し替えはここを変更するだけで対応できる。
        builder.Services.AddSingleton<ITransferStateDb>(_ => new SqliteTransferStateDb(dbPath));

        var app = builder.Build();
        app.Urls.Add($"http://localhost:{port}");

        // 起動時に 1 回だけスキーマ初期化（IDisposable はホスト停止時に解放される）
        var db = app.Services.GetRequiredService<ITransferStateDb>();
        await db.InitializeAsync(ct).ConfigureAwait(false);

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

        // GET /  → ダッシュボード HTML
        app.MapGet("/", () => Results.Content(IndexHtml, "text/html; charset=utf-8"));

        await app.RunAsync(ct).ConfigureAwait(false);
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
          </style>
        </head>
        <body>
          <header>
            <h1>&#128640; CloudMigrator</h1>
            <span class="badge">Dashboard</span>
            <span class="phase-badge" id="phaseBadge"></span>
            <span class="refresh-indicator" id="refreshTxt">次回更新まで 5s</span>
          </header>
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

          <script>
            const POLL_INTERVAL = 5000; // ms
            const MAX_HISTORY = 60;     // 直近 5 分分（5s × 60）

            let latestFilesPerMin = 0;  // 最新スループット（ETA 計算用）

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
            }

            async function refreshMetrics() {
              const [rlRes, filesRes, bytesRes, parallelRes] = await Promise.all([
                fetch('/api/metrics?name=rate_limit_pct&minutes=60'),
                fetch('/api/metrics?name=throughput_files_per_min&minutes=60'),
                fetch('/api/metrics?name=throughput_bytes_per_sec&minutes=60'),
                fetch('/api/metrics?name=current_parallelism&minutes=60'),
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
                }
              }
              if (bytesRes.ok) {
                const data = await bytesRes.json();
                if (data.length) {
                  bytesChart.data.labels = data.map(p => fmtTime(p.timestamp));
                  bytesChart.data.datasets[0].data = data.map(p => p.value);
                  bytesChart.update();
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

              // フェーズバッジ更新
              const badge = document.getElementById('phaseBadge');
              badge.className = 'phase-badge ' + p.phase;
              badge.textContent = phaseLabels[p.phase] ?? p.phase;
              badge.style.display = 'inline';

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
          </script>
        </body>
        </html>
        """;
}
