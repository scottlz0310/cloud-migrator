namespace CloudMigrator.Routes;

/// <summary>
/// <see cref="IMigrationRouteDescriptor"/> の登録・解決を担うレジストリ。
/// DI コンテナから <c>IEnumerable&lt;IMigrationRouteDescriptor&gt;</c> を受け取り、
/// プロバイダー名をキーとして解決する。
/// </summary>
public sealed class MigrationRouteRegistry
{
    private readonly IReadOnlyDictionary<string, IMigrationRouteDescriptor> _descriptors;

    /// <summary>
    /// <paramref name="descriptors"/> に重複する <see cref="IMigrationRouteDescriptor.ProviderName"/> が含まれる場合、
    /// 後勝ちで上書きされる（<see cref="Enumerable.ToDictionary"/> の動作に準じる）。
    /// </summary>
    public MigrationRouteRegistry(IEnumerable<IMigrationRouteDescriptor> descriptors)
    {
        _descriptors = descriptors.ToDictionary(
            d => d.ProviderName,
            d => d,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// プロバイダー識別子からルート descriptor を解決する（大文字小文字を問わない）。
    /// </summary>
    /// <exception cref="InvalidOperationException">未登録のプロバイダー名の場合。</exception>
    public IMigrationRouteDescriptor Resolve(string providerName) =>
        _descriptors.TryGetValue(providerName, out var d)
            ? d
            : throw new InvalidOperationException($"未登録のプロバイダーです: '{providerName}'");

    /// <summary>登録済みの全 descriptor を ProviderName 昇順で返す。</summary>
    public IReadOnlyCollection<IMigrationRouteDescriptor> All =>
        _descriptors.Values.OrderBy(d => d.ProviderName, StringComparer.OrdinalIgnoreCase).ToList();
}
