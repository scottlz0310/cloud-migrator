using CloudMigrator.Providers.Graph.Auth;
using CloudMigrator.Providers.Graph.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// 検証対象: GraphClientFactory
/// 目的: GraphServiceClient が各オプションパラメーターのブランチを通じて正しく生成されることを確認する
/// </summary>
public sealed class GraphClientFactoryTests
{
    /// <summary>
    /// テスト用のダミー認証情報。実際の API 疎通は行わない。
    /// </summary>
    private static GraphAuthenticator CreateAuthenticator() =>
        new("test-client-id", "test-tenant-id", "test-client-secret");

    [Fact]
    public void Create_WithDefaultParameters_ReturnsGraphServiceClient()
    {
        // 検証対象: Create（デフォルトパラメーター）
        // 目的: 必須の authenticator のみ渡した場合に GraphServiceClient が生成されること
        var client = GraphClientFactory.Create(CreateAuthenticator());

        client.Should().NotBeNull();
    }

    [Fact]
    public void Create_WithOnRateLimitCallback_ReturnsGraphServiceClient()
    {
        // 検証対象: Create（onRateLimit != null ブランチ）
        // 目的: onRateLimit コールバックを指定した場合でも GraphServiceClient が生成されること
        var callbackInvoked = false;
        var client = GraphClientFactory.Create(
            CreateAuthenticator(),
            onRateLimit: _ => callbackInvoked = true);

        client.Should().NotBeNull();
        callbackInvoked.Should().BeFalse(); // 生成時点ではコールバックが呼ばれていないこと
    }

    [Fact]
    public void Create_WithCopyLocationCaptureHandler_ReturnsGraphServiceClient()
    {
        // 検証対象: Create（copyLocationCapture != null ブランチ）
        // 目的: CopyLocationCaptureHandler を渡した場合でも GraphServiceClient が生成されること
        using var captureHandler = new CopyLocationCaptureHandler();
        var client = GraphClientFactory.Create(
            CreateAuthenticator(),
            copyLocationCapture: captureHandler);

        client.Should().NotBeNull();
    }

    [Fact]
    public void Create_WithAllOptionalParameters_ReturnsGraphServiceClient()
    {
        // 検証対象: Create（全オプションパラメーター）
        // 目的: onRateLimit・copyLocationCapture・rateLimitLogger をすべて指定した場合でも正常に生成されること
        using var captureHandler = new CopyLocationCaptureHandler();
        var mockLogger = new Mock<ILogger>();
        var client = GraphClientFactory.Create(
            CreateAuthenticator(),
            timeoutSec: 60,
            maxRetry: 2,
            onRateLimit: _ => { },
            copyLocationCapture: captureHandler,
            rateLimitLogger: mockLogger.Object);

        client.Should().NotBeNull();
    }
}
