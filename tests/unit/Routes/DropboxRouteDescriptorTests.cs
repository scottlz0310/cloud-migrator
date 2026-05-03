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
    // 検証対象: DropboxRouteDescriptor.ProviderName  目的: "dropbox" を返すことを確認する
    public void ProviderName_ShouldBe_Dropbox()
    {
        var sut = new DropboxRouteDescriptor();
        sut.ProviderName.Should().Be(RouteProviderNames.Dropbox);
    }

    [Fact]
    // 検証対象: DropboxRouteDescriptor.DisplayName  目的: 空でない表示名を返すことを確認する
    public void DisplayName_ShouldNotBeEmpty()
    {
        var sut = new DropboxRouteDescriptor();
        sut.DisplayName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    // 検証対象: DropboxRouteDescriptor.HasFolderCreationPhase  目的: Dropbox はフォルダ作成フェーズを持たないことを確認する
    public void HasFolderCreationPhase_ShouldBeFalse()
    {
        var sut = new DropboxRouteDescriptor();
        sut.HasFolderCreationPhase.Should().BeFalse();
    }

    [Fact]
    // 検証対象: DropboxRouteDescriptor.StateDbPath  目的: MigratorOptions.Paths.DropboxStateDb の値を返すことを確認する
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
    // 検証対象: DropboxRouteDescriptor.SettingsSections  目的: Dropbox 専用セクションが含まれることを確認する
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
    // 検証対象: DropboxRouteDescriptor.SettingsSections  目的: SP 専用セクションが含まれないことを確認する
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
    // 検証対象: DropboxRouteDescriptor.SettingsSections  目的: 共通セクションが含まれることを確認する
    public void SettingsSections_ShouldContain_CommonSections(SettingsSectionId section)
    {
        var sut = new DropboxRouteDescriptor();
        sut.SettingsSections.Should().Contain(section);
    }
}
