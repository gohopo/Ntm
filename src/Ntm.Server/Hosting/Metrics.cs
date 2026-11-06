using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Ntm.Gateway.Live;
using Ntm.Gateway.Routing;
using Ntm.Gateway.Traffic;

namespace Ntm.Server.Hosting;

/// <summary>/metrics 的开关（Q339）。默认关；开了而且配了令牌，就要带 <c>Authorization: Bearer 令牌</c>。</summary>
public sealed class MetricsOptions
{
    public const string SectionName = "Ntm:Metrics";

    public bool Enabled { get; set; }

    public string? Token { get; set; }
}

/// <summary>Prometheus 文本格式，自己拼，不引第三方库。</summary>
internal static class MetricsEndpoint
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    public static IResult Handle(
        HttpContext context,
        IOptions<MetricsOptions> options,
        GatewayMetrics metrics,
        InFlightRegistry inFlight,
        CooldownRegistry cooldowns,
        TrafficRecorder recorder)
    {
        if (!Authorized(context.Request, options.Value.Token))
        {
            return Results.Unauthorized();
        }

        var text = new StringBuilder();
        Header(text, "ntm_build_info", "gauge", "网关的版本，值恒为 1");
        Line(text, "ntm_build_info", 1, ("version", BuildInfo.Version), ("commit", BuildInfo.Commit ?? ""));

        Header(text, "ntm_start_time_seconds", "gauge", "网关启动的时刻（Unix 秒）");
        Line(text, "ntm_start_time_seconds", StartedAt.ToUnixTimeSeconds());

        Header(text, "ntm_requests_total", "counter", "结束的请求数");
        foreach ((GatewayMetrics.RequestKey key, long count) in metrics.Requests())
        {
            Line(text, "ntm_requests_total", count,
                ("model", key.PublishedModel), ("status", Name(key.Status)), ("origin", Name(key.Origin)));
        }

        Header(text, "ntm_attempts_total", "counter", "发往上游的尝试数，failure 为 none 表示成功");
        foreach ((GatewayMetrics.AttemptKey key, long count) in metrics.Attempts())
        {
            Line(text, "ntm_attempts_total", count,
                ("channel_id", key.ChannelId.ToString(CultureInfo.InvariantCulture)),
                ("channel", key.ChannelName),
                ("failure", Name(key.Failure)));
        }

        Header(text, "ntm_tokens_total", "counter", "用量（词元），按下发模型");
        Header(text, "ntm_cost_dollars_total", "counter", "费用（美元，含倍率），按下发模型；无单价的不计");
        foreach ((string model, GatewayMetrics.TokenTotals totals) in metrics.Tokens())
        {
            Line(text, "ntm_tokens_total", totals.Input, ("model", model), ("type", "input"));
            Line(text, "ntm_tokens_total", totals.CacheRead, ("model", model), ("type", "cache_read"));
            Line(text, "ntm_tokens_total", totals.CacheWrite, ("model", model), ("type", "cache_write"));
            Line(text, "ntm_tokens_total", totals.Output, ("model", model), ("type", "output"));
            Line(text, "ntm_cost_dollars_total", totals.CostNanoUsd / 1_000_000_000m, ("model", model));
        }

        Header(text, "ntm_in_flight_requests", "gauge", "进行中的请求数");
        Line(text, "ntm_in_flight_requests", inFlight.Count);

        Header(text, "ntm_cooldowns", "gauge", "正在生效的冷却条目数");
        Line(text, "ntm_cooldowns", cooldowns.Active().Count);

        Header(text, "ntm_record_queue_length", "gauge", "还没写进库的请求记录数");
        Line(text, "ntm_record_queue_length", recorder.Pending);

        return Results.Text(text.ToString(), "text/plain; version=0.0.4; charset=utf-8");
    }

    private static bool Authorized(HttpRequest request, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return true;
        }

        string header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(header[prefix.Length..].Trim()),
            Encoding.UTF8.GetBytes(token));
    }

    private static string Name<T>(T value)
        where T : struct, Enum
    {
        string name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static void Header(StringBuilder text, string name, string type, string help) =>
        text.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n')
            .Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');

    private static void Line(StringBuilder text, string name, decimal value, params (string Key, string Value)[] labels)
    {
        text.Append(name);
        if (labels.Length > 0)
        {
            text.Append('{');
            for (int i = 0; i < labels.Length; i++)
            {
                if (i > 0)
                {
                    text.Append(',');
                }

                text.Append(labels[i].Key).Append("=\"").Append(Escape(labels[i].Value)).Append('"');
            }

            text.Append('}');
        }

        text.Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
