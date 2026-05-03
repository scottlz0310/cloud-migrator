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
    // 検証対象: SharePointRouteDescriptor.ProviderName  目的: "sharepoint" を返すことを確認する
    public void ProviderName_ShouldBe_SharePoint()
    {
        var sut = new SharePointRouteDescriptor();
        sut.ProviderName.Should().Be(RouteProviderNames.SharePoint);
    }

    [Fact]
    // 検証対象: SharePointRouteDescriptor.DisplayName  目的: 空でない表示名を返すことを確認する
    public void DisplayName_ShouldNotBeEmpty()
    {
        var sut = new SharePointRouteDescriptor();
        sut.DisplayName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    // 検証対象: SharePointRouteDescriptor.HasFolderCreationPhase  目的: SP はフォルダ作成フェーズを持つことを確認する
    public void HasFolderCreationPhase_ShouldBeTrue()
    {
        var sut = new SharePointRouteDescriptor();
        sut.HasFolderCreationPhase.Should().BeTrue();
    }

    [Fact]
    // 検証対象: SharePointRouteDescriptor.StateDbPath  目的: MigratorOptions.Paths.SharePointStateDb の値を返すことを確認する
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
    // 検証対象: SharePointRouteDescriptor.SettingsSections  目的: SP 専用セクションが含まれることを確認する
    public void SettingsSections_ShouldContain_SharePointSpecificSections(SettingsSectionId section)
    {
        var sut = new SharePointRouteDescriptor();
        sut.SettingsSections.Should().Contain(section);
    }

    [Theory]
    [InlineData(SettingsSectionId.SimpleUploadLimit)]
    [InlineData(SettingsSectionId.UploadChunkSize)]
    [InlineData(SettingsSectionId.EnableEnsureFolder)]
    // 検証対象: SharePointRouteDescriptor.SettingsSections  目的: Dropbox 専用セクションが含まれないことを確認する
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
    // 検証対象: SharePointRouteDescriptor.SettingsSections  目的: 共通セクションが含まれることを確認する
    public void SettingsSections_ShouldContain_CommonSections(SettingsSectionId section)
    {
        var sut = new SharePointRouteDescriptor();
        sut.SettingsSections.Should().Contain(section);
    }
}
