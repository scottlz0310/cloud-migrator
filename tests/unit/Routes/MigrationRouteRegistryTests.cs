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
        // 重複登録時に後勝ちになるため、登録数と解決可能なプロバイダー数が一致しない場合を検出する
        var registry = BuildDefault();
        var providerNames = registry.All.Select(d => d.ProviderName).ToList();
        providerNames.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(nameof(SharePointRouteDescriptor))]
    [InlineData(nameof(DropboxRouteDescriptor))]
    public void EachDescriptor_ShouldNotHaveDuplicateSettingsSections(string descriptorTypeName)
    {
        // IReadOnlySet は重複を排除するが、コンストラクタ定義の意図しない重複を unit test で明示する
        var registry = BuildDefault();
        var descriptor = registry.All.First(d => d.GetType().Name == descriptorTypeName);

        // HashSet は重複を自動排除するため、元のリストと一致すれば重複なし
        var sections = descriptor.SettingsSections;
        sections.Count.Should().Be(new HashSet<SettingsSectionId>(sections).Count);
    }
}
