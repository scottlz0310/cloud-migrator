namespace CloudMigrator.Providers.Abstractions;

/// <summary>
/// ページング列挙 API の 1 ページ分の結果。
/// </summary>
public sealed record StoragePage
{
    /// <summary>このページのアイテム一覧</summary>
    public required IReadOnlyList<StorageItem> Items { get; init; }

    /// <summary>
    /// 次ページを取得するためのカーソル文字列（Dropbox cursor / Graph skipToken 等）。
    /// null の場合はこのページが最終ページ。
    /// </summary>
    public string? Cursor { get; init; }

    /// <summary>次ページが存在するか</summary>
    public bool HasMore { get; init; }
}
