using Ntm.Gateway;
using Ntm.Gateway.Relay;
using Ntm.Server.Admin;
using Ntm.Server.Hosting;

// 子命令要在配置系统之前截住：裸的位置参数会被命令行配置源忽略。
switch (args.Length > 0 ? args[0] : null)
{
    case ResetAdminPasswordCommand.Name:
        return await ResetAdminPasswordCommand.RunAsync(args[1..], Console.Out);
    case HealthcheckCommand.Name:
        return await HealthcheckCommand.RunAsync(args[1..], Console.Out);
    case BackupCommand.Name:
        return await BackupCommand.RunAsync(args[1..], Console.Out);
    case RestoreCommand.Name:
        return await RestoreCommand.RunAsync(args[1..], Console.Out);
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddNtmGateway(builder.Configuration);
builder.Services.AddNtmServer(builder.Configuration);
builder.Services.AddNtmTrustedProxies();

WebApplication app = builder.Build();
StartupLog.Starting(app.Logger, BuildInfo.Version, BuildInfo.Commit ?? "未知");

try
{
    await app.Services.InitializeNtmGatewayAsync(app.Configuration);
}
catch (NtmStartupException ex)
{
    // 起不来的原因只打一行，不打堆栈（数据目录被占、库比程序新）。
    await Console.Error.WriteLineAsync(ex.Message);
    return 1;
}

// 最先跑：后面的 cookie Secure 判断和登录限流都要看到真实的协议和来源地址（Q129）。
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseNtmSecurityHeaders();

// 前端静态文件不要求登录：登录页本身也是静态文件。
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// 不要求登录：容器和负载均衡器的健康检查用它（Q338）。
app.MapGet("/healthz", HealthEndpoint.HandleAsync);

// 默认关；开了就是 Prometheus 文本格式（Q339）。
if (app.Configuration.GetValue<bool>($"{MetricsOptions.SectionName}:{nameof(MetricsOptions.Enabled)}"))
{
    app.MapGet("/metrics", MetricsEndpoint.Handle);
}

app.MapPost(
    "/v1/chat/completions",
    (HttpContext context, ChatRelay relay) => relay.HandleAsync(context, InboundDialect.OpenAiChat));

app.MapPost(
    "/v1/messages",
    (HttpContext context, ChatRelay relay) => relay.HandleAsync(context, InboundDialect.Anthropic));

app.MapPost(
    "/v1/responses",
    (HttpContext context, ChatRelay relay) => relay.HandleAsync(context, InboundDialect.OpenAiResponses));

// Responses 配套：compact、按 id 取回 / 删除 / 取消、对话、input_tokens（Q268，ADR-0020）。
app.MapResponsesCompanionEndpoints();

// Claude Code 用它估上下文用量；不支持的上游由网关估算（Q251–Q254，ADR-0019）。
app.MapPost(
    "/v1/messages/count_tokens",
    (HttpContext context, TokenCounter counter) => counter.HandleAsync(context));

// 嵌入和图片生成只在 OpenAI 格式之间透传（Q220/Q221，ADR-0018）。
app.MapPost(
    "/v1/embeddings",
    (HttpContext context, ChatRelay relay) => relay.HandleAsync(context, InboundDialect.Embeddings));

app.MapPost(
    "/v1/images/generations",
    (HttpContext context, ChatRelay relay) => relay.HandleAsync(context, InboundDialect.Images));

// 两种客户端的模型列表都是 GET /v1/models，按请求头区分形状，见 ModelsCatalog.DialectFor。
app.MapGet("/v1/models", (HttpContext context, ModelsCatalog catalog) => catalog.HandleAsync(
    context,
    ModelsCatalog.DialectFor(context.Request)));

app.MapAdminApi();
app.MapNtmFrontend();

await app.RunAsync();
return 0;
