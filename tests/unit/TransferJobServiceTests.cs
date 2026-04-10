using CloudMigrator.Core.Transfer;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// 検証対象: TransferJobService
/// 目的: SemaphoreSlim によるジョブ排他制御とインメモリ状態管理を検証する
/// </summary>
public sealed class TransferJobServiceTests
{
    [Fact]
    public async Task TryStartAsync_ReturnsJob_WhenNoJobRunning()
    {
        // 検証対象: TryStartAsync  目的: ジョブがない場合 Pending 状態の TransferJobInfo を返す
        var service = new TransferJobService();

        var job = await service.TryStartAsync();

        job.Should().NotBeNull();
        job!.Status.Should().Be(JobStatus.Pending);
        job.JobId.Should().NotBeNullOrEmpty();
        job.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task TryStartAsync_ReturnsNull_WhenJobAlreadyRunning()
    {
        // 検証対象: TryStartAsync  目的: ジョブ実行中の場合 null を返す（HTTP 409 Conflict 用）
        var tcs = new TaskCompletionSource();
        var service = new TransferJobService(_ => tcs.Task);

        var firstJob = await service.TryStartAsync();
        var secondJob = await service.TryStartAsync();

        firstJob.Should().NotBeNull();
        secondJob.Should().BeNull("実行中のジョブがあるため 2 本目は拒否される");

        tcs.SetResult(); // 後片付け
    }

    [Fact]
    public async Task GetJob_ReturnsJob_WhenExists()
    {
        // 検証対象: GetJob  目的: TryStartAsync で登録したジョブを jobId で取得できる
        var service = new TransferJobService();
        var job = await service.TryStartAsync();

        var found = service.GetJob(job!.JobId);

        found.Should().NotBeNull();
        found!.JobId.Should().Be(job.JobId);
    }

    [Fact]
    public void GetJob_ReturnsNull_WhenNotExists()
    {
        // 検証対象: GetJob  目的: 存在しない jobId に対して null を返す（HTTP 404 NotFound 用）
        var service = new TransferJobService();

        var result = service.GetJob("nonexistent-id");

        result.Should().BeNull();
    }

    [Fact]
    public async Task RunJob_SetsStatusCompleted_WhenWorkSucceeds()
    {
        // 検証対象: RunJobAsync → Completed 遷移  目的: 正常完了後に Completed 状態になる
        var tcs = new TaskCompletionSource();
        var service = new TransferJobService(_ => tcs.Task);
        var job = await service.TryStartAsync();

        tcs.SetResult();
        await WaitUntilAsync(() => service.GetJob(job!.JobId)?.Status == JobStatus.Completed);

        var updated = service.GetJob(job!.JobId);
        updated!.Status.Should().Be(JobStatus.Completed);
        updated.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RunJob_SetsStatusFailed_WhenWorkThrows()
    {
        // 検証対象: RunJobAsync → Failed 遷移  目的: 例外発生時に Failed 状態とエラーメッセージを設定する
        var service = new TransferJobService(
            _ => Task.FromException(new InvalidOperationException("テストエラー")));
        var job = await service.TryStartAsync();

        await WaitUntilAsync(() => service.GetJob(job!.JobId)?.Status == JobStatus.Failed);

        var updated = service.GetJob(job!.JobId);
        updated!.Status.Should().Be(JobStatus.Failed);
        // errorMessage には汎用メッセージが設定される（内部例外の詳細は外部に漏らさない）
        updated.ErrorMessage.Should().NotBeNullOrEmpty();
        updated.ErrorMessage.Should().Be(JobErrorMessages.GenericFailure);
        updated.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RunJob_SetsStatusCancelled_WhenCancelled()
    {
        // 検証対象: RunJobAsync → Cancelled 遷移  目的: キャンセル時に Cancelled 状態になる
        using var cts = new CancellationTokenSource();
        var service = new TransferJobService(async ct => await Task.Delay(Timeout.Infinite, ct));
        var job = await service.TryStartAsync(cts.Token);

        // ジョブが Running 状態に遷移するまで待ってからキャンセル
        await WaitUntilAsync(() => service.GetJob(job!.JobId)?.Status == JobStatus.Running);
        cts.Cancel();
        await WaitUntilAsync(() => service.GetJob(job!.JobId)?.Status == JobStatus.Cancelled);

        var updated = service.GetJob(job!.JobId);
        updated!.Status.Should().Be(JobStatus.Cancelled);
        updated.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TryStartAsync_AllowsNewJob_AfterPreviousCompletes()
    {
        // 検証対象: SemaphoreSlim 解放  目的: 先行ジョブ完了後は新たなジョブを開始できる
        var tcs = new TaskCompletionSource();
        var service = new TransferJobService(_ => tcs.Task);

        var job1 = await service.TryStartAsync();
        var midCheck = await service.TryStartAsync(); // Running 中は null

        tcs.SetResult();
        await WaitUntilAsync(() => service.GetJob(job1!.JobId)?.Status == JobStatus.Completed); // セマフォ解放を待つ

        var job2 = await service.TryStartAsync(); // 解放後は取得できる

        job1.Should().NotBeNull();
        midCheck.Should().BeNull("先行ジョブが実行中のため 2 本目は拒否される");
        job2.Should().NotBeNull("先行ジョブ完了後は新しいジョブを開始できる");
        job2!.JobId.Should().NotBe(job1!.JobId);
    }

    /// <summary>
    /// <paramref name="condition"/> が true になるまで 20ms 間隔でポーリングするヘルパー。
    /// <paramref name="timeoutMs"/> 経過後はタイムアウトして返る（アサーションは呼び元に委ねる）。
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }
}
