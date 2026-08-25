using DeskButler.Application.Modules;
using DeskButler.Core.Time;
using DeskButler.Modules.WorkspaceRecovery.Capture;

namespace DeskButler.Modules.WorkspaceRecovery;

/// <summary>提供平台无关的桌面变化信号边界；Windows 具体 hook 留给基础设施层。</summary>
public interface IDesktopChangeSource
{
    /// <summary>当窗口现场发生可能有意义的变化时触发。</summary>
    event EventHandler? DesktopChanged;
}

/// <summary>连接桌面变化、防抖调度和有界最终现场刷新的工作区恢复模块。</summary>
public sealed class WorkspaceRecoveryModule : IModule
{
    private static readonly TimeSpan DefaultFinalFlushTimeout = TimeSpan.FromSeconds(5);

    private readonly object syncRoot = new();
    private readonly IDesktopChangeSource desktopChangeSource;
    private readonly SnapshotScheduler scheduler;
    private readonly CaptureCoordinator coordinator;
    private readonly IClock clock;
    private readonly TimeSpan finalFlushTimeout;
    private readonly AutomaticCaptureGate automaticCaptureGate;
    private bool subscriptionPending;
    private bool subscribed;
    private bool stopped;
    private TaskCompletionSource? stopCompletion;
    private Exception? lastFailure;

    /// <summary>创建使用默认五秒最终刷新上限的工作区恢复模块。</summary>
    /// <param name="desktopChangeSource">平台桌面变化事件源。</param>
    /// <param name="scheduler">将变化信号合并为保存请求的调度器。</param>
    /// <param name="coordinator">执行实际现场捕获和持久化的协调器。</param>
    /// <param name="clock">提供可控最终刷新上限的时钟。</param>
    public WorkspaceRecoveryModule(
        IDesktopChangeSource desktopChangeSource,
        SnapshotScheduler scheduler,
        CaptureCoordinator coordinator,
        IClock clock)
        : this(desktopChangeSource, scheduler, coordinator, clock, new AutomaticCaptureGate(false), DefaultFinalFlushTimeout)
    {
    }

    /// <summary>创建绑定进程内自动捕获门禁的工作区恢复模块。</summary>
    public WorkspaceRecoveryModule(
        IDesktopChangeSource desktopChangeSource,
        SnapshotScheduler scheduler,
        CaptureCoordinator coordinator,
        IClock clock,
        AutomaticCaptureGate automaticCaptureGate)
        : this(desktopChangeSource, scheduler, coordinator, clock, automaticCaptureGate, DefaultFinalFlushTimeout)
    {
    }

    /// <summary>创建使用指定最终刷新上限的工作区恢复模块。</summary>
    /// <param name="desktopChangeSource">平台桌面变化事件源。</param>
    /// <param name="scheduler">将变化信号合并为保存请求的调度器。</param>
    /// <param name="coordinator">执行实际现场捕获和持久化的协调器。</param>
    /// <param name="clock">提供可控最终刷新上限的时钟。</param>
    /// <param name="finalFlushTimeout">停止时最终刷新允许占用的最长时长。</param>
    public WorkspaceRecoveryModule(
        IDesktopChangeSource desktopChangeSource,
        SnapshotScheduler scheduler,
        CaptureCoordinator coordinator,
        IClock clock,
        TimeSpan finalFlushTimeout)
        : this(desktopChangeSource, scheduler, coordinator, clock, new AutomaticCaptureGate(false), finalFlushTimeout)
    {
    }

    /// <summary>创建使用指定运行期门禁和最终刷新上限的工作区恢复模块。</summary>
    public WorkspaceRecoveryModule(
        IDesktopChangeSource desktopChangeSource,
        SnapshotScheduler scheduler,
        CaptureCoordinator coordinator,
        IClock clock,
        AutomaticCaptureGate automaticCaptureGate,
        TimeSpan finalFlushTimeout)
    {
        this.desktopChangeSource = desktopChangeSource ?? throw new ArgumentNullException(nameof(desktopChangeSource));
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.automaticCaptureGate = automaticCaptureGate ?? throw new ArgumentNullException(nameof(automaticCaptureGate));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(finalFlushTimeout, TimeSpan.Zero);
        this.finalFlushTimeout = finalFlushTimeout;
    }

    /// <inheritdoc />
    public string Id => "workspace-recovery";

    /// <inheritdoc />
    public ModuleDescriptor Descriptor { get; } = new(
        "workspace-recovery", "工作现场恢复", new Version(1, 0), true,
        ["窗口捕获", "现场恢复"], ["捕获开关", "永久排除"], ["最近失败", "快照健康"]);

