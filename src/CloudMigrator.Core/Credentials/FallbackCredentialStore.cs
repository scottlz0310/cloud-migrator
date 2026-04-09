namespace CloudMigrator.Core.Credentials;

/// <summary>
/// プライマリストアが null を返した場合にフォールバックストアを試みる
/// <see cref="ICredentialStore"/> 実装。
/// プライマリが例外をスローした場合はサイレントフォールバックせず例外を伝播させる。
/// </summary>
public sealed class FallbackCredentialStore : ICredentialStore
{
    private readonly ICredentialStore _primary;
    private readonly ICredentialStore _fallback;

    public FallbackCredentialStore(ICredentialStore primary, ICredentialStore fallback)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(fallback);
        _primary = primary;
        _fallback = fallback;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// プライマリが null を返した場合のみフォールバックを試みる。
    /// プライマリがエラーをスローした場合はサイレントフォールバックしない。
    /// </remarks>
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var value = await _primary.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (value is not null)
            return value;

        return await _fallback.GetAsync(key, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>プライマリに委譲する。</remarks>
    public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
        => _primary.SaveAsync(key, value, cancellationToken);

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        if (await _primary.ExistsAsync(key, cancellationToken).ConfigureAwait(false))
            return true;

        return await _fallback.ExistsAsync(key, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>プライマリに委譲する。</remarks>
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        => _primary.DeleteAsync(key, cancellationToken);
}
