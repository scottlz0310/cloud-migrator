namespace CloudMigrator.Core.Configuration;

/// <summary>
/// アプリケーションデータディレクトリのパス定数。
/// バイナリ: %LOCALAPPDATA%\Programs\CloudMigrator\
/// データ  : %APPDATA%\CloudMigrator\（設定・ログ・DB）
///
/// 環境変数 MIGRATOR_DATA_DIR を設定すると、%APPDATA%\CloudMigrator\ の代わりにそのパスを使用する。
/// CI 環境やテストで任意ディレクトリへのリダイレクトに利用できる。
/// </summary>
public static class AppDataPaths
{
    /// <summary>
    /// データルート。
    /// MIGRATOR_DATA_DIR 環境変数が設定されている場合はその値を使用する。
    /// 未設定の場合は %APPDATA%\CloudMigrator\。
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            var envVal = Environment.GetEnvironmentVariable("MIGRATOR_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(envVal))
                return Path.GetFullPath(envVal);

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CloudMigrator");
        }
    }

    /// <summary>ログ・DB 格納ディレクトリ: {DataDirectory}\logs\</summary>
    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>設定ファイルディレクトリ: {DataDirectory}\configs\</summary>
    public static string ConfigDirectory => Path.Combine(DataDirectory, "configs");

    /// <summary>config.json のフルパス: {DataDirectory}\configs\config.json</summary>
    public static string ConfigFile => Path.Combine(ConfigDirectory, "config.json");

    /// <summary>指定ファイル名のログパスを返す。</summary>
    public static string LogFile(string fileName) => Path.Combine(LogsDirectory, fileName);

    /// <summary>AppData 配下の必須ディレクトリをすべて作成する（冪等）。</summary>
    public static void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
