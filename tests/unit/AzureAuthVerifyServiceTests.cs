using CloudMigrator.Providers.Graph.Auth;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// AzureAuthVerifyService / IAzureAuthVerifyService のユニットテスト。
/// </summary>
public sealed class AzureAuthVerifyServiceTests
{
    private readonly AzureAuthVerifyService _sut = new();

    // ── 入力バリデーション ──────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_WhenClientIdIsEmpty_ReturnsFailure()
    {
        // 検証対象: VerifyAsync  目的: ClientId 未入力時に失敗結果が返されること
        var result = await _sut.VerifyAsync(
            clientId: string.Empty,
            tenantId: "tenant-id",
            clientSecret: "secret");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task VerifyAsync_WhenTenantIdIsEmpty_ReturnsFailure()
    {
        // 検証対象: VerifyAsync  目的: TenantId 未入力時に失敗結果が返されること
        var result = await _sut.VerifyAsync(
            clientId: "client-id",
            tenantId: string.Empty,
            clientSecret: "secret");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task VerifyAsync_WhenClientSecretIsEmpty_ReturnsFailure()
    {
        // 検証対象: VerifyAsync  目的: ClientSecret 未入力時に失敗結果が返されること
        var result = await _sut.VerifyAsync(
            clientId: "client-id",
            tenantId: "tenant-id",
            clientSecret: string.Empty);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task VerifyAsync_WhenAllInputsWhitespace_ReturnsFailure()
    {
        // 検証対象: VerifyAsync  目的: すべての入力が空白文字のみの場合に失敗結果が返されること
        var result = await _sut.VerifyAsync(
            clientId: "   ",
            tenantId: "   ",
            clientSecret: "   ");

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task VerifyAsync_WhenInvalidCredentials_ReturnsFailureWithMessage()
    {
        // 検証対象: VerifyAsync  目的: 無効な認証情報（存在しない ClientId 等）で失敗結果とエラーメッセージが返されること
        // 注意: このテストは実際に Azure AD に接続を試みるため E2E カテゴリに分類（CI 除外対象）
        var result = await _sut.VerifyAsync(
            clientId: "00000000-0000-0000-0000-000000000000",
            tenantId: "00000000-0000-0000-0000-000000000000",
            clientSecret: "invalid-secret");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    // ── IAzureAuthVerifyService (インターフェース) ────────────────────

    [Fact]
    public async Task VerifyAsync_ViaInterface_WhenClientIdIsEmpty_ReturnsFailure()
    {
        // 検証対象: IAzureAuthVerifyService  目的: インターフェース経由で呼び出した場合も入力バリデーションが機能すること
        IAzureAuthVerifyService service = _sut;

        var result = await service.VerifyAsync(string.Empty, "tenant", "secret");

        result.IsSuccess.Should().BeFalse();
    }

    // ── CancellationToken ──────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        // 検証対象: VerifyAsync  目的: キャンセル要求時に OperationCanceledException がスローされること
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // 入力バリデーションで早期リターンするため、有効な形式の値を渡す
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await _sut.VerifyAsync(
                "00000000-0000-0000-0000-000000000000",
                "00000000-0000-0000-0000-000000000000",
                "some-secret",
                cts.Token));
    }
}
