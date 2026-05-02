using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Core.Configuration;

/// <summary>
/// 設定変更を SHA-256 ハッシュで検知する（FR-10）。
/// キャッシュファイルは設定が変わった場合に無効化する。
/// </summary>
public static class ConfigHashChecker
{
    /// <summary>
    /// ハッシュファイルのスキーマバージョンプレフィックス。
    /// このバージョンが付いていない旧形式ファイルは「変更なし」として扱い、
    /// 次回転送成功時に新形式へ自動移行する（アップグレード時の誤 DB 初期化を防ぐ）。
    /// </summary>
    private const string HashVersionPrefix = "v2:";
    /// <summary>
    /// 設定の SHA-256 ハッシュを計算する。
    /// Graph/Dropbox の識別子・ルートパスなど転送結果に影響する値を対象とする。
    /// </summary>
    public static string ComputeHash(MigratorOptions options)
    {
        var sb = new StringBuilder();
        sb.Append(options.Graph.ClientId).Append('|');
        sb.Append(options.Graph.TenantId).Append('|');
        sb.Append(options.Graph.OneDriveUserId).Append('|');
        sb.Append(options.Graph.SharePointSiteId).Append('|');
        sb.Append(options.Graph.SharePointDriveId).Append('|');
        // Dropbox.RootPath / DestinationRoot は Trim + バックスラッシュ→スラッシュ変換 +
        // 末尾スラッシュ除去で正規化し、表記揺れによるハッシュ差異を防ぐ
        sb.Append(
            (options.Dropbox.RootPath ?? string.Empty)
                .Trim()
                .Replace('\\', '/')
                .Trim('/')).Append('|');
        sb.Append(
            (options.DestinationRoot ?? string.Empty)
                .Trim()
                .Replace('\\', '/')
                .Trim('/')).Append('|');
        // DestinationProvider（#198）: sharepoint/dropbox のルート変更を検知する
        // "graph" は "sharepoint" の旧エイリアス（ConfigurationService.NormalizeProvider と同じ規則）
        var normalizedProvider = options.DestinationProvider?.Trim().ToLowerInvariant() switch
        {
            "sharepoint" or "graph" => "sharepoint",
            "dropbox" => "dropbox",
            var v => v ?? string.Empty
        };
        sb.Append(normalizedProvider).Append('|');
        // OneDriveSourceFolder（#198）: 転送元フォルダパス変更を検知する
        sb.Append(
            (options.Graph.OneDriveSourceFolder ?? string.Empty)
                .Trim()
                .Replace('\\', '/')
                .Trim('/'));

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// ハッシュファイルを読み込み、新しいハッシュと比較する。
    /// ファイルが存在しない場合は true を返す。
    /// 旧形式（バージョンプレフィックスなし）のファイルはアップグレード時の誤 DB 初期化を防ぐため
    /// 変更なし（false）として扱い、次回転送成功時に新形式へ移行する。
    /// </summary>
    public static async Task<bool> HasChangedAsync(
        string hashFilePath,
        string newHash,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(hashFilePath))
            return true;

        var stored = (await File.ReadAllTextAsync(hashFilePath, cancellationToken)
            .ConfigureAwait(false)).Trim();

        // 旧形式（バージョンプレフィックスなし）: 変更なしとして扱い新形式への移行を待つ
        if (!stored.StartsWith(HashVersionPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var storedHash = stored[HashVersionPrefix.Length..];
        return !string.Equals(storedHash, newHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ハッシュをバージョンプレフィックス付きでファイルへ保存する。</summary>
    public static async Task SaveHashAsync(
        string hashFilePath,
        string hash,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(hashFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(hashFilePath, HashVersionPrefix + hash, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// キャッシュファイル群（OneDrive・SharePoint・Dropbox キャッシュ・skip_list）をすべて削除する。
    /// 設定変更時に呼び出され、FR-10 に従ってキャッシュと skip_list を一括無効化する。
    /// </summary>
    public static void ClearAll(PathOptions paths, ILogger logger)
    {
        DeleteIfExists(paths.OneDriveCache, logger);
        DeleteIfExists(paths.SharePointCache, logger);
        DeleteIfExists(paths.DropboxCache, logger);
        DeleteIfExists(paths.SkipList, logger);
    }

    /// <summary>スキップリストを削除する（--full-rebuild 時に使用）。</summary>
    public static void ClearSkipList(PathOptions paths, ILogger logger)
    {
        DeleteIfExists(paths.SkipList, logger);
    }

    private static void DeleteIfExists(string filePath, ILogger logger)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                logger.LogInformation("ファイルを削除しました: {Path}", filePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "ファイル削除に失敗しました: {Path}", filePath);
        }
    }
}