    /// <summary>获取模块边界或后台调度器最近一次可观察失败。</summary>
    public Exception? LastFailure
    {
        get
        {
            lock (syncRoot)
            {
                return lastFailure ?? scheduler.LastFailure;
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (syncRoot)
        {
            if (stopped)
            {
                throw new InvalidOperationException("工作区恢复模块停止后不能重新启动。");
            }

            if (subscribed || subscriptionPending)
            {
                return Task.CompletedTask;
            }

            subscriptionPending = true;
        }

        try
        {
            desktopChangeSource.DesktopChanged += OnDesktopChanged;
        }
        catch
        {
            lock (syncRoot)
            {
                subscriptionPending = false;
            }

            throw;
        }

        var removeAfterConcurrentStop = false;
        lock (syncRoot)
        {
            subscriptionPending = false;
            if (stopped)
            {
                removeAfterConcurrentStop = true;
            }
            else
            {
                subscribed = true;
            }
        }

        if (removeAfterConcurrentStop)
        {
            desktopChangeSource.DesktopChanged -= OnDesktopChanged;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource completion;
        var startCore = false;
        var removeSubscription = false;
        lock (syncRoot)
        {
            if (stopCompletion is null)
            {
                stopped = true;
                removeSubscription = subscribed;
                subscribed = false;
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                stopCompletion = completion;
                startCore = true;
            }
            else
            {
                completion = stopCompletion;
            }
        }

        if (startCore)
        {
            _ = RunStopCoreAndCompleteAsync(completion, removeSubscription);
        }

        // caller token 只取消本次等待；唯一 stop core 使用自己的五秒预算继续清理。
        return completion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>执行唯一模块停止核心，并完成所有 Stop 调用共享的稳定句柄。</summary>
    /// <param name="completion">在状态锁内预先发布的共享完成句柄。</param>
    /// <param name="removeSubscription">是否需要移除已建立的事件订阅。</param>
    private async Task RunStopCoreAndCompleteAsync(TaskCompletionSource completion, bool removeSubscription)
    {
        try
        {
            if (removeSubscription)
            {
                // 先断开事件源，避免最终刷新期间的新原生回调重新产生普通防抖批次。
                desktopChangeSource.DesktopChanged -= OnDesktopChanged;
            }

            await StopAndSaveWithinLimitAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // 模块停止是尽力清理边界；异常可观察，但不会让共享清理句柄成为未观察 fault。
            SetLastFailure(exception);
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    /// <summary>把最薄事件回调转换为线程安全变化信号，异常绝不越过平台回调边界。</summary>
    /// <param name="sender">发出变化的事件源。</param>
    /// <param name="eventArgs">不携带窗口敏感数据的空事件参数。</param>
    private void OnDesktopChanged(object? sender, EventArgs eventArgs)
    {
        try
        {
            if (automaticCaptureGate.IsPaused)
            {
                return;
            }

            scheduler.NotifyDesktopChanged();
        }
        catch (Exception exception)
        {
            SetLastFailure(exception);
        }
    }

    /// <summary>以虚拟时钟和外部取消共同限制 scheduler 停止及最终刷新，并观察迟到任务异常。</summary>
    private async Task StopAndSaveWithinLimitAsync()
    {
        using var shutdownSource = new CancellationTokenSource();
        using var timeoutSource = new CancellationTokenSource();
        var shutdownTask = StopSchedulerThenSaveAsync(shutdownSource.Token);
        var timeoutTask = clock.DelayAsync(finalFlushTimeout, timeoutSource.Token);

        var completedTask = await Task.WhenAny(shutdownTask, timeoutTask).ConfigureAwait(false);
        if (ReferenceEquals(completedTask, shutdownTask))
        {
            await timeoutSource.CancelAsync().ConfigureAwait(false);
            await ObserveExpectedCancellationAsync(timeoutTask).ConfigureAwait(false);
            try
            {
                await shutdownTask.ConfigureAwait(false);
                SetLastFailure(null);
            }
            catch (Exception exception)
            {
                SetLastFailure(exception);
            }

            return;
        }

        shutdownSource.Cancel();
        var timeoutFailure = new TimeoutException("工作区最终快照未能在时间上限内完成。");
        SetLastFailure(timeoutFailure);
        ObserveLateCompletion(shutdownTask, timeoutFailure);
    }

    /// <summary>先停止普通调度循环，再在剩余的同一取消预算内执行最终快照。</summary>
    /// <param name="cancellationToken">整个关机捕获链共享的有界令牌。</param>
    private async Task StopSchedulerThenSaveAsync(CancellationToken cancellationToken)
    {
        await scheduler.StopAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (automaticCaptureGate.IsPaused)
        {
            return;
        }

        await coordinator.SaveNowAsync("module-stop", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>更新模块边界最近失败状态。</summary>
    /// <param name="failure">异常；成功时为空。</param>
    private void SetLastFailure(Exception? failure)
    {
        lock (syncRoot)
        {
            lastFailure = failure;
        }
    }

    /// <summary>观察被主动取消的时钟等待，避免形成未观察任务。</summary>
    /// <param name="task">预期因取消结束的等待任务。</param>
    private static async Task ObserveExpectedCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 保存先完成时，取消未使用的虚拟超时等待属于预期清理。
        }
    }

    /// <summary>为超时后仍可能结束的协作式保存注册异常观察，不阻塞关机路径。</summary>
    /// <param name="task">可能迟到结束的保存任务。</param>
    private void ObserveLateCompletion(Task task, TimeoutException timeoutFailure)
    {
        _ = task.ContinueWith(
            static (completed, state) =>
            {
                var observation = (LateFailureObservation)state!;
                var rootFailures = completed.Exception!.Flatten().InnerExceptions;
                observation.Module.SetLastFailure(
                    new AggregateException(
                        "停止超时后后台任务又失败。",
                        [observation.TimeoutFailure, .. rootFailures]));
            },
            new LateFailureObservation(this, timeoutFailure),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>绑定模块与原始超时异常，供迟到 fault continuation 保留完整失败上下文。</summary>
    /// <param name="Module">接收聚合失败状态的模块。</param>
    /// <param name="TimeoutFailure">先前已记录的内部停止超时。</param>
    private sealed record LateFailureObservation(
        WorkspaceRecoveryModule Module,
        TimeoutException TimeoutFailure);
}
