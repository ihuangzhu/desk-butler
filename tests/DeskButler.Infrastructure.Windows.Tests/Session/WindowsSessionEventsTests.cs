using DeskButler.Infrastructure.Windows.Session;

namespace DeskButler.Infrastructure.Windows.Tests.Session;

public sealed class WindowsSessionEventsTests
{
    /// <summary>启动和释放必须幂等，且释放后不再接受系统会话事件。</summary>
    [Fact]
    public void StartAndDisposeManageSubscriptionIdempotently()
    {
        var source = new FakeSessionEndingSource();
        var delay = new ManualDelay();
        var callbackCount = 0;
        var events = new WindowsSessionEvents(
            _ =>
            {
                Interlocked.Increment(ref callbackCount);
                return Task.CompletedTask;
            },
            source,
            delay,
            TimeSpan.FromSeconds(5));

        events.Start();
        events.Start();
        events.Dispose();
        events.Dispose();
        source.Raise();

        Assert.Equal(1, source.SubscriptionCount);
        Assert.Equal(1, source.UnsubscriptionCount);
        Assert.Equal(0, Volatile.Read(ref callbackCount));
    }

    /// <summary>Subscribe 内同步到达的事件发生在 Starting 窗口，也不得丢失。</summary>
    [Fact]
    public async Task EventRaisedInsideSubscriptionIsAcceptedDuringStarting()
    {
        var source = new RaisingDuringSubscriptionSource();
        var callbackCount = 0;
        using var events = new WindowsSessionEvents(
            _ =>
            {
                Interlocked.Increment(ref callbackCount);
                return Task.CompletedTask;
            },
            source,
            new ManualDelay(),
            TimeSpan.FromSeconds(5));

        events.Start();
        await events.LastRequestCompletion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref callbackCount));
    }

    /// <summary>Dispose 与 Starting 竞争时，Subscribe 返回后必须立即补退订且拒绝后续事件。</summary>
    [Fact]
    public async Task DisposeDuringSubscriptionUnsubscribesAfterSubscribeReturns()
    {
        var source = new BlockingSubscriptionSource();
        var callbackCount = 0;
        var events = new WindowsSessionEvents(
            _ =>
            {
                Interlocked.Increment(ref callbackCount);
                return Task.CompletedTask;
            },
            source,
            new ManualDelay(),
            TimeSpan.FromSeconds(5));
        var startTask = Task.Run(events.Start, TestContext.Current.CancellationToken);

        Assert.True(source.SubscriptionEntered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        events.Dispose();
        Assert.Equal(0, source.UnsubscriptionCount);
        source.AllowSubscriptionToReturn.Set();
        await startTask.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, source.SubscriptionCount);
        Assert.Equal(1, source.UnsubscriptionCount);
        source.Raise();
        Assert.Equal(0, Volatile.Read(ref callbackCount));
    }

    /// <summary>退订失败必须回滚为 Started，使事件仍被接受且下一次 Dispose 可以重试。</summary>
    [Fact]
    public async Task FailedUnsubscriptionCanBeRetriedWithoutLosingEvents()
    {
        var source = new FailsFirstUnsubscriptionSource();
        var callbackCount = 0;
        var events = new WindowsSessionEvents(
            _ =>
            {
                Interlocked.Increment(ref callbackCount);
                return Task.CompletedTask;
            },
            source,
            new ManualDelay(),
            TimeSpan.FromSeconds(5));
        events.Start();

        Assert.Throws<InvalidOperationException>(events.Dispose);
        source.Raise();
        await events.LastRequestCompletion.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref callbackCount));

        events.Dispose();
        source.Raise();
        Assert.Equal(1, Volatile.Read(ref callbackCount));
        Assert.Equal(2, source.UnsubscriptionAttempts);
    }

    /// <summary>Subscribe 失败必须回滚为 Stopped，使下一次 Start 可以重试。</summary>
    [Fact]
    public async Task SubscriptionFailureRollsBackAndCanBeRetried()
    {
        var source = new FailsFirstSubscriptionSource();
        var callbackCount = 0;
        using var events = new WindowsSessionEvents(
            _ =>
            {
                Interlocked.Increment(ref callbackCount);
                return Task.CompletedTask;
            },
            source,
            new ManualDelay(),
            TimeSpan.FromSeconds(5));

        Assert.Throws<InvalidOperationException>(events.Start);
        events.Start();
        source.Raise();
        await events.LastRequestCompletion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, source.SubscriptionAttempts);
        Assert.Equal(1, Volatile.Read(ref callbackCount));
    }

    /// <summary>多个 source 实例必须共享一次 native 订阅，并逐个隔离 handler 异常。</summary>
    [Fact]
    public void SystemSourcesShareProcessWideNativeSubscription()
    {
        var native = new FakeNativeSessionEndingEvents();
        var firstSource = new SystemSessionEndingSource(native);
        var secondSource = new SystemSessionEndingSource(native);
        var firstCalls = 0;
        var secondCalls = 0;
        EventHandler firstHandler = (_, _) =>
        {
            firstCalls++;
            throw new InvalidOperationException("first handler failed");
        };
        EventHandler secondHandler = (_, _) => secondCalls++;
        var firstSubscribed = false;
        var secondSubscribed = false;

        try
        {
            firstSource.SessionEnding += firstHandler;
            firstSubscribed = true;
            secondSource.SessionEnding += secondHandler;
            secondSubscribed = true;

            Assert.Equal(1, native.SubscriptionCount);
            Assert.Null(Record.Exception(native.Raise));
            Assert.Equal(1, firstCalls);
            Assert.Equal(1, secondCalls);

            firstSource.SessionEnding -= firstHandler;
            firstSubscribed = false;
            Assert.Equal(0, native.UnsubscriptionCount);
            native.Raise();
            Assert.Equal(1, firstCalls);
            Assert.Equal(2, secondCalls);

            secondSource.SessionEnding -= secondHandler;
            secondSubscribed = false;
            Assert.Equal(1, native.UnsubscriptionCount);
        }
        finally
        {
            if (firstSubscribed)
            {
                firstSource.SessionEnding -= firstHandler;
            }

            if (secondSubscribed)
            {
                secondSource.SessionEnding -= secondHandler;
            }
        }
    }

    /// <summary>最后一次 native 退订失败必须保留最后 registration，且不复活已移除的其他 handler。</summary>
    [Fact]
    public void FailedFinalNativeUnsubscriptionRetainsOnlyLastHandlerForRetry()
    {
        var native = new FailsFirstNativeUnsubscriptionEvents();
        var firstSource = new SystemSessionEndingSource(native);
        var secondSource = new SystemSessionEndingSource(native);
        var firstCalls = 0;
        var secondCalls = 0;
        EventHandler firstHandler = (_, _) => firstCalls++;
        EventHandler secondHandler = (_, _) => secondCalls++;
        firstSource.SessionEnding += firstHandler;
        secondSource.SessionEnding += secondHandler;

        firstSource.SessionEnding -= firstHandler;
        Assert.Throws<InvalidOperationException>(() => secondSource.SessionEnding -= secondHandler);
        native.Raise();

        Assert.Equal(0, firstCalls);
        Assert.Equal(1, secondCalls);
        secondSource.SessionEnding -= secondHandler;
        Assert.Equal(2, native.UnsubscriptionAttempts);
        Assert.Equal(1, native.SuccessfulUnsubscriptions);
    }

    /// <summary>native adapter 在另一线程同步回调时，退订路径不得持有进程级 static lock。</summary>
    [Fact]
    public void NativeUnsubscriptionRunsOutsideStaticSourceLock()
    {
        var native = new CrossThreadRaisingUnsubscriptionEvents();
        var source = new SystemSessionEndingSource(native);
        var callbackCount = 0;
        EventHandler handler = (_, _) => callbackCount++;
        source.SessionEnding += handler;

        source.SessionEnding -= handler;

        Assert.Equal(1, callbackCount);
        Assert.Equal(1, native.SuccessfulUnsubscriptions);
    }

    /// <summary>native adapter 在另一线程同步回调时，订阅路径也不得持有进程级 static lock。</summary>
    [Fact]
    public void NativeSubscriptionRunsOutsideStaticSourceLock()
    {
        var native = new CrossThreadRaisingSubscriptionEvents();
        var source = new SystemSessionEndingSource(native);
        var callbackCount = 0;
        EventHandler handler = (_, _) => callbackCount++;

        source.SessionEnding += handler;

        Assert.Equal(1, callbackCount);
        source.SessionEnding -= handler;
    }

    /// <summary>系统 handler 只排队工作并返回，checkpoint 不得占用系统事件线程。</summary>
    [Fact]
    public async Task SessionEndingQueuesCheckpointOffSystemEventThread()
    {
        var source = new FakeSessionEndingSource();
        var delay = new ManualDelay();
        using var callbackStarted = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        var callbackThreadId = 0;
        using var events = new WindowsSessionEvents(
            cancellationToken =>
            {
                callbackThreadId = Environment.CurrentManagedThreadId;
                callbackStarted.Set();
                releaseCallback.Wait(cancellationToken);
                return Task.CompletedTask;
            },
            source,
            delay,
            TimeSpan.FromSeconds(5));
        events.Start();
        var systemEventThreadId = Environment.CurrentManagedThreadId;

        source.Raise();

        Assert.True(callbackStarted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.NotEqual(systemEventThreadId, callbackThreadId);
        releaseCallback.Set();
        await events.LastRequestCompletion.WaitAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>同步 callback 异常不得越过系统事件边界，并必须可观察。</summary>
    [Fact]
    public async Task SynchronousCallbackFailureIsObservedWithoutEscapingRaise()
    {
        var source = new FakeSessionEndingSource();
        var expected = new InvalidOperationException("checkpoint failed");
        using var events = new WindowsSessionEvents(
            _ => throw expected,
            source,
            new ManualDelay(),
            TimeSpan.FromSeconds(5));
        events.Start();

        var raised = Record.Exception(source.Raise);
        await events.LastRequestCompletion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Null(raised);
        Assert.Same(expected, events.LastFailure);
    }

    /// <summary>异步 callback fault 必须被 wrapper 观察，不形成未观察任务。</summary>
    [Fact]
    public async Task AsynchronousCallbackFailureIsObserved()
    {
        var source = new FakeSessionEndingSource();
        var expected = new IOException("save failed");
        using var events = new WindowsSessionEvents(
            _ => Task.FromException(expected),
            source,
            new ManualDelay(),
            TimeSpan.FromSeconds(5));
        events.Start();

        source.Raise();
        await events.LastRequestCompletion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Same(expected, events.LastFailure);
    }

    /// <summary>默认 final-checkpoint 预算必须为五秒且由可控 delay 驱动。</summary>
    [Fact]
    public async Task DefaultCheckpointBudgetIsFiveSeconds()
    {
        var source = new FakeSessionEndingSource();
        var delay = new ManualDelay();
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var events = new WindowsSessionEvents(_ => neverCompletes.Task, source, delay);
        events.Start();

        source.Raise();
        Assert.True(delay.Requested.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.Equal(TimeSpan.FromSeconds(5), delay.LastDuration);
        delay.Complete();
        await events.LastRequestCompletion.WaitAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>超时必须有界完成并请求取消，即使 callback 完全忽略取消。</summary>
    [Fact]
    public async Task TimeoutCompletesWrapperAndRequestsCancellationWithoutWaitingForCallback()
    {
        var source = new FakeSessionEndingSource();
        var delay = new ManualDelay();
        using var cancellationRequested = new ManualResetEventSlim();
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var events = new WindowsSessionEvents(
            cancellationToken =>
            {
                cancellationToken.Register(cancellationRequested.Set);
                return neverCompletes.Task;
            },
            source,
            delay,
            TimeSpan.FromSeconds(7));
        events.Start();

        source.Raise();
        Assert.True(delay.Requested.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        delay.Complete();
        await events.LastRequestCompletion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.IsType<TimeoutException>(events.LastFailure);
        Assert.True(cancellationRequested.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    /// <summary>阻塞式 cancellation callback 只能占用独立工作项，不能卡住有界 wrapper。</summary>
    [Fact]
    public async Task BlockingCancellationCallbackDoesNotBlockBoundedWrapper()
    {
        var source = new FakeSessionEndingSource();
        var delay = new ManualDelay();
        using var cancellationStarted = new ManualResetEventSlim();
        using var releaseCancellation = new ManualResetEventSlim();
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var events = new WindowsSessionEvents(
            cancellationToken =>
            {
                cancellationToken.Register(() =>
                {
                    cancellationStarted.Set();
                    releaseCancellation.Wait();
                });
                return neverCompletes.Task;
            },
            source,
            delay,
            TimeSpan.FromSeconds(5));
        events.Start();

        try
        {
            source.Raise();
            Assert.True(delay.Requested.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            delay.Complete();

            Assert.True(cancellationStarted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.True(events.LastRequestCompletion.IsCompleted);
            await events.LastRequestCompletion.WaitAsync(TestContext.Current.CancellationToken);
            Assert.IsType<TimeoutException>(events.LastFailure);
        }
        finally
        {
            releaseCancellation.Set();
        }
    }

    /// <summary>callback 在返回 Task 前同步阻塞时，整个调用阶段仍必须受五秒预算约束。</summary>
    [Fact]
    public async Task BlockingBeforeReturningTaskDoesNotBypassCheckpointBudget()
    {
        var source = new FakeSessionEndingSource();
        var delay = new ManualDelay();
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        var callbackToken = CancellationToken.None;
        using var events = new WindowsSessionEvents(
            token =>
            {
                callbackEntered.Set();
                releaseCallback.Wait(CancellationToken.None);
                callbackToken = token;
                return Task.CompletedTask;
            },
            source,
            delay,
            TimeSpan.FromSeconds(5));
        events.Start();

        try
        {
            source.Raise();
            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.True(delay.Requested.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            delay.Complete();

            await events.LastRequestCompletion.WaitAsync(TestContext.Current.CancellationToken);
            Assert.IsType<TimeoutException>(events.LastFailure);
        }
        finally
        {
            releaseCallback.Set();
        }

        await events.LastLateObservation.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(callbackToken.IsCancellationRequested);
    }

    /// <summary>超时后不合作任务的迟到 fault 必须被观察并保留超时上下文。</summary>
    [Fact]
    public async Task LateFaultAfterTimeoutIsObserved()
    {
        var source = new FakeSessionEndingSource();
        var delay = new ManualDelay();
        var callback = new TaskCompletionSource();
        using var events = new WindowsSessionEvents(
            _ => callback.Task,
            source,
            delay,
            TimeSpan.FromSeconds(5));
        events.Start();

        source.Raise();
        Assert.True(delay.Requested.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        delay.Complete();
        await events.LastRequestCompletion.WaitAsync(TestContext.Current.CancellationToken);
        var lateFailure = new IOException("late failure");
        callback.SetException(lateFailure);
        await events.LastLateObservation.WaitAsync(TestContext.Current.CancellationToken);

        var aggregate = Assert.IsType<AggregateException>(events.LastFailure);
        Assert.Contains(aggregate.InnerExceptions, exception => exception is TimeoutException);
        Assert.Contains(aggregate.InnerExceptions, exception => ReferenceEquals(exception, lateFailure));
    }

    /// <summary>Dispose 仅阻止新事件，已经排队的 checkpoint 仍允许正常结束。</summary>
    [Fact]
    public async Task DisposeAllowsAlreadyQueuedCheckpointToFinish()
    {
        var source = new FakeSessionEndingSource();
        var delay = new ManualDelay();
        var callback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new WindowsSessionEvents(_ => callback.Task, source, delay, TimeSpan.FromSeconds(5));
        events.Start();
        source.Raise();
        Assert.True(delay.Requested.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        events.Dispose();
        callback.SetResult();
        await events.LastRequestCompletion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Null(events.LastFailure);
    }

    private sealed class FakeSessionEndingSource : ISessionEndingSource
    {
        private EventHandler? handlers;

        public int SubscriptionCount { get; private set; }

        public int UnsubscriptionCount { get; private set; }

        public event EventHandler? SessionEnding
        {
            add
            {
                SubscriptionCount++;
                handlers += value;
            }
            remove
            {
                UnsubscriptionCount++;
                handlers -= value;
            }
        }

        public void Raise() => handlers?.Invoke(this, EventArgs.Empty);
    }

    private sealed class RaisingDuringSubscriptionSource : ISessionEndingSource
    {
        private EventHandler? handlers;

        public event EventHandler? SessionEnding
        {
            add
            {
                handlers += value;
                handlers?.Invoke(this, EventArgs.Empty);
            }
            remove => handlers -= value;
        }
    }

    private sealed class BlockingSubscriptionSource : ISessionEndingSource
    {
        private EventHandler? handlers;

        public ManualResetEventSlim SubscriptionEntered { get; } = new();

        public ManualResetEventSlim AllowSubscriptionToReturn { get; } = new();

        public int SubscriptionCount { get; private set; }

        public int UnsubscriptionCount { get; private set; }

        public event EventHandler? SessionEnding
        {
            add
            {
                SubscriptionCount++;
                handlers += value;
                SubscriptionEntered.Set();
                AllowSubscriptionToReturn.Wait(CancellationToken.None);
            }
            remove
            {
                UnsubscriptionCount++;
                handlers -= value;
            }
        }

        public void Raise() => handlers?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FailsFirstSubscriptionSource : ISessionEndingSource
    {
        private EventHandler? handlers;

        public int SubscriptionAttempts { get; private set; }

        public event EventHandler? SessionEnding
        {
            add
            {
                SubscriptionAttempts++;
                if (SubscriptionAttempts == 1)
                {
                    throw new InvalidOperationException("subscribe failed");
                }

                handlers += value;
            }
            remove => handlers -= value;
        }

        public void Raise() => handlers?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FailsFirstUnsubscriptionSource : ISessionEndingSource
    {
        private EventHandler? handlers;

        public int UnsubscriptionAttempts { get; private set; }

        public event EventHandler? SessionEnding
        {
            add => handlers += value;
            remove
            {
                UnsubscriptionAttempts++;
                if (UnsubscriptionAttempts == 1)
                {
                    throw new InvalidOperationException("unsubscribe failed");
                }

                handlers -= value;
            }
        }

        public void Raise() => handlers?.Invoke(this, EventArgs.Empty);
    }

    private sealed class ManualDelay : ISessionEventDelay
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Requested { get; } = new();

        public TimeSpan LastDuration { get; private set; }

        public Task DelayAsync(TimeSpan duration)
        {
            LastDuration = duration;
            Requested.Set();
            return completion.Task;
        }

        public void Complete() => completion.TrySetResult();
    }

    private sealed class FakeNativeSessionEndingEvents : ISystemSessionEndingNativeEvents
    {
        private EventHandler? handlers;

        public int SubscriptionCount { get; private set; }

        public int UnsubscriptionCount { get; private set; }

        public void Subscribe(EventHandler handler)
        {
            SubscriptionCount++;
            handlers += handler;
        }

        public void Unsubscribe(EventHandler handler)
        {
            UnsubscriptionCount++;
            handlers -= handler;
        }

        public void Raise() => handlers?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FailsFirstNativeUnsubscriptionEvents : ISystemSessionEndingNativeEvents
    {
        private EventHandler? handlers;

        public int UnsubscriptionAttempts { get; private set; }

        public int SuccessfulUnsubscriptions { get; private set; }

        public void Subscribe(EventHandler handler) => handlers += handler;

        public void Unsubscribe(EventHandler handler)
        {
            UnsubscriptionAttempts++;
            if (UnsubscriptionAttempts == 1)
            {
                throw new InvalidOperationException("native unsubscribe failed");
            }

            handlers -= handler;
            SuccessfulUnsubscriptions++;
        }

        public void Raise() => handlers?.Invoke(this, EventArgs.Empty);
    }

    private sealed class CrossThreadRaisingUnsubscriptionEvents : ISystemSessionEndingNativeEvents
    {
        private EventHandler? handlers;

        public int SuccessfulUnsubscriptions { get; private set; }

        public void Subscribe(EventHandler handler) => handlers += handler;

        public void Unsubscribe(EventHandler handler)
        {
            var raiseTask = Task.Run(Raise, TestContext.Current.CancellationToken);
            if (!raiseTask.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
            {
                throw new TimeoutException("native callback 等待 static source lock 超时。");
            }

            handlers -= handler;
            SuccessfulUnsubscriptions++;
        }

        private void Raise() => handlers?.Invoke(this, EventArgs.Empty);
    }

    private sealed class CrossThreadRaisingSubscriptionEvents : ISystemSessionEndingNativeEvents
    {
        private EventHandler? handlers;

        public void Subscribe(EventHandler handler)
        {
            handlers += handler;
            var raiseTask = Task.Run(Raise, TestContext.Current.CancellationToken);
            if (!raiseTask.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
            {
                throw new TimeoutException("native callback 等待 static source lock 超时。");
            }
        }

        public void Unsubscribe(EventHandler handler) => handlers -= handler;

        private void Raise() => handlers?.Invoke(this, EventArgs.Empty);
    }
}
