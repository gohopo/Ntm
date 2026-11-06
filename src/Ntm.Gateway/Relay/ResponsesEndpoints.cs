using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ntm.Gateway.Relay;

/// <summary>
/// Responses 的配套端点（ADR-0020）：compact 照常选路记账；按 id 的资源、对话和 input_tokens
/// 回到 ResponseLanding 记下的那个渠道密钥。
/// </summary>
public static class ResponsesEndpoints
{
    public static IEndpointRouteBuilder MapResponsesCompanionEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost(
            "/v1/responses/compact",
            (HttpContext context, ChatRelay relay) => relay.HandleAsync(context, InboundDialect.ResponsesCompact));
        app.MapPost(
            "/v1/responses/input_tokens",
            (HttpContext context, ResponsesResourceRelay relay) => relay.HandleInputTokensAsync(context));

        app.MapMethods(
            "/v1/responses/{id}",
            [HttpMethods.Get, HttpMethods.Delete],
            (string id, HttpContext context, ResponsesResourceRelay relay) => relay.HandleResponseAsync(context, id, string.Empty));
        app.MapPost(
            "/v1/responses/{id}/cancel",
            (string id, HttpContext context, ResponsesResourceRelay relay) => relay.HandleResponseAsync(context, id, "/cancel"));
        app.MapGet(
            "/v1/responses/{id}/input_items",
            (string id, HttpContext context, ResponsesResourceRelay relay) => relay.HandleResponseAsync(context, id, "/input_items"));

        app.MapPost(
            "/v1/conversations",
            (HttpContext context, ResponsesResourceRelay relay) => relay.HandleCreateConversationAsync(context));
        app.MapMethods(
            "/v1/conversations/{id}",
            [HttpMethods.Get, HttpMethods.Post, HttpMethods.Delete],
            (string id, HttpContext context, ResponsesResourceRelay relay) => relay.HandleConversationAsync(context, id, string.Empty));
        app.MapMethods(
            "/v1/conversations/{id}/items",
            [HttpMethods.Get, HttpMethods.Post],
            (string id, HttpContext context, ResponsesResourceRelay relay) => relay.HandleConversationAsync(context, id, "/items"));
        app.MapMethods(
            "/v1/conversations/{id}/items/{item}",
            [HttpMethods.Get, HttpMethods.Delete],
            (string id, string item, HttpContext context, ResponsesResourceRelay relay) =>
                relay.HandleConversationAsync(context, id, "/items/" + Uri.EscapeDataString(item)));

        return app;
    }
}
