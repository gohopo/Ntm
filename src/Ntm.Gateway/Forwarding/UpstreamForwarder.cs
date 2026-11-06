using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;
using Ntm.Protocol.Anthropic;
using Ntm.Protocol.Ir;
using Ntm.Protocol.OpenAiChat;
using Ntm.Protocol.OpenAiResponses;
using Ntm.Protocol.Sse;

namespace Ntm.Gateway.Forwarding;

/// <summary>
/// 把一次请求发给一个上游候选，并把响应交给客户端。
///
/// 入站与上游 ApiFormat 相同时走字节透传：原始字节原样转发，同时把同一份字节喂给只读的嗅探器
/// 取用量与停止原因（ADR-0002）。提交点由 <see cref="ResponseCommitter"/> 把着（ADR-0008）。
/// </summary>
public sealed class UpstreamForwarder
{
    /// <summary>序幕缓冲上限。超过就认为上游其实已经在出内容了，直接提交，不再无界攒着。</summary>
    public const int MaxPreambleBytes = 256 * 1024;

    private const int ReadBufferSize = 64 * 1024;
    private const int MaxErrorBodyBytes = 64 * 1024;

    private readonly Func<Upstream.ProxyAddress?, HttpMessageInvoker> _invokers;
    private readonly ForwardTimeouts _timeouts;
    private readonly TimeProvider _timeProvider;

    /// <summary>所有请求都走同一个出站通道，不管代理。测试用它接假上游。</summary>
    public UpstreamForwarder(HttpMessageInvoker invoker, ForwardTimeouts? timeouts = null, TimeProvider? timeProvider = null)
        : this(_ => invoker, timeouts, timeProvider)
    {
    }

    /// <summary>按渠道代理挑连接池（Q132）。</summary>
    public UpstreamForwarder(Upstream.UpstreamHttpClientFactory factory, ForwardTimeouts? timeouts = null, TimeProvider? timeProvider = null)
        : this(factory.For, timeouts, timeProvider)
    {
    }

