namespace CloudMigrator.Core.Credentials;

/// <summary>
/// 認証情報ストア抽象。
/// キーと値のペアとして認証情報（トークン・シークレット等）の永続化を担う。
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// 指定キーの認証情報を取得する。
    /// キーが登録されていない場合は null を返す。
    /// </summary>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定キーの認証情報を保存する。既存の値は上書きされる。
    /// </summary>
    Task SaveAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定キーの認証情報が登録されているかどうかを返す。
    /// </summary>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定キーの認証情報を削除する。キーが存在しない場合は何もしない。
    /// </summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}
