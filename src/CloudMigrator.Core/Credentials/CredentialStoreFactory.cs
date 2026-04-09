using System.Runtime.Versioning;

namespace CloudMigrator.Core.Credentials;

/// <summary>
/// プラットフォームに適した <see cref="ICredentialStore"/> を生成するファクトリ。
/// Windows: WindowsCredentialStore（プライマリ）+ EnvironmentCredentialStore（フォールバック）。
/// 非 Windows: 起動時の OS チェックで通常到達しないが、EnvironmentCredentialStore を返す。
/// </summary>
public static class CredentialStoreFactory
{
    /// <summary>
    /// 実行プラットフォームに応じた <see cref="ICredentialStore"/> を生成する。
    /// </summary>
    public static ICredentialStore Create()
    {
        if (OperatingSystem.IsWindows())
        {
#pragma warning disable CA1416 // IsWindows() ガード済み
            var winStore = new WindowsCredentialStore();
#pragma warning restore CA1416
#pragma warning disable CS0618 // 後方互換フォールバックとして意図的に使用
            var envStore = new EnvironmentCredentialStore();
#pragma warning restore CS0618
            return new FallbackCredentialStore(winStore, envStore);
        }

        // 非 Windows（Program.cs の起動チェックで通常は到達しない）
#pragma warning disable CS0618
        return new EnvironmentCredentialStore();
#pragma warning restore CS0618
    }
}
