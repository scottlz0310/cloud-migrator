namespace CloudMigrator.Providers.Abstractions;

/// <summary>
/// ストレージアイテム（ファイル・フォルダ）の共通表現
/// </summary>
public sealed record StorageItem
{
    /// <summary>プロバイダー内部 ID</summary>
    public required string Id { get; init; }

    /// <summary>ファイル名（パス区切りなし）</summary>
    public required string Name { get; init; }

    /// <summary>ルートからの相対パス（末尾スラッシュなし）</summary>
    public required string Path { get; init; }

    /// <summary>ファイルサイズ（バイト）。フォルダは null</summary>
    public long? SizeBytes { get; init; }

    /// <summary>最終更新日時（UTC）</summary>
    public DateTimeOffset? LastModifiedUtc { get; init; }

    /// <summary>フォルダかどうか</summary>
    public bool IsFolder { get; init; }

    /// <summary>スキップリスト判定キー（FR-07: path + name の組み合わせ）</summary>
    public string SkipKey => string.IsNullOrEmpty(Path) ? Name : $"{Path}/{Name}";
}
