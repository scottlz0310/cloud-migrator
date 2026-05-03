using System.Collections.Frozen;
using CloudMigrator.Core.Configuration;

namespace CloudMigrator.Routes.Descriptors;

/// <summary>
/// Dropbox 移行ルートのメタ情報。
/// </summary>
public sealed class DropboxRouteDescriptor : IMigrationRouteDescriptor
{
    /// <inheritdoc/>
    public string ProviderName => RouteProviderNames.Dropbox;

    /// <inheritdoc/>
    public string DisplayName => "Dropbox";

    /// <inheritdoc/>
    public bool HasFolderCreationPhase => false;

    /// <inheritdoc/>
    public string StateDbPath(MigratorOptions opts) => opts.Paths.DropboxStateDb;

    /// <inheritdoc/>
    public IReadOnlySet<SettingsSectionId> SettingsSections { get; } = FrozenSet.Create(
        SettingsSectionId.MaxParallelTransfers,
        SettingsSectionId.Timeout,
        SettingsSectionId.RetryPolicy,
        SettingsSectionId.FileTransfer,
        SettingsSectionId.SimpleUploadLimit,
        SettingsSectionId.UploadChunkSize,
        SettingsSectionId.EnableEnsureFolder
    );
}
