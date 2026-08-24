using DeskButler.Application.Events;

namespace DeskButler.Application.Tests.Events;

public sealed class EventBusTests
{
    /// <summary>验证发布使用订阅快照，使发布期间新增的订阅者仅从下一次发布开始接收事件。</summary>
    [Fact]
    public async Task PublishAsyncUsesSubscriptionSnapshotWhenHandlerSubscribesAnotherHandler()
    {
        var bus = new InProcessEventBus();
        var calls = new List<string>();
        var secondSubscriberAdded = false;

        bus.Subscribe<TestEvent>("first", (_, _) =>
        {
            calls.Add("first");
            if (!secondSubscriberAdded)
            {
                secondSubscriberAdded = true;
                bus.Subscribe<TestEvent>("second", (_, _) =>
                {
                    calls.Add("second");
                    return Task.CompletedTask;
                });
            }

            return Task.CompletedTask;
        });

        var firstPublish = await bus.PublishAsync(new TestEvent(), CancellationToken.None);

        Assert.Empty(firstPublish.Failures);
        Assert.Equal(["first"], calls);

        calls.Clear();
        var secondPublish = await bus.PublishAsync(new TestEvent(), CancellationToken.None);

        Assert.Empty(secondPublish.Failures);
        Assert.Equal(["first", "second"], calls);
    }

    /// <summary>验证单个订阅者失败不会阻止后续订阅者，并将失败异常及订阅者标识返回给调用方。</summary>
    [Fact]
    public async Task PublishAsyncAggregatesSubscriberFailureAndContinuesOtherHandlers()
    {
        var bus = new InProcessEventBus();
        var successfulCalls = new List<string>();
        bus.Subscribe<TestEvent>("failing", (_, _) => Task.FromException(new InvalidOperationException("订阅失败")));
        bus.Subscribe<TestEvent>("successful", (_, _) =>
        {
            successfulCalls.Add("successful");
            return Task.CompletedTask;
        });

        var result = await bus.PublishAsync(new TestEvent(), CancellationToken.None);

        var failure = Assert.Single(result.Failures);
        Assert.Equal("failing", failure.SubscriberId);
        Assert.IsType<InvalidOperationException>(failure.Exception);
        Assert.Equal(["successful"], successfulCalls);
    }

    private sealed class TestEvent
    {
        /// <summary>初始化测试事件。</summary>
        public TestEvent()
        {
        }
    }
}
