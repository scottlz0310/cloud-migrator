using CloudMigrator.Routes;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit.Routes;

/// <summary>
/// 検証対象: MigrationPipelineRunnerRegistry
/// 目的: Resolve の正常ケース・エイリアス正規化・エラーケースを検証する
/// </summary>
public class MigrationPipelineRunnerRegistryTests
{
    private static MigrationPipelineRunnerRegistry CreateRegistry(params IMigrationPipelineRunner[] runners)
        => new(runners);

    // ── Resolve 正常ケース ──────────────────────────────────────────────────

    [Fact]
    public void Resolve_SharePoint_ReturnsSharePointRunner()
    {
        // 検証対象: Resolve("sharepoint")
        // 目的: "sharepoint" プロバイダーに対応する runner が返ること
        var runner = new FakeRunner(RouteProviderNames.SharePoint);
        var registry = CreateRegistry(runner);

        registry.Resolve(RouteProviderNames.SharePoint).Should().BeSameAs(runner);
    }

    [Fact]
    public void Resolve_Dropbox_ReturnsDropboxRunner()
    {
        // 検証対象: Resolve("dropbox")
        // 目的: "dropbox" プロバイダーに対応する runner が返ること
        var runner = new FakeRunner(RouteProviderNames.Dropbox);
        var registry = CreateRegistry(runner);

        registry.Resolve(RouteProviderNames.Dropbox).Should().BeSameAs(runner);
    }

    // ── 大文字小文字を無視して解決できること ────────────────────────────────

    [Theory]
    [InlineData("SharePoint")]
    [InlineData("SHAREPOINT")]
    [InlineData("sharepoint")]
    public void Resolve_CaseInsensitive_SharePoint(string input)
    {
        // 検証対象: Resolve（大文字小文字を問わない）
        // 目的: OrdinalIgnoreCase で解決されること
        var runner = new FakeRunner(RouteProviderNames.SharePoint);
        var registry = CreateRegistry(runner);

        registry.Resolve(input).Should().BeSameAs(runner);
    }

    [Theory]
    [InlineData("Dropbox")]
    [InlineData("DROPBOX")]
    [InlineData("dropbox")]
    public void Resolve_CaseInsensitive_Dropbox(string input)
    {
        // 検証対象: Resolve（大文字小文字を問わない）
        // 目的: OrdinalIgnoreCase で解決されること
        var runner = new FakeRunner(RouteProviderNames.Dropbox);
        var registry = CreateRegistry(runner);

        registry.Resolve(input).Should().BeSameAs(runner);
    }

    // ── "graph" エイリアス正規化 ──────────────────────────────────────────

    [Fact]
    public void Resolve_GraphAlias_ResolvesAsSharePoint()
    {
        // 検証対象: Resolve("graph")
        // 目的: 旧エイリアス "graph" が "sharepoint" に正規化されて解決されること（MigratorOptions の旧表記互換）
        var runner = new FakeRunner(RouteProviderNames.SharePoint);
        var registry = CreateRegistry(runner);

        registry.Resolve("graph").Should().BeSameAs(runner);
    }

    // ── 未登録プロバイダー → InvalidOperationException ──────────────────

    [Fact]
    public void Resolve_UnknownProvider_ThrowsInvalidOperationException()
    {
        // 検証対象: Resolve（未登録プロバイダー）
        // 目的: 未登録のプロバイダー名で InvalidOperationException が発生すること
        var registry = CreateRegistry(new FakeRunner(RouteProviderNames.SharePoint));

        registry.Invoking(r => r.Resolve("unknown"))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*unknown*");
    }

    // ── null → ArgumentNullException ────────────────────────────────────

    [Fact]
    public void Resolve_Null_ThrowsArgumentNullException()
    {
        // 検証対象: Resolve(null)
        // 目的: null に対して ArgumentNullException が発生すること
        var registry = CreateRegistry(new FakeRunner(RouteProviderNames.SharePoint));

        registry.Invoking(r => r.Resolve(null!))
            .Should().Throw<ArgumentNullException>();
    }

    // ── 重複 ProviderName はコンストラクタで検出 ─────────────────────────

    [Fact]
    public void Constructor_DuplicateProviderName_ThrowsArgumentException()
    {
        // 検証対象: コンストラクタ（重複 ProviderName）
        // 目的: 同じ ProviderName を持つ runner を複数登録した場合に ArgumentException が発生すること
        var runner1 = new FakeRunner(RouteProviderNames.SharePoint);
        var runner2 = new FakeRunner(RouteProviderNames.SharePoint);

        var act = () => CreateRegistry(runner1, runner2);
        act.Should().Throw<ArgumentException>().WithMessage("*sharepoint*");
    }

    // ── テスト用スタブ ────────────────────────────────────────────────────

    private sealed class FakeRunner(string providerName) : IMigrationPipelineRunner
    {
        public string ProviderName { get; } = providerName;

        public Task RunAsync(
            CloudMigrator.Core.Configuration.MigratorOptions opts,
            CloudMigrator.Core.State.ITransferStateDb stateDb,
            CancellationToken ct) => Task.CompletedTask;
    }
}
