using CloudMigrator.Core.Configuration;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// AppDataPaths および AppConfiguration の AppData 関連ロジックを検証するユニットテスト。
/// </summary>
public sealed class AppDataPathsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _savedDataDir;

    public AppDataPathsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"appdata_paths_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // テスト中は MIGRATOR_DATA_DIR で AppData を一時ディレクトリへリダイレクト
        _savedDataDir = Environment.GetEnvironmentVariable("MIGRATOR_DATA_DIR");
        Environment.SetEnvironmentVariable("MIGRATOR_DATA_DIR", _tempDir);
    }

    public void Dispose()
    {
        // 環境変数を元に戻す
        Environment.SetEnvironmentVariable("MIGRATOR_DATA_DIR", _savedDataDir);
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void DataDirectory_ShouldUseMigratorDataDir_WhenEnvVarIsSet()
    {
        // 検証対象: AppDataPaths.DataDirectory  目的: MIGRATOR_DATA_DIR 環境変数が優先されること
        AppDataPaths.DataDirectory.Should().Be(_tempDir);
    }

    [Fact]
    public void ConfigFile_ShouldBeUnderDataDirectory()
    {
        // 検証対象: AppDataPaths.ConfigFile  目的: ConfigFile が DataDirectory\configs\config.json であること
        AppDataPaths.ConfigFile.Should().Be(
            Path.Combine(_tempDir, "configs", "config.json"));
    }

    [Fact]
    public void LogsDirectory_ShouldBeUnderDataDirectory()
    {
        // 検証対象: AppDataPaths.LogsDirectory  目的: LogsDirectory が DataDirectory\logs であること
        AppDataPaths.LogsDirectory.Should().Be(Path.Combine(_tempDir, "logs"));
    }

    [Fact]
    public void EnsureDirectoriesExist_ShouldCreateSubDirectories()
    {
        // 検証対象: AppDataPaths.EnsureDirectoriesExist  目的: configs・logs ディレクトリが作成されること
        AppDataPaths.EnsureDirectoriesExist();

        Directory.Exists(AppDataPaths.ConfigDirectory).Should().BeTrue();
        Directory.Exists(AppDataPaths.LogsDirectory).Should().BeTrue();
    }

    [Fact]
    public void MigrateConfigIfNeeded_ShouldCopyConfig_WhenSrcExistsAndDestDoesNot()
    {
        // 検証対象: AppConfiguration.MigrateConfigIfNeeded  目的: 移行元が存在し移行先がない場合にコピーされること
        var originalCwd = Directory.GetCurrentDirectory();
        var srcConfigDir = Path.Combine(_tempDir, "src_cwd", "configs");
        Directory.CreateDirectory(srcConfigDir);
        var srcConfig = Path.Combine(srcConfigDir, "config.json");
        File.WriteAllText(srcConfig, """{"migrated": true}""");

        // AppData 側（MIGRATOR_DATA_DIR 配下）はまだ存在しない
        AppDataPaths.ConfigFile.Should().NotBe(srcConfig);
        File.Exists(AppDataPaths.ConfigFile).Should().BeFalse();

        try
        {
            Directory.SetCurrentDirectory(Path.Combine(_tempDir, "src_cwd"));
            AppConfiguration.MigrateConfigIfNeeded();
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }

        File.Exists(AppDataPaths.ConfigFile).Should().BeTrue();
        File.ReadAllText(AppDataPaths.ConfigFile).Should().Contain("migrated");
    }

    [Fact]
    public void MigrateConfigIfNeeded_ShouldNotOverwrite_WhenDestAlreadyExists()
    {
        // 検証対象: AppConfiguration.MigrateConfigIfNeeded  目的: 移行先が既に存在する場合は上書きしないこと
        AppDataPaths.EnsureDirectoriesExist();
        File.WriteAllText(AppDataPaths.ConfigFile, """{"existing": true}""");

        var originalCwd = Directory.GetCurrentDirectory();
        var srcConfigDir = Path.Combine(_tempDir, "src_cwd2", "configs");
        Directory.CreateDirectory(srcConfigDir);
        File.WriteAllText(Path.Combine(srcConfigDir, "config.json"), """{"new": true}""");

        try
        {
            Directory.SetCurrentDirectory(Path.Combine(_tempDir, "src_cwd2"));
            AppConfiguration.MigrateConfigIfNeeded();
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }

        // 既存ファイルが保持されていること
        File.ReadAllText(AppDataPaths.ConfigFile).Should().Contain("existing");
    }

    [Fact]
    public void ResolveConfigPath_ShouldReturnAppDataPath_WhenConfigExistsInAppData()
    {
        // 検証対象: AppConfiguration.ResolveConfigPath  目的: AppData の config が優先されること
        AppDataPaths.EnsureDirectoriesExist();
        File.WriteAllText(AppDataPaths.ConfigFile, "{}");

        var resolved = AppConfiguration.ResolveConfigPath();

        resolved.Should().Be(AppDataPaths.ConfigFile);
    }
}
