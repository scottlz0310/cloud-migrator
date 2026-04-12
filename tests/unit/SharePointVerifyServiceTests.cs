using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.Credentials;
using CloudMigrator.Providers.Graph.Auth;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using Moq;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// 検証対象: SharePointVerifyService
/// 目的: Credential / Discovery / Preflight の 3 層検証が、
///       各レイヤーの成否に応じて正しく動作すること（後続スキップ・エラー伝播・呼び出し制御）を確認する。
/// Preflight 層は GraphClient を直接生成するため E2E カテゴリで別途確認する。
/// </summary>
public sealed class SharePointVerifyServiceTests
{
    // ── ヘルパーファクトリ ─────────────────────────────────────────────────

    private static (
        SharePointVerifyService Sut,
        Mock<ICredentialStore> CredStore,
        Mock<IConfigurationService> ConfigSvc,
        Mock<IAzureAuthVerifyService> AuthSvc,
        Mock<IGraphDiscoveryService> DiscSvc) Build(
        string clientId = "test-client",
        string tenantId = "test-tenant",
        string? clientSecret = "test-secret",
        AzureAuthVerifyResult? authResult = null,
        DiscoveryVerifyResult? driveResult = null,
        string sharePointDriveId = "drive-id")
    {
        authResult ??= new AzureAuthVerifyResult(true);
        driveResult ??= new DiscoveryVerifyResult(true);

        var credStore = new Mock<ICredentialStore>();
        credStore
            .Setup(c => c.GetAsync(CredentialKeys.AzureClientSecret, It.IsAny<CancellationToken>()))
            .ReturnsAsync(clientSecret);

        var configSvc = new Mock<IConfigurationService>();
        configSvc
            .Setup(c => c.GetGraphConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphConfigDto(clientId, tenantId, string.Empty));
        configSvc
            .Setup(c => c.GetDiscoveryConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiscoveryConfigDto(
                string.Empty, string.Empty, string.Empty, string.Empty,
                string.Empty, sharePointDriveId, string.Empty, string.Empty,
                string.Empty, "sharepoint"));

        var authSvc = new Mock<IAzureAuthVerifyService>();
        authSvc
            .Setup(a => a.VerifyAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(authResult);

        var discSvc = new Mock<IGraphDiscoveryService>();
        discSvc
            .Setup(d => d.VerifyDriveAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(driveResult);

        var sut = new SharePointVerifyService(
            credStore.Object,
            configSvc.Object,
            authSvc.Object,
            discSvc.Object,
            NullLogger<SharePointVerifyService>.Instance);

        return (sut, credStore, configSvc, authSvc, discSvc);
    }

    // ── Credential 層: バリデーション ─────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_WhenClientIdIsEmpty_CredentialFailsAndDiscoveryPreflightSkipped()
    {
        // 検証対象: VerifyAsync (Credential 層)  目的: ClientId が空の場合に Credential 失敗、後続の Discovery / Preflight がスキップされること
        var (sut, _, _, _, _) = Build(clientId: string.Empty);

        var result = await sut.VerifyAsync();

        result.IsSuccess.Should().BeFalse();
        result.Checks.Should().HaveCount(3);
        result.Checks[0].Layer.Should().Be(SharePointVerifyLayer.Credential);
        result.Checks[0].IsSuccess.Should().BeFalse();
        result.Checks[0].Detail.Should().NotBeNullOrEmpty();
        result.Checks[1].Layer.Should().Be(SharePointVerifyLayer.Discovery);
        result.Checks[1].IsSuccess.Should().BeFalse();
        result.Checks[1].Detail.Should().Contain("スキップ");
        result.Checks[2].Layer.Should().Be(SharePointVerifyLayer.Preflight);
        result.Checks[2].IsSuccess.Should().BeFalse();
        result.Checks[2].Detail.Should().Contain("スキップ");
    }

    [Fact]
    public async Task VerifyAsync_WhenTenantIdIsEmpty_CredentialFailsAndDiscoveryPreflightSkipped()
    {
        // 検証対象: VerifyAsync (Credential 層)  目的: TenantId が空の場合に Credential 失敗、後続がスキップされること
        var (sut, _, _, _, _) = Build(tenantId: string.Empty);

        var result = await sut.VerifyAsync();

        result.IsSuccess.Should().BeFalse();
        result.Checks[0].Layer.Should().Be(SharePointVerifyLayer.Credential);
        result.Checks[0].IsSuccess.Should().BeFalse();
        result.Checks[1].Detail.Should().Contain("スキップ");
        result.Checks[2].Detail.Should().Contain("スキップ");
    }

    [Fact]
    public async Task VerifyAsync_WhenClientSecretIsNull_CredentialFailsWithMessage()
    {
        // 検証対象: VerifyAsync (Credential 層)  目的: ClientSecret が未保存（null）の場合に Credential 失敗となること
        var (sut, _, _, _, _) = Build(clientSecret: null);

        var result = await sut.VerifyAsync();

        result.IsSuccess.Should().BeFalse();
        result.Checks[0].Layer.Should().Be(SharePointVerifyLayer.Credential);
        result.Checks[0].IsSuccess.Should().BeFalse();
        result.Checks[0].Detail.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task VerifyAsync_WhenClientSecretIsWhitespace_CredentialFails()
    {
        // 検証対象: VerifyAsync (Credential 層)  目的: ClientSecret が空白文字のみの場合に Credential 失敗となること
        var (sut, _, _, _, _) = Build(clientSecret: "   ");

        var result = await sut.VerifyAsync();

        result.IsSuccess.Should().BeFalse();
        result.Checks[0].IsSuccess.Should().BeFalse();
    }

    // ── Credential 層: Azure 認証失敗 ────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_WhenAzureAuthFails_CredentialFailsWithErrorMessage()
    {
        // 検証対象: VerifyAsync (Credential 層)  目的: Azure 認証失敗時にエラーメッセージが Detail に伝播すること
        var (sut, _, _, _, _) = Build(
            authResult: new AzureAuthVerifyResult(false, "認証トークン取得エラー"));

        var result = await sut.VerifyAsync();

        result.IsSuccess.Should().BeFalse();
        result.Checks[0].IsSuccess.Should().BeFalse();
        result.Checks[0].Detail.Should().Be("認証トークン取得エラー");
    }

    [Fact]
    public async Task VerifyAsync_WhenAzureAuthFailsWithNullMessage_CredentialFailsWithDefaultMessage()
    {
        // 検証対象: VerifyAsync (Credential 層)  目的: Azure 認証失敗かつ ErrorMessage が null の場合にデフォルトメッセージが返ること
        var (sut, _, _, _, _) = Build(
            authResult: new AzureAuthVerifyResult(false, null));

        var result = await sut.VerifyAsync();

        result.IsSuccess.Should().BeFalse();
        result.Checks[0].IsSuccess.Should().BeFalse();
        result.Checks[0].Detail.Should().NotBeNullOrEmpty();
    }

    // ── Discovery 層 ─────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_WhenSharePointDriveIdIsEmpty_DiscoveryFailsAndPreflightSkipped()
    {
        // 検証対象: VerifyAsync (Discovery 層)  目的: SharePoint Drive ID が空の場合に Discovery 失敗、Preflight がスキップされること
        var (sut, _, _, _, _) = Build(sharePointDriveId: string.Empty);

        var result = await sut.VerifyAsync();

        result.IsSuccess.Should().BeFalse();
        result.Checks.Should().HaveCount(3);
        result.Checks[0].IsSuccess.Should().BeTrue();
        result.Checks[1].Layer.Should().Be(SharePointVerifyLayer.Discovery);
        result.Checks[1].IsSuccess.Should().BeFalse();
        result.Checks[1].Detail.Should().NotBeNullOrEmpty();
        result.Checks[2].Layer.Should().Be(SharePointVerifyLayer.Preflight);
        result.Checks[2].IsSuccess.Should().BeFalse();
        result.Checks[2].Detail.Should().Contain("スキップ");
    }

    [Fact]
    public async Task VerifyAsync_WhenVerifyDriveFails_DiscoveryFailsWithMessageAndPreflightSkipped()
    {
        // 検証対象: VerifyAsync (Discovery 層)  目的: VerifyDriveAsync 失敗時にエラーメッセージが伝播し Preflight がスキップされること
        var (sut, _, _, _, _) = Build(
            driveResult: new DiscoveryVerifyResult(false, "Drive が見つかりません"));

        var result = await sut.VerifyAsync();

        result.IsSuccess.Should().BeFalse();
        result.Checks[1].IsSuccess.Should().BeFalse();
        result.Checks[1].Detail.Should().Be("Drive が見つかりません");
        result.Checks[2].Detail.Should().Contain("スキップ");
    }

    // ── 呼び出し制御の検証 ────────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_WhenCredentialAndDiscoverySucceed_VerifyDriveCalledWithCorrectArgs()
    {
        // 検証対象: VerifyAsync  目的: Credential / Discovery 成功時に VerifyDriveAsync が正しい引数で 1 回だけ呼ばれること
        var (sut, _, _, _, discSvc) = Build();

        await sut.VerifyAsync();

        discSvc.Verify(d => d.VerifyDriveAsync(
            "test-client", "test-tenant", "test-secret", "drive-id",
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task VerifyAsync_WhenCredentialFails_VerifyDriveNeverCalled()
    {
        // 検証対象: VerifyAsync  目的: Credential 失敗時に VerifyDriveAsync が呼ばれないこと（余分なネットワーク呼び出しがないこと）
        var (sut, _, _, _, discSvc) = Build(clientId: string.Empty);

        await sut.VerifyAsync();

        discSvc.Verify(d => d.VerifyDriveAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task VerifyAsync_WhenCredentialFails_AzureAuthVerifyNeverCalled()
    {
        // 検証対象: VerifyAsync  目的: ClientId/TenantId 不足でクレデンシャル検証失敗時に Azure 認証 API が呼ばれないこと
        var (sut, _, _, authSvc, _) = Build(clientId: string.Empty);

        await sut.VerifyAsync();

        authSvc.Verify(a => a.VerifyAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task VerifyAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        // 検証対象: VerifyAsync  目的: キャンセルトークンが発動した場合に OperationCanceledException がスローされること
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var credStore = new Mock<ICredentialStore>();
        credStore
            .Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var configSvc = new Mock<IConfigurationService>();
        configSvc
            .Setup(c => c.GetGraphConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphConfigDto("cid", "tid", string.Empty));

        var sut = new SharePointVerifyService(
            credStore.Object,
            configSvc.Object,
            Mock.Of<IAzureAuthVerifyService>(),
            Mock.Of<IGraphDiscoveryService>(),
            NullLogger<SharePointVerifyService>.Instance);

        var act = async () => await sut.VerifyAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Preflight 層（ClientFactory 注入による例外分岐テスト）─────────────

    private static ODataError MakeSPODataError(int statusCode, string? code = null, string? message = null) =>
        new() { ResponseStatusCode = statusCode, Error = new MainError { Code = code, Message = message } };

    [Fact]
    public async Task VerifyAsync_WhenPreflight403WithAuthorizationRequestDenied_PreflightFailsWithPermissionError()
    {
        // 検証対象: VerifyAsync (Preflight 層)  目的: ClientFactory が Authorization_RequestDenied 403 をスローした場合に Preflight 失敗となること
        var (sut, _, _, _, _) = Build();
        sut.ClientFactory = (_, _, _) => throw MakeSPODataError(403, "Authorization_RequestDenied");

        var result = await sut.VerifyAsync();

        result.IsSuccess.Should().BeFalse();
        result.Checks.Should().HaveCount(3);
        result.Checks[0].IsSuccess.Should().BeTrue();
        result.Checks[1].IsSuccess.Should().BeTrue();
        result.Checks[2].Layer.Should().Be(SharePointVerifyLayer.Preflight);
        result.Checks[2].IsSuccess.Should().BeFalse();
        result.Checks[2].Detail.Should().Contain("権限");
    }

    [Fact]
    public async Task VerifyAsync_WhenPreflight403UnknownCode_PreflightFails()
    {
        // 検証対象: VerifyAsync (Preflight 層)  目的: ClientFactory が 403（未知コード）をスローした場合に Preflight 失敗となること
        var (sut, _, _, _, _) = Build();
        sut.ClientFactory = (_, _, _) => throw MakeSPODataError(403, "Forbidden_Other");

        var result = await sut.VerifyAsync();

        result.Checks[2].IsSuccess.Should().BeFalse();
        result.Checks[2].Detail.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task VerifyAsync_WhenPreflightGenericODataError_PreflightFailsWithStatusCode()
    {
        // 検証対象: VerifyAsync (Preflight 層)  目的: ClientFactory が汎用 ODataError をスローした場合に Preflight 失敗となること
        var (sut, _, _, _, _) = Build();
        sut.ClientFactory = (_, _, _) => throw MakeSPODataError(500, message: "Internal Server Error");

        var result = await sut.VerifyAsync();

        result.Checks[2].IsSuccess.Should().BeFalse();
        result.Checks[2].Detail.Should().Contain("500");
    }

    [Fact]
    public async Task VerifyAsync_WhenPreflightApiException_PreflightFails()
    {
        // 検証対象: VerifyAsync (Preflight 層)  目的: ClientFactory が ApiException をスローした場合に Preflight 失敗となること
        var (sut, _, _, _, _) = Build();
        sut.ClientFactory = (_, _, _) => throw new ApiException { ResponseStatusCode = 403 };

        var result = await sut.VerifyAsync();

        result.Checks[2].IsSuccess.Should().BeFalse();
        result.Checks[2].Detail.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task VerifyAsync_WhenPreflightNetworkError_PreflightFailsWithMessage()
    {
        // 検証対象: VerifyAsync (Preflight 層)  目的: ClientFactory が汎用例外をスローした場合に Preflight 失敗となること
        var (sut, _, _, _, _) = Build();
        sut.ClientFactory = (_, _, _) => throw new HttpRequestException("Network unreachable");

        var result = await sut.VerifyAsync();

        result.Checks[2].IsSuccess.Should().BeFalse();
        result.Checks[2].Detail.Should().Contain("Network unreachable");
    }

    [Fact]
    public async Task VerifyAsync_WhenPreflightCancelled_ThrowsOperationCanceledException()
    {
        // 検証対象: VerifyAsync (Preflight 層)  目的: ClientFactory が OperationCanceledException をスローした場合に握り潰されず再スローされること
        var (sut, _, _, _, _) = Build();
        sut.ClientFactory = (_, _, _) => throw new OperationCanceledException();

        var act = async () => await sut.VerifyAsync();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
