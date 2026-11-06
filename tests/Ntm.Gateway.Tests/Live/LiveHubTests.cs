using Ntm.Gateway.Live;
using Ntm.Gateway.Traffic;
using Ntm.Protocol;

namespace Ntm.Gateway.Tests.Live;

/// <summary>实时链路的分发（Q114）：不阻塞发布者，跟不上的订阅者被断开，连接数有上限。</summary>
public class LiveHubTests
{
    private static RequestRecord Record() => new()
    {
        Id = Guid.NewGuid(),
        StartedAt = DateTimeOffset.UnixEpoch,
        PublishedModel = "gpt-4o",
        InboundApiFormat = ApiFormat.OpenAiChat,
        RequestKind = RequestKind.Chat,
        Status = RequestStatus.Running,
    };

    [Fact]
    public void ASubscriberThatFallsBehindIsDisconnectedInsteadOfBlocking()
    {
        var hub = new LiveHub();
        using LiveSubscription slow = hub.TrySubscribe()!;
        var registry = new InFlightRegistry(hub);

        for (int i = 0; i <= LiveHub.SubscriberBuffer; i++)
        {
            registry.Register(Record());
        }

        Assert.True(slow.Overflowed);
        Assert.Equal(0, hub.SubscriberCount);
        Assert.True(slow.Reader.Completion.IsCompleted || slow.Reader.Count == LiveHub.SubscriberBuffer);
    }

    [Fact]
    public void SubscriptionsAreCapped()
    {
        var hub = new LiveHub();
        var subscriptions = Enumerable.Range(0, LiveHub.MaxSubscribers).Select(_ => hub.TrySubscribe()!).ToList();

        Assert.Null(hub.TrySubscribe());
        subscriptions[0].Dispose();
        using LiveSubscription? again = hub.TrySubscribe();
        Assert.NotNull(again);

        foreach (LiveSubscription subscription in subscriptions)
        {
            subscription.Dispose();
        }
    }

    [Fact]
    public void ShutdownInterruptsEverythingButKeepsAnAdminInterruptAsAdmin()
    {
        var registry = new InFlightRegistry();
        RequestRecord first = Record();
        RequestRecord second = Record();
        InFlightRequest byAdmin = registry.Register(first);
        InFlightRequest other = registry.Register(second);

        Assert.True(registry.TryInterrupt(first.Id));
        Assert.Equal(2, registry.InterruptAllForShutdown());

        Assert.Equal(InterruptReason.Admin, byAdmin.InterruptReason);
        Assert.Equal(InterruptReason.Shutdown, other.InterruptReason);
        Assert.True(other.InterruptRequested);
    }

    [Fact]
    public void InterruptIsOnlyAcceptedWhileTheRequestIsInFlight()
    {
        var registry = new InFlightRegistry();
        RequestRecord record = Record();
        InFlightRequest running = registry.Register(record);

        Assert.True(registry.TryInterrupt(record.Id));
        Assert.True(running.InterruptRequested);
        Assert.True(running.ToLive().InterruptRequested);

        registry.Complete(record);
        Assert.False(registry.TryInterrupt(record.Id));
        Assert.Null(registry.Find(record.Id));
    }
}
