using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.State;
using CloudMigrator.Dashboard;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudMigrator.Tests.Unit;

public sealed class TransferStateDbAccessorTests : IAsyncDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"cloud_migrator_state_accessor_{Guid.NewGuid():N}");

    public TransferStateDbAccessorTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task GetForOptionsAsync_DropboxRoute_UsesDropboxStateDb()
    {
        var opts = CreateOptions("dropbox");
        await using var accessor = CreateAccessor(() => opts);

        var db = await accessor.GetForOptionsAsync(opts, CancellationToken.None);
        await db.SaveCheckpointAsync("route", "dropbox", CancellationToken.None);

        File.Exists(opts.Paths.DropboxStateDb).Should().BeTrue();
        File.Exists(opts.Paths.SharePointStateDb).Should().BeFalse();
        (await db.GetCheckpointAsync("route", CancellationToken.None)).Should().Be("dropbox");
    }

    [Fact]
    public async Task GetCurrentAsync_WhenProviderChanges_ReturnsDbForLatestProvider()
    {
        var opts = CreateOptions("sharepoint");
        await using var accessor = CreateAccessor(() => opts);

        var sharePointDb = await accessor.GetCurrentAsync(CancellationToken.None);
        await sharePointDb.SaveCheckpointAsync("route", "sharepoint", CancellationToken.None);

        opts.DestinationProvider = "dropbox";
        var dropboxDb = await accessor.GetCurrentAsync(CancellationToken.None);
        await dropboxDb.SaveCheckpointAsync("route", "dropbox", CancellationToken.None);

        dropboxDb.Should().NotBeSameAs(sharePointDb);
        (await sharePointDb.GetCheckpointAsync("route", CancellationToken.None)).Should().Be("sharepoint");
        (await dropboxDb.GetCheckpointAsync("route", CancellationToken.None)).Should().Be("dropbox");
    }

    [Fact]
    public async Task GetForOptionsAsync_ExplicitDbPath_OverridesProviderStateDbPaths()
    {
        var explicitPath = Path.Combine(_tempDir, "explicit.db");
        var sharePointOpts = CreateOptions("sharepoint");
        var dropboxOpts = CreateOptions("dropbox");
        await using var accessor = CreateAccessor(() => sharePointOpts, explicitPath);

        var sharePointDb = await accessor.GetForOptionsAsync(sharePointOpts, CancellationToken.None);
        var dropboxDb = await accessor.GetForOptionsAsync(dropboxOpts, CancellationToken.None);
        await dropboxDb.SaveCheckpointAsync("route", "explicit", CancellationToken.None);

        dropboxDb.Should().BeSameAs(sharePointDb);
        File.Exists(explicitPath).Should().BeTrue();
        File.Exists(sharePointOpts.Paths.SharePointStateDb).Should().BeFalse();
        File.Exists(dropboxOpts.Paths.DropboxStateDb).Should().BeFalse();
    }

    [Fact]
    public async Task GetForOptionsAsync_InvalidStateDbPath_ReturnsNullTransferStateDb()
    {
        var opts = CreateOptions("dropbox");
        opts.Paths.DropboxStateDb = "";
        await using var accessor = CreateAccessor(() => opts);

        var db = await accessor.GetForOptionsAsync(opts, CancellationToken.None);

        db.Should().BeSameAs(NullTransferStateDb.Instance);
    }

    [Fact]
    public async Task GetForOptionsAsync_InitializationFailure_DoesNotCacheNullTransferStateDb()
    {
        var blockedPath = Path.Combine(_tempDir, "blocked.db");
        Directory.CreateDirectory(blockedPath);
        var opts = CreateOptions("dropbox");
        opts.Paths.DropboxStateDb = blockedPath;
        await using var accessor = CreateAccessor(() => opts);

        var first = await accessor.GetForOptionsAsync(opts, CancellationToken.None);
        Directory.Delete(blockedPath);
        var second = await accessor.GetForOptionsAsync(opts, CancellationToken.None);
        await second.SaveCheckpointAsync("route", "dropbox", CancellationToken.None);

        first.Should().BeSameAs(NullTransferStateDb.Instance);
        second.Should().NotBeSameAs(NullTransferStateDb.Instance);
        (await second.GetCheckpointAsync("route", CancellationToken.None)).Should().Be("dropbox");
    }

    private TransferStateDbAccessor CreateAccessor(Func<MigratorOptions> optionsFactory, string? explicitDbPath = null)
    {
        var accessor = new TransferStateDbAccessor(
            optionsFactory,
            explicitDbPath,
            NullLogger<TransferStateDbAccessor>.Instance);
        return accessor;
    }

    private MigratorOptions CreateOptions(string destinationProvider)
        => new()
        {
            DestinationProvider = destinationProvider,
            Paths = new PathOptions
            {
                DropboxStateDb = Path.Combine(_tempDir, $"{Guid.NewGuid():N}_dropbox.db"),
                SharePointStateDb = Path.Combine(_tempDir, $"{Guid.NewGuid():N}_sharepoint.db"),
            },
        };

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);

        return ValueTask.CompletedTask;
    }
}
