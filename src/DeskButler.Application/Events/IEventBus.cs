namespace DeskButler.Application.Events;

/// <summary>定义在当前进程内发布和订阅应用事件的总线。</summary>
public interface IEventBus
{
    /// <summary>订阅指定类型的事件。</summary>
    /// <typeparam name="TEvent">要接收的事件类型。</typeparam>
    /// <param name="subscriberId">订阅者的稳定标识，用于诊断发布失败。</param>
    /// <param name="handler">接收事件的异步处理委托。</param>
    /// <returns>用于取消订阅的句柄。</returns>
    IDisposable Subscribe<TEvent>(string subscriberId, Func<TEvent, CancellationToken, Task> handler);

    /// <summary>向当前订阅者发布事件并返回全部处理失败。</summary>
    /// <typeparam name="TEvent">要发布的事件类型。</typeparam>
    /// <param name="domainEvent">要发布的事件实例。</param>
    /// <param name="cancellationToken">传递给各订阅者的取消令牌。</param>
    /// <returns>包含所有订阅者处理失败的发布结果。</returns>
    Task<EventPublishResult> PublishAsync<TEvent>(TEvent domainEvent, CancellationToken cancellationToken);
}

/// <summary>表示一次事件发布完成后的聚合结果。</summary>
public sealed class EventPublishResult
{
    /// <summary>使用订阅者失败集合初始化发布结果。</summary>
    /// <param name="failures">发布期间捕获的订阅者失败。</param>
    public EventPublishResult(IEnumerable<EventSubscriberFailure> failures)
    {
        Failures = failures.ToArray();
    }

    /// <summary>获取发布期间捕获的订阅者失败。</summary>
    public IReadOnlyList<EventSubscriberFailure> Failures { get; }
}

/// <summary>描述一个订阅者处理事件时发生的异常。</summary>
public sealed class EventSubscriberFailure
{
    /// <summary>使用订阅者标识和异常初始化失败信息。</summary>
    /// <param name="subscriberId">失败订阅者的稳定标识。</param>
    /// <param name="exception">订阅者处理事件时抛出的异常。</param>
    public EventSubscriberFailure(string subscriberId, Exception exception)
    {
        SubscriberId = subscriberId;
        Exception = exception;
    }

    /// <summary>获取失败订阅者的稳定标识。</summary>
    public string SubscriberId { get; }

    /// <summary>获取订阅者处理事件时抛出的原始异常。</summary>
    public Exception Exception { get; }
}
