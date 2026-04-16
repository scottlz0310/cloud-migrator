namespace CloudMigrator.Dashboard;

/// <summary>
/// SettingsPage の保存前バリデーションを UI から分離した純粋な検証クラス。
/// 各メソッドはエラーメッセージを返し、null は検証通過を表す。
/// </summary>
public static class SettingsValidation
{
    public static string? ValidateMaxParallelTransfers(int v) =>
        v is < 1 or > 256 ? "最大並行転送数は 1〜256 の範囲で入力してください。" : null;

    public static string? ValidateMaxParallelFolderCreations(int v) =>
        v is < 1 or > 32 ? "最大並行フォルダ作成数は 1〜32 の範囲で入力してください。" : null;

    public static string? ValidateChunkSizeMb(int v) =>
        v is < 1 or > 100 ? "チャンクサイズは 1〜100 MB の範囲で入力してください。" : null;

    public static string? ValidateLargeFileThresholdMb(int v) =>
        v is < 1 or > 100 ? "大ファイル閾値は 1〜100 MB の範囲で入力してください。" : null;

    public static string? ValidateRetryCount(int v) =>
        v is < 0 or > 10 ? "リトライ回数は 0〜10 の範囲で入力してください。" : null;

    public static string? ValidateTimeoutSec(int v) =>
        v is < 30 or > 3600 ? "タイムアウトは 30〜3600 秒の範囲で入力してください。" : null;

    public static string? ValidateRcWindowSecs(bool useRateControl, int shortWindowSec, int longWindowSec) =>
        useRateControl && shortWindowSec >= longWindowSec
            ? "短期ウィンドウ (秒) は中期ウィンドウ (秒) より小さい値にしてください。"
            : null;

    public static string? ValidateRcDecayFactors(bool useRateControl, double minDecayFactor, double maxDecayFactor) =>
        useRateControl && minDecayFactor >= maxDecayFactor
            ? "最小減衰率は最大減衰率より小さい値にしてください。"
            : null;

    public static string? ValidateAdaptiveDecreasePercent(bool useRateControl, bool adaptiveEnabled, int decreasePercent) =>
        !useRateControl && adaptiveEnabled && decreasePercent is < 1 or > 99
            ? "減速率は 1〜99 の範囲で入力してください。"
            : null;

    public static string? ValidateAdaptiveIncreaseIntervalSec(bool useRateControl, bool adaptiveEnabled, int intervalSec) =>
        !useRateControl && adaptiveEnabled && intervalSec is not (30 or 60 or 90 or 120)
            ? "増速インターバルは 30 / 60 / 90 / 120 秒から選択してください。"
            : null;

    public static string? ValidateAdaptiveInitialDegree(bool useRateControl, bool adaptiveEnabled, int initialDegree) =>
        !useRateControl && adaptiveEnabled && initialDegree is < 0 or > 256
            ? "初期並列数は 0〜256 の範囲で入力してください。"
            : null;
}
