namespace CloudMigrator.Routes;

/// <summary>
/// <see cref="IMigrationPipelineRunner"/> の登録・解決を担うレジストリ。
/// DI コンテナから <c>IEnumerable&lt;IMigrationPipelineRunner&gt;</c> を受け取り、
/// プロバイダー名をキーとして解決する。
/// </summary>
public sealed class MigrationPipelineRunnerRegistry
{
    private readonly IReadOnlyDictionary<string, IMigrationPipelineRunner> _runners;

    /// <summary>
    /// <paramref name="runners"/> に重複する <see cref="IMigrationPipelineRunner.ProviderName"/> が含まれる場合、
    /// <see cref="ArgumentException"/> を投げて起動を中断する。
    /// </summary>
    public MigrationPipelineRunnerRegistry(IEnumerable<IMigrationPipelineRunner> runners)
    {
        _runners = runners.ToDictionary(
            r => r.ProviderName,
            r => r,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// プロバイダー識別子からランナーを解決する（大文字小文字を問わない）。
    /// 旧エイリアス <c>"graph"</c> は <c>"sharepoint"</c> に正規化してから解決する。
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="providerName"/> が null の場合。</exception>
    /// <exception cref="InvalidOperationException">未登録のプロバイダー名の場合。</exception>
    public IMigrationPipelineRunner Resolve(string providerName)
    {
        ArgumentNullException.ThrowIfNull(providerName);
        // "graph" は "sharepoint" の旧エイリアス（MigrationRouteRegistry と同じ正規化規則）
        var normalized = string.Equals(providerName, "graph", StringComparison.OrdinalIgnoreCase)
            ? RouteProviderNames.SharePoint
            : providerName;
        return _runners.TryGetValue(normalized, out var runner)
            ? runner
            : throw new InvalidOperationException($"未登録のプロバイダーです: '{providerName}'");
    }
}
