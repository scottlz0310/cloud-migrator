using CloudMigrator.Core.Configuration;
using CloudMigrator.Dashboard;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudMigrator.Tests.Unit;

public sealed class TransferStateDbAccessorTests : IAsyncDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"cloud_migrator_state_accessor_{Guid.NewGuid():N}");
    private readonly List<TransferStateDbAccessor> _accessors = [];

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

    private TransferStateDbAccessor CreateAccessor(Func<MigratorOptions> optionsFactory, string? explicitDbPath = null)
    {
        var accessor = new TransferStateDbAccessor(
            optionsFactory,
            explicitDbPath,
            NullLogger<TransferStateDbAccessor>.Instance);
        _accessors.Add(accessor);
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

    public async ValueTask DisposeAsync()
    {
        foreach (var accessor in _accessors)
            await accessor.DisposeAsync();

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
