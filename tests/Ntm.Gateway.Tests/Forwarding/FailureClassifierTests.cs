using System.Net;
using Ntm.Gateway.Forwarding;
using Ntm.Gateway.Traffic;

namespace Ntm.Gateway.Tests.Forwarding;

public class FailureClassifierTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, FailureClass.AuthFailure)]
    [InlineData(HttpStatusCode.Forbidden, FailureClass.AuthFailure)]
    [InlineData(HttpStatusCode.PaymentRequired, FailureClass.QuotaExhausted)]
    [InlineData(HttpStatusCode.TooManyRequests, FailureClass.RateLimited)]
    [InlineData(HttpStatusCode.BadRequest, FailureClass.ClientRequestError)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, FailureClass.ClientRequestError)]
    [InlineData(HttpStatusCode.UnprocessableContent, FailureClass.ClientRequestError)]
    [InlineData(HttpStatusCode.RequestTimeout, FailureClass.UpstreamServerError)]
    [InlineData(HttpStatusCode.InternalServerError, FailureClass.UpstreamServerError)]
    [InlineData(HttpStatusCode.BadGateway, FailureClass.UpstreamServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, FailureClass.UpstreamOverloaded)]
    [InlineData(HttpStatusCode.GatewayTimeout, FailureClass.UpstreamServerError)]
    [InlineData(HttpStatusCode.NotFound, FailureClass.ConnectionFailure)]
    [InlineData(HttpStatusCode.Conflict, FailureClass.Unknown)]
    public void PlainStatusCodesMapToClasses(HttpStatusCode status, FailureClass expected) =>
        Assert.Equal(expected, FailureClassifier.FromStatus(status, "{\"error\":{\"message\":\"nope\"}}"));

    [Fact]
    public void AnthropicOverloadedStatusIsUpstreamOverloaded() =>
        Assert.Equal(
            FailureClass.UpstreamOverloaded,
            FailureClassifier.FromStatus((HttpStatusCode)529, "{\"error\":{\"type\":\"overloaded_error\"}}"));

    [Fact]
    public void OpenAiQuotaExhaustedArrivesAsA429ButIsNotRateLimiting()
    {
        // 真实的 OpenAI 响应：429 + insufficient_quota。按限流冷却 1 分钟是错的，
        // 这是密钥级的 30 分钟问题，换模型重试一样没用。
        const string Body =
            "{\"error\":{\"message\":\"You exceeded your current quota\",\"type\":\"insufficient_quota\"}}";

        Assert.Equal(
            FailureClass.QuotaExhausted,
            FailureClassifier.FromStatus(HttpStatusCode.TooManyRequests, Body));
    }

    [Fact]
    public void AnthropicLowCreditArrivesAsA400ButIsNotAClientError()
    {
        // 真实的 Anthropic 响应：400 + credit balance is too low。原样返回客户端是错的。
        const string Body =
            "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\","
            + "\"message\":\"Your credit balance is too low to access the Anthropic API\"}}";

        Assert.Equal(FailureClass.QuotaExhausted, FailureClassifier.FromStatus(HttpStatusCode.BadRequest, Body));
    }

    [Fact]
    public void ChineseArrearageTextIsQuotaExhausted() =>
        Assert.Equal(
            FailureClass.QuotaExhausted,
            FailureClassifier.FromStatus(HttpStatusCode.Forbidden, "{\"message\":\"账户余额不足，请充值\"}"));

    [Fact]
    public void ModelMissingInA400IsNotAClientError()
    {
        // 不少国内上游把「模型不存在」放在 400 里。原样返回会让客户端以为自己写错了，
        // 其实应该冷却「渠道 + 模型」并换下一个候选。
        const string Body = "{\"code\":20012,\"message\":\"Model does not exist\"}";

        Assert.Equal(FailureClass.ModelNotFound, FailureClassifier.FromStatus(HttpStatusCode.BadRequest, Body));
    }

    [Fact]
    public void ChineseModelMissingTextIsRecognized() =>
        Assert.Equal(
            FailureClass.ModelNotFound,
            FailureClassifier.FromStatus(HttpStatusCode.NotFound, "{\"message\":\"模型不存在\"}"));

    [Fact]
    public void NotFoundWithoutModelWordingStaysChannelLevel()
    {
        // 路径打错、反代没配好：整个渠道都不可用，不能只怪这一个模型。
        const string Body = "<html><head><title>404 Not Found</title></head></html>";

        Assert.Equal(FailureClass.ConnectionFailure, FailureClassifier.FromStatus(HttpStatusCode.NotFound, Body));
    }

    [Fact]
    public void AStrayNotFoundPhraseDoesNotBecomeModelNotFound()
    {
        // 「模型」和「找不到」必须同时出现，否则任何一句 not found 都会被当成模型缺失。
        const string Body = "{\"error\":{\"message\":\"The requested file was not found\"}}";

        Assert.Equal(FailureClass.ClientRequestError, FailureClassifier.FromStatus(HttpStatusCode.BadRequest, Body));
    }

    [Theory]
    [InlineData("{\"error\":{\"type\":\"rate_limit_error\"}}", FailureClass.RateLimited)]
    [InlineData("{\"error\":{\"code\":\"too_many_requests\"}}", FailureClass.RateLimited)]
    [InlineData("{\"message\":\"请求过于频繁，请稍后再试\"}", FailureClass.RateLimited)]
    [InlineData("{\"error\":{\"type\":\"insufficient_quota\"}}", FailureClass.QuotaExhausted)]
    [InlineData("{\"message\":\"余额不足\"}", FailureClass.QuotaExhausted)]
    [InlineData("{\"error\":{\"type\":\"authentication_error\"}}", FailureClass.AuthFailure)]
    [InlineData("{\"error\":{\"type\":\"overloaded_error\"}}", FailureClass.UpstreamOverloaded)]
    [InlineData("{\"message\":\"系统繁忙\"}", FailureClass.UpstreamOverloaded)]
    [InlineData("{\"error\":{\"message\":\"model gpt-9 not found\"}}", FailureClass.ModelNotFound)]
    [InlineData("{\"error\":{\"type\":\"api_error\"}}", FailureClass.SoftError)]
    public void InStreamErrorPayloadsAreSubdivided(string payload, FailureClass expected) =>
        Assert.Equal(expected, FailureClassifier.FromErrorPayload(payload));

    [Fact]
    public void QuotaWinsOverRateLimitWhenBothAppear()
    {
        // SSE 里的限流标记常常和额度字样一起出现；额度是更严重、更长的那一个。
        const string Payload = "{\"error\":{\"type\":\"rate_limit_error\",\"message\":\"insufficient_quota\"}}";

        Assert.Equal(FailureClass.QuotaExhausted, FailureClassifier.FromErrorPayload(Payload));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnUnknownPayloadFallsBackToSoftError(string? payload) =>
        Assert.Equal(FailureClass.SoftError, FailureClassifier.FromErrorPayload(payload));
}
