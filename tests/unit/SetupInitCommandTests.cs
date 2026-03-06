using CloudMigrator.Setup.Cli.Commands;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// Setup init コマンドのファイル生成ロジックを検証するユニットテスト。
/// </summary>
public sealed class SetupInitCommandTests : IDisposable
{
    private readonly string _tempDir;

    public SetupInitCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"setup_init_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task WriteTemplateAsync_ShouldSkipExistingFile_WhenForceIsFalse()
    {
        // 検証対象: WriteTemplateAsync  目的: 既存ファイルをforceなしで上書きしないこと
        var filePath = Path.Combine(_tempDir, "sample.env");
        await File.WriteAllTextAsync(filePath, "old-value");
        var results = new List<InitFileResult>();

        await InitCommand.WriteTemplateAsync(
            filePath,
            content: "new-value",
            force: false,
            results,
            CancellationToken.None);

        results.Should().ContainSingle(x => x.Status == InitFileStatus.Skipped);
        (await File.ReadAllTextAsync(filePath)).Should().Be("old-value");
    }

    [Fact]
    public async Task WriteTemplateAsync_ShouldOverwriteExistingFile_WhenForceIsTrue()
    {
        // 検証対象: WriteTemplateAsync  目的: force指定時は既存ファイルを上書きすること
        var filePath = Path.Combine(_tempDir, "config.json");
        await File.WriteAllTextAsync(filePath, "old-config");
        var results = new List<InitFileResult>();

        await InitCommand.WriteTemplateAsync(
            filePath,
            content: "new-config",
            force: true,
            results,
            CancellationToken.None);

        results.Should().ContainSingle(x => x.Status == InitFileStatus.Written);
        (await File.ReadAllTextAsync(filePath)).Should().Be("new-config");
    }
}
