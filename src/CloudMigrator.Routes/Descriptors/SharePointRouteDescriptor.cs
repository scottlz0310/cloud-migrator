using CloudMigrator.Core.Configuration;

namespace CloudMigrator.Routes.Descriptors;

/// <summary>
/// SharePoint Online 移行ルートのメタ情報。
/// </summary>
public sealed class SharePointRouteDescriptor : IMigrationRouteDescriptor
{
    /// <inheritdoc/>
    public string ProviderName => MigrationProviderNames.SharePoint;

    /// <inheritdoc/>
    public string DisplayName => "SharePoint Online";

    /// <inheritdoc/>
    public bool HasFolderCreationPhase => true;

    /// <inheritdoc/>
    public string StateDbPath(MigratorOptions opts) => opts.Paths.SharePointStateDb;

    /// <inheritdoc/>
    public IReadOnlySet<SettingsSectionId> SettingsSections { get; } = new HashSet<SettingsSectionId>
    {
        SettingsSectionId.MaxParallelTransfers,
        SettingsSectionId.Timeout,
        SettingsSectionId.RetryPolicy,
        SettingsSectionId.LargeFileThreshold,
        SettingsSectionId.TransferEngine,
        SettingsSectionId.RateControl,
        SettingsSectionId.HybridRateController,
        SettingsSectionId.DynamicParallelism,
        SettingsSectionId.MaxParallelFolderCreations,
    };
}
