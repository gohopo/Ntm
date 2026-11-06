namespace Ntm.Protocol.Ir;

/// <summary>
/// 一个已经转成 IR 的请求，某种上游格式能不能如实表达（ADR-0014）。
/// IR 本身装得下、但个别上游格式没有对应位置的东西在这里判断；IR 都装不下的，读入时就已经报了。
/// 表达不了就让这种格式的候选退出这次选路，而不是悄悄丢掉参数。
/// </summary>
public static class IrTargetCheck
{
    /// <summary>返回非空表示 <paramref name="target"/> 格式的上游接不了这个请求，内容是给 Admin 看的原因。</summary>
    public static string? Unrepresentable(IrRequest request, ApiFormat target)
    {
        ArgumentNullException.ThrowIfNull(request);

        return target switch
        {
            ApiFormat.Anthropic when request.ResponseFormat is { } format =>
                format.Kind == IrResponseFormatKind.JsonSchema
                    ? "请求要 response_format=json_schema，这个上游格式没有等价的保证。"
                    : "请求要 response_format=json_object，这个上游格式没有等价的保证。",
            ApiFormat.Anthropic when request.Logprobs is not null => "请求要 logprobs，这个上游格式不返回它。",
            ApiFormat.OpenAiResponses when request.StopSequences.Count > 0 => "请求带了停止序列，这个上游格式没有停止序列。",
            _ => null,
        };
    }
}
