using System.IO;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.State;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Dashboard;

public sealed class TransferStateDbAccessor : ITransferStateDbAccessor
{
    private readonly Func<MigratorOptions> _optionsFactory;
    private readonly string? _explicitDbPath;
    private readonly ILogger<TransferStateDbAccessor> _logger;
    private readonly Dictionary<string, ITransferStateDb> _dbByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private bool _disposed;

    public TransferStateDbAccessor(
        Func<MigratorOptions> optionsFactory,
        string? explicitDbPath,
        ILogger<TransferStateDbAccessor> logger)
    {
        _optionsFactory = optionsFactory;
        _explicitDbPath = string.IsNullOrWhiteSpace(explicitDbPath) ? null : explicitDbPath;
        _logger = logger;
    }

    public Task<ITransferStateDb> GetCurrentAsync(CancellationToken ct)
        => GetForOptionsAsync(_optionsFactory(), ct);

    public async Task<ITransferStateDb> GetForOptionsAsync(MigratorOptions options, CancellationToken ct)
    {
        string dbPath;
        try
        {
            dbPath = ResolveDbPath(options, _explicitDbPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "転送状態 DB パスの解決に失敗しました。DB なしモードで続行します。");
            return NullTransferStateDb.Instance;
        }

        return await GetByPathAsync(dbPath, ct).ConfigureAwait(false);
    }

    internal static string ResolveDbPath(MigratorOptions options, string? explicitDbPath)
    {
        var path = !string.IsNullOrWhiteSpace(explicitDbPath)
            ? explicitDbPath
            : IsDropbox(options.DestinationProvider)
                ? options.Paths.DropboxStateDb
                : options.Paths.SharePointStateDb;

        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("転送状態 DB のパスが設定されていません。");

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }

    private async Task<ITransferStateDb> GetByPathAsync(string dbPath, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_dbByPath.TryGetValue(dbPath, out var existing))
                return existing;
        }

        ITransferStateDb created;
        SqliteTransferStateDb? sqliteDb = null;
        try
        {
            sqliteDb = new SqliteTransferStateDb(dbPath);
            await sqliteDb.InitializeAsync(ct).ConfigureAwait(false);
            created = sqliteDb;
            sqliteDb = null;
        }
        catch (OperationCanceledException)
        {
            if (sqliteDb is not null)
                await sqliteDb.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (sqliteDb is not null)
                await sqliteDb.DisposeAsync().ConfigureAwait(false);
            _logger.LogError(ex, "転送状態 DB の初期化に失敗しました。DB なしモードで続行します。Path: {DbPath}", dbPath);
            created = NullTransferStateDb.Instance;
        }

        ITransferStateDb? disposeTarget = null;
        ITransferStateDb? result = null;
        var throwDisposed = false;

        lock (_gate)
        {
            if (_disposed)
            {
                if (!ReferenceEquals(created, NullTransferStateDb.Instance))
                    disposeTarget = created;
                throwDisposed = true;
            }
            else if (_dbByPath.TryGetValue(dbPath, out var existing))
            {
                if (!ReferenceEquals(created, NullTransferStateDb.Instance))
                    disposeTarget = created;
                result = existing;
            }
            else
            {
                _dbByPath[dbPath] = created;
                result = created;
            }
        }

        if (disposeTarget is not null)
            await disposeTarget.DisposeAsync().ConfigureAwait(false);

        if (throwDisposed)
            throw new ObjectDisposedException(nameof(TransferStateDbAccessor));

        return result!;
    }

    private static bool IsDropbox(string destinationProvider)
        => destinationProvider.Equals("dropbox", StringComparison.OrdinalIgnoreCase);

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TransferStateDbAccessor));
    }

    public async ValueTask DisposeAsync()
    {
        ITransferStateDb[] dbs;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            dbs = _dbByPath.Values
                .Where(db => !ReferenceEquals(db, NullTransferStateDb.Instance))
                .Distinct()
                .ToArray();
            _dbByPath.Clear();
        }

        foreach (var db in dbs)
            await db.DisposeAsync().ConfigureAwait(false);
    }
}
