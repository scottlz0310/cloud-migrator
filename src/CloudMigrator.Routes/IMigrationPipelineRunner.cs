using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.State;

namespace CloudMigrator.Routes;

/// <summary>
/// 移行パイプラインの実行を担うランナーの抽象。
/// 各プロバイダー固有の実装（<c>SharePointPipelineRunner</c> / <c>DropboxPipelineRunner</c> など）が
/// このインターフェースを実装し、DI 経由で <see cref="MigrationPipelineRunnerRegistry"/> に登録する。
/// 新プロバイダー追加時は Runner を追加して DI 登録するだけでよく、<c>App.xaml.cs</c> の変更は不要。
/// </summary>
public interface IMigrationPipelineRunner
{
    /// <summary>
    /// 対応するプロバイダー識別子（例: "sharepoint", "dropbox"）。
    /// <see cref="MigratorOptions.DestinationProvider"/> の値と対応する。
    /// <see cref="RouteProviderNames"/> の定数を使用すること。
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// 移行パイプラインを実行する。
    /// </summary>
    /// <param name="opts">実行時設定。呼び出し元が <c>AppConfiguration.Build()</c> から取得して渡す。</param>
    /// <param name="stateDb">状態 DB。呼び出し元が <c>ITransferStateDbAccessor</c> から解決して渡す。</param>
    /// <param name="ct">キャンセルトークン。</param>
    Task RunAsync(MigratorOptions opts, ITransferStateDb stateDb, CancellationToken ct);
}
