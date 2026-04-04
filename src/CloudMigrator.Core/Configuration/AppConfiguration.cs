using Microsoft.Extensions.Configuration;
using System.Reflection;

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
    /// DOTNET_ENVIRONMENT または ASPNETCORE_ENVIRONMENT が "Development" の場合、
    /// エントリアセンブリの UserSecretsId を使って dotnet user-secrets を自動ロードする。
    /// </summary>
    public static IConfiguration Build(string? configPath = null)
    {
        var resolvedPath = configPath ?? ResolveConfigPath();
        var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                  ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                  ?? "Production";

        var builder = new ConfigurationBuilder()
            .AddJsonFile(resolvedPath, optional: true, reloadOnChange: false);

        if (env.Equals("Development", StringComparison.OrdinalIgnoreCase))
        {
            var entryAssembly = Assembly.GetEntryAssembly();
            if (entryAssembly is not null)
                builder.AddUserSecrets(entryAssembly, optional: true);
        }

        return builder
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
    /// Dropbox アクセストークンを環境変数 MIGRATOR__DROPBOX__ACCESSTOKEN から取得する。
    /// この値は config.json に書かず、必ず環境変数で提供すること。
    /// </summary>
    public static string GetDropboxAccessToken()
        => Environment.GetEnvironmentVariable("MIGRATOR__DROPBOX__ACCESSTOKEN") ?? string.Empty;

    /// <summary>Dropbox リフレッシュトークン。設定時はアクセストークン期限切れ時に自動更新を行う。</summary>
    public static string GetDropboxRefreshToken()
        => Environment.GetEnvironmentVariable("MIGRATOR__DROPBOX__REFRESHTOKEN") ?? string.Empty;

    /// <summary>Dropbox リフレッシュ用クライアント ID。</summary>
    public static string GetDropboxClientId()
        => Environment.GetEnvironmentVariable("MIGRATOR__DROPBOX__CLIENTID") ?? string.Empty;

    /// <summary>Dropbox リフレッシュ用クライアントシークレット。</summary>
    public static string GetDropboxClientSecret()
        => Environment.GetEnvironmentVariable("MIGRATOR__DROPBOX__CLIENTSECRET") ?? string.Empty;

    /// <summary>
    /// config.json を探す。
    /// 優先順位:
    ///   1. %APPDATA%\CloudMigrator\configs\config.json（AppDataPaths.ConfigFile）
    ///   2. 現在のワーキングディレクトリ（開発時: dotnet run はリポジトリルートから実行されることが多い）
    ///   3. AppContext.BaseDirectory から最大 6 階層上まで遡って検索
    ///      （dotnet run 時は bin/Debug/net10.0/ が起点となるため、リポジトリルートに届くよう余裕を持たせる）
    /// </summary>
    public static string ResolveConfigPath()
    {
        // 1. AppData を最優先（バイナリ配布時の標準パス）
        var appDataCandidate = AppDataPaths.ConfigFile;
        if (File.Exists(appDataCandidate))
            return appDataCandidate;

        // 2. ワーキングディレクトリ（開発時フォールバック）
        var cwdCandidate = Path.Combine(Directory.GetCurrentDirectory(), "configs", "config.json");
        if (File.Exists(cwdCandidate))
            return cwdCandidate;

        // 3. BaseDirectory 遡り
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 6; i++)
        {
            var candidate = Path.Combine(dir, "configs", "config.json");
            if (File.Exists(candidate))
                return candidate;

            var parent = Directory.GetParent(dir);
            if (parent is null)
                break;
            dir = parent.FullName;
        }

        // 見つからない場合は AppData パスを返す（optional: true なので起動は継続する）
        return appDataCandidate;
    }

    /// <summary>
    /// 既存の ./configs/config.json を AppData へ自動移行する（初回起動時）。
    /// 条件:
    ///   - ./configs/config.json が存在する
    ///   - %APPDATA%\CloudMigrator\configs\config.json が存在しない
    /// 上記を満たす場合のみ AppData 側へコピーし、ログを出力する。
    /// </summary>
    public static void MigrateConfigIfNeeded()
    {
        var srcPath = Path.Combine(Directory.GetCurrentDirectory(), "configs", "config.json");
        var destPath = AppDataPaths.ConfigFile;

        if (!File.Exists(srcPath) || File.Exists(destPath))
            return;

        try
        {
            AppDataPaths.EnsureDirectoriesExist();
            File.Copy(srcPath, destPath, overwrite: false);
            Console.WriteLine($"[INFO] config migrated: {srcPath} → {destPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] config migration failed: {ex.Message}");
        }
    }
}
