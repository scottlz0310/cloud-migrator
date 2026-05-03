namespace CloudMigrator.Routes;

/// <summary>
/// 設定ページのセクション識別子。
/// <see cref="IMigrationRouteDescriptor.SettingsSections"/> で各ルートが対応するセクションを示す。
/// 表示制御は SettingsPage 側に残す（#196 で対応）。
/// </summary>
public enum SettingsSectionId
{
    // ── 共通セクション ────────────────────────────────────────────────
    MaxParallelTransfers,
    Timeout,
    RetryPolicy,
    LargeFileThreshold,

    // ── SharePoint 専用セクション ─────────────────────────────────────
    TransferEngine,
    RateControl,
    HybridRateController,
    DynamicParallelism,
    MaxParallelFolderCreations,

    // ── Dropbox 専用セクション ────────────────────────────────────────
    SimpleUploadLimit,
    UploadChunkSize,
    EnableEnsureFolder,
}
