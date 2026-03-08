using CloudMigrator.Core.Configuration;
using CloudMigrator.Setup.Cli.Commands;
using FluentAssertions;
using System.Text.Json;

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
    public void ParseDrives_ShouldThrow_WhenJsonIsInvalid()
    {
        // 検証対象: ParseDrives  目的: 不正なJSONでInvalidOperationExceptionにラップされてスローされること
        var json = "not-json{{{";

        var act = () => BootstrapCommand.ParseDrives(json);

        act.Should().Throw<InvalidOperationException>().WithMessage("*JSON 解析に失敗*");
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

    // ===== ApplyBootstrapEnvTemplate (OneDriveSourceFolder) =====

    [Fact]
    public void ApplyBootstrapEnvTemplate_ShouldSetSourceFolder_WhenFolderNotFromEnv()
    {
        // 検証対象: ApplyBootstrapEnvTemplate  目的: 手動入力のフォルダパスが .env テンプレートに反映されること
        var template = InitCommand.DefaultEnvTemplate;

        var result = BootstrapCommand.ApplyBootstrapEnvTemplate(
            template,
            effectiveOneDriveUser: "user@example.com", upnFromEnv: false,
            oneDriveSourceFolder: "Documents/Projects", sourceFolderFromEnv: false,
            siteId: "site-1", driveId: "drive-1",
            clientId: "cid-1", clientIdFromEnv: false,
            tenantId: "tid-1", tenantIdFromEnv: false,
            secretFromEnv: false);

        result.Should().Contain("MIGRATOR__GRAPH__ONEDRIVESOURCEFOLDER=Documents/Projects");
    }

    [Fact]
    public void ApplyBootstrapEnvTemplate_ShouldCommentOutSourceFolder_WhenFolderFromEnv()
    {
        // 検証対象: ApplyBootstrapEnvTemplate  目的: env変数由来のフォルダはコメントアウトされること
        var template = InitCommand.DefaultEnvTemplate;

        var result = BootstrapCommand.ApplyBootstrapEnvTemplate(
            template,
            effectiveOneDriveUser: "user@example.com", upnFromEnv: false,
            oneDriveSourceFolder: "Documents/Projects", sourceFolderFromEnv: true,
            siteId: "site-1", driveId: "drive-1",
            clientId: "cid-1", clientIdFromEnv: false,
            tenantId: "tid-1", tenantIdFromEnv: false,
            secretFromEnv: false);

        result.Should().Contain("# MIGRATOR__GRAPH__ONEDRIVESOURCEFOLDER=");
    }

    [Fact]
    public void ApplyBootstrapEnvTemplate_ShouldNotSetSourceFolder_WhenFolderIsEmpty()
    {
        // 検証対象: ApplyBootstrapEnvTemplate  目的: フォルダ未指定（空文字）の場合、プレースホルダーのまま変更されないこと
        var template = InitCommand.DefaultEnvTemplate;

        var result = BootstrapCommand.ApplyBootstrapEnvTemplate(
            template,
            effectiveOneDriveUser: "user@example.com", upnFromEnv: false,
            oneDriveSourceFolder: "", sourceFolderFromEnv: false,
            siteId: "site-1", driveId: "drive-1",
            clientId: "cid-1", clientIdFromEnv: false,
            tenantId: "tid-1", tenantIdFromEnv: false,
            secretFromEnv: false);

        // 空フォルダ指定時はキーを書き換えずプレースホルダー（空）のまま維持
        result.Should().Contain("MIGRATOR__GRAPH__ONEDRIVESOURCEFOLDER=");
        result.Should().NotContain("# MIGRATOR__GRAPH__ONEDRIVESOURCEFOLDER=");
    }

    // ===== LoadConfigJsonOptions =====

    [Fact]
    public void LoadConfigJsonOptions_ShouldReturnDefaults_WhenFileNotFound()
    {
        // 検証対象: LoadConfigJsonOptions  目的: config.json が存在しない場合にデフォルト値を返すこと
        var result = BootstrapCommand.LoadConfigJsonOptions("nonexistent_config.json");

        result.Should().NotBeNull();
        result.Graph.ClientId.Should().BeEmpty();
        result.Graph.SharePointSiteId.Should().BeEmpty();
    }

    [Fact]
    public void LoadConfigJsonOptions_ShouldReadValues_WhenConfigJsonExists()
    {
        // 検証対象: LoadConfigJsonOptions  目的: config.json が存在する場合に値を正しく読み込むこと
        var configPath = Path.GetTempFileName();
        try
        {
            var config = new
            {
                migrator = new
                {
                    graph = new
                    {
                        clientId = "test-client-id",
                        tenantId = "test-tenant-id",
                        sharePointSiteId = "test-site-id",
                        sharePointDriveId = "test-drive-id",
                        oneDriveUserId = "user@example.com",
                        oneDriveSourceFolder = "Documents/Archive"
                    }
                }
            };
            File.WriteAllText(configPath, JsonSerializer.Serialize(config));

            var result = BootstrapCommand.LoadConfigJsonOptions(configPath);

            result.Graph.ClientId.Should().Be("test-client-id");
            result.Graph.TenantId.Should().Be("test-tenant-id");
            result.Graph.SharePointSiteId.Should().Be("test-site-id");
            result.Graph.SharePointDriveId.Should().Be("test-drive-id");
            result.Graph.OneDriveUserId.Should().Be("user@example.com");
            result.Graph.OneDriveSourceFolder.Should().Be("Documents/Archive");
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public void LoadConfigJsonOptions_ShouldIgnoreEnvVariables_WhenReadingConfigJson()
    {
        // 検証対象: LoadConfigJsonOptions  目的: config.json 読み込みが環境変数の影響を受けないこと
        var configPath = Path.GetTempFileName();
        try
        {
            var config = new { migrator = new { graph = new { clientId = "json-client-id" } } };
            File.WriteAllText(configPath, JsonSerializer.Serialize(config));

            Environment.SetEnvironmentVariable("MIGRATOR__GRAPH__CLIENTID", "env-client-id");
            var result = BootstrapCommand.LoadConfigJsonOptions(configPath);
            Environment.SetEnvironmentVariable("MIGRATOR__GRAPH__CLIENTID", null);

            // config.json のみを読み込むため、env変数の値は反映されない
            result.Graph.ClientId.Should().Be("json-client-id");
        }
        finally
        {
            File.Delete(configPath);
            Environment.SetEnvironmentVariable("MIGRATOR__GRAPH__CLIENTID", null);
        }
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
    public string PromptMasked(string label, bool hasExistingValue = false) => "test-secret";
    public bool PromptBool(string label, bool defaultValue = false) => defaultValue;

    public int PromptInt(string label, int min, int max, int? defaultValue = null)
    {
        LastPromptIntDefault = defaultValue;
        return _promptIntResponse;
    }
}
