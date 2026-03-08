using System.Net;
using CloudMigrator.Setup.Cli.Commands;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// BootstrapCommand の HTTP 関連メソッドを検証するユニットテスト。
/// </summary>
public sealed class SetupBootstrapHttpTests
{
    // ===== TryGetGraphJsonAsync =====

    [Fact]
    public async Task TryGetGraphJsonAsync_ShouldReturnBody_On200()
    {
        // 検証対象: TryGetGraphJsonAsync  目的: 200 成功時にレスポンスボディを返すこと
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"id":"user-1"}""");
        using var client = new HttpClient(handler);

        var result = await BootstrapCommand.TryGetGraphJsonAsync(
            client, "https://graph.test/users/upn", "test.probe", CancellationToken.None);

        result.Should().Be("""{"id":"user-1"}""");
    }

    [Fact]
    public async Task TryGetGraphJsonAsync_ShouldReturnNull_On403Forbidden()
    {
        // 検証対象: TryGetGraphJsonAsync  目的: 403 のみ graceful fallback として null を返すこと
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied"}}""");
        using var client = new HttpClient(handler);

        var result = await BootstrapCommand.TryGetGraphJsonAsync(
            client, "https://graph.test/users/upn", "onedrive.user", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryGetGraphJsonAsync_ShouldThrow_On401WithProbeNameAndSnippet()
    {
        // 検証対象: TryGetGraphJsonAsync  目的: 401 は InvalidOperationException をスローし probeName と本文スニペットを含むこと
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Unauthorized, """{"error":{"code":"InvalidAuthenticationToken"}}""");
        using var client = new HttpClient(handler);

        var act = async () => await BootstrapCommand.TryGetGraphJsonAsync(
            client, "https://graph.test/users/upn", "onedrive.user", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*onedrive.user*")
            .WithMessage("*401*");
    }

    [Fact]
    public async Task TryGetGraphJsonAsync_ShouldThrow_On404WithProbeNameAndSnippet()
    {
        // 検証対象: TryGetGraphJsonAsync  目的: 404（UPN typo 等）は InvalidOperationException をスローすること
        var handler = new FakeHttpMessageHandler(HttpStatusCode.NotFound, """{"error":{"code":"Request_ResourceNotFound"}}""");
        using var client = new HttpClient(handler);

        var act = async () => await BootstrapCommand.TryGetGraphJsonAsync(
            client, "https://graph.test/users/unknown", "onedrive.user", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*onedrive.user*")
            .WithMessage("*404*");
    }

    [Fact]
    public async Task TryGetGraphJsonAsync_ShouldThrow_On429TooManyRequests()
    {
        // 検証対象: TryGetGraphJsonAsync  目的: 429（レート制限）は InvalidOperationException をスローすること
        var handler = new FakeHttpMessageHandler(HttpStatusCode.TooManyRequests, "Too many requests");
        using var client = new HttpClient(handler);

        var act = async () => await BootstrapCommand.TryGetGraphJsonAsync(
            client, "https://graph.test/users/upn", "onedrive.user", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*onedrive.user*");
    }

    // ===== CommentOutEnvKey =====

    [Fact]
    public void CommentOutEnvKey_ShouldCommentOut_ExistingKey()
    {
        // 検証対象: CommentOutEnvKey  目的: 対象キーが存在する場合にコメントアウトされること
        var template = "MIGRATOR__GRAPH__CLIENTID=abc123\nMIGRATOR__GRAPH__TENANTID=tenant-x\n";

        var result = BootstrapCommand.CommentOutEnvKey(template, "MIGRATOR__GRAPH__CLIENTID");

        result.Should().Contain("# MIGRATOR__GRAPH__CLIENTID=");
        result.Should().NotContain("MIGRATOR__GRAPH__CLIENTID=abc123");
        result.Should().Contain("MIGRATOR__GRAPH__TENANTID=tenant-x");
    }

    [Fact]
    public void CommentOutEnvKey_ShouldReturnUnchanged_WhenKeyNotFound()
    {
        // 検証対象: CommentOutEnvKey  目的: 対象キーが存在しない場合にテンプレートが変更されないこと
        var template = "MIGRATOR__GRAPH__TENANTID=tenant-x\n";

        var result = BootstrapCommand.CommentOutEnvKey(template, "MIGRATOR__GRAPH__CLIENTID");

        result.Should().Be(template);
    }

    [Fact]
    public void CommentOutEnvKey_ShouldCommentOut_KeyWithEmptyValue()
    {
        // 検証対象: CommentOutEnvKey  目的: 値が空（KEY=）のエントリもコメントアウトされること
        var template = "MIGRATOR__GRAPH__CLIENTID=\n";

        var result = BootstrapCommand.CommentOutEnvKey(template, "MIGRATOR__GRAPH__CLIENTID");

        result.Should().Contain("# MIGRATOR__GRAPH__CLIENTID=");
        result.Should().NotContain("MIGRATOR__GRAPH__CLIENTID=\n");
    }

    [Fact]
    public void CommentOutEnvKey_ShouldNotAffect_OtherKeysWithSamePrefix()
    {
        // 検証対象: CommentOutEnvKey  目的: 同じプレフィックスを持つ別キーを誤って変更しないこと
        var template = "MIGRATOR__GRAPH__CLIENTID=abc\nMIGRATOR__GRAPH__CLIENTID_EXTRA=xyz\n";

        var result = BootstrapCommand.CommentOutEnvKey(template, "MIGRATOR__GRAPH__CLIENTID");

        result.Should().Contain("# MIGRATOR__GRAPH__CLIENTID=");
        result.Should().Contain("MIGRATOR__GRAPH__CLIENTID_EXTRA=xyz");
    }
}

/// <summary>テスト用 HttpMessageHandler。指定したステータスコードとボディを返す。</summary>
internal sealed class FakeHttpMessageHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body),
        };
        return Task.FromResult(response);
    }
}
