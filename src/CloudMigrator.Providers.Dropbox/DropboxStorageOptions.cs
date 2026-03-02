namespace CloudMigrator.Providers.Dropbox;

/// <summary>
/// DropboxStorageProvider の動作設定。
/// </summary>
public sealed class DropboxStorageOptions
{
    /// <summary>クロール時の起点パス。空文字の場合は Dropbox ルートを使用。</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>単純アップロードの上限サイズ（MB）。超過時は upload session を使用。</summary>
    public int SimpleUploadLimitMb { get; set; } = 100;

    /// <summary>upload session のチャンクサイズ（MB）。</summary>
    public int UploadChunkSizeMb { get; set; } = 8;
}
