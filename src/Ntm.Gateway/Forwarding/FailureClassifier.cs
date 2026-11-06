using System.Net;
using Ntm.Gateway.Traffic;

namespace Ntm.Gateway.Forwarding;

/// <summary>
/// 把上游的失败归到 <see cref="FailureClass"/>（Q28）。归类决定三件事：
/// 换不换候选、冷却哪个作用域、退避多久。
///
/// 状态码不够用，必须看错误体：OpenAI 的额度用尽是 429 + <c>insufficient_quota</c>，
/// Anthropic 的余额不足是 400 + <c>credit balance</c>，很多国内上游把「模型不存在」放在 400 里。
/// 只看状态码会把 30 分钟的额度问题当成 1 分钟的限流，把该换渠道的模型缺失当成原样返回的客户端错误。
/// </summary>
internal static class FailureClassifier
{
    // 额度、余额、欠费。放在限流之前判断：这类错误经常同时带着 429 或 rate 字样。
    private static readonly string[] QuotaMarkers =
    [
        "insufficient_quota",
        "insufficient quota",
        "credit balance",
        "billing_not_active",
        "exceeded your current quota",
        "arrearage",
        "余额不足",
        "额度不足",
        "欠费",
        "账户余额",
    ];

    private static readonly string[] RateLimitMarkers =
    [
        "rate_limit",
        "rate limit",
        "ratelimit",
        "too many requests",
        "too_many_requests",
        "tpm limit",
        "rpm limit",
        "请求过于频繁",
        "频率过高",
        "限流",
    ];

    private static readonly string[] OverloadedMarkers =
    [
        "overloaded",
        "server_is_busy",
        "server is busy",
        "capacity",
        "系统繁忙",
        "服务繁忙",
        "负载过高",
    ];

    private static readonly string[] AuthMarkers =
    [
        "authentication_error",
        "invalid_api_key",
        "invalid api key",
        "unauthorized",
        "api key not valid",
        "令牌无效",
        "密钥无效",
    ];

    private static readonly string[] ModelMissingMarkers =
    [
        "not found",
        "does not exist",
        "not exist",
        "unsupported model",
        "unknown model",
        "no such model",
        "不存在",
        "不支持该模型",
    ];

    /// <summary>非 2xx 的归类。<paramref name="body"/> 是有界读到的错误体原文。</summary>
    public static FailureClass FromStatus(HttpStatusCode status, string body) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            HasQuotaMarker(body) ? FailureClass.QuotaExhausted : FailureClass.AuthFailure,

        HttpStatusCode.PaymentRequired => FailureClass.QuotaExhausted,

        // 429 默认是限流（冷却「密钥 + 模型」1 分钟），但额度用尽也常走 429，
        // 那是 30 分钟的「密钥」级问题，换个模型重试一样没用。
        HttpStatusCode.TooManyRequests =>
            HasQuotaMarker(body) ? FailureClass.QuotaExhausted : FailureClass.RateLimited,

        HttpStatusCode.NotFound when LooksLikeModelMissing(body) => FailureClass.ModelNotFound,

        // 路径打错、反代配置错：整个渠道都不可用。
        HttpStatusCode.NotFound => FailureClass.ConnectionFailure,

        // 400 是个杂货铺：既有真正的请求格式错误（原样返回客户端），
        // 也有余额不足和模型不存在（该冷却、该换候选）。
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableContent =>
            HasQuotaMarker(body) ? FailureClass.QuotaExhausted
            : LooksLikeModelMissing(body) ? FailureClass.ModelNotFound
            : FailureClass.ClientRequestError,

        HttpStatusCode.RequestEntityTooLarge => FailureClass.ClientRequestError,

        HttpStatusCode.RequestTimeout => FailureClass.UpstreamServerError,

        HttpStatusCode.ServiceUnavailable => FailureClass.UpstreamOverloaded,

        // 529 是 Anthropic 的 overloaded_error，.NET 没有对应的枚举名。
        (HttpStatusCode)529 => FailureClass.UpstreamOverloaded,

        >= HttpStatusCode.InternalServerError => FailureClass.UpstreamServerError,

        _ => FailureClass.Unknown,
    };

    /// <summary>
    /// HTTP 200 里的错误：响应体是错误，或者 SSE 流里的错误事件。按错误原文细分，
    /// 这样 SSE 里的限流标记和明确的额度用尽能和普通上游故障走一样的切换路径，但冷却到对的作用域。
    /// </summary>
    public static FailureClass FromErrorPayload(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return FailureClass.SoftError;
        }

        if (HasQuotaMarker(payload))
        {
            return FailureClass.QuotaExhausted;
        }

        if (Contains(payload, RateLimitMarkers))
        {
            return FailureClass.RateLimited;
        }

        if (Contains(payload, AuthMarkers))
        {
            return FailureClass.AuthFailure;
        }

        if (LooksLikeModelMissing(payload))
        {
            return FailureClass.ModelNotFound;
        }

        if (Contains(payload, OverloadedMarkers))
        {
            return FailureClass.UpstreamOverloaded;
        }

        // 认不出来：按「渠道 + 模型」冷却 1 分钟，换下一个候选。
        return FailureClass.SoftError;
    }

    private static bool HasQuotaMarker(string body) => Contains(body, QuotaMarkers);

    // 「模型」和「找不到」要同时出现，否则随便一句 "not found" 都会被当成模型缺失。
    private static bool LooksLikeModelMissing(string body) =>
        (body.Contains("model", StringComparison.OrdinalIgnoreCase) || body.Contains("模型", StringComparison.Ordinal))
        && Contains(body, ModelMissingMarkers);

    private static bool Contains(string body, string[] markers)
    {
        foreach (string marker in markers)
        {
            if (body.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
