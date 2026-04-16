namespace CloudMigrator.Core.Transfer;

/// <summary>
/// 転送レート制御コントローラーの契約。
/// <para>
/// パイプライン層はこのインターフェースのみを参照する（具象クラスへの参照禁止）。
/// DI 設定で <see cref="AdaptiveConcurrencyControllerAdapter"/> または
/// <see cref="RateControlledTransferController"/> のいずれかに切り替え可能。
/// </para>
/// </summary>
public interface ITransferRateController
{
    /// <summary>転送スロットを非同期に取得する。利用可能になるまで待機する。</summary>
    Task AcquireAsync(CancellationToken ct);

    /// <summary>取得済みの転送スロットを解放する。</summary>
    void Release();

    /// <summary>HTTP リクエスト送信直前に呼び出す。インフライトカウンターに加算される。</summary>
    void NotifyRequestSent();

    /// <summary>転送成功時に呼び出す。</summary>
    /// <param name="latency">実際の転送レイテンシ。</param>
    void NotifySuccess(TimeSpan latency);

    /// <summary>
    /// キャンセル・非レート制限エラーなど、成功以外の完了時に呼び出す。
    /// インフライトカウンターを戻すが、成功数 / レイテンシ等のメトリクスには計上しない。
    /// </summary>
    /// <param name="latency">処理時間（参考値）。</param>
    void NotifyCompleted(TimeSpan latency);

    /// <summary>429/503 を受信した際に呼び出す。</summary>
    /// <param name="retryAfter">サーバーから返された Retry-After 値（null の場合は不明）。</param>
    void NotifyRateLimit(TimeSpan? retryAfter);

    /// <summary>リトライを予約した際に呼び出す。インフライトカウンター（Retry 待ち）に加算される。</summary>
    /// <param name="retryAfter">リトライまでの待機時間。</param>
    void NotifyRetryScheduled(TimeSpan retryAfter);

    /// <summary>リトライが完了した際に呼び出す。インフライトカウンター（Retry 待ち）から減算される。</summary>
    void NotifyRetryCompleted();

    /// <summary>現在のインフライト数（実行中 + Retry 待ち）。</summary>
    int CurrentInFlight { get; }

    /// <summary>現在の目標レート（req/sec）。旧 Adapter の場合は現在の並列度を返す。</summary>
    double CurrentRateLimit { get; }
}
