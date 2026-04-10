using System.Text.Json;
using CloudMigrator.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Core.Wizard;

/// <summary>
/// wizard-state.json を <c>%APPDATA%\CloudMigrator\</c> に読み書きする <see cref="IWizardStateService"/> 実装。
/// <list type="bullet">
///   <item><description>未知の <c>schemaVersion</c> / パース失敗時: バックアップ化 → 初期化。</description></item>
///   <item><description><see cref="WizardStepState.InProgress"/> はファイルに書き出さない（<see cref="WizardStepState.NotStarted"/> に戻してから保存）。</description></item>
/// </list>
/// </summary>
public sealed class WizardStateService : IWizardStateService
{
    /// <summary>現在のスキーマバージョン。</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _stateFilePath;
    private readonly ILogger<WizardStateService> _logger;

    public WizardStateService(ILogger<WizardStateService> logger)
        : this(AppDataPaths.WizardStateFile(), logger)
    {
    }

    /// <summary>テスト用コンストラクタ。ファイルパスを直接指定する。</summary>
    internal WizardStateService(string stateFilePath, ILogger<WizardStateService> logger)
    {
        _stateFilePath = stateFilePath;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<WizardState> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_stateFilePath))
        {
            _logger.LogDebug("wizard-state.json が存在しません。初期状態を返します。");
            return new WizardState();
        }

        try
        {
            await using var stream = File.OpenRead(_stateFilePath);
            var state = await JsonSerializer.DeserializeAsync<WizardState>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (state is null)
            {
                _logger.LogWarning("wizard-state.json のデシリアライズ結果が null でした。初期化します。");
                await BackupAndResetAsync(cancellationToken).ConfigureAwait(false);
                return new WizardState();
            }

            if (state.SchemaVersion != CurrentSchemaVersion)
            {
                _logger.LogWarning(
                    "wizard-state.json の schemaVersion={Version} は未知です（現在の対応: {Current}）。バックアップ化して初期化します。",
                    state.SchemaVersion, CurrentSchemaVersion);
                await BackupAndResetAsync(cancellationToken).ConfigureAwait(false);
                return new WizardState();
            }

            return state;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "wizard-state.json のパースに失敗しました。バックアップ化して初期化します。");
            await BackupAndResetAsync(cancellationToken).ConfigureAwait(false);
            return new WizardState();
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "wizard-state.json の読み込みに失敗しました。初期状態を返します。");
            return new WizardState();
        }
    }

    /// <inheritdoc/>
    public async Task SaveAsync(WizardState state, CancellationToken cancellationToken = default)
    {
        var toSave = state.ToSafeForPersistence();

        try
        {
            EnsureDirectoryExists();
            var tmpPath = _stateFilePath + ".tmp";
            await using (var stream = File.Create(tmpPath))
            {
                await JsonSerializer.SerializeAsync(stream, toSave, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            // アトミックな置き換え
            File.Move(tmpPath, _stateFilePath, overwrite: true);
            _logger.LogDebug("wizard-state.json を保存しました。");
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "wizard-state.json の保存に失敗しました。");
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        var fresh = new WizardState();
        await SaveAsync(fresh, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("ウィザード状態をリセットしました。");
    }

    /// <inheritdoc/>
    public Task<bool> IsFirstRunAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(!File.Exists(_stateFilePath));

    // ── プライベートヘルパー ─────────────────────────────────────────────

    private async Task BackupAndResetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var backupPath = Path.ChangeExtension(_stateFilePath, null) + ".backup.json";
            File.Copy(_stateFilePath, backupPath, overwrite: true);
            _logger.LogInformation("破損した wizard-state.json を {BackupPath} にバックアップしました。", backupPath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "wizard-state.json のバックアップに失敗しました。");
        }

        await ResetAsync(cancellationToken).ConfigureAwait(false);
    }

    private void EnsureDirectoryExists()
    {
        var dir = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
}
