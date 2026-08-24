using Microsoft.Win32;

namespace DeskButler.Infrastructure.Windows.Session;

/// <summary>把 Windows 会话结束通知桥接为异步、有界的最终 checkpoint 请求。</summary>
public sealed class WindowsSessionEvents : IDisposable
{
    private static readonly TimeSpan DefaultCheckpointTimeout = TimeSpan.FromSeconds(5);

    private readonly object syncRoot = new();
    private readonly Func<CancellationToken, Task> requestFinalCheckpoint;
    private readonly ISessionEndingSource source;
    private readonly ISessionEventDelay delay;
    private readonly TimeSpan checkpointTimeout;
    private SubscriptionState subscriptionState;
    private bool requestQueued;
    private Task lastRequestCompletion = Task.CompletedTask;
    private Task lastLateObservation = Task.CompletedTask;
    private Exception? lastFailure;

    /// <summary>使用真实 SystemEvents 和默认五秒预算创建桥接。</summary>
    public WindowsSessionEvents(Func<CancellationToken, Task> requestFinalCheckpoint)
        : this(
            requestFinalCheckpoint,
            new SystemSessionEndingSource(),
            new SystemSessionEventDelay(),
            DefaultCheckpointTimeout)
    {
    }

    /// <summary>使用可控事件源和 delay 创建默认五秒预算的桥接。</summary>
    internal WindowsSessionEvents(
        Func<CancellationToken, Task> requestFinalCheckpoint,
        ISessionEndingSource source,
        ISessionEventDelay delay)
        : this(requestFinalCheckpoint, source, delay, DefaultCheckpointTimeout)
    {
    }

    /// <summary>使用可控事件源、delay 和预算创建桥接。</summary>
    internal WindowsSessionEvents(
        Func<CancellationToken, Task> requestFinalCheckpoint,
        ISessionEndingSource source,
        ISessionEventDelay delay,
        TimeSpan checkpointTimeout)
    {
        this.requestFinalCheckpoint = requestFinalCheckpoint ?? throw new ArgumentNullException(nameof(requestFinalCheckpoint));
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(checkpointTimeout, TimeSpan.Zero);
        this.checkpointTimeout = checkpointTimeout;
    }

    /// <summary>获取最近一次 final-checkpoint 请求的可观察失败。</summary>
    public Exception? LastFailure
    {
        get
        {
            lock (syncRoot)
            {
                return lastFailure;
            }
        }
    }

    /// <summary>获取最后一个已排队请求的稳定完成句柄，供边界测试使用。</summary>
    internal Task LastRequestCompletion
    {
        get
        {
            lock (syncRoot)
            {
                return lastRequestCompletion;
            }
        }
    }

    /// <summary>获取最近一个迟到 callback 观察器的稳定完成句柄。</summary>
    internal Task LastLateObservation
    {
        get
        {
            lock (syncRoot)
            {
                return lastLateObservation;
            }
        }
    }

    /// <summary>幂等订阅 Windows 会话结束事件。</summary>
    public void Start()
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(subscriptionState == SubscriptionState.Disposed, this);
            if (subscriptionState is SubscriptionState.Starting or SubscriptionState.Started or SubscriptionState.Disposing)
            {
                return;
            }

