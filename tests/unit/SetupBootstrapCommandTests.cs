using CloudMigrator.Setup.Cli.Commands;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// Setup bootstrap コマンドのロジックを検証するユニットテスト。
/// </summary>
public sealed class SetupBootstrapCommandTests
{
    // ===== ParseDrives =====

    [Fact]
    public void ParseDrives_ShouldReturnAllDriveEntries_FromValidJson()
    {
        // 検証対象: ParseDrives  目的: 正常なGraph drives応答から全ドライブを取得できること
        var json = """{"value":[{"id":"id-1","name":"Documents"},{"id":"id-2","name":"Archive"},{"id":"id-3","name":"Images"}]}""";

        var drives = BootstrapCommand.ParseDrives(json);

        drives.Should().HaveCount(3);
        drives[0].Should().Be(new DriveEntry("id-1", "Documents"));
        drives[1].Should().Be(new DriveEntry("id-2", "Archive"));
        drives[2].Should().Be(new DriveEntry("id-3", "Images"));
    }

    [Fact]
    public void ParseDrives_ShouldThrow_WhenValuePropertyMissing()
    {
        // 検証対象: ParseDrives  目的: valueプロパティが欠落した応答でInvalidOperationExceptionをスローすること
        var json = """{"notValue":[]}""";

        var act = () => BootstrapCommand.ParseDrives(json);

        act.Should().Throw<InvalidOperationException>().WithMessage("*形式が不正*");
    }

    [Fact]
    public void ParseDrives_ShouldSkipEntries_WhenIdOrNameIsEmpty()
    {
        // 検証対象: ParseDrives  目的: idまたはnameが空のエントリをスキップすること
        var json = """{"value":[{"id":"","name":"NoId"},{"id":"id-2","name":""},{"id":"id-3","name":"Valid"}]}""";

        var drives = BootstrapCommand.ParseDrives(json);

        drives.Should().ContainSingle();
        drives[0].Should().Be(new DriveEntry("id-3", "Valid"));
    }

    // ===== SelectDrive =====

    [Fact]
    public void SelectDrive_ShouldAutoSelect_WhenOnlyOneDriveExists()
    {
        // 検証対象: SelectDrive  目的: ドライブが1件のみの場合、ユーザー入力なしで自動選択されること
        var drives = new[] { new DriveEntry("id-1", "Documents") };
        var console = new TestBootstrapConsole();

        var result = BootstrapCommand.SelectDrive(drives, console);

        result.Should().Be(new DriveEntry("id-1", "Documents"));
        console.Output.Should().Contain(x => x.Contains("自動選択"));
    }

    [Fact]
    public void SelectDrive_ShouldReturnSelectedDrive_ByInputIndex()
    {
        // 検証対象: SelectDrive  目的: 複数ドライブのうち指定番号のドライブが返されること
        var drives = new[]
        {
            new DriveEntry("id-1", "Documents"),
            new DriveEntry("id-2", "Archive"),
            new DriveEntry("id-3", "Images"),
        };
        var console = new TestBootstrapConsole(promptIntResponse: 2); // 2番目を選択

        var result = BootstrapCommand.SelectDrive(drives, console);

        result.Should().Be(new DriveEntry("id-2", "Archive"));
    }

    [Fact]
    public void SelectDrive_ShouldDefaultToDocuments_WhenDocumentsDriveExists()
    {
        // 検証対象: SelectDrive  目的: 複数ドライブにDocumentsが存在する場合、デフォルト候補がDocumentsになること
        var drives = new[]
        {
            new DriveEntry("id-1", "Archive"),
            new DriveEntry("id-2", "Documents"),
            new DriveEntry("id-3", "Images"),
        };
        var console = new TestBootstrapConsole(promptIntResponse: 2); // Documents は2番目

        var result = BootstrapCommand.SelectDrive(drives, console);

        result.Should().Be(new DriveEntry("id-2", "Documents"));
        // デフォルト候補のインデックスが "2" であることをコンソールに出力していること
        console.LastPromptIntDefault.Should().Be(2);
    }

    [Fact]
    public void SelectDrive_ShouldThrow_WhenDrivesListIsEmpty()
    {
        // 検証対象: SelectDrive  目的: ドライブが0件の場合にInvalidOperationExceptionをスローすること
        var console = new TestBootstrapConsole();

        var act = () => BootstrapCommand.SelectDrive([], console);

        act.Should().Throw<InvalidOperationException>().WithMessage("*見つかりませんでした*");
    }
}

/// <summary>テスト用 IBootstrapConsole 実装。</summary>
internal sealed class TestBootstrapConsole : IBootstrapConsole
{
    private readonly int _promptIntResponse;

    public List<string> Output { get; } = [];
    public int? LastPromptIntDefault { get; private set; }

    public TestBootstrapConsole(int promptIntResponse = 1) => _promptIntResponse = promptIntResponse;

    public void WriteLine(string message = "") => Output.Add(message);
    public string Prompt(string label, string? defaultValue = null) => defaultValue ?? "";
    public string PromptMasked(string label) => "test-secret";
    public bool PromptBool(string label, bool defaultValue = false) => defaultValue;

    public int PromptInt(string label, int min, int max, int? defaultValue = null)
    {
        LastPromptIntDefault = defaultValue;
        return _promptIntResponse;
    }
}
