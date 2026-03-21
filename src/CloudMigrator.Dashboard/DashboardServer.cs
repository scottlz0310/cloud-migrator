using CloudMigrator.Core.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

        var app = builder.Build();
        app.Urls.Add($"http://localhost:{port}");

        // 起動時に 1 回だけ DB を初期化し、各ハンドラーで共有する
        await using var db = new SqliteTransferStateDb(dbPath);
        await db.InitializeAsync(ct).ConfigureAwait(false);

        // ── API エンドポイント ────────────────────────────────────────────────

        // GET /api/status  → ステータス別件数・完了率・バイト数
        app.MapGet("/api/status", async (CancellationToken cancellationToken) =>
        {
            var summary = await db.GetSummaryAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(summary);
        });

        // GET /api/metrics?name=rate_limit_pct&minutes=60  → 時系列メトリクス
        app.MapGet("/api/metrics", async (
            string? name,
            int? minutes,
            CancellationToken cancellationToken) =>
        {
            var data = await db.GetMetricsAsync(
                name ?? "rate_limit_pct",
                minutes ?? 60,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(data);
        });

        // GET /api/errors  → 最近の失敗ファイル（最大5件）
        app.MapGet("/api/errors", async (CancellationToken cancellationToken) =>
        {
            var summary = await db.GetSummaryAsync(cancellationToken).ConfigureAwait(false);
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
          </style>
        </head>
        <body>
          <header>
            <h1>&#128640; CloudMigrator</h1>
            <span class="badge">Dashboard</span>
            <span class="refresh-indicator" id="refreshTxt">次回更新まで 5s</span>
          </header>
          <main>
            <section class="cards">
              <div class="card done"><div class="label">完了</div><div class="value" id="c-done">—</div></div>
              <div class="card pending"><div class="label">待機中</div><div class="value" id="c-pending">—</div></div>
              <div class="card processing"><div class="label">処理中</div><div class="value" id="c-processing">—</div></div>
              <div class="card failed"><div class="label">失敗</div><div class="value" id="c-failed">—</div></div>
              <div class="card"><div class="label">合計</div><div class="value" id="c-total">—</div></div>
              <div class="card"><div class="label">転送済みバイト</div><div class="value" id="c-bytes">—</div></div>
            </section>

            <section class="progress-wrap">
              <h2>完了率</h2>
              <div class="bar-bg"><div class="bar-fg" id="progressBar" style="width:0%"></div></div>
              <div class="bar-label" id="progressLabel">0 / 0 (0.0%)</div>
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
            </section>

            <section class="errors">
              <h2>最近の失敗ファイル</h2>
              <div id="errorList"><span class="no-errors">現在エラーはありません</span></div>
            </section>
          </main>

          <script>
            const POLL_INTERVAL = 5000; // ms
            const MAX_HISTORY = 60;     // 直近 5 分分（5s × 60）

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
              document.getElementById('c-total').textContent      = fmt(s.total);
              document.getElementById('c-bytes').textContent      = fmtBytes(s.totalDoneSizeBytes);

              const pct = s.total > 0 ? (s.done / s.total * 100) : 0;
              document.getElementById('progressBar').style.width  = pct.toFixed(1) + '%';
              document.getElementById('progressLabel').textContent =
                `${fmt(s.done)} / ${fmt(s.total)} (${pct.toFixed(1)}%)`;

              // 完了推移グラフ更新
              const now = new Date().toLocaleTimeString('ja-JP', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
              doneHistory.push(s.done);
              doneLabels.push(now);
              if (doneHistory.length > MAX_HISTORY) { doneHistory.shift(); doneLabels.shift(); }
              doneChart.update();
            }

            async function refreshMetrics() {
              const res = await fetch('/api/metrics?name=rate_limit_pct&minutes=60');
              if (!res.ok) return;
              const data = await res.json();
              if (!data.length) return;
              rlChart.data.labels   = data.map(p => fmtTime(p.timestamp));
              rlChart.data.datasets[0].data = data.map(p => p.value);
              rlChart.update();
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
              await Promise.allSettled([refreshStatus(), refreshMetrics(), refreshErrors()]);
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