            // 先发布 Starting，再离锁订阅；Subscribe 内同步到达的事件也必须被接受。
            subscriptionState = SubscriptionState.Starting;
        }

        try
        {
            source.SessionEnding += OnSessionEnding;
        }
        catch
        {
            lock (syncRoot)
            {
                if (subscriptionState == SubscriptionState.Starting)
                {
                    subscriptionState = SubscriptionState.Stopped;
                }
                else if (subscriptionState == SubscriptionState.Disposing)
                {
                    subscriptionState = SubscriptionState.Disposed;
                }
            }

            throw;
        }

        var removeAfterConcurrentDispose = false;
        lock (syncRoot)
        {
            if (subscriptionState == SubscriptionState.Disposing)
            {
                removeAfterConcurrentDispose = true;
            }
            else
            {
                subscriptionState = SubscriptionState.Started;
            }
        }

        if (removeAfterConcurrentDispose)
        {
            try
            {
                source.SessionEnding -= OnSessionEnding;
            }
            catch
            {
                lock (syncRoot)
                {
                    if (subscriptionState == SubscriptionState.Disposing)
                    {
                        subscriptionState = SubscriptionState.Started;
                    }
                }

                throw;
            }

            lock (syncRoot)
            {
                subscriptionState = SubscriptionState.Disposed;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var removeSubscription = false;
        lock (syncRoot)
        {
            if (subscriptionState == SubscriptionState.Disposed)
            {
                return;
            }

            if (subscriptionState == SubscriptionState.Stopped)
            {
                subscriptionState = SubscriptionState.Disposed;
                return;
            }

            if (subscriptionState == SubscriptionState.Starting)
            {
                // Start 在外部 Subscribe 返回后负责退订；失败则回滚为 Started 供后续重试。
                subscriptionState = SubscriptionState.Disposing;
                return;
            }

            if (subscriptionState == SubscriptionState.Disposing)
            {
                return;
            }

            removeSubscription = true;
            subscriptionState = SubscriptionState.Disposing;
        }

        if (removeSubscription)
        {
            try
            {
                source.SessionEnding -= OnSessionEnding;
            }
            catch
            {
                lock (syncRoot)
                {
                    if (subscriptionState == SubscriptionState.Disposing)
                    {
                        subscriptionState = SubscriptionState.Started;
                    }
                }

                throw;
            }

            lock (syncRoot)
            {
                subscriptionState = SubscriptionState.Disposed;
            }
        }
    }

    /// <summary>系统事件线程只发布一次稳定完成句柄并排队，不执行任何 checkpoint 或取消回调。</summary>
    private void OnSessionEnding(object? sender, EventArgs eventArgs)
    {
        TaskCompletionSource? completion = null;
        try
        {
            lock (syncRoot)
            {
                if (subscriptionState is not (
                        SubscriptionState.Starting or
                        SubscriptionState.Started or
                        SubscriptionState.Disposing) || requestQueued)
                {
                    return;
                }

                requestQueued = true;
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lastRequestCompletion = completion.Task;
            }

            var queued = ThreadPool.QueueUserWorkItem(
                static state => state.Owner.BeginQueuedRequest(state.Completion),
                new QueuedRequest(this, completion),
                preferLocal: false);
            if (!queued)
            {
                var failure = new InvalidOperationException("无法将会话结束 checkpoint 排入线程池。");
                SetLastFailure(failure);
                completion.TrySetResult();
            }
        }
        catch (Exception exception)
        {
            // 原生 SystemEvents 回调边界绝不允许托管异常越出。
            SetLastFailure(exception);
            completion?.TrySetResult();
        }
    }

    /// <summary>在线程池启动有界请求，并通过 continuation 收口全部完成路径。</summary>
    private void BeginQueuedRequest(TaskCompletionSource completion)
    {
        Task requestTask;
        try
        {
            requestTask = RequestWithinLimitAsync();
        }
        catch (Exception exception)
        {
            SetLastFailure(exception);
            completion.TrySetResult();
            return;
        }

        _ = requestTask.ContinueWith(
            static (completed, state) =>
            {
                _ = completed.Exception;
                ((TaskCompletionSource)state!).TrySetResult();
            },
            completion,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>在 callback 完成和可控预算之间竞速；超时后不等待不合作任务。</summary>
    private async Task RequestWithinLimitAsync()
    {
        var callbackCancellation = new CancellationTokenSource();
        Task timeoutTask;
        try
        {
            // 预算必须先启动，避免 callback 在返回 Task 之前同步阻塞而绕过上限。
            timeoutTask = delay.DelayAsync(checkpointTimeout)
                ?? throw new InvalidOperationException("Session delay 返回了空任务。");
        }
        catch (Exception exception)
        {
            callbackCancellation.Dispose();
            SetLastFailure(exception);
            return;
        }

        Task callbackTask;
        try
        {
            var callbackToken = callbackCancellation.Token;
            callbackTask = Task.Run(async () =>
            {
                var requested = requestFinalCheckpoint(callbackToken)
                    ?? throw new InvalidOperationException("Final-checkpoint callback 返回了空任务。");
                await requested.ConfigureAwait(false);
            });
        }
        catch (Exception exception)
        {
            callbackCancellation.Dispose();
            SetLastFailure(exception);
            return;
        }

        var completedTask = await Task.WhenAny(callbackTask, timeoutTask).ConfigureAwait(false);
        if (callbackTask.IsCompleted || ReferenceEquals(completedTask, callbackTask))
        {
            try
            {
                await callbackTask.ConfigureAwait(false);
                SetLastFailure(null);
            }
            catch (Exception exception)
            {
                SetLastFailure(exception);
            }
            finally
            {
                callbackCancellation.Dispose();
            }

            return;
        }

        if (timeoutTask.IsFaulted || timeoutTask.IsCanceled)
        {
            try
            {
                await timeoutTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                SetLastFailure(exception);
                ObserveLateCompletion(callbackTask, exception);
                QueueCancellation(callbackCancellation, callbackTask, exception);
            }

            return;
        }

        var timeoutFailure = new TimeoutException($"会话结束 checkpoint 未能在 {checkpointTimeout} 内完成。");
        SetLastFailure(timeoutFailure);
        ObserveLateCompletion(callbackTask, timeoutFailure);
        QueueCancellation(callbackCancellation, callbackTask, timeoutFailure);
    }

    /// <summary>在独立工作项触发取消，避免阻塞式 token callback 卡住有界 wrapper。</summary>
    private void QueueCancellation(
        CancellationTokenSource sourceToCancel,
        Task callbackTask,
        Exception boundaryFailure)
    {
        var lifetime = new CancellationLifetime(sourceToCancel);
        _ = callbackTask.ContinueWith(
            static (_, state) => ((CancellationLifetime)state!).Release(),
            lifetime,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var cancellationTask = Task.Run(sourceToCancel.Cancel);
        _ = cancellationTask.ContinueWith(
            static (completed, state) =>
            {
                var observation = (CancellationObservation)state!;
                try
                {
                    if (completed.IsFaulted)
                    {
                        observation.Owner.CombineLateFailure(
                            observation.BoundaryFailure,
                            completed.Exception!.Flatten().InnerExceptions);
                    }
                }
                finally
                {
                    observation.Lifetime.Release();
                }
            },
            new CancellationObservation(this, lifetime, boundaryFailure),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>观察超时后 callback 的迟到 fault；取消和成功不会覆盖既有超时。</summary>
    private void ObserveLateCompletion(Task callbackTask, Exception boundaryFailure)
    {
        var observationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (syncRoot)
        {
            lastLateObservation = observationCompletion.Task;
        }

        _ = callbackTask.ContinueWith(
            static (completed, state) =>
            {
                var observation = (LateFailureObservation)state!;
                try
                {
                    if (completed.IsFaulted)
                    {
                        observation.Owner.CombineLateFailure(
                            observation.BoundaryFailure,
                            completed.Exception!.Flatten().InnerExceptions);
                    }
                }
                finally
                {
                    observation.Completion.TrySetResult();
                }
            },
            new LateFailureObservation(this, boundaryFailure, observationCompletion),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>线程安全更新最近失败状态。</summary>
    private void SetLastFailure(Exception? failure)
    {
        lock (syncRoot)
        {
            lastFailure = failure;
        }
    }

    /// <summary>把边界失败与迟到失败合并，完整保留诊断上下文。</summary>
    private void CombineLateFailure(Exception boundaryFailure, IEnumerable<Exception> lateFailures)
    {
        lock (syncRoot)
        {
            var accumulated = lastFailure is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions
                : [boundaryFailure];
            lastFailure = new AggregateException(
                "会话结束 checkpoint 越过预算后又发生失败。",
                [.. accumulated, .. lateFailures]);
        }
    }

    private sealed record QueuedRequest(WindowsSessionEvents Owner, TaskCompletionSource Completion);

    private sealed record LateFailureObservation(
        WindowsSessionEvents Owner,
        Exception BoundaryFailure,
        TaskCompletionSource Completion);

    private sealed record CancellationObservation(
        WindowsSessionEvents Owner,
        CancellationLifetime Lifetime,
        Exception BoundaryFailure);

    private enum SubscriptionState
    {
        Stopped,
        Starting,
        Started,
        Disposing,
        Disposed
    }

    /// <summary>只有 callback 与后台 Cancel 都结束后才释放仍被 token 使用的 CTS。</summary>
    private sealed class CancellationLifetime
    {
        private readonly CancellationTokenSource source;
        private int remainingOwners = 2;

        internal CancellationLifetime(CancellationTokenSource source)
        {
            this.source = source;
        }

        internal void Release()
        {
            if (Interlocked.Decrement(ref remainingOwners) == 0)
            {
                source.Dispose();
            }
        }
    }
}

/// <summary>隔离静态 SystemEvents.SessionEnding 订阅。</summary>
internal interface ISessionEndingSource
{
    /// <summary>当 Windows 正在注销或关闭会话时触发。</summary>
    event EventHandler? SessionEnding;
}

/// <summary>提供可控的 session checkpoint 预算等待。</summary>
internal interface ISessionEventDelay
{
    /// <summary>等待指定预算，不携带 callback 的取消令牌。</summary>
    Task DelayAsync(TimeSpan duration);
}

/// <summary>隔离真实 SystemEvents 的 attach/detach，供进程级 source 聚合订阅。</summary>
internal interface ISystemSessionEndingNativeEvents
{
    /// <summary>挂接唯一进程级 handler。</summary>
    void Subscribe(EventHandler handler);

    /// <summary>移除唯一进程级 handler。</summary>
    void Unsubscribe(EventHandler handler);
}

/// <summary>进程级共享 SystemEvents.SessionEnding；所有 source 实例只建立一个 native 订阅。</summary>
internal sealed class SystemSessionEndingSource : ISessionEndingSource
{
    private static readonly object NativeSyncRoot = new();
    private static readonly List<HandlerRegistration> Handlers = [];
    private static ISystemSessionEndingNativeEvents? activeNativeEvents;
    private static NativeSubscriptionState nativeState;

    private readonly ISystemSessionEndingNativeEvents nativeEvents;

    /// <summary>使用进程级真实 SystemEvents adapter。</summary>
    internal SystemSessionEndingSource()
        : this(SystemEventsSessionEndingNativeEvents.Instance)
    {
    }

    /// <summary>使用显式 native adapter，供隔离集成测试共享。</summary>
    internal SystemSessionEndingSource(ISystemSessionEndingNativeEvents nativeEvents)
    {
        this.nativeEvents = nativeEvents ?? throw new ArgumentNullException(nameof(nativeEvents));
    }

    public event EventHandler? SessionEnding
    {
        add
        {
            if (value is null)
            {
                return;
            }

            HandlerRegistration registration;
            lock (NativeSyncRoot)
            {
                ThrowIfNativeTransitionInProgress();
                if (activeNativeEvents is not null && !ReferenceEquals(activeNativeEvents, nativeEvents))
                {
                    throw new InvalidOperationException("进程内 SystemSessionEndingSource 必须共享同一个 native adapter。");
                }

                registration = new HandlerRegistration(this, value);
                Handlers.Add(registration);
                activeNativeEvents ??= nativeEvents;
                if (nativeState == NativeSubscriptionState.Attached)
                {
                    return;
                }

                nativeState = NativeSubscriptionState.Attaching;
            }

            try
            {
                // registration 已先发布；同步或跨线程 native callback 都可在锁外取得快照。
                nativeEvents.Subscribe(OnNativeSessionEnding);
            }
            catch
            {
                lock (NativeSyncRoot)
                {
                    Handlers.Remove(registration);
                    nativeState = NativeSubscriptionState.Detached;
                    if (Handlers.Count == 0)
                    {
                        activeNativeEvents = null;
                    }
                }

                throw;
            }

            lock (NativeSyncRoot)
            {
                if (nativeState != NativeSubscriptionState.Attaching)
                {
                    throw new InvalidOperationException("native SessionEnding attach 状态已被意外改变。");
                }

                nativeState = NativeSubscriptionState.Attached;
            }
        }
        remove
        {
            if (value is null)
            {
                return;
            }

            HandlerRegistration registration;
            ISystemSessionEndingNativeEvents adapterToDetach;
            lock (NativeSyncRoot)
            {
                ThrowIfNativeTransitionInProgress();
                var index = Handlers.FindLastIndex(registration =>
                    ReferenceEquals(registration.Source, this) && registration.Handler == value);
                if (index < 0)
                {
                    return;
                }

                if (Handlers.Count > 1)
                {
                    Handlers.RemoveAt(index);
                    return;
                }

                registration = Handlers[index];
                adapterToDetach = activeNativeEvents
                    ?? throw new InvalidOperationException("存在 SessionEnding handler 但 native adapter 不可用。");
                if (nativeState != NativeSubscriptionState.Attached)
                {
                    throw new InvalidOperationException("存在 SessionEnding handler 但 native attach 状态无效。");
                }

                // 两阶段 detach：成功前保留 registration，失败后仍能收事件并重试。
                nativeState = NativeSubscriptionState.Detaching;
            }

            try
            {
                adapterToDetach.Unsubscribe(OnNativeSessionEnding);
            }
            catch
            {
                lock (NativeSyncRoot)
                {
                    if (nativeState == NativeSubscriptionState.Detaching)
                    {
                        nativeState = NativeSubscriptionState.Attached;
                    }
                }

                throw;
            }

            lock (NativeSyncRoot)
            {
                if (nativeState != NativeSubscriptionState.Detaching)
                {
                    throw new InvalidOperationException("native SessionEnding detach 状态已被意外改变。");
                }

                Handlers.Remove(registration);
                nativeState = NativeSubscriptionState.Detached;
                activeNativeEvents = null;
            }
        }
    }

    /// <summary>过渡期间拒绝新的 add/remove，调用方可在当前 attach/detach 完成后重试。</summary>
    private static void ThrowIfNativeTransitionInProgress()
    {
        if (nativeState is NativeSubscriptionState.Attaching or NativeSubscriptionState.Detaching)
        {
            throw new InvalidOperationException("native SessionEnding 订阅正在切换，请稍后重试。");
        }
    }

    /// <summary>复制静态 handler 快照后逐个隔离，任何订阅者异常都不越 native 回调。</summary>
    private static void OnNativeSessionEnding(object? sender, EventArgs eventArgs)
    {
        HandlerRegistration[] snapshot;
        lock (NativeSyncRoot)
        {
            snapshot = [.. Handlers];
        }

        foreach (var registration in snapshot)
        {
            try
            {
                registration.Handler(registration.Source, EventArgs.Empty);
            }
            catch (Exception)
            {
                // 每个 WindowsSessionEvents 自身会观察错误；额外订阅者也不能污染系统回调。
            }
        }
    }

    private sealed record HandlerRegistration(SystemSessionEndingSource Source, EventHandler Handler);

    private enum NativeSubscriptionState
    {
        Detached,
        Attaching,
        Attached,
        Detaching
    }
}

/// <summary>把 Microsoft.Win32.SystemEvents 转换为唯一无敏感负载进程级事件。</summary>
internal sealed class SystemEventsSessionEndingNativeEvents : ISystemSessionEndingNativeEvents
{
    private readonly object syncRoot = new();
    private EventHandler? handler;

    private SystemEventsSessionEndingNativeEvents()
    {
    }

    internal static SystemEventsSessionEndingNativeEvents Instance { get; } = new();

    /// <inheritdoc />
    public void Subscribe(EventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (syncRoot)
        {
            if (this.handler is not null)
            {
                throw new InvalidOperationException("SystemEvents.SessionEnding 已有进程级订阅。");
            }

            this.handler = handler;
            try
            {
                SystemEvents.SessionEnding += OnSystemSessionEnding;
            }
            catch
            {
                this.handler = null;
                throw;
            }
        }
    }

    /// <inheritdoc />
    public void Unsubscribe(EventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (syncRoot)
        {
            if (this.handler != handler)
            {
                return;
            }

            SystemEvents.SessionEnding -= OnSystemSessionEnding;
            this.handler = null;
        }
    }

    /// <summary>丢弃系统原因负载，只转发生命周期信号。</summary>
    private void OnSystemSessionEnding(object sender, SessionEndingEventArgs eventArgs)
    {
        EventHandler? snapshot;
        lock (syncRoot)
        {
            snapshot = handler;
        }

        snapshot?.Invoke(sender, EventArgs.Empty);
    }
}

/// <summary>使用系统计时器实现真实会话预算等待。</summary>
internal sealed class SystemSessionEventDelay : ISessionEventDelay
{
    /// <inheritdoc />
    public Task DelayAsync(TimeSpan duration) => Task.Delay(duration);
}
