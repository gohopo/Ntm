using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Ntm.Gateway.Live;

namespace Ntm.Server.Admin;

/// <summary>实时链路的连接参数（Q114）。测试里调短。</summary>
internal sealed class LiveStreamOptions
{
    public TimeSpan Heartbeat { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>一条连接最长活多久。到点服务端关掉，EventSource 自己重连，重连时会话 cookie 重新检查一遍。</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// GET /api/live：实时链路（Q110）。Server-Sent Events，一个全局流，前端自己过滤。
/// 连上先发全量（所有进行中的 Request），再发增量；不支持 Last-Event-ID，断线重连就再拿一次全量。
/// </summary>
internal static partial class LiveEndpoints
{
    private static readonly byte[] HeartbeatFrame = Encoding.UTF8.GetBytes(": ping\n\n");

    public static RouteGroupBuilder MapLiveEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", StreamAsync);
        return group;
    }

    private static async Task StreamAsync(
        HttpContext context,
        LiveHub hub,
        InFlightRegistry inFlight,
        IOptions<JsonOptions> json,
        IOptions<LiveStreamOptions> options,
        ILogger<LiveHub> logger)
    {
        HttpResponse response = context.Response;

        // 先订阅再取全量：两者之间发生的事件不会漏，重复的前端按 RequestId 合并。
        using LiveSubscription? subscription = hub.TrySubscribe();
        if (subscription is null)
        {
            response.StatusCode = StatusCodes.Status429TooManyRequests;
            await response.WriteAsJsonAsync(
                new { title = "实时链路的连接数已满。", status = StatusCodes.Status429TooManyRequests },
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        LiveStreamOptions settings = options.Value;
        JsonSerializerOptions serializer = json.Value.SerializerOptions;

        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        lifetime.CancelAfter(settings.Lifetime);
        CancellationToken stop = lifetime.Token;

        try
        {
            // 断线后 3 秒重连。
            await response.WriteAsync("retry: 3000\n\n", stop).ConfigureAwait(false);
            LiveEvent snapshot = new LiveSnapshot([.. inFlight.Snapshot().Select(r => r.ToLive())]);
            await WriteEventAsync(response, snapshot, serializer, stop).ConfigureAwait(false);

            Task<bool>? waiting = null;
            while (true)
            {
                waiting ??= subscription.Reader.WaitToReadAsync(stop).AsTask();
                Task finished = await Task.WhenAny(waiting, Task.Delay(settings.Heartbeat, stop)).ConfigureAwait(false);
                if (finished != waiting)
                {
                    stop.ThrowIfCancellationRequested();
                    await response.Body.WriteAsync(HeartbeatFrame, stop).ConfigureAwait(false);
                    await response.Body.FlushAsync(stop).ConfigureAwait(false);
                    continue;
                }

                bool more = await waiting.ConfigureAwait(false);
                waiting = null;
                if (!more)
                {
                    // 缓冲满被断开：前端重连拿全量。
                    if (subscription.Overflowed)
                    {
                        LogSubscriberOverflow(logger);
                    }

                    return;
                }

                while (subscription.Reader.TryRead(out LiveEvent? liveEvent))
                {
                    await WriteEventAsync(response, liveEvent, serializer, stop).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // 到了最长连接时间，或客户端走了。
        }
        catch (IOException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
    }

    private static async Task WriteEventAsync(
        HttpResponse response,
        LiveEvent liveEvent,
        JsonSerializerOptions serializer,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(liveEvent, serializer);
        await response.Body.WriteAsync("data: "u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await response.Body.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await response.Body.WriteAsync("\n\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "实时链路的一个订阅者跟不上（缓冲已满），已断开，它会重连拿全量。")]
    private static partial void LogSubscriberOverflow(ILogger logger);
}
