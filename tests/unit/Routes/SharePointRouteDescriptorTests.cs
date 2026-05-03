using CloudMigrator.Core.Configuration;
using CloudMigrator.Routes;
using CloudMigrator.Routes.Descriptors;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit.Routes;

// 検証対象: SharePointRouteDescriptor  目的: 各プロパティと StateDbPath の正確性を保証する

public class SharePointRouteDescriptorTests
{
    private static MigratorOptions BuildOptions(string sharePointDb = "sp.db") =>
        new() { Paths = new PathOptions { SharePointStateDb = sharePointDb } };

    [Fact]
    public void ProviderName_ShouldBe_SharePoint()
    {
        var sut = new SharePointRouteDescriptor();
        sut.ProviderName.Should().Be(MigrationProviderNames.SharePoint);
    }

    [Fact]
    public void DisplayName_ShouldNotBeEmpty()
    {
        var sut = new SharePointRouteDescriptor();
        sut.DisplayName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void HasFolderCreationPhase_ShouldBeTrue()
    {
        var sut = new SharePointRouteDescriptor();
        sut.HasFolderCreationPhase.Should().BeTrue();
    }

    [Fact]
    public void StateDbPath_ShouldReturn_SharePointStateDb()
    {
        var sut = new SharePointRouteDescriptor();
        var opts = BuildOptions("custom_sp.db");

        sut.StateDbPath(opts).Should().Be("custom_sp.db");
    }

    [Theory]
    [InlineData(SettingsSectionId.TransferEngine)]
    [InlineData(SettingsSectionId.RateControl)]
    [InlineData(SettingsSectionId.HybridRateController)]
    [InlineData(SettingsSectionId.DynamicParallelism)]
    [InlineData(SettingsSectionId.MaxParallelFolderCreations)]
    public void SettingsSections_ShouldContain_SharePointSpecificSections(SettingsSectionId section)
    {
        var sut = new SharePointRouteDescriptor();
        sut.SettingsSections.Should().Contain(section);
    }

    [Theory]
    [InlineData(SettingsSectionId.SimpleUploadLimit)]
    [InlineData(SettingsSectionId.UploadChunkSize)]
    [InlineData(SettingsSectionId.EnableEnsureFolder)]
    public void SettingsSections_ShouldNotContain_DropboxSpecificSections(SettingsSectionId section)
    {
        var sut = new SharePointRouteDescriptor();
        sut.SettingsSections.Should().NotContain(section);
    }

    [Theory]
    [InlineData(SettingsSectionId.MaxParallelTransfers)]
    [InlineData(SettingsSectionId.Timeout)]
    [InlineData(SettingsSectionId.RetryPolicy)]
    [InlineData(SettingsSectionId.FileTransfer)]
    public void SettingsSections_ShouldContain_CommonSections(SettingsSectionId section)
    {
        var sut = new SharePointRouteDescriptor();
        sut.SettingsSections.Should().Contain(section);
    }
}
