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
    /// 設定の SHA-256 ハッシュを計算する。
    /// Graph 資格情報・OneDrive/SharePoint ID など転送結果に影響する値を対象とする。
    /// </summary>
    public static string ComputeHash(MigratorOptions options)
    {
        var sb = new StringBuilder();
        sb.Append(options.Graph.ClientId).Append('|');
        sb.Append(options.Graph.TenantId).Append('|');
        sb.Append(options.Graph.OneDriveUserId).Append('|');
        sb.Append(options.Graph.SharePointSiteId).Append('|');
        sb.Append(options.Graph.SharePointDriveId).Append('|');
        sb.Append(options.DestinationRoot);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// ハッシュファイルを読み込み、新しいハッシュと比較する。
    /// ファイルが存在しない場合・内容が異なる場合は true を返す。
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

        return !string.Equals(stored, newHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ハッシュをファイルへ保存する。</summary>
    public static async Task SaveHashAsync(
        string hashFilePath,
        string hash,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(hashFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(hashFilePath, hash, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// キャッシュファイル群（OneDrive・SharePoint キャッシュ）を削除する。
    /// skip_list は削除しない（--full-rebuild 時は呼び出し元が別途削除）。
    /// </summary>
    public static void ClearCaches(PathOptions paths, ILogger logger)
    {
        DeleteIfExists(paths.OneDriveCache, logger);
        DeleteIfExists(paths.SharePointCache, logger);
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
                logger.LogInformation("キャッシュを削除しました: {Path}", filePath);
            }
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "キャッシュ削除に失敗しました: {Path}", filePath);
        }
    }
}
