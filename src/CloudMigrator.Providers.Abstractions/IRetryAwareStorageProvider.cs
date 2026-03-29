namespace CloudMigrator.Providers.Abstractions;

/// <summary>
/// プロバイダー内部のリトライ回数を公開する任意拡張契約。
/// </summary>
public interface IRetryAwareStorageProvider
{
    long TotalRetryCount { get; }
}
