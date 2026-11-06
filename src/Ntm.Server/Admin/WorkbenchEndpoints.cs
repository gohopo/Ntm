using Ntm.Gateway.Configuration;
using Ntm.Gateway.Relay;
using Ntm.Protocol;

namespace Ntm.Server.Admin;

/// <summary>
/// /api/workbench：Admin 在 Workbench 里发测试请求（Q200–Q202）。用管理员登录态鉴权，不用 API 密钥。
/// 请求体是前端拼好的原生请求，原样交给中继；响应是客户端会收到的那份字节，响应头 x-ntm-request-id 指向请求记录。
/// 上游的认证失败到这里是 502，不会是 401：前端把没有 x-ntm-request-id 的 401 当作登录失效（Q201）。
/// <c>kind</c> 选请求类型（Q238）：嵌入和图片走 OpenAI 透传，只能落到 OpenAI Chat 格式的渠道。
/// </summary>
internal static class WorkbenchEndpoints
{
    public static RouteGroupBuilder MapWorkbenchEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/channels/{id:int}/send", SendToChannelAsync);
        group.MapPost("/models/send", SendToModelAsync);
        return group;
    }

    /// <summary>
    /// 固定到一个渠道：不选路，用渠道自己的格式透传（Q203）。停用的渠道也能测；
    /// 给了 <paramref name="key"/> 就只用那一把渠道密钥（停用的也行），否则按优先级用启用的密钥。
    /// </summary>
    private static async Task SendToChannelAsync(
        int id,
        int? key,
        string? kind,
        HttpContext context,
        ConfigSnapshotProvider snapshots,
        ChatRelay relay)
    {
        if (!TryParseKind(kind, out RequestKind requestKind))
        {
            await new ValidationErrors().Add("kind", "没有这种请求类型。").ToResult().ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        ChannelSnapshot? channel = await snapshots.LoadChannelAsync(id, key, context.RequestAborted).ConfigureAwait(false);
        if (channel is null)
        {
            await AdminResults.NotFound("渠道").ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (key is not null && channel.Keys.Count == 0)
        {
            await AdminResults.NotFound("渠道密钥").ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (requestKind != RequestKind.Chat && channel.ApiFormat != ApiFormat.OpenAiChat)
        {
            await new ValidationErrors().Add("kind", "嵌入和图片只能用 OpenAI Chat 格式的渠道测试。").ToResult().ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        InboundDialect dialect = requestKind == RequestKind.Chat ? DialectOf(channel.ApiFormat) : PassthroughDialect(requestKind);
        await relay.HandleWorkbenchAsync(context, dialect, channel).ConfigureAwait(false);
    }

    /// <summary>以下发模型为目标：走正常选路，入站格式由 Admin 选，默认 OpenAI Chat（Q203）。</summary>
    private static async Task SendToModelAsync(string? format, string? kind, HttpContext context, ChatRelay relay)
    {
        if (!TryParseKind(kind, out RequestKind requestKind))
        {
            await new ValidationErrors().Add("kind", "没有这种请求类型。").ToResult().ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        // 嵌入和图片只有一种入站格式，format 不用看（Q238）。
        if (requestKind != RequestKind.Chat)
        {
            await relay.HandleWorkbenchAsync(context, PassthroughDialect(requestKind), pinned: null).ConfigureAwait(false);
            return;
        }

        ApiFormat inbound = ApiFormat.OpenAiChat;
        if (!string.IsNullOrEmpty(format)
            && !(Enum.TryParse(format, ignoreCase: true, out inbound) && Enum.IsDefined(inbound) && !int.TryParse(format, out _)))
        {
            await new ValidationErrors().Add("format", "没有这种接口格式。").ToResult().ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await relay.HandleWorkbenchAsync(context, DialectOf(inbound), pinned: null).ConfigureAwait(false);
    }

    /// <summary>chat（默认）、embedding、image。</summary>
    private static bool TryParseKind(string? text, out RequestKind kind)
    {
        kind = RequestKind.Chat;
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        return Enum.TryParse(text, ignoreCase: true, out kind)
            && kind is RequestKind.Chat or RequestKind.Embedding or RequestKind.Image
            && !int.TryParse(text, out _);
    }

    private static InboundDialect PassthroughDialect(RequestKind kind) =>
        kind == RequestKind.Embedding ? InboundDialect.Embeddings : InboundDialect.Images;

    private static InboundDialect DialectOf(ApiFormat format) => format switch
    {
        ApiFormat.Anthropic => InboundDialect.Anthropic,
        ApiFormat.OpenAiResponses => InboundDialect.OpenAiResponses,
        _ => InboundDialect.OpenAiChat,
    };
}
