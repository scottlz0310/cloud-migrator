using CloudMigrator.Core.Credentials;
using FluentAssertions;
using Moq;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// CredentialStore 実装クラスのユニットテスト。
/// </summary>
public sealed class CredentialStoreTests
{
    // =========================================================
    //  EnvironmentCredentialStore
    // =========================================================

    [Fact]
    public async Task EnvironmentCredentialStore_GetAsync_ShouldReturnNull_WhenEnvVarNotSet()
    {
        // 検証対象: EnvironmentCredentialStore.GetAsync  目的: 対応する環境変数が未設定の場合 null を返すこと
        Environment.SetEnvironmentVariable("MIGRATOR__GRAPH__CLIENTSECRET", null);
#pragma warning disable CS0618
        var store = new EnvironmentCredentialStore();
#pragma warning restore CS0618

        var result = await store.GetAsync(CredentialKeys.AzureClientSecret);

        result.Should().BeNull();
    }

    [Fact]
    public async Task EnvironmentCredentialStore_GetAsync_ShouldReturnValue_WhenEnvVarSet()
    {
        // 検証対象: EnvironmentCredentialStore.GetAsync  目的: 環境変数に値がある場合にその値を返すこと
        const string expected = "test-secret-value";
        Environment.SetEnvironmentVariable("MIGRATOR__GRAPH__CLIENTSECRET", expected);
        try
        {
#pragma warning disable CS0618
            var store = new EnvironmentCredentialStore();
#pragma warning restore CS0618

            var result = await store.GetAsync(CredentialKeys.AzureClientSecret);

            result.Should().Be(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MIGRATOR__GRAPH__CLIENTSECRET", null);
        }
    }

    [Fact]
    public async Task EnvironmentCredentialStore_GetAsync_ShouldReturnNull_WhenKeyUnknown()
    {
        // 検証対象: EnvironmentCredentialStore.GetAsync  目的: マッピングに存在しないキーに対して null を返すこと
#pragma warning disable CS0618
        var store = new EnvironmentCredentialStore();
#pragma warning restore CS0618

        var result = await store.GetAsync("unknown/key");

        result.Should().BeNull();
    }

    [Fact]
    public async Task EnvironmentCredentialStore_ExistsAsync_ShouldReturnFalse_WhenEnvVarNotSet()
    {
        // 検証対象: EnvironmentCredentialStore.ExistsAsync  目的: 未設定の環境変数に対して false を返すこと
        Environment.SetEnvironmentVariable("MIGRATOR__DROPBOX__ACCESSTOKEN", null);
#pragma warning disable CS0618
        var store = new EnvironmentCredentialStore();
#pragma warning restore CS0618

        var exists = await store.ExistsAsync(CredentialKeys.DropboxAccessToken);

        exists.Should().BeFalse();
    }

    [Fact]
    public async Task EnvironmentCredentialStore_ExistsAsync_ShouldReturnTrue_WhenEnvVarSet()
    {
        // 検証対象: EnvironmentCredentialStore.ExistsAsync  目的: 設定済みの環境変数に対して true を返すこと
        Environment.SetEnvironmentVariable("MIGRATOR__DROPBOX__ACCESSTOKEN", "some-token");
        try
        {
#pragma warning disable CS0618
            var store = new EnvironmentCredentialStore();
#pragma warning restore CS0618

            var exists = await store.ExistsAsync(CredentialKeys.DropboxAccessToken);

            exists.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("MIGRATOR__DROPBOX__ACCESSTOKEN", null);
        }
    }

    [Fact]
    public async Task EnvironmentCredentialStore_SaveAsync_ShouldThrow_NotSupported()
    {
        // 検証対象: EnvironmentCredentialStore.SaveAsync  目的: 書き込み操作が NotSupportedException をスローすること
#pragma warning disable CS0618
        var store = new EnvironmentCredentialStore();
#pragma warning restore CS0618

        var act = async () => await store.SaveAsync(CredentialKeys.AzureClientSecret, "value");

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task EnvironmentCredentialStore_DeleteAsync_ShouldThrow_NotSupported()
    {
        // 検証対象: EnvironmentCredentialStore.DeleteAsync  目的: 削除操作が NotSupportedException をスローすること
#pragma warning disable CS0618
        var store = new EnvironmentCredentialStore();
#pragma warning restore CS0618

        var act = async () => await store.DeleteAsync(CredentialKeys.AzureClientSecret);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    // =========================================================
    //  FallbackCredentialStore
    // =========================================================

    [Fact]
    public async Task FallbackCredentialStore_GetAsync_ShouldReturnPrimary_WhenPrimaryHasValue()
    {
        // 検証対象: FallbackCredentialStore.GetAsync  目的: プライマリに値がある場合はプライマリの値を返すこと
        var primary = new Mock<ICredentialStore>();
        var fallback = new Mock<ICredentialStore>();
        primary.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync("primary-value");

        var store = new FallbackCredentialStore(primary.Object, fallback.Object);

        var result = await store.GetAsync("any-key");

        result.Should().Be("primary-value");
        fallback.Verify(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FallbackCredentialStore_GetAsync_ShouldReturnFallback_WhenPrimaryReturnsNull()
    {
        // 検証対象: FallbackCredentialStore.GetAsync  目的: プライマリが null を返した場合にフォールバックの値を返すこと
        var primary = new Mock<ICredentialStore>();
        var fallback = new Mock<ICredentialStore>();
        primary.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((string?)null);
        fallback.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("fallback-value");

        var store = new FallbackCredentialStore(primary.Object, fallback.Object);

        var result = await store.GetAsync("any-key");

        result.Should().Be("fallback-value");
    }

    [Fact]
    public async Task FallbackCredentialStore_GetAsync_ShouldNotFallback_WhenPrimaryThrows()
    {
        // 検証対象: FallbackCredentialStore.GetAsync  目的: プライマリが例外をスローした場合はサイレントフォールバックしないこと
        var primary = new Mock<ICredentialStore>();
        var fallback = new Mock<ICredentialStore>();
        primary.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("Credential Manager エラー"));

        var store = new FallbackCredentialStore(primary.Object, fallback.Object);

        var act = async () => await store.GetAsync("any-key");

        await act.Should().ThrowAsync<InvalidOperationException>();
        fallback.Verify(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FallbackCredentialStore_SaveAsync_ShouldDelegateToPrimary()
    {
        // 検証対象: FallbackCredentialStore.SaveAsync  目的: SaveAsync がプライマリに委譲されること
        var primary = new Mock<ICredentialStore>();
        var fallback = new Mock<ICredentialStore>();
        primary.Setup(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);

        var store = new FallbackCredentialStore(primary.Object, fallback.Object);

        await store.SaveAsync("key", "value");

        primary.Verify(s => s.SaveAsync("key", "value", It.IsAny<CancellationToken>()), Times.Once);
        fallback.Verify(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FallbackCredentialStore_ExistsAsync_ShouldReturnTrue_WhenPrimaryExists()
    {
        // 検証対象: FallbackCredentialStore.ExistsAsync  目的: プライマリに存在する場合は true を返すこと
        var primary = new Mock<ICredentialStore>();
        var fallback = new Mock<ICredentialStore>();
        primary.Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        var store = new FallbackCredentialStore(primary.Object, fallback.Object);

        var exists = await store.ExistsAsync("key");

        exists.Should().BeTrue();
        fallback.Verify(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FallbackCredentialStore_ExistsAsync_ShouldCheckFallback_WhenPrimaryReturnsFalse()
    {
        // 検証対象: FallbackCredentialStore.ExistsAsync  目的: プライマリが false の場合フォールバックも確認すること
        var primary = new Mock<ICredentialStore>();
        var fallback = new Mock<ICredentialStore>();
        primary.Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);
        fallback.Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

        var store = new FallbackCredentialStore(primary.Object, fallback.Object);

        var exists = await store.ExistsAsync("key");

        exists.Should().BeTrue();
    }

    // =========================================================
    //  CredentialKeys 定数値の検証
    // =========================================================

    [Fact]
    public void CredentialKeys_ShouldHaveExpectedValues()
    {
        // 検証対象: CredentialKeys 定数  目的: 定数値が仕様通りであること（変更防止）
        CredentialKeys.AzureClientSecret.Should().Be("cloud-migrator/azure/client-secret");
        CredentialKeys.AzureAccessToken.Should().Be("cloud-migrator/azure/access-token");
        CredentialKeys.DropboxAppKey.Should().Be("cloud-migrator/dropbox/app-key");
        CredentialKeys.DropboxAccessToken.Should().Be("cloud-migrator/dropbox/access-token");
        CredentialKeys.DropboxRefreshToken.Should().Be("cloud-migrator/dropbox/refresh-token");
    }

    // =========================================================
    //  WindowsCredentialStore（Windows のみ実行）
    // =========================================================

    [Fact]
    public async Task WindowsCredentialStore_GetAsync_ShouldReturnNull_WhenKeyNotFound()
    {
        // 検証対象: WindowsCredentialStore.GetAsync  目的: 未登録キーに対して null を返すこと
        if (!OperatingSystem.IsWindows()) return;

#pragma warning disable CA1416
        var store = new WindowsCredentialStore();
        var key = $"cloud-migrator-test/{Guid.NewGuid():N}";

        var result = await store.GetAsync(key);

        result.Should().BeNull();
#pragma warning restore CA1416
    }

    [Fact]
    public async Task WindowsCredentialStore_SaveAndGet_ShouldRoundTrip()
    {
        // 検証対象: WindowsCredentialStore.SaveAsync / GetAsync  目的: 書き込んだ値が正しく読み取れること
        if (!OperatingSystem.IsWindows()) return;

#pragma warning disable CA1416
        var store = new WindowsCredentialStore();
        var key = $"cloud-migrator-test/{Guid.NewGuid():N}";
        const string value = "test-secret-12345";

        try
        {
            await store.SaveAsync(key, value);
            var result = await store.GetAsync(key);

            result.Should().Be(value);
        }
        finally
        {
            await store.DeleteAsync(key);
        }
#pragma warning restore CA1416
    }

    [Fact]
    public async Task WindowsCredentialStore_ExistsAsync_ShouldReturnFalse_WhenKeyNotFound()
    {
        // 検証対象: WindowsCredentialStore.ExistsAsync  目的: 未登録キーに対して false を返すこと
        if (!OperatingSystem.IsWindows()) return;

#pragma warning disable CA1416
        var store = new WindowsCredentialStore();
        var key = $"cloud-migrator-test/{Guid.NewGuid():N}";

        var exists = await store.ExistsAsync(key);

        exists.Should().BeFalse();
#pragma warning restore CA1416
    }

    [Fact]
    public async Task WindowsCredentialStore_DeleteAsync_ShouldBeIdempotent_WhenKeyNotFound()
    {
        // 検証対象: WindowsCredentialStore.DeleteAsync  目的: 存在しないキーを削除しても例外が発生しないこと
        if (!OperatingSystem.IsWindows()) return;

#pragma warning disable CA1416
        var store = new WindowsCredentialStore();
        var key = $"cloud-migrator-test/{Guid.NewGuid():N}";

        var act = async () => await store.DeleteAsync(key);

        await act.Should().NotThrowAsync();
#pragma warning restore CA1416
    }

    [Fact]
    public async Task WindowsCredentialStore_SaveAsync_ShouldOverwrite_WhenKeyExists()
    {
        // 検証対象: WindowsCredentialStore.SaveAsync  目的: 既存キーを上書き保存できること
        if (!OperatingSystem.IsWindows()) return;

#pragma warning disable CA1416
        var store = new WindowsCredentialStore();
        var key = $"cloud-migrator-test/{Guid.NewGuid():N}";

        try
        {
            await store.SaveAsync(key, "original");
            await store.SaveAsync(key, "updated");

            var result = await store.GetAsync(key);
            result.Should().Be("updated");
        }
        finally
        {
            await store.DeleteAsync(key);
        }
#pragma warning restore CA1416
    }
}
