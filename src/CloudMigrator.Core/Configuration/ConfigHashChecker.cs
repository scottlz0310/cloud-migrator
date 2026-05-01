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
        sb.Append(options.DestinationProvider?.Trim().ToLowerInvariant() ?? string.Empty).Append('|');
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
