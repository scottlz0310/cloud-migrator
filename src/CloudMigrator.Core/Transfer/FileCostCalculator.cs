namespace CloudMigrator.Core.Transfer;

/// <summary>
/// トークンバケットの重み付きコスト算出モード。
/// </summary>
public enum FileCostMode
{
    /// <summary>ファイルサイズを 3 区分（小/中/大）に分類し、離散的なコスト値を返す。</summary>
    Discrete,

    /// <summary>ファイルサイズに比例した連続値を返す（<c>[MinCost, MaxCost]</c> でクランプ）。</summary>
    Continuous,
}

/// <summary>
/// v0.6.0 スループット制御向けの重み付きコスト算出ロジック（#160）。
/// <para>
/// ファイルサイズから <see cref="WeightedTokenBucket"/> が消費するトークン数（cost）を算出する。
/// 小ファイル偏重 / 大ファイル偏重どちらの分布でも実帯域に近い制御を実現するため、
/// サイズに応じてコストを加重する。
/// </para>
/// <para>
/// 離散モード（デフォルト）: 1 MiB 未満 = small、100 MiB 未満 = medium、それ以上 = large。<br/>
/// 連続モード: <c>cost = clamp(ceil(size / scaleBytes), minCost, maxCost)</c>。
/// </para>
/// </summary>
public sealed class FileCostCalculator
{
    /// <summary>小ファイル判定のサイズ上限（1 MiB）。</summary>
    public const long SmallFileThresholdBytes = 1L * 1024 * 1024;

    /// <summary>中ファイル判定のサイズ上限（100 MiB）。</summary>
    public const long MediumFileThresholdBytes = 100L * 1024 * 1024;

    private readonly FileCostMode _mode;
    private readonly int _smallFileCost;
    private readonly int _mediumFileCost;
    private readonly int _largeFileCost;
    private readonly long _costScaleBytes;
    private readonly int _minCost;
    private readonly int _maxCost;

    /// <summary>
    /// 重み付きコスト算出器を初期化する。
    /// </summary>
    /// <param name="mode">算出モード（離散 or 連続）。</param>
    /// <param name="smallFileCost">小ファイル（〜1 MiB）のコスト（1 以上）。</param>
    /// <param name="mediumFileCost">中ファイル（1〜100 MiB）のコスト（<paramref name="smallFileCost"/> 以上）。</param>
    /// <param name="largeFileCost">大ファイル（100 MiB〜）のコスト（<paramref name="mediumFileCost"/> 以上）。</param>
    /// <param name="costScaleBytes">連続モードのスケール係数（1 以上）。`cost = size / scaleBytes`。</param>
    /// <param name="minCost">連続モードの下限コスト（1 以上）。</param>
    /// <param name="maxCost">連続モードの上限コスト（<paramref name="minCost"/> 以上）。</param>
    public FileCostCalculator(
        FileCostMode mode = FileCostMode.Discrete,
        int smallFileCost = 1,
        int mediumFileCost = 5,
        int largeFileCost = 20,
        long costScaleBytes = 10_000_000L,
        int minCost = 1,
        int maxCost = 50)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(smallFileCost, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(mediumFileCost, smallFileCost);
        ArgumentOutOfRangeException.ThrowIfLessThan(largeFileCost, mediumFileCost);
        ArgumentOutOfRangeException.ThrowIfLessThan(costScaleBytes, 1L);
        ArgumentOutOfRangeException.ThrowIfLessThan(minCost, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCost, minCost);

        _mode = mode;
        _smallFileCost = smallFileCost;
        _mediumFileCost = mediumFileCost;
        _largeFileCost = largeFileCost;
        _costScaleBytes = costScaleBytes;
        _minCost = minCost;
        _maxCost = maxCost;
    }

    /// <summary>現在の算出モード。</summary>
    public FileCostMode Mode => _mode;

    /// <summary>
    /// ファイルサイズからトークン消費コストを算出する。
    /// </summary>
    /// <param name="sizeBytes">ファイルサイズ（バイト）。負値は 0 として扱う。</param>
    /// <returns>トークン消費コスト（1 以上）。</returns>
    public int Calculate(long sizeBytes)
    {
        var size = Math.Max(0L, sizeBytes);
        return _mode == FileCostMode.Discrete
            ? CalculateDiscrete(size)
            : CalculateContinuous(size);
    }

    private int CalculateDiscrete(long size) =>
        size < SmallFileThresholdBytes ? _smallFileCost :
        size < MediumFileThresholdBytes ? _mediumFileCost :
        _largeFileCost;

    private int CalculateContinuous(long size)
    {
        var raw = (double)size / _costScaleBytes;
        // int キャストオーバーフロー防止: maxCost に達する前に早期リターン
        if (raw >= _maxCost)
            return _maxCost;
        var rounded = (int)Math.Ceiling(raw);
        return Math.Clamp(rounded < 1 ? 1 : rounded, _minCost, _maxCost);
    }
}
