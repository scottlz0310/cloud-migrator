using System.Net;
using System.Text;
using CloudMigrator.Core.Credentials;
using CloudMigrator.Providers.Dropbox.Auth;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// 検証対象: DropboxVerifyService
/// 目的: Credential / Discovery / Preflight の各 Verify 層が
///       クレデンシャル状態や HTTP レスポンスに応じて正しい結果を返すことを確認する
/// </summary>
public sealed class DropboxVerifyServiceTests
{
    // ── ヘルパー ─────────────────────────────────────────────────────────

    private static ICredentialStore BuildCredentialStore(
        string? accessToken = "dummy-access-token",
        bool hasAppKey = true)
    {
        var store = new Mock<ICredentialStore>();
        store.Setup(s => s.GetAsync(CredentialKeys.DropboxAccessToken, It.IsAny<CancellationToken>()))
             .ReturnsAsync(accessToken);
        store.Setup(s => s.ExistsAsync(CredentialKeys.DropboxAppKey, It.IsAny<CancellationToken>()))
             .ReturnsAsync(hasAppKey);
        return store.Object;
    }

    /// <summary>
    /// URL に含まれる文字列ごとに異なるレスポンスを返す HttpMessageHandler をセットアップする。
    /// </summary>
    private static IHttpClientFactory BuildHttpFactory(
        params (string UrlContains, HttpStatusCode StatusCode, string Body)[] responses)
    {
        var handler = new Mock<HttpMessageHandler>();
        foreach (var (urlContains, statusCode, body) in responses)
        {
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.ToString().Contains(urlContains)),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(() => new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                });
        }

        var factory = new Mock<IHttpClientFactory>();
        // 毎回新しい HttpClient を生成（DropboxVerifyService は各層で using var http を使うため）
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(handler.Object));
        return factory.Object;
    }

    private static DropboxVerifyService BuildSut(
        ICredentialStore credentialStore,
        IHttpClientFactory httpFactory)
        => new(credentialStore, httpFactory, NullLogger<DropboxVerifyService>.Instance);

    // ── Credential 層 ─────────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_WhenAccessTokenMissing_CredentialLayerFailsAndLaterLayersSkipped()
    {
        // 検証対象: VerifyAsync (Credential 層)  目的: アクセストークン不在時に後続層がスキップされること
        var credStore = BuildCredentialStore(accessToken: null, hasAppKey: true);
        var factory = BuildHttpFactory(); // HTTP 呼び出しなし
        var sut = BuildSut(credStore, factory);

        var result = await sut.VerifyAsync();

        result.IsSuccess.Should().BeFalse();
        result.Checks.Should().HaveCount(3);
        result.Checks[0].Layer.Should().Be(DropboxVerifyLayer.Credential);
        result.Checks[0].IsSuccess.Should().BeFalse();
        result.Checks[1].Layer.Should().Be(DropboxVerifyLayer.Discovery);
        result.Checks[1].IsSuccess.Should().BeFalse();
        result.Checks[1].Detail.Should().Contain("スキップ");
        result.Checks[2].Layer.Should().Be(DropboxVerifyLayer.Preflight);
        result.Checks[2].IsSuccess.Should().BeFalse();
        result.Checks[2].Detail.Should().Contain("スキップ");
    }

    [Fact]
    public async Task VerifyAsync_WhenAppKeyMissing_CredentialLayerFailsAndLaterLayersSkipped()
    {
        // 検証対象: VerifyAsync (Credential 層)  目的: App Key 不在時に後続層がスキップされること
        var credStore = BuildCredentialStore(accessToken: "token", hasAppKey: false);
        var factory = BuildHttpFactory();
        var sut = BuildSut(credStore, factory);

        var result = await sut.VerifyAsync();

        result.IsSuccess.Should().BeFalse();
        result.Checks[0].Layer.Should().Be(DropboxVerifyLayer.Credential);
        result.Checks[0].IsSuccess.Should().BeFalse();
        result.Checks[1].IsSuccess.Should().BeFalse();
        result.Checks[2].IsSuccess.Should().BeFalse();
    }

    // ── Discovery 層 ─────────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_WhenDiscoveryFails_PreflightLayerSkipped()
    {
        // 検証対象: VerifyAsync (Discovery 層)  目的: Discovery 失敗時に Preflight がスキップされること
        var credStore = BuildCredentialStore();
        var factory = BuildHttpFactory(
            ("files/list_folder", HttpStatusCode.Unauthorized, """{"error_summary":"expired_access_token/..."}"""));
        var sut = BuildSut(credStore, factory);

        var result = await sut.VerifyAsync();

        result.IsSuccess.Should().BeFalse();
        result.Checks.Should().HaveCount(3);
        result.Checks[0].Layer.Should().Be(DropboxVerifyLayer.Credential);
        result.Checks[0].IsSuccess.Should().BeTrue();
        result.Checks[1].Layer.Should().Be(DropboxVerifyLayer.Discovery);
        result.Checks[1].IsSuccess.Should().BeFalse();
        result.Checks[2].Layer.Should().Be(DropboxVerifyLayer.Preflight);
        result.Checks[2].IsSuccess.Should().BeFalse();
        result.Checks[2].Detail.Should().Contain("スキップ");
    }

    // ── Preflight 層 ─────────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_WhenPreflightUploadFails_PreflightLayerFails()
    {
        // 検証対象: VerifyAsync (Preflight 層)  目的: アップロード失敗時に Preflight が失敗になること
        var credStore = BuildCredentialStore();
        var factory = BuildHttpFactory(
            ("files/list_folder", HttpStatusCode.OK, """{"entries":[],"cursor":"abc","has_more":false}"""),
            ("files/upload", HttpStatusCode.Forbidden, """{"error_summary":"insufficient_permissions"}"""));
        var sut = BuildSut(credStore, factory);

        var result = await sut.VerifyAsync();

        result.IsSuccess.Should().BeFalse();
        result.Checks[2].Layer.Should().Be(DropboxVerifyLayer.Preflight);
        result.Checks[2].IsSuccess.Should().BeFalse();
        result.Checks[2].Detail.Should().Contain("アップロード");
    }

    [Fact]
    public async Task VerifyAsync_WhenPreflightDeleteFails_PreflightLayerSucceeds_WithSoftError()
    {
        // 検証対象: VerifyAsync (Preflight 層)  目的: 削除失敗はソフトエラーとして Preflight を成功にすること
        var credStore = BuildCredentialStore();
        var factory = BuildHttpFactory(
            ("files/list_folder", HttpStatusCode.OK, """{"entries":[],"cursor":"abc","has_more":false}"""),
            ("files/upload", HttpStatusCode.OK, """{"id":"id:abc","name":".cloudmigrator-preflight-check.tmp"}"""),
            ("files/delete_v2", HttpStatusCode.InternalServerError, """{"error_summary":"internal_error"}"""));
        var sut = BuildSut(credStore, factory);

        var result = await sut.VerifyAsync();

        result.IsSuccess.Should().BeTrue();
        result.Checks[2].Layer.Should().Be(DropboxVerifyLayer.Preflight);
        result.Checks[2].IsSuccess.Should().BeTrue();
        result.Checks[2].Detail.Should().Contain("削除に失敗");
    }

    // ── 全成功 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_WhenAllLayersSucceed_ReturnsSuccess()
    {
        // 検証対象: VerifyAsync (全層)  目的: 全層成功時に IsSuccess=true を返すこと
        var credStore = BuildCredentialStore();
        var factory = BuildHttpFactory(
            ("files/list_folder", HttpStatusCode.OK, """{"entries":[],"cursor":"abc","has_more":false}"""),
            ("files/upload", HttpStatusCode.OK, """{"id":"id:abc","name":".cloudmigrator-preflight-check.tmp"}"""),
            ("files/delete_v2", HttpStatusCode.OK, """{"metadata":{"id":"id:abc"}}"""));
        var sut = BuildSut(credStore, factory);

        var result = await sut.VerifyAsync();

        result.IsSuccess.Should().BeTrue();
        result.Checks.Should().HaveCount(3);
        result.Checks.Should().AllSatisfy(c => c.IsSuccess.Should().BeTrue());
    }
}