    private UpstreamForwarder(
        Func<Upstream.ProxyAddress?, HttpMessageInvoker> invokers,
        ForwardTimeouts? timeouts,
        TimeProvider? timeProvider)
    {
        _invokers = invokers;
        _timeouts = timeouts ?? new ForwardTimeouts();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ForwardOutcome> ForwardAsync(
        ForwardRequest request,
        ResponseCommitter committer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(committer);

        if (!request.Passthrough && !request.Converts)
        {
            // 明确拒绝，而不是悄悄当成透传把错格式的字节交给客户端。
            throw new NotSupportedException(
                $"不支持从 {request.UpstreamApiFormat} 上游转换到 {request.InboundApiFormat} 入站。");
        }

        using HttpRequestMessage message = BuildRequest(request);
        request.Debug?.CaptureUpstreamRequest(message, request.Body.Span);
        committer.MarkRequestStarted();

        HttpResponseMessage response;
        try
        {
            response = await _invokers(request.Proxy).SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 客户端自己走了，不是上游的错，不冷却（Q20）。
            return new ForwardOutcome { FailureClass = FailureClass.ClientDisconnected };
        }
        catch (HttpRequestException ex)
        {
            // 代理连不上、认证失败也在这里：整个渠道冷却（Q133）。错误信息说清是经哪个代理，免得被当成上游的问题去查。
            return new ForwardOutcome
            {
                FailureClass = FailureClass.ConnectionFailure,
                ErrorMessage = Truncate(Via(request.Proxy) + ex.Message),
            };
        }
        catch (OperationCanceledException)
        {
            // 连接超时也落在这里。
            return new ForwardOutcome
            {
                FailureClass = FailureClass.ConnectionFailure,
                ErrorMessage = Via(request.Proxy) + "连接上游超时。",
            };
        }

        using (response)
        {
            DateTimeOffset headersAt = _timeProvider.GetUtcNow();
            request.Debug?.CaptureResponseHead(
                (int)response.StatusCode,
                response.Headers,
                response.Content.Headers);

            // http:// 上游经 HTTP 代理时，代理认证失败是一个普通的 407 响应，不是异常。它是代理的问题，不是密钥的。
            if (request.Proxy is not null && response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
            {
                return new ForwardOutcome
                {
                    FailureClass = FailureClass.ConnectionFailure,
                    HttpStatusCode = (int)response.StatusCode,
                    ErrorMessage = Via(request.Proxy) + "代理要求认证，或者用户名密码不对（HTTP 407）。",
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return await ReadErrorAsync(request, response, cancellationToken).ConfigureAwait(false);
            }

            // 图片请求要了流，上游却回了一整个 JSON（不少兼容上游不支持图片流）：按非流式原样转给客户端（Q232）。
            bool jsonReply = request.RequestKind == RequestKind.Image
                && response.Content.Headers.ContentType?.MediaType is { } mediaType
                && mediaType.EndsWith("json", StringComparison.OrdinalIgnoreCase);
            if (!request.IsStream || jsonReply)
            {
                return await ForwardNonStreamAsync(request, response, committer, cancellationToken).ConfigureAwait(false);
            }

            return request.Passthrough
                ? await ForwardStreamAsync(request, response, committer, headersAt, cancellationToken).ConfigureAwait(false)
                : await ForwardConvertedStreamAsync(request, response, committer, headersAt, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Via(Upstream.ProxyAddress? proxy) => proxy is null ? "" : $"经代理 {proxy.Endpoint}：";

    /// <summary>
    /// 发一个小请求、整个读回响应体（count_tokens 用，Q252/Q253）。不提交给客户端，不分类失败；
    /// 网络错误、超时都返回 <c>Status = 0</c>。<paramref name="cancellationToken"/> 是客户端断开。
    /// </summary>
    public async Task<(int Status, byte[]? Body)> ExchangeAsync(
        ForwardRequest request,
        TimeSpan timeout,
        int maxBodyBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using HttpRequestMessage message = BuildRequest(request);
        try
        {
            using HttpResponseMessage response = await _invokers(request.Proxy)
                .SendAsync(message, deadline.Token)
                .ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                return (status, null);
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBodyBytes)
                {
                    return (0, null);
                }

                buffer.Write(chunk, 0, read);
            }

            return (status, buffer.ToArray());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (0, null);
        }
        catch (HttpRequestException)
        {
            return (0, null);
        }
    }

    /// <summary>
    /// 原样发一个请求，读完响应头就返回（按 id 的 Responses 资源用，ADR-0020）。不分类失败、不提交给客户端，
    /// 响应体由调用方读，读完要释放。网络错误照常抛 <see cref="HttpRequestException"/>。
    /// </summary>
    public Task<HttpResponseMessage> SendRawAsync(ForwardRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendRawCoreAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendRawCoreAsync(ForwardRequest request, CancellationToken cancellationToken)
    {
        using HttpRequestMessage message = BuildRequest(request);
        return await _invokers(request.Proxy).SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage BuildRequest(ForwardRequest request)
    {
        var message = new HttpRequestMessage(request.Method, request.RequestUri);

        // 透传就是把客户端的原始字节原样发出去，只在必要时定点改写（ADR-0002）。
        // GET / DELETE 没有请求体时什么都不带。
        if (request.Method == HttpMethod.Post || !request.Body.IsEmpty)
        {
            message.Content = new ReadOnlyMemoryContent(request.Body);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
        }

        ApiFormatAuthScheme scheme = ApiFormatAuth.For(request.UpstreamApiFormat, request.AnthropicBearerAuth);
        message.Headers.TryAddWithoutValidation(
            scheme.HeaderName,
            scheme.ValuePrefix is null ? request.Secret : scheme.ValuePrefix + request.Secret);

        foreach ((string name, string value) in scheme.AdditionalHeaders)
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        // 客户端带来的同名头要盖掉格式默认头（例如 anthropic-version），而不是并列两个值（Q165）。
        foreach ((string name, string value) in request.ForwardedHeaders)
        {
            message.Headers.Remove(name);
            message.Headers.TryAddWithoutValidation(name, value);
        }

        // 渠道的请求头规则最后跑：它能覆盖 anthropic-version 这类格式默认头，
        // 但认证头在保存时就被拒绝了，这里碰不到（Q145）。
        request.Rewriter.ApplyToHeaders(message.Headers);

        return message;
    }

    /// <summary>
    /// 非 2xx：把有界的错误体读完，交给调用方决定换候选还是原样返回。这里不写客户端。
    /// </summary>
    private async Task<ForwardOutcome> ReadErrorAsync(
        ForwardRequest request,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string body = await ReadBoundedStringAsync(response, cancellationToken).ConfigureAwait(false);
        request.Debug?.CaptureResponseChunk(System.Text.Encoding.UTF8.GetBytes(body));
        int status = (int)response.StatusCode;
        FailureClass failure = FailureClassifier.FromStatus(response.StatusCode, body);

        // 嵌入和图片请求打到一个没有这个端点的上游（404/405 而不带「模型不存在」）：只冷却「渠道 + 模型」，
        // 不按「路径打错」冷却整个渠道，免得把同一个渠道的对话也挡掉（Q235）。
        if (request.RequestKind != RequestKind.Chat
            && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            failure = FailureClass.ModelNotFound;
        }

        return new ForwardOutcome
        {
            FailureClass = failure,
            HttpStatusCode = status,
            ErrorMessage = Truncate(body),
            ErrorBody = body,
            ErrorContentType = response.Content.Headers.ContentType?.ToString(),

            // 重置时间只对「等一会儿就好」的失败有意义。认证失败给个 Retry-After 也不该影响退避。
            UpstreamResetAt = failure is FailureClass.RateLimited
                or FailureClass.QuotaExhausted
                or FailureClass.UpstreamOverloaded
                ? UpstreamResetParser.Parse(response.Headers, _timeProvider.GetUtcNow())
                : null,
        };
    }

    /// <summary>
    /// 非流式：响应头和响应体一起到，整体是原子的。
    /// 把有界的响应体整个读完解析一次，而不是像 ccLoad 那样只探 512 字节——
    /// 超过 512 字节的 200 错误体在那种做法下会当成成功放过去。
    /// </summary>
    private async Task<ForwardOutcome> ForwardNonStreamAsync(
        ForwardRequest request,
        HttpResponseMessage response,
        ResponseCommitter committer,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_timeouts.NonStreamTotal);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        byte[] body;
        try
        {
            body = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);
            request.Debug?.CaptureResponseChunk(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ForwardOutcome { FailureClass = FailureClass.ClientDisconnected };
        }
        catch (OperationCanceledException)
        {
            return new ForwardOutcome
            {
                FailureClass = FailureClass.IdleTimeout,
                HttpStatusCode = (int)response.StatusCode,
                ErrorMessage = "读取非流式响应体超时。",
            };
        }

        if (request.RequestKind != RequestKind.Chat)
        {
            return await CommitPassthroughResultAsync(request, response, committer, body, cancellationToken).ConfigureAwait(false);
        }

        UpstreamResponseReading reading = UpstreamResponseReading.Read(
            body,
            request.UpstreamApiFormat,
            needsIr: !request.Passthrough);

        if (reading.IsError || reading.IsMalformed)
        {
            // HTTP 200 但响应体是错误：按上游故障处理，走和普通失败一样的切换路径。
            return new ForwardOutcome
            {
                FailureClass = FailureClassifier.FromErrorPayload(reading.ErrorPayload),
                HttpStatusCode = (int)response.StatusCode,
                Usage = reading.Usage,
                UpstreamModel = reading.UpstreamModel,
                ErrorMessage = Truncate(reading.ErrorPayload ?? "响应体不是合法的 JSON。"),
            };
        }

        byte[] outgoing = body;
        if (!request.Passthrough)
        {
            // 经由 IR 写回客户端的格式。写不出来（比如工具参数不是合法 JSON）就是这次尝试失败，
            // 客户端还什么都没收到，可以换候选，绝不假装成功。
            (byte[]? converted, string? error) = Convert(request, reading);
            if (converted is null)
            {
                return new ForwardOutcome
                {
                    FailureClass = FailureClass.SoftError,
                    HttpStatusCode = (int)response.StatusCode,
                    Usage = reading.Usage,
                    UpstreamModel = reading.UpstreamModel,
                    ErrorMessage = Truncate(error),
                };
            }

            outgoing = converted;
        }

        committer.CopyUpstreamHead(response);
        await committer.CommitAsync(outgoing, cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        return new ForwardOutcome
        {
            Committed = true,
            HttpStatusCode = (int)response.StatusCode,
            Usage = reading.Usage,
            SawThinking = reading.SawThinking,
            UpstreamModel = reading.UpstreamModel,
            ResponseId = reading.ResponseId,
            FinishReason = reading.FinishReason,
            FirstEventAt = now,
            FirstContentEventAt = now,
        };
    }

    /// <summary>
    /// 嵌入和图片的非流式响应：认出成败、用量和条数，字节原样交给客户端（Q228–Q230）。
    /// 2xx 却一条结果都没有是软错误，客户端还什么都没收到，可以换候选。
    /// </summary>
    private async Task<ForwardOutcome> CommitPassthroughResultAsync(
        ForwardRequest request,
        HttpResponseMessage response,
        ResponseCommitter committer,
        byte[] body,
        CancellationToken cancellationToken)
    {
        OpenAiPassthroughResponseReading reading = OpenAiPassthroughResponseInspector.Read(body, request.RequestKind);
        if (reading.IsError || reading.IsMalformed || reading.IsEmpty)
        {
            string message = reading.IsError
                ? reading.ErrorPayload ?? "上游返回了错误。"
                : reading.IsMalformed
                    ? "响应体不是合法的 JSON。"
                    : request.RequestKind == RequestKind.Image ? "上游没有返回图片。" : "上游没有返回向量。";
            return new ForwardOutcome
            {
                FailureClass = reading.IsError ? FailureClassifier.FromErrorPayload(reading.ErrorPayload) : FailureClass.SoftError,
                HttpStatusCode = (int)response.StatusCode,
                Usage = reading.Usage,
                UpstreamModel = reading.UpstreamModel,
                ErrorMessage = Truncate(message),
            };
        }

        committer.CopyUpstreamHead(response);
        await committer.CommitAsync(body, cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        return new ForwardOutcome
        {
            Committed = true,
            HttpStatusCode = (int)response.StatusCode,
            Usage = reading.Usage,
            UpstreamModel = reading.UpstreamModel,
            ImageCount = request.RequestKind == RequestKind.Image ? reading.ItemCount : 0,
            FirstEventAt = now,
            FirstContentEventAt = now,
        };
    }

    /// <summary>把非流式响应从上游格式写成客户端格式。失败时第二项是原因。</summary>
    private (byte[]? Body, string? Error) Convert(ForwardRequest request, UpstreamResponseReading reading)
    {
        ResponseConversion conversion = request.Conversion!;
        if (reading.Response is not { } ir)
        {
            return (null, "上游响应里没有可用的内容。");
        }

        long now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        switch (request.InboundApiFormat)
        {
            case ApiFormat.OpenAiChat:
                {
                    OpenAiChatResponseWriting written = OpenAiChatResponseWriter.Write(
                        ir,
                        conversion.MessageId,
                        conversion.FallbackModel,
                        now);
                    return (written.Body, written.Error);
                }

            case ApiFormat.OpenAiResponses:
                {
                    ResponsesResponseWriting written = ResponsesResponseWriter.Write(
                        ir,
                        conversion.MessageId,
                        conversion.FallbackModel,
                        now);
                    return (written.Body, written.Error);
                }

            default:
                {
                    AnthropicResponseWriting written = AnthropicResponseWriter.Write(
                        ir,
                        conversion.MessageId,
                        conversion.FallbackModel);
                    return (written.Body, written.Error);
                }
        }
    }

    /// <summary>
    /// 流式转换：上游格式 → 客户端格式。
    /// 检视器仍是判断的唯一来源（ContentEvent、错误、用量），翻译器只负责产出字节；
    /// 提交点与透传完全一样（ADR-0008）：第一个 ContentEvent 翻译出的字节连同之前的序幕一起放出去。
    /// </summary>
    private async Task<ForwardOutcome> ForwardConvertedStreamAsync(
        ForwardRequest request,
        HttpResponseMessage response,
        ResponseCommitter committer,
        DateTimeOffset headersAt,
        CancellationToken cancellationToken)
    {
        Stream upstream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        using var sniffer = new SseFrameSniffer();
        var inspector = new UpstreamStreamInspection(request.UpstreamApiFormat);
        using UpstreamStreamTranslation translator = UpstreamStreamTranslation.For(
            request,
            _timeProvider.GetUtcNow().ToUnixTimeSeconds());
        var output = new ArrayBufferWriter<byte>(4096);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);

        DateTimeOffset? firstEventAt = null;
        DateTimeOffset? firstContentAt = null;
        string? streamError = null;
        bool forcedCommit = false;
        bool clientNotified = false;

        try
        {
            while (!inspector.SawTerminal)
            {
                int read;
                try
                {
                    read = await ReadWithKeepAliveAsync(
                        upstream,
                        buffer,
                        committer,
                        firstEventAt is null,
                        request.RequestKind,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return Outcome(FailureClass.ClientDisconnected);
                }
                catch (OperationCanceledException)
                {
                    return await FailAsync(
                        firstEventAt is null ? FailureClass.FirstEventTimeout : FailureClass.IdleTimeout,
                        firstEventAt is null ? "等待第一个上游事件超时。" : "上游流静默超时。").ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    return await FailAsync(FailureClass.StreamInterrupted, ex.Message).ConfigureAwait(false);
                }

                if (read == 0)
                {
                    if (sniffer.TryReadFinalFrame())
                    {
                        HandleFrame(sniffer.Data);
                    }

                    break;
                }

                firstEventAt ??= _timeProvider.GetUtcNow();
                request.Debug?.CaptureResponseChunk(buffer.AsSpan(0, read));
                sniffer.Append(buffer.AsSpan(0, read));

                bool chunkHasContent = false;
                while (sniffer.TryReadFrame())
                {
                    if (HandleFrame(sniffer.Data))
                    {
                        chunkHasContent = true;
                    }
                }

                if (sniffer.Abandoned)
                {
                    // 一帧超过上限：转换需要完整的帧，没法继续翻译。
                    return await FailAsync(FailureClass.SoftError, "上游单帧过大，无法转换。").ConfigureAwait(false);
                }

                if (streamError is not null)
                {
                    // 还没提交就失败：客户端什么都没收到，可以换候选。提交之后的失败在下面统一处理。
                    return await FailAsync(FailureClassifier.FromErrorPayload(streamError), streamError)
                        .ConfigureAwait(false);
                }

                await EmitAsync(chunkHasContent).ConfigureAwait(false);
            }

            if (streamError is not null)
            {
                return await FailAsync(FailureClass.SoftError, streamError).ConfigureAwait(false);
            }

            bool ended = inspector.SawTerminal || inspector.FinishReason is not null;
            if (ended && (committer.Committed || inspector.HasContentEvent))
            {
                translator.Complete(inspector.Usage, output);
                await EmitAsync(chunkHasContent: !committer.Committed).ConfigureAwait(false);
                return Outcome(FailureClass.None);
            }

            if (committer.Committed || inspector.HasContentEvent)
            {
                // 有输出，但上游在给出结束标记前断开：不写 message_stop，写一个流内错误。
                await EmitAsync(chunkHasContent: !committer.Committed).ConfigureAwait(false);
                return await FailAsync(FailureClass.StreamInterrupted, "上游在流结束前断开。").ConfigureAwait(false);
            }

            // 干净地结束了，但一个 ContentEvent 都没有：失败的尝试。把收尾事件也放进序幕，
            // 这样最后一个候选只能原样交出时，客户端拿到的仍是一条完整、合法的空消息。
            translator.Complete(inspector.Usage, output);
            committer.BufferPreamble(output.WrittenSpan);
            output.ResetWrittenCount();
            return Outcome(FailureClass.UpstreamServerError, "上游结束了流但没有输出。");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // 检视一帧并翻译它。返回这一帧是不是 ContentEvent。
        bool HandleFrame(ReadOnlySpan<byte> data)
        {
            StreamFrameVerdict verdict = inspector.Inspect(data);
            switch (verdict)
            {
                case StreamFrameVerdict.Error:
                    streamError ??= inspector.ErrorPayload;
                    return false;
                case StreamFrameVerdict.Malformed:
                    return false;
                case StreamFrameVerdict.Terminal:
                    // 结束帧也交给读取器：Responses 的停止原因（例如 incomplete）只在 response.completed 里。
                    translator.Translate(data, output);
                    return false;
                default:
                    translator.Translate(data, output);
                    return verdict == StreamFrameVerdict.Content;
            }
        }

        // 把翻译好的字节交给提交器：已提交就直接写，遇到内容就提交，否则攒进序幕。
        async Task EmitAsync(bool chunkHasContent)
        {
            if (committer.Committed)
            {
                if (output.WrittenCount > 0)
                {
                    await committer.WriteCommittedAsync(output.WrittenMemory, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (chunkHasContent)
            {
                firstContentAt = _timeProvider.GetUtcNow();
                committer.CopyUpstreamHead(response);
                await committer.CommitAsync(output.WrittenMemory, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                committer.BufferPreamble(output.WrittenSpan);
                if (committer.BufferedBytes >= MaxPreambleBytes)
                {
                    forcedCommit = true;
                    firstContentAt = _timeProvider.GetUtcNow();
                    committer.CopyUpstreamHead(response);
                    await committer.CommitAsync(ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
                }
            }

            output.ResetWrittenCount();
        }

        // 失败收尾。没提交：什么都不写，留给调用方换候选。已提交：先把已翻译的放出去，
        // 再按 Anthropic 格式写一个流内错误——转换路径里上游的错误帧不会被原样转发，只能由这里告知客户端。
        async Task<ForwardOutcome> FailAsync(FailureClass failure, string? error)
        {
            if (committer.Committed)
            {
                if (output.WrittenCount > 0)
                {
                    await committer.WriteCommittedAsync(output.WrittenMemory, cancellationToken).ConfigureAwait(false);
                    output.ResetWrittenCount();
                }

                translator.WriteError(output, "上游在输出过程中失败。");
                try
                {
                    await committer.WriteCommittedAsync(output.WrittenMemory, cancellationToken).ConfigureAwait(false);
                    clientNotified = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // 客户端已经走了，告知不了。
                }

                output.ResetWrittenCount();
            }

            return Outcome(failure, error);
        }

        ForwardOutcome Outcome(FailureClass failure, string? error = null) => new()
        {
            Committed = committer.Committed,
            FailureClass = failure,
            HttpStatusCode = (int)response.StatusCode,
            Usage = inspector.Usage,
            PartialUsage = inspector.PartialUsage,
            OutputAsciiChars = inspector.OutputAsciiChars,
            OutputWideChars = inspector.OutputWideChars,
            SawThinking = inspector.SawThinking,
            UpstreamModel = inspector.UpstreamModel,
            ResponseId = inspector.ResponseId,
            FinishReason = inspector.FinishReason,
            FirstEventAt = firstEventAt ?? headersAt,
            FirstContentEventAt = firstContentAt,
            ErrorMessage = error is null ? null : Truncate(error),
            EndedEmpty = inspector.EndedEmpty && !forcedCommit,
            ClientNotified = clientNotified,
        };
    }

    /// <summary>
    /// 流式：原始字节原样转发，同一份字节喂给嗅探器。
    /// 第一个 ContentEvent 之前的字节都先缓冲，到了再按原顺序一起放出去（ADR-0008）。
    /// </summary>
    private async Task<ForwardOutcome> ForwardStreamAsync(
        ForwardRequest request,
        HttpResponseMessage response,
        ResponseCommitter committer,
        DateTimeOffset headersAt,
        CancellationToken cancellationToken)
    {
        Stream upstream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        using var sniffer = new SseFrameSniffer();
        var inspector = new UpstreamStreamInspection(request.UpstreamApiFormat, request.RequestKind);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);

        // 只有注入了 include_usage 而客户端没要时才逐帧过滤；否则原始块直接转发，零拷贝。
        using InjectedUsageFilter? filter = request.SwallowUsageOnlyFrames ? new InjectedUsageFilter() : null;
        bool filtering = filter is not null;

        DateTimeOffset? firstEventAt = null;
        DateTimeOffset? firstContentAt = null;
        string? streamError = null;
        bool forcedCommit = false;

        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await ReadWithKeepAliveAsync(
                        upstream,
                        buffer,
                        committer,
                        firstEventAt is null,
                        request.RequestKind,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return Outcome(FailureClass.ClientDisconnected);
                }
                catch (OperationCanceledException)
                {
                    return Outcome(
                        firstEventAt is null ? FailureClass.FirstEventTimeout : FailureClass.IdleTimeout,
                        firstEventAt is null ? "等待第一个上游事件超时。" : "上游流静默超时。");
                }
                catch (IOException ex)
                {
                    return Outcome(FailureClass.StreamInterrupted, ex.Message);
                }

                if (read == 0)
                {
                    break;
                }

                var chunk = new ReadOnlyMemory<byte>(buffer, 0, read);
                firstEventAt ??= _timeProvider.GetUtcNow();
                request.Debug?.CaptureResponseChunk(chunk.Span);

                bool chunkHasContent = false;
                if (filtering)
                {
                    filter!.Append(chunk.Span);
                }

                sniffer.Append(chunk.Span);
                while (sniffer.TryReadFrame())
                {
                    StreamFrameVerdict verdict = inspector.Inspect(sniffer.Data);
                    if (verdict == StreamFrameVerdict.Content)
                    {
                        chunkHasContent = true;
                    }
                    else if (verdict == StreamFrameVerdict.Error)
                    {
                        streamError = inspector.ErrorPayload;
                    }
                    else if (verdict == StreamFrameVerdict.UsageOnly && filtering)
                    {
                        filter!.Cut(sniffer.FrameStartOffset, sniffer.FrameEndOffset);
                    }
                }

                ReadOnlyMemory<byte> outgoing = chunk;
                if (filtering)
                {
                    if (sniffer.Abandoned)
                    {
                        // 嗅探放弃了，帧边界不再可信：剩下的全放出去，此后原样转发。
                        outgoing = filter!.ReleaseAll();
                        filtering = false;
                    }
                    else
                    {
                        outgoing = filter!.Release(sniffer.ConsumedOffset);
                    }
                }

                if (committer.Committed)
                {
                    if (!outgoing.IsEmpty)
                    {
                        await committer.WriteCommittedAsync(outgoing, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                // 流内错误而且还没提交：这次尝试失败，客户端什么都没收到，可以换候选。
                if (streamError is not null)
                {
                    return Outcome(FailureClassifier.FromErrorPayload(streamError), streamError);
                }

                if (chunkHasContent)
                {
                    firstContentAt = _timeProvider.GetUtcNow();
                    committer.CopyUpstreamHead(response);
                    await committer.CommitAsync(outgoing, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                committer.BufferPreamble(outgoing.Span);

                // 序幕攒太多：上游其实在出东西，只是谓词没认出来。提交，别无界攒着。
                if (committer.BufferedBytes >= MaxPreambleBytes)
                {
                    forcedCommit = true;
                    firstContentAt = _timeProvider.GetUtcNow();
                    committer.CopyUpstreamHead(response);
                    await committer.CommitAsync(ReadOnlyMemory<byte>.Empty, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            // 流结束时还有未成帧的尾巴。
            bool tailHasContent = false;
            if (sniffer.TryReadFinalFrame())
            {
                StreamFrameVerdict verdict = inspector.Inspect(sniffer.Data);
                if (verdict == StreamFrameVerdict.Content)
                {
                    tailHasContent = true;
                }
                else if (verdict == StreamFrameVerdict.Error)
                {
                    streamError ??= inspector.ErrorPayload;
                }
                else if (verdict == StreamFrameVerdict.UsageOnly && filtering)
                {
                    filter!.Cut(sniffer.FrameStartOffset, sniffer.FrameEndOffset);
                }
            }

            if (filtering)
            {
                ReadOnlyMemory<byte> tail = filter!.ReleaseAll();
                if (!tail.IsEmpty)
                {
                    if (committer.Committed)
                    {
                        await committer.WriteCommittedAsync(tail, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        committer.BufferPreamble(tail.Span);
                    }
                }
            }

            if (!committer.Committed && tailHasContent && streamError is null)
            {
                // 最后一帧没有收尾空行，但带着真实输出。
                firstContentAt = _timeProvider.GetUtcNow();
                committer.CopyUpstreamHead(response);
                await committer.CommitAsync(ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            }

            if (committer.Committed)
            {
                // 提交之后的失败不能再换候选，但仍是失败：要冷却、要计已经产生的用量（Q20）。
                if (streamError is not null)
                {
                    return Outcome(FailureClassifier.FromErrorPayload(streamError), streamError);
                }

                if (!inspector.SawTerminal && inspector.FinishReason is null)
                {
                    return Outcome(FailureClass.StreamInterrupted, "上游在流结束前断开。");
                }

                return Outcome(FailureClass.None);
            }

            // 干净地结束了，但一个 ContentEvent 都没有：失败的尝试。序幕留在缓冲里不动——
            // 还有候选就丢掉它换下一个；没有了，由调用方决定原样交出去。这里不写客户端。
            return Outcome(
                streamError is not null
                    ? FailureClassifier.FromErrorPayload(streamError)
                    : FailureClass.UpstreamServerError,
                streamError ?? "上游结束了流但没有输出。");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        ForwardOutcome Outcome(FailureClass failure, string? error = null) => new()
        {
            Committed = committer.Committed,
            FailureClass = failure,
            HttpStatusCode = (int)response.StatusCode,
            Usage = inspector.Usage,
            PartialUsage = inspector.PartialUsage,
            OutputAsciiChars = inspector.OutputAsciiChars,
            OutputWideChars = inspector.OutputWideChars,
            SawThinking = inspector.SawThinking,
            UpstreamModel = inspector.UpstreamModel,
            ResponseId = inspector.ResponseId,
            FinishReason = inspector.FinishReason,
            FirstEventAt = firstEventAt ?? headersAt,
            FirstContentEventAt = firstContentAt,
            ErrorMessage = error is null ? null : Truncate(error),
            EndedEmpty = inspector.EndedEmpty && !forcedCommit,
            ImageCount = inspector.ImageCount,
        };
    }

    /// <summary>
    /// 读一段上游字节。等待期间顺便处理两件事：到了上限就先发响应头，发过响应头就按节奏发 keepalive。
    /// 同一个读任务一直保留，不会并发发起第二次读。
    /// </summary>
    private async Task<int> ReadWithKeepAliveAsync(
        Stream upstream,
        byte[] buffer,
        ResponseCommitter committer,
        bool waitingForFirstEvent,
        RequestKind kind,
        CancellationToken cancellationToken)
    {
        TimeSpan budget = waitingForFirstEvent
            ? (kind == RequestKind.Image ? _timeouts.ImageFirstEvent : _timeouts.FirstEvent)
            : _timeouts.StreamIdle;

        using var timeout = new CancellationTokenSource(budget);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        Task<int> readTask = upstream.ReadAsync(buffer, linked.Token).AsTask();

        while (true)
        {
            TimeSpan? wait = NextHousekeepingDelay(committer);
            if (wait is null)
            {
                return await readTask.ConfigureAwait(false);
            }

            using var housekeeping = new CancellationTokenSource();
            Task delay = Task.Delay(wait.Value, _timeProvider, housekeeping.Token);
            Task finished = await Task.WhenAny(readTask, delay).ConfigureAwait(false);

            if (finished == readTask)
            {
                await housekeeping.CancelAsync().ConfigureAwait(false);
                return await readTask.ConfigureAwait(false);
            }

            if (committer.ShouldSendHeadersNow())
            {
                await committer.SendStreamHeadersEarlyAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (committer.NextKeepAliveAt is { } due && _timeProvider.GetUtcNow() >= due)
            {
                await committer.WriteKeepAliveAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>下一件要做的事还有多久。没有就返回 null。</summary>
    private TimeSpan? NextHousekeepingDelay(ResponseCommitter committer)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (committer.Committed)
        {
            return null;
        }

        if (!committer.HeadersSent)
        {
            if (committer.RequestStartedAt is not { } startedAt)
            {
                return null;
            }

            TimeSpan remaining = startedAt + ResponseCommitter.DefaultHeaderDelay - now;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        if (committer.NextKeepAliveAt is not { } due)
        {
            return null;
        }

        TimeSpan untilKeepAlive = due - now;
        return untilKeepAlive > TimeSpan.Zero ? untilKeepAlive : TimeSpan.Zero;
    }

    private static async Task<string> ReadBoundedStringAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            byte[] buffer = new byte[MaxErrorBodyBytes];
            int total = 0;

            while (total < buffer.Length)
            {
                int read = await stream
                    .ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return string.Empty;
        }
    }

    private static string? Truncate(string? value) =>
        value is null || value.Length <= 2000 ? value : value[..2000];
}
