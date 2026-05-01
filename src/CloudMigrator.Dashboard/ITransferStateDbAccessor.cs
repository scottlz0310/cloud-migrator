using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.State;

namespace CloudMigrator.Dashboard;

public interface ITransferStateDbAccessor : IAsyncDisposable
{
    Task<ITransferStateDb> GetCurrentAsync(CancellationToken ct);

    Task<ITransferStateDb> GetForOptionsAsync(MigratorOptions options, CancellationToken ct);
}
