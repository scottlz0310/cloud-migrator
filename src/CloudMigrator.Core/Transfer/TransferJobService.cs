using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Core.Transfer;

/// <summary>
/// ジョブエラー時にクライアントへ返す汎用メッセージ定数。
/// 内部例外の詳細（パス・URL 等）を外部に漏らさないよう、
/// すべての失敗ケースでこの汎用文言を使用する。
/// 詳細は Phase 5 でロガー注入後にサーバーログへ記録する。
/// </summary>
internal static class JobErrorMessages
{
    internal const string GenericFailure = "転送処理中にエラーが発生しました。詳細はサーバーログを参照してください。";
}

/// <summary>
/// 転送ジョブのライフサイクルを管理するサービス契約。
/// </summary>
public interface ITransferJobService
{
    /// <summary>
    /// 新しい転送ジョブを開始する。
    /// 既にジョブが実行中または待機中の場合は <c>null</c> を返す（HTTP 409 Conflict 用）。
    /// </summary>
    Task<TransferJobInfo?> TryStartAsync();

    /// <summary>
    /// 実行中のジョブをキャンセルする。ジョブがなければ何もしない。
    /// </summary>
    void Cancel();

    /// <summary>
    /// 指定 ID のジョブ状態を取得する。
    /// 存在しない jobId の場合は <c>null</c> を返す（HTTP 404 NotFound 用）。
    /// </summary>
    TransferJobInfo? GetJob(string jobId);

    /// <summary>現在実行中のジョブ情報。なければ <c>null</c>。</summary>
    TransferJobInfo? CurrentJob { get; }

    /// <summary>ジョブが現在実行中（または待機中）かどうか。</summary>
    bool IsRunning { get; }
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
    private readonly ILogger<TransferJobService> _logger;
    private CancellationTokenSource? _currentJobCts;
    private string? _currentJobId;

    /// <param name="work">
    /// ジョブで実行する非同期処理。
    /// <c>null</c> の場合は即時完了する。
    /// </param>
    public TransferJobService(
        ILogger<TransferJobService> logger,
        Func<CancellationToken, Task>? work = null)
    {
        _logger = logger;
        _work = work;
    }

    /// <inheritdoc />
    public async Task<TransferJobInfo?> TryStartAsync()
    {
        // ノンブロッキング取得: 既に別ジョブが保有中なら即座に false が返る
        if (!await _semaphore.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            return null;

        var jobId = Guid.NewGuid().ToString("D");
        var job = new TransferJobInfo(jobId, JobStatus.Pending, null, null, null);
        _jobs[jobId] = job;
        _jobOrder.Enqueue(jobId);
        _currentJobId = jobId;

        // 上限超過時は最古エントリを削除する
        while (_jobOrder.Count > MaxJobHistoryCount)
        {
            if (_jobOrder.TryDequeue(out var oldId))
                _jobs.TryRemove(oldId, out _);
        }

        // 内部 CTS を新規作成・セマフォは RunJobAsync の finally で解放（fire-and-forget）
        _currentJobCts = new CancellationTokenSource();
        _ = RunJobAsync(jobId, _currentJobCts.Token);

        return job;
    }

    /// <inheritdoc />
    public void Cancel() => _currentJobCts?.Cancel();

    /// <inheritdoc />
    public TransferJobInfo? GetJob(string jobId)
    {
        _jobs.TryGetValue(jobId, out var job);
        return job;
    }

    /// <inheritdoc />
    public TransferJobInfo? CurrentJob =>
        _currentJobId is null ? null : GetJob(_currentJobId);

    /// <inheritdoc />
    public bool IsRunning => _semaphore.CurrentCount == 0;

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
                _logger.LogInformation("ジョブ {JobId} はキャンセルされました。", jobId);
                _jobs[jobId] = _jobs[jobId] with
                {
                    Status = JobStatus.Cancelled,
                    CompletedAt = DateTimeOffset.UtcNow,
                };
            }
            else
            {
                _logger.LogError(ex, "ジョブ {JobId} が内部 OperationCanceledException で失敗しました。", jobId);
                _jobs[jobId] = _jobs[jobId] with
                {
                    Status = JobStatus.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = JobErrorMessages.GenericFailure,
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ジョブ {JobId} が予期せぬ例外で失敗しました。", jobId);
            _jobs[jobId] = _jobs[jobId] with
            {
                Status = JobStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = JobErrorMessages.GenericFailure,
            };
        }
        finally
        {
            _currentJobId = null;
            _currentJobCts?.Dispose();
            _currentJobCts = null;
            _semaphore.Release();
        }
    }
}
