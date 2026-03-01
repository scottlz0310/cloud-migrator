using Microsoft.Extensions.Configuration;

namespace CloudMigrator.Core.Configuration;

/// <summary>
/// 設定ローダー。優先順位: 環境変数 > config.json > デフォルト値（OPS-01）
/// 環境変数キーは MIGRATOR__GRAPH__CLIENTID のように __ 区切りでセクションを表現する（.NET 標準規約）
/// </summary>
public static class AppConfiguration
{
    /// <summary>
    /// IConfiguration を構築する。
    /// configPath が null の場合は configs/config.json を自動検索する。
    /// </summary>
    public static IConfiguration Build(string? configPath = null)
    {
        var resolvedPath = configPath ?? ResolveConfigPath();

        return new ConfigurationBuilder()
            .AddJsonFile(resolvedPath, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
    }

    /// <summary>
    /// クライアントシークレットを環境変数 MIGRATOR__GRAPH__CLIENTSECRET から取得する。
    /// この値は config.json に書かず、必ず環境変数で提供すること（OPS-02, セキュリティ要件）。
    /// </summary>
    public static string GetGraphClientSecret()
        => Environment.GetEnvironmentVariable("MIGRATOR__GRAPH__CLIENTSECRET") ?? string.Empty;

    /// <summary>
    /// 実行ファイルから configs/config.json を探す（最大4階層上まで）
    /// </summary>
    private static string ResolveConfigPath()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 4; i++)
        {
            var candidate = Path.Combine(dir, "configs", "config.json");
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir) ?? dir;
        }
        // 見つからない場合は標準パスを返す（optional: true なので起動は継続する）
        return Path.Combine(AppContext.BaseDirectory, "configs", "config.json");
    }
}
