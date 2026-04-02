using System.Collections.Concurrent;

namespace CloudMigrator.Dashboard;

/// <summary>
/// 転送ジョブのライフサイクルを管理するサービス契約。
/// </summary>
public interface ITransferJobService
{
    /// <summary>
    /// 新しい転送ジョブを開始する。
    /// 既にジョブが実行中または待機中の場合は <c>null</c> を返す（HTTP 409 Conflict 用）。
    /// </summary>
    /// <param name="ct">ジョブ停止シグナルとして伝播するキャンセルトークン。</param>
    Task<TransferJobInfo?> TryStartAsync(CancellationToken ct = default);

    /// <summary>
    /// 指定 ID のジョブ状態を取得する。
    /// 存在しない jobId の場合は <c>null</c> を返す（HTTP 404 NotFound 用）。
    /// </summary>
    TransferJobInfo? GetJob(string jobId);
}

/// <summary>
/// <see cref="ITransferJobService"/> のインメモリ実装。
/// <see cref="SemaphoreSlim"/> で同時実行 1 本に制限する。
/// ジョブはサーバー再起動でリセットされる。
/// </summary>
/// <remarks>
/// ジョブ履歴はインメモリで最大 <see cref="MaxJobHistoryCount"/> 件まで保持する。
/// 1 回の移行セッションで 1 エントリが増加するペースのため、
/// 通常運用（1 日数回程度）ではメモリ消費は軽微であるが、
/// 件数超過時は古いジョブエントリを FIFO で削除する。
/// </remarks>
public sealed class TransferJobService : ITransferJobService
{
    /// <summary>インメモリで保持するジョブ履歴の最大件数。</summary>
    internal const int MaxJobHistoryCount = 100;

    private readonly Func<CancellationToken, Task>? _work;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ConcurrentDictionary<string, TransferJobInfo> _jobs = new();
    private readonly Queue<string> _jobOrder = new();

    /// <param name="work">
    /// ジョブで実行する非同期処理。
    /// <c>null</c> の場合は即時完了する（Phase 5 で実際の移行処理を注入予定）。
    /// </param>
    public TransferJobService(Func<CancellationToken, Task>? work = null)
    {
        _work = work;
    }

    /// <inheritdoc />
    public async Task<TransferJobInfo?> TryStartAsync(CancellationToken ct = default)
    {
        // ノンブロッキング取得: 既に別ジョブが保有中なら即座に false が返る
        if (!await _semaphore.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            return null;

        var jobId = Guid.NewGuid().ToString("D");
        var job = new TransferJobInfo(jobId, JobStatus.Pending, null, null, null);
        _jobs[jobId] = job;
        _jobOrder.Enqueue(jobId);

        // 上限超過時は最古エントリを削除する
        while (_jobOrder.Count > MaxJobHistoryCount)
        {
            if (_jobOrder.TryDequeue(out var oldId))
                _jobs.TryRemove(oldId, out _);
        }

        // セマフォは RunJobAsync の finally で解放する（fire-and-forget）
        _ = RunJobAsync(jobId, ct);

        return job;
    }

    /// <inheritdoc />
    public TransferJobInfo? GetJob(string jobId)
    {
        _jobs.TryGetValue(jobId, out var job);
        return job;
    }

    private async Task RunJobAsync(string jobId, CancellationToken ct)
    {
        try
        {
            _jobs[jobId] = _jobs[jobId] with { Status = JobStatus.Running, StartedAt = DateTimeOffset.UtcNow };

            if (_work is not null)
                await _work(ct).ConfigureAwait(false);

            _jobs[jobId] = _jobs[jobId] with
            {
                Status = JobStatus.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
            };
        }
        catch (OperationCanceledException ex)
        {
            // ct 由来のキャンセルのみ Cancelled に遷移する。
            // ct と無関係な内部タイムアウト等の OCE は Failed として扱う。
            if (ct.IsCancellationRequested)
            {
                _jobs[jobId] = _jobs[jobId] with
                {
                    Status = JobStatus.Cancelled,
                    CompletedAt = DateTimeOffset.UtcNow,
                };
            }
            else
            {
                _jobs[jobId] = _jobs[jobId] with
                {
                    Status = JobStatus.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = ex.Message,
                };
            }
        }
        catch (Exception ex)
        {
            _jobs[jobId] = _jobs[jobId] with
            {
                Status = JobStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = ex.Message,
            };
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
