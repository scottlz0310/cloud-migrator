namespace CloudMigrator.Providers.Abstractions;

/// <summary>
/// ファイル転送ジョブ。クロール結果から生成されキューに投入される。
/// </summary>
public sealed record TransferJob
{
    /// <summary>転送元アイテム</summary>
    public required StorageItem Source { get; init; }

    /// <summary>転送先のルートパス</summary>
    public required string DestinationRoot { get; init; }

    /// <summary>転送先での相対パス（Source.Path に基づく）</summary>
    public string DestinationPath =>
        $"{DestinationRoot.TrimEnd('/')}/{Source.Path.TrimStart('/')}".TrimEnd('/');

    /// <summary>転送先でのフルパス（ファイル名を含む）</summary>
    public string DestinationFullPath => $"{DestinationPath}/{Source.Name.TrimStart('/')}";
}
