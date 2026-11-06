using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Ntm.Gateway.Configuration;
using Ntm.Gateway.Security;
using Ntm.Protocol;

namespace Ntm.Gateway.Relay;

/// <summary>
/// <c>GET /v1/models</c>。列出可路由的 PublishedModel：已启用、而且至少有一个候选。
/// 启用了却一个候选都没有的不列出来——客户端看到它只会在请求时拿到 404，
/// 列表里有、请求却不通是更糟的体验（AxonHub 也丢掉零连接的模型）。
///
/// 需要 API 密钥：模型清单暴露的是网关的配置。
/// 带模型限制或渠道黑白名单的密钥只看到自己能用的那些（Q102）。
/// </summary>
public sealed class ModelsCatalog(ConfigSnapshotProvider snapshots, TimeProvider timeProvider)
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 两种客户端都用 <c>GET /v1/models</c>，只能看请求头分辨该给哪种形状。
    /// Anthropic 的 SDK 和 Claude Code 总会带 <c>anthropic-version</c>，旧版用 <c>x-api-key</c> 带密钥；
    /// 新版（Python SDK 2.0、TS SDK 1.0 起）和 Claude Code 的 <c>ANTHROPIC_AUTH_TOKEN</c> 改用
    /// <c>Authorization: Bearer</c>，所以只看 <c>x-api-key</c> 会把它们当成 OpenAI 客户端。
    /// OpenAI 的客户端两个头都不带。
    /// </summary>
    public static InboundDialect DialectFor(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Headers.ContainsKey("x-api-key") || request.Headers.ContainsKey("anthropic-version")
            ? InboundDialect.Anthropic
            : InboundDialect.OpenAiChat;
    }

    public async Task HandleAsync(HttpContext context, InboundDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(dialect);

        ConfigSnapshot snapshot = snapshots.Current;
        string? presented = RelayAuthentication.ReadPresentedApiKey(context.Request);
        ApiKeySnapshot? token = presented is null
            ? null
            : snapshot.FindApiKey(SecretFingerprint.Compute(presented));

        if (token is null || token.IsExpired(timeProvider.GetUtcNow()))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";
            byte[] error = dialect.ErrorBody(
                presented is null ? GatewayErrorKind.MissingApiKey : GatewayErrorKind.InvalidApiKey,
                presented is null ? "缺少 API 密钥。" : "API 密钥无效或已过期。");
            await context.Response.Body.WriteAsync(error, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        // 只按 API 密钥的静态限制过滤（Q102）：模型限制与渠道黑白名单。费用上限、可用时段、RPM、
        // 冷却都是会变的状态，清单跟着它跳来跳去只会让客户端的模型下拉框闪烁。
        List<PublishedModelRoute> routable = [.. snapshot.Routes
            .Where(r => r.Enabled && token.AllowsModel(r.Name))
            // Anthropic 形状的清单给的是 Claude 客户端，只列对话模型（Q223）。
            .Where(r => dialect.Format != ApiFormat.Anthropic || r.RequestKind == RequestKind.Chat)
            .Where(r => r.PriorityGroups.Any(g => g.Candidates.Any(c => token.AllowsChannel(c.ChannelId))))
            .OrderBy(r => r.Name, StringComparer.Ordinal)];

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        byte[] body = dialect.Format == ApiFormat.Anthropic ? Anthropic(routable) : OpenAi(routable);
        await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    private static byte[] OpenAi(List<PublishedModelRoute> routes)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("object", "list");
            writer.WriteStartArray("data");
            foreach (PublishedModelRoute route in routes)
            {
                writer.WriteStartObject();
                writer.WriteString("id", route.Name);
                writer.WriteString("object", "model");
                writer.WriteNumber("created", route.CreatedAt.ToUnixTimeSeconds());
                writer.WriteString("owned_by", "ntm");
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static byte[] Anthropic(List<PublishedModelRoute> routes)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("data");
            foreach (PublishedModelRoute route in routes)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "model");
                writer.WriteString("id", route.Name);
                writer.WriteString("display_name", route.DisplayName ?? route.Name);
                writer.WriteString("created_at", route.CreatedAt.ToString("o"));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            // 不分页：清单是本地配置，不会长到需要翻页。
            writer.WriteBoolean("has_more", false);
            if (routes.Count > 0)
            {
                writer.WriteString("first_id", routes[0].Name);
                writer.WriteString("last_id", routes[^1].Name);
            }
            else
            {
                writer.WriteNull("first_id");
                writer.WriteNull("last_id");
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }
}
