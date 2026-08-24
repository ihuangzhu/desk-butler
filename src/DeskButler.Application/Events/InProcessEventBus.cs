namespace DeskButler.Application.Events;

/// <summary>在当前进程内同步维护订阅关系并发布事件。</summary>
public sealed class InProcessEventBus : IEventBus
{
    private readonly object syncRoot = new();
    private readonly Dictionary<Type, List<IEventSubscriber>> subscribers = [];

    /// <summary>初始化空的进程内事件订阅表。</summary>
    public InProcessEventBus()
    {
    }

    /// <inheritdoc />
    public IDisposable Subscribe<TEvent>(string subscriberId, Func<TEvent, CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriberId);
        ArgumentNullException.ThrowIfNull(handler);

        var subscriber = new EventSubscriber<TEvent>(subscriberId, handler);
        lock (syncRoot)
        {
            // 订阅表在锁内更新，发布时仅复制当前列表以避免回调修改枚举集合。
            if (!subscribers.TryGetValue(typeof(TEvent), out var eventSubscribers))
            {
                eventSubscribers = [];
                subscribers.Add(typeof(TEvent), eventSubscribers);
            }

            eventSubscribers.Add(subscriber);
        }

        return new Subscription(this, typeof(TEvent), subscriber);
    }

    /// <inheritdoc />
    public async Task<EventPublishResult> PublishAsync<TEvent>(TEvent domainEvent, CancellationToken cancellationToken)
    {
        IEventSubscriber[] snapshot;
        lock (syncRoot)
        {
            // 回调执行前复制订阅列表，确保本轮发布的接收者集合固定。
            snapshot = subscribers.TryGetValue(typeof(TEvent), out var eventSubscribers)
                ? [.. eventSubscribers]
                : [];
        }

        var failures = new List<EventSubscriberFailure>();
        foreach (var subscriber in snapshot)
        {
            try
            {
                await subscriber.HandleAsync(domainEvent!, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(new EventSubscriberFailure(subscriber.Id, exception));
            }
        }

        return new EventPublishResult(failures);
    }

    /// <summary>从订阅表中移除指定订阅者。</summary>
    /// <param name="eventType">订阅事件的类型。</param>
    /// <param name="subscriber">要移除的订阅者。</param>
    private void Unsubscribe(Type eventType, IEventSubscriber subscriber)
    {
        lock (syncRoot)
        {
            if (!subscribers.TryGetValue(eventType, out var eventSubscribers))
            {
                return;
            }

            eventSubscribers.Remove(subscriber);
            if (eventSubscribers.Count == 0)
            {
                subscribers.Remove(eventType);
            }
        }
    }

    /// <summary>定义事件订阅项的非泛型调用边界。</summary>
    private interface IEventSubscriber
    {
        /// <summary>获取订阅者的稳定标识。</summary>
        string Id { get; }

        /// <summary>使用运行时事件实例调用订阅者。</summary>
        /// <param name="domainEvent">待发布的事件实例。</param>
        /// <param name="cancellationToken">传递给订阅者的取消令牌。</param>
        /// <returns>订阅者处理完成的任务。</returns>
        Task HandleAsync(object domainEvent, CancellationToken cancellationToken);
    }

    /// <summary>保存某一强类型事件订阅者，并将其适配为内部调用边界。</summary>
    /// <typeparam name="TEvent">订阅事件的类型。</typeparam>
    private sealed class EventSubscriber<TEvent> : IEventSubscriber
    {
        private readonly Func<TEvent, CancellationToken, Task> handler;

        /// <summary>使用订阅者标识和处理委托初始化订阅项。</summary>
        /// <param name="id">订阅者的稳定标识。</param>
        /// <param name="handler">处理事件的强类型委托。</param>
        public EventSubscriber(string id, Func<TEvent, CancellationToken, Task> handler)
        {
            Id = id;
            this.handler = handler;
        }

        /// <inheritdoc />
        public string Id { get; }

        /// <inheritdoc />
        public Task HandleAsync(object domainEvent, CancellationToken cancellationToken)
        {
            return handler((TEvent)domainEvent, cancellationToken);
        }
    }

    /// <summary>表示可安全重复释放的事件订阅句柄。</summary>
    private sealed class Subscription : IDisposable
    {
        private readonly InProcessEventBus eventBus;
        private readonly Type eventType;
        private IEventSubscriber? subscriber;

        /// <summary>使用订阅所属总线、事件类型和订阅者初始化句柄。</summary>
        /// <param name="eventBus">持有订阅表的事件总线。</param>
        /// <param name="eventType">订阅事件的类型。</param>
        /// <param name="subscriber">要在释放时移除的订阅者。</param>
        public Subscription(InProcessEventBus eventBus, Type eventType, IEventSubscriber subscriber)
        {
            this.eventBus = eventBus;
            this.eventType = eventType;
            this.subscriber = subscriber;
        }

        /// <summary>释放句柄并仅移除一次关联订阅。</summary>
        public void Dispose()
        {
            var currentSubscriber = Interlocked.Exchange(ref subscriber, null);
            if (currentSubscriber is not null)
            {
                eventBus.Unsubscribe(eventType, currentSubscriber);
            }
        }
    }
}
