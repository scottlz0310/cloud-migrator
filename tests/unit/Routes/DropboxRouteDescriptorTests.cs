using CloudMigrator.Core.Configuration;
using CloudMigrator.Routes;
using CloudMigrator.Routes.Descriptors;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit.Routes;

// 検証対象: DropboxRouteDescriptor  目的: 各プロパティと StateDbPath の正確性を保証する

public class DropboxRouteDescriptorTests
{
    private static MigratorOptions BuildOptions(string dropboxDb = "dropbox.db") =>
        new() { Paths = new PathOptions { DropboxStateDb = dropboxDb } };

    [Fact]
    public void ProviderName_ShouldBe_Dropbox()
    {
        var sut = new DropboxRouteDescriptor();
        sut.ProviderName.Should().Be(MigrationProviderNames.Dropbox);
    }

    [Fact]
    public void DisplayName_ShouldNotBeEmpty()
    {
        var sut = new DropboxRouteDescriptor();
        sut.DisplayName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void HasFolderCreationPhase_ShouldBeFalse()
    {
        var sut = new DropboxRouteDescriptor();
        sut.HasFolderCreationPhase.Should().BeFalse();
    }

    [Fact]
    public void StateDbPath_ShouldReturn_DropboxStateDb()
    {
        var sut = new DropboxRouteDescriptor();
        var opts = BuildOptions("custom_dropbox.db");

        sut.StateDbPath(opts).Should().Be("custom_dropbox.db");
    }

    [Theory]
    [InlineData(SettingsSectionId.SimpleUploadLimit)]
    [InlineData(SettingsSectionId.UploadChunkSize)]
    [InlineData(SettingsSectionId.EnableEnsureFolder)]
    public void SettingsSections_ShouldContain_DropboxSpecificSections(SettingsSectionId section)
    {
        var sut = new DropboxRouteDescriptor();
        sut.SettingsSections.Should().Contain(section);
    }

    [Theory]
    [InlineData(SettingsSectionId.TransferEngine)]
    [InlineData(SettingsSectionId.RateControl)]
    [InlineData(SettingsSectionId.HybridRateController)]
    [InlineData(SettingsSectionId.DynamicParallelism)]
    [InlineData(SettingsSectionId.MaxParallelFolderCreations)]
    public void SettingsSections_ShouldNotContain_SharePointSpecificSections(SettingsSectionId section)
    {
        var sut = new DropboxRouteDescriptor();
        sut.SettingsSections.Should().NotContain(section);
    }

    [Theory]
    [InlineData(SettingsSectionId.MaxParallelTransfers)]
    [InlineData(SettingsSectionId.Timeout)]
    [InlineData(SettingsSectionId.RetryPolicy)]
    [InlineData(SettingsSectionId.FileTransfer)]
    public void SettingsSections_ShouldContain_CommonSections(SettingsSectionId section)
    {
        var sut = new DropboxRouteDescriptor();
        sut.SettingsSections.Should().Contain(section);
    }
}
