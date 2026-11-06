using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Ntm.Gateway.Tests.Forwarding;

/// <summary>
/// 一个会记录写入内容的 HttpResponse：响应头发出的时刻、发出时的状态码和头、以及之后的每一段字节。
/// 用它能直接验到「提交之前客户端一个字节都没拿到」。
/// </summary>
public sealed class RecordingHttpResponse : IDisposable
{
    private readonly MemoryStream _body = new();
    private readonly Feature _feature;

    public RecordingHttpResponse()
    {
        var context = new DefaultHttpContext();
        _feature = new Feature(_body, this);
        context.Features.Set<IHttpResponseFeature>(_feature);
        context.Features.Set<IHttpResponseBodyFeature>(_feature);
        Response = context.Response;
    }

    public HttpResponse Response { get; }

    /// <summary>响应头是否已经发出。</summary>
    public bool HasStarted => _feature.HasStarted;

    /// <summary>响应头发出时的状态码。</summary>
    public int? StatusCodeWhenStarted { get; private set; }

    /// <summary>响应头发出时的头快照。</summary>
    public Dictionary<string, string> HeadersWhenStarted { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>是否调用过 DisableBuffering。</summary>
    public bool BufferingDisabled { get; private set; }

    /// <summary>目前客户端收到的全部字节。</summary>
    public byte[] Written => _body.ToArray();

    public string WrittenText => System.Text.Encoding.UTF8.GetString(Written);

    public void Dispose() => _body.Dispose();

    private void OnStarting()
    {
        StatusCodeWhenStarted = Response.StatusCode;
        foreach ((string name, var values) in Response.Headers)
        {
            HeadersWhenStarted[name] = values.ToString();
        }
    }

    private sealed class Feature(MemoryStream body, RecordingHttpResponse owner)
        : IHttpResponseFeature, IHttpResponseBodyFeature
    {
        public int StatusCode { get; set; } = 200;

        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public Stream Body
        {
            get => body;
            set => throw new NotSupportedException();
        }

        public Stream Stream => body;

        public PipeWriter Writer => PipeWriter.Create(body);

        public bool HasStarted { get; private set; }

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }

        public void DisableBuffering() => owner.BufferingDisabled = true;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (!HasStarted)
            {
                HasStarted = true;
                owner.OnStarting();
            }

            return Task.CompletedTask;
        }

        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CompleteAsync() => Task.CompletedTask;
    }
}
