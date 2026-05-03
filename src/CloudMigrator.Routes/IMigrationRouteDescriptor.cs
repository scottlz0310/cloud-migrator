using CloudMigrator.Core.Configuration;

namespace CloudMigrator.Routes;

/// <summary>
/// 移行ルート（転送先プロバイダー）のメタ情報を提供する。
/// 各プロバイダー固有の情報をこのインターフェースに集約し、
/// Dashboard / Settings がプロバイダーを直接知らない構造への移行を可能にする（#196 以降で置換）。
/// </summary>
public interface IMigrationRouteDescriptor
{
    /// <summary>
    /// プロバイダー識別子（例: "sharepoint", "dropbox"）。
    /// <see cref="MigratorOptions.DestinationProvider"/> の値と対応する。
    /// <see cref="RouteProviderNames"/> の定数を使用する。
    /// </summary>
    string ProviderName { get; }

    /// <summary>ユーザー向け表示名（例: "SharePoint Online", "Dropbox"）。</summary>
    string DisplayName { get; }

    /// <summary>
    /// フォルダ先行作成フェーズを持つか。
    /// Dashboard のフェーズ表示判定に使用する（現在は isDropbox 分岐で代替。#196 で移行）。
    /// </summary>
    bool HasFolderCreationPhase { get; }

    /// <summary>
    /// state DB のファイルパスを返す。
    /// <paramref name="opts"/> から <see cref="MigratorOptions.Paths"/> 経由で解決する。
    /// </summary>
    string StateDbPath(MigratorOptions opts);

    /// <summary>
    /// 設定ページで表示するセクション識別子のセット（順序なし・重複なし）。
    /// SettingsPage 側で <c>Contains(SettingsSectionId.Xxx)</c> による表示可否判定に使用する（#196 で移行）。
    /// </summary>
    IReadOnlySet<SettingsSectionId> SettingsSections { get; }
}
