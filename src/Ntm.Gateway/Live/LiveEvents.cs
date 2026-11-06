using System.Text.Json.Serialization;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Gateway.Live;

/// <summary>
/// 实时链路的一条事件（Q40/Q113）：每个状态转换一条，只有 ID、名字、时刻和数字，不含请求体和回复内容。
/// <c>type</c> 字段区分种类，前端按它分派。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LiveSnapshot), "snapshot")]
[JsonDerivedType(typeof(RequestStartedEvent), "requestStarted")]
[JsonDerivedType(typeof(AttemptStartedEvent), "attemptStarted")]
[JsonDerivedType(typeof(AttemptFinishedEvent), "attemptFinished")]
[JsonDerivedType(typeof(CommittedEvent), "committed")]
[JsonDerivedType(typeof(RequestFinishedEvent), "requestFinished")]
public abstract record LiveEvent;

/// <summary>刚连上时的全量：所有进行中的 Request（Q110）。之后只发增量。</summary>
public sealed record LiveSnapshot(IReadOnlyList<LiveRequest> Requests) : LiveEvent;

public sealed record RequestStartedEvent(LiveRequest Request) : LiveEvent;

public sealed record AttemptStartedEvent(Guid RequestId, LiveAttempt Attempt) : LiveEvent;

/// <summary>一次 Attempt 结束。<see cref="LiveAttempt.FailureClass"/> 是 None 就是成功。</summary>
public sealed record AttemptFinishedEvent(Guid RequestId, LiveAttempt Attempt) : LiveEvent;

/// <summary>第一个内容事件已经发给客户端（Commit），之后不会再换候选。</summary>
public sealed record CommittedEvent(Guid RequestId, int AttemptNumber, DateTimeOffset CommittedAt, long? TimeToFirstContentMs)
    : LiveEvent;

public sealed record RequestFinishedEvent(
    Guid RequestId,
    RequestStatus Status,
    DateTimeOffset FinishedAt,
    long DurationMs,
    int? HttpStatusCode,
    long InputTokens,
    long OutputTokens,
    UsageSource UsageSource,
    long? CostNanoUsd) : LiveEvent;

/// <summary>进行中的一个 Request，附带它已经开始过的 Attempt。</summary>
public sealed record LiveRequest(
    Guid RequestId,
    DateTimeOffset StartedAt,
    int? ApiKeyId,
    string? ApiKeyName,
    RequestOrigin Origin,
    string PublishedModel,
    RequestKind RequestKind,
    bool IsStream,
    DateTimeOffset? CommittedAt,
    bool InterruptRequested,
    IReadOnlyList<LiveAttempt> Attempts);

public sealed record LiveAttempt(
    int AttemptNumber,
    int ChannelId,
    string ChannelName,
    string KeyPrefix,
    string UpstreamModel,
    ApiFormat UpstreamApiFormat,
    string RequestUri,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    FailureClass? FailureClass,
    int? HttpStatusCode,
    string? ErrorMessage,
    CooldownScope? CooldownScope,
    DateTimeOffset? CooldownUntil)
{
    /// <summary>从 Attempt 记录拷一份不可变的；没结束时结果字段为 null。</summary>
    public static LiveAttempt From(AttemptRecord attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        bool finished = attempt.FinishedAt is not null;
        return new LiveAttempt(
            attempt.Ordinal,
            attempt.ChannelId,
            attempt.ChannelName,
            attempt.ChannelKeyPrefix,
            attempt.UpstreamModel,
            attempt.UpstreamApiFormat,
            attempt.RequestUri,
            attempt.StartedAt,
            attempt.FinishedAt,
            finished ? attempt.FailureClass : null,
            attempt.HttpStatusCode,
            finished ? attempt.ErrorMessage : null,
            finished ? attempt.CooldownScope : null,
            attempt.CooldownUntil);
    }
}
