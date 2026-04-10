using CloudMigrator.Core.Wizard;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// WizardStateService のテスト。
/// ファイル I/O を含むため並列実行を無効化する。
/// </summary>
[CollectionDefinition(nameof(WizardStateServiceTests), DisableParallelization = true)]
public sealed class WizardStateServiceCollection { }

/// <summary>
/// WizardStateService のユニットテスト。
/// </summary>
[Collection(nameof(WizardStateServiceTests))]
public sealed class WizardStateServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _stateFile;
    private readonly WizardStateService _sut;

    public WizardStateServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"wizard_state_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _stateFile = Path.Combine(_tempDir, "wizard-state.json");
        _sut = new WizardStateService(_stateFile, NullLogger<WizardStateService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── LoadAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_WhenFileDoesNotExist_ReturnsFreshState()
    {
        // 検証対象: LoadAsync  目的: ファイル不在時は初期状態が返されること
        var state = await _sut.LoadAsync();

        state.IsCompleted.Should().BeFalse();
        state.SelectedRoute.Should().Be(WizardRoute.None);
        state.Step0RouteSelection.Should().Be(WizardStepState.NotStarted);
    }

    [Fact]
    public async Task LoadAsync_WhenJsonIsCorrupt_CreatesBackupAndReturnsFreshState()
    {
        // 検証対象: LoadAsync  目的: JSON パース失敗時にバックアップを作成し初期状態を返すこと
        await File.WriteAllTextAsync(_stateFile, "{ this is not valid json !!!");

        var state = await _sut.LoadAsync();

        state.IsCompleted.Should().BeFalse();

        // バックアップファイルが作成されていること
        var backupPath = Path.Combine(_tempDir, "wizard-state.backup.json");
        File.Exists(backupPath).Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_WhenSchemaVersionIsOld_CreatesBackupAndReturnsFreshState()
    {
        // 検証対象: LoadAsync  目的: 旧 schemaVersion のファイルはバックアップ化されて初期化されること
        var oldVersionJson = """
            {
              "schemaVersion": 0,
              "isCompleted": true,
              "selectedRoute": "None",
              "step0RouteSelection": "Verified",
              "step3DropboxOAuth": "Verified",
              "step4ConnectionTest": "Verified"
            }
            """;
        await File.WriteAllTextAsync(_stateFile, oldVersionJson);

        var state = await _sut.LoadAsync();

        state.IsCompleted.Should().BeFalse("旧バージョンは初期化されるため");

        var backupPath = Path.Combine(_tempDir, "wizard-state.backup.json");
        File.Exists(backupPath).Should().BeTrue("バックアップファイルが作成されること");
    }

    [Fact]
    public async Task LoadAsync_WhenSchemaVersionIsNewer_ReturnsBestEffortState()
    {
        // 検証対象: LoadAsync  目的: 上位 schemaVersion のファイルは既知フィールドを best-effort で読み込むこと
        var futureVersionJson = $$$"""
            {
              "schemaVersion": {{{WizardStateService.CurrentSchemaVersion + 1}}},
              "isCompleted": true,
              "selectedRoute": "OneDriveToDropbox",
              "step0RouteSelection": "Verified",
              "step3DropboxOAuth": "Verified",
              "step4ConnectionTest": "Verified",
              "unknownFutureField": "someValue"
            }
            """;
        await File.WriteAllTextAsync(_stateFile, futureVersionJson);

        var state = await _sut.LoadAsync();

        // バックアップファイルが作成されていないこと（best-effort 読み込みのため）
        var backupPath = Path.Combine(_tempDir, "wizard-state.backup.json");
        File.Exists(backupPath).Should().BeFalse("上位バージョンはバックアップしない");

        // 既知フィールドが読み込まれていること
        state.IsCompleted.Should().BeTrue();
        state.SelectedRoute.Should().Be(WizardRoute.OneDriveToDropbox);
    }

    // ── SaveAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_WhenStateIsInProgress_ConvertsToNotStarted()
    {
        // 検証対象: SaveAsync  目的: InProgress は保存時に NotStarted へ変換されること
        var state = new WizardState
        {
            Step0RouteSelection = WizardStepState.InProgress,
            Step3DropboxOAuth = WizardStepState.InProgress,
        };

        await _sut.SaveAsync(state);

        // ファイルを再ロードして確認
        var loaded = await _sut.LoadAsync();
        loaded.Step0RouteSelection.Should().Be(WizardStepState.NotStarted);
        loaded.Step3DropboxOAuth.Should().Be(WizardStepState.NotStarted);
    }

    [Fact]
    public async Task SaveAsync_WhenStateIsVerified_PersistsAsVerified()
    {
        // 検証対象: SaveAsync  目的: Verified は保存後も Verified で読み出せること
        var state = new WizardState
        {
            SelectedRoute = WizardRoute.OneDriveToDropbox,
            Step0RouteSelection = WizardStepState.Verified,
            Step3DropboxOAuth = WizardStepState.Verified,
            Step4ConnectionTest = WizardStepState.Verified,
            IsCompleted = true,
        };

        await _sut.SaveAsync(state);

        var loaded = await _sut.LoadAsync();
        loaded.SelectedRoute.Should().Be(WizardRoute.OneDriveToDropbox);
        loaded.Step0RouteSelection.Should().Be(WizardStepState.Verified);
        loaded.Step3DropboxOAuth.Should().Be(WizardStepState.Verified);
        loaded.Step4ConnectionTest.Should().Be(WizardStepState.Verified);
        loaded.IsCompleted.Should().BeTrue();
    }

    // ── IsFirstRunAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task IsFirstRunAsync_WhenFileDoesNotExist_ReturnsTrue()
    {
        // 検証対象: IsFirstRunAsync  目的: ファイル不在時は初回起動と判定されること
        var result = await _sut.IsFirstRunAsync();

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsFirstRunAsync_WhenFileExists_ReturnsFalse()
    {
        // 検証対象: IsFirstRunAsync  目的: ファイル存在時は初回起動でないと判定されること
        await _sut.SaveAsync(new WizardState());

        var result = await _sut.IsFirstRunAsync();

        result.Should().BeFalse();
    }
}
