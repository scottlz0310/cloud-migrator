using CloudMigrator.Routes;
using CloudMigrator.Routes.Descriptors;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit.Routes;

// 検証対象: MigrationRouteRegistry  目的: Resolve・All の正確性と重複保護を保証する

public class MigrationRouteRegistryTests
{
    private static MigrationRouteRegistry BuildDefault() =>
        new([new SharePointRouteDescriptor(), new DropboxRouteDescriptor()]);

    [Fact]
    public void Resolve_ShouldReturn_SharePointDescriptor_WhenProviderNameIsSharePoint()
    {
        var registry = BuildDefault();
        registry.Resolve("sharepoint").Should().BeOfType<SharePointRouteDescriptor>();
    }

    [Fact]
    public void Resolve_ShouldReturn_DropboxDescriptor_WhenProviderNameIsDropbox()
    {
        var registry = BuildDefault();
        registry.Resolve("dropbox").Should().BeOfType<DropboxRouteDescriptor>();
    }

    [Theory]
    [InlineData("SharePoint")]
    [InlineData("SHAREPOINT")]
    [InlineData("sharepoint")]
    [InlineData("graph")]   // 旧エイリアス（既存 configs との後方互換）
    [InlineData("GRAPH")]
    public void Resolve_ShouldBeCaseInsensitive_ForSharePoint(string providerName)
    {
        var registry = BuildDefault();
        registry.Resolve(providerName).ProviderName.Should().Be(MigrationProviderNames.SharePoint);
    }

    [Theory]
    [InlineData("Dropbox")]
    [InlineData("DROPBOX")]
    [InlineData("dropbox")]
    public void Resolve_ShouldBeCaseInsensitive_ForDropbox(string providerName)
    {
        var registry = BuildDefault();
        registry.Resolve(providerName).ProviderName.Should().Be(MigrationProviderNames.Dropbox);
    }

    [Fact]
    public void Resolve_ShouldThrow_InvalidOperationException_ForUnknownProvider()
    {
        var registry = BuildDefault();
        var act = () => registry.Resolve("unknown_provider");
        act.Should().Throw<InvalidOperationException>().WithMessage("*unknown_provider*");
    }

    [Fact]
    public void All_ShouldContainAllRegisteredDescriptors()
    {
        var registry = BuildDefault();
        registry.All.Should().HaveCount(2);
    }

    [Fact]
    public void All_ShouldReturn_DescriptorsInStableOrder()
    {
        // 登録順を逆にしても ProviderName 昇順で返ることを確認する
        var registry = new MigrationRouteRegistry(
            [new DropboxRouteDescriptor(), new SharePointRouteDescriptor()]);

        var names = registry.All.Select(d => d.ProviderName).ToList();
        names.Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllDescriptors_ShouldHaveUniqueProviderNames()
    {
        // 同一 ProviderName を持つ descriptor を 2 件渡すと ToDictionary が ArgumentException を投げる
        var act = () => new MigrationRouteRegistry(
            [new SharePointRouteDescriptor(), new SharePointRouteDescriptor()]);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(nameof(SharePointRouteDescriptor), 9)]   // 共通 4 + SP 専用 5
    [InlineData(nameof(DropboxRouteDescriptor), 7)]       // 共通 4 + Dropbox 専用 3
    public void EachDescriptor_ShouldNotHaveDuplicateSettingsSections(string descriptorTypeName, int expectedCount)
    {
        // IReadOnlySet は重複を HashSet 初期化時に除去するため、意図しない重複があると Count が期待値を下回る。
        // 期待件数を明示することで、コンストラクタ初期化子への誤った重複追加を検出する。
        var registry = BuildDefault();
        var descriptor = registry.All.First(d => d.GetType().Name == descriptorTypeName);
        descriptor.SettingsSections.Count.Should().Be(expectedCount);
    }
}
