namespace CloudMigrator.Providers.Graph.Auth;

/// <summary>
/// SharePoint 接続検証の層。
/// </summary>
public enum SharePointVerifyLayer
{
    /// <summary>クレデンシャルの存在確認と App-only 認証確認。</summary>
    Credential,

    /// <summary>SharePoint Drive ID の API 到達確認。</summary>
    Discovery,

    /// <summary>小ファイル書き込み・削除による読み書き権限確認。</summary>
    Preflight,
}

/// <summary>
/// SharePoint 接続検証の各層の結果。
/// </summary>
/// <param name="Layer">検証層。</param>
/// <param name="IsSuccess">成功かどうか。</param>
/// <param name="Detail">補足情報またはエラー詳細。</param>
public sealed record SharePointVerifyCheck(SharePointVerifyLayer Layer, bool IsSuccess, string? Detail);

/// <summary>
/// SharePoint 接続検証の全体結果。
/// </summary>
/// <param name="IsSuccess">全層が成功した場合 true。</param>
/// <param name="Checks">各層の検証結果。</param>
public sealed record SharePointVerifyResult(bool IsSuccess, IReadOnlyList<SharePointVerifyCheck> Checks);

/// <summary>
/// SharePoint 接続を Credential / Discovery / Preflight の 3 層で検証するサービス抽象。
/// </summary>
public interface ISharePointVerifyService
{
    /// <summary>
    /// Credential Verify → Discovery Verify → Migration Preflight の順で検証を実行する。
    /// いずれかの層で失敗した場合、後続の層はスキップされる。
    /// </summary>
    Task<SharePointVerifyResult> VerifyAsync(CancellationToken cancellationToken = default);
}
