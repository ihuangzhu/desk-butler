using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace DeskButler.Desktop.Hosting;

/// <summary>表示一个可独立重试的异步清理步骤。</summary>
internal sealed record CleanupStep(string Name, Func<ValueTask> RunAsync);

/// <summary>原子协调清理步骤状态、live pass 共享与最终结果发布。</summary>
internal sealed class CleanupPassCoordinator(IEnumerable<CleanupStep> steps)
{
    private readonly CleanupStepState[] states = steps.Select(step => new CleanupStepState(step)).ToArray();
    private readonly object syncRoot = new();
    private Task? inFlight;

    /// <summary>获取所有步骤是否均已成功，且没有仍在执行的 pass。</summary>
    internal bool IsComplete
    {
        get
        {
            lock (syncRoot)
            {
                DiscardCompletedPass();
                return inFlight is null && states.All(state => state.Completed);
            }
        }
    }

    /// <summary>加入 live pass，或为尚未完成的步骤原子建立一个新 pass。</summary>
    internal CleanupPass Enter()
    {
        lock (syncRoot)
        {
            DiscardCompletedPass();
            if (inFlight is not null)
            {
                return new CleanupPass(inFlight, null);
            }

            if (states.All(state => state.Completed))
            {
                return new CleanupPass(Task.CompletedTask, null);
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            inFlight = completion.Task;
            return new CleanupPass(completion.Task, completion);
        }
    }

    /// <summary>取得本轮尚未成功的稳定步骤快照。</summary>
    internal IReadOnlyList<CleanupStepState> GetIncompleteStates()
    {
        lock (syncRoot)
        {
            return states.Where(state => !state.Completed).ToArray();
        }
    }

    /// <summary>把一个成功步骤标记为后续 pass 不再执行。</summary>
    internal void MarkCompleted(CleanupStepState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (syncRoot)
        {
            state.Completed = true;
        }
    }

    /// <summary>在同一锁内撤销 live 登记并发布本轮最终结果。</summary>
    internal void Publish(CleanupPass pass, Exception? failure)
    {
        var completion = pass.Completion
            ?? throw new InvalidOperationException("只有新建清理 pass 的调用方可以发布结果。");
        lock (syncRoot)
        {
            if (ReferenceEquals(inFlight, pass.Task))
            {
                inFlight = null;
            }

            if (failure is null)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(failure);
            }
        }
    }

    /// <summary>已向外完成的 Task 不再属于 live pass，即使旧发布顺序暂未清除其引用。</summary>
    private void DiscardCompletedPass()
    {
        if (inFlight?.IsCompleted == true)
        {
            inFlight = null;
        }
    }
}

/// <summary>表示一次共享清理 pass；只有创建方持有其完成源。</summary>
internal readonly record struct CleanupPass(Task Task, TaskCompletionSource? Completion)
{
    internal bool StartsPass => Completion is not null;
}

/// <summary>保存单个清理步骤及其永久成功位。</summary>
internal sealed class CleanupStepState(CleanupStep step)
{
    internal CleanupStep Step { get; } = step;

    internal bool Completed { get; set; }
}

/// <summary>逐步尽力清理；成功步骤只执行一次，失败步骤可在后续调用重试。</summary>
internal sealed class BestEffortAsyncCleanup(IEnumerable<CleanupStep> steps)
{
    private readonly CleanupPassCoordinator coordinator = new(steps);

    /// <summary>获取所有步骤是否均已成功完成。</summary>
    internal bool IsComplete => coordinator.IsComplete;

    /// <summary>共享一个正在执行的清理 pass；全部完成后直接幂等返回。</summary>
    internal ValueTask RunAsync()
    {
        var pass = coordinator.Enter();
        if (pass.StartsPass)
        {
            // 必须先发布 live pass 再在锁外调用资源代码，确保同步重入也只看到同一个 pass。
            _ = RunPassAndCompleteAsync(pass);
        }

        return new ValueTask(pass.Task);
    }

    /// <summary>执行一次 pass，并在所有步骤与聚合完成后开放失败步骤重试。</summary>
    private async Task RunPassAndCompleteAsync(CleanupPass pass)
    {
        Exception? failure = null;
        try
        {
            await RunPassAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        coordinator.Publish(pass, failure);
    }

    /// <summary>按既定顺序尝试全部未完成步骤，并汇总本次 pass 的错误。</summary>
    private async Task RunPassAsync()
    {
        var failures = new List<Exception>();
        foreach (var state in coordinator.GetIncompleteStates())
        {
            try
            {
                await state.Step.RunAsync().ConfigureAwait(false);
                coordinator.MarkCompleted(state);
            }
            catch (Exception exception)
            {
                failures.Add(new InvalidOperationException($"清理步骤“{state.Step.Name}”失败。", exception));
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("DeskButler 清理未完全成功。", failures);
        }
    }
}

/// <summary>在所属 WPF Dispatcher 上异步执行清理，并把关闭中的 Dispatcher 视为 best-effort 完成。</summary>
internal static class DispatcherCleanup
{
    /// <summary>同线程直接清理；跨线程异步投递，避免同步 Invoke 与退出路径相互等待。</summary>
    internal static ValueTask RunAsync(Dispatcher dispatcher, Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(cleanup);
        if (dispatcher.CheckAccess())
        {
            cleanup();
            return ValueTask.CompletedTask;
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return ValueTask.CompletedTask;
        }

        return MarshalAsync(dispatcher, cleanup);
    }

    /// <summary>等待异步 Dispatcher 操作；关闭竞态只终止本次 best-effort 清理。</summary>
    private static async ValueTask MarshalAsync(Dispatcher dispatcher, Action cleanup)
    {
        var cleanupStarted = 0;
        try
        {
            await dispatcher.InvokeAsync(
                () =>
                {
                    Volatile.Write(ref cleanupStarted, 1);
                    cleanup();
                },
                DispatcherPriority.Send).Task.ConfigureAwait(false);
        }
        catch (Exception) when (
            Volatile.Read(ref cleanupStarted) == 0 &&
            (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished))
        {
            // 仅忽略尚未进入的排队操作；cleanup 自身失败必须传播并保留清理重试资格。
        }
    }
}

/// <summary>在组合根返回前即时接管资源，并把逆构造清理所有权一次性交给成品。</summary>
internal sealed class CompositionResourceOwner
{
    private readonly List<CleanupStep> ownedSteps = [];
    private BestEffortAsyncCleanup? preparedCleanup;
    private bool transferred;

    /// <summary>在统一构造边界内运行工厂；失败时清理，成功时只转移一次所有权。</summary>
    internal static async Task<T> BuildAsync<T>(Func<CompositionResourceOwner, Task<T>> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        var owner = new CompositionResourceOwner();
        try
        {
            var result = await build(owner);
            owner.Transfer();
            return result;
        }
        catch (Exception constructionFailure)
        {
            try
            {
                await owner.DisposeAsync();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "DeskButler 对象图构造失败且部分资源清理未完全成功。",
                    constructionFailure,
                    cleanupFailure);
            }

            ExceptionDispatchInfo.Capture(constructionFailure).Throw();
            throw new InvalidOperationException("无法重新抛出对象图构造异常。");
        }
    }

    /// <summary>创建资源后立即登记其清理动作，并返回同一实例供后续组合。</summary>
    internal T Own<T>(string name, T resource, Func<T, ValueTask> disposeAsync)
        where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(disposeAsync);
        ThrowIfSealed();
        ownedSteps.Add(new CleanupStep(name, () => disposeAsync(resource)));
        return resource;
    }

    /// <summary>封存当前资源栈并生成按逆构造顺序执行的幂等清理器。</summary>
    internal BestEffortAsyncCleanup PrepareCleanup()
    {
        preparedCleanup ??= new BestEffortAsyncCleanup(ownedSteps.AsEnumerable().Reverse());
        return preparedCleanup;
    }

    /// <summary>构造失败时释放尚未转交给成品的全部资源。</summary>
    private async ValueTask DisposeAsync()
    {
        if (!transferred)
        {
            await PrepareCleanup().RunAsync();
        }
    }

    /// <summary>封存并把所有权标记为已转交，防止 owner 再次释放同一资源。</summary>
    private void Transfer()
    {
        _ = PrepareCleanup();
        if (transferred)
        {
            throw new InvalidOperationException("对象图资源所有权已经转移。");
        }

        transferred = true;
    }

    /// <summary>所有权栈封存后禁止继续登记，避免成品漏掉迟到资源。</summary>
    private void ThrowIfSealed()
    {
        if (preparedCleanup is not null)
        {
            throw new InvalidOperationException("对象图资源所有权已经封存。");
        }
    }
}

/// <summary>分别记录组合根三个启动阶段，并在任一阶段失败时运行同一资源清理路径。</summary>
internal sealed class CompositionStartupCoordinator
{
    private readonly Func<CancellationToken, Task> startModuleAsync;
    private readonly Func<CancellationToken, Task> stopModuleAsync;
    private readonly Action startSession;
    private readonly Action disposeSession;
    private readonly Func<CancellationToken, Task> startDesktopAsync;
    private readonly Func<ValueTask> disposeDesktopAsync;
    private bool moduleStarted;
    private bool sessionStarted;
    private bool desktopStarted;
    private bool startupFailed;

    /// <summary>使用真实或可控的模块、会话与桌面变化启动边界创建协调器。</summary>
    internal CompositionStartupCoordinator(
        Func<CancellationToken, Task> startModuleAsync,
        Func<CancellationToken, Task> stopModuleAsync,
        Action startSession,
        Action disposeSession,
        Func<CancellationToken, Task> startDesktopAsync,
        Func<ValueTask> disposeDesktopAsync)
    {
        this.startModuleAsync = startModuleAsync ?? throw new ArgumentNullException(nameof(startModuleAsync));
        this.stopModuleAsync = stopModuleAsync ?? throw new ArgumentNullException(nameof(stopModuleAsync));
        this.startSession = startSession ?? throw new ArgumentNullException(nameof(startSession));
        this.disposeSession = disposeSession ?? throw new ArgumentNullException(nameof(disposeSession));
        this.startDesktopAsync = startDesktopAsync ?? throw new ArgumentNullException(nameof(startDesktopAsync));
        this.disposeDesktopAsync = disposeDesktopAsync ?? throw new ArgumentNullException(nameof(disposeDesktopAsync));
    }

    /// <summary>按模块、会话、桌面顺序启动，并在部分失败时清理全部已构造资源。</summary>
    internal async Task StartAsync(
        BestEffortAsyncCleanup cleanup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        ObjectDisposedException.ThrowIf(cleanup.IsComplete, this);
        if (desktopStarted)
        {
            return;
        }

        if (startupFailed)
        {
            throw new InvalidOperationException("组合根启动已经失败，只允许继续释放资源。");
        }

        try
        {
            await startModuleAsync(cancellationToken);
            moduleStarted = true;
            startSession();
            sessionStarted = true;
            await startDesktopAsync(cancellationToken);
            desktopStarted = true;
        }
        catch (Exception startFailure)
        {
            startupFailed = true;
            try
            {
                await cleanup.RunAsync();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "DeskButler 启动失败且部分资源清理未完全成功。",
                    startFailure,
                    cleanupFailure);
            }

            ExceptionDispatchInfo.Capture(startFailure).Throw();
            throw new InvalidOperationException("无法重新抛出组合根启动异常。");
        }
    }

    /// <summary>仅在模块阶段已经成功时停止模块，成功后清除阶段所有权。</summary>
    internal async ValueTask StopModuleIfStartedAsync()
    {
        if (!moduleStarted)
        {
            return;
        }

        await stopModuleAsync(CancellationToken.None).ConfigureAwait(false);
        moduleStarted = false;
    }

    /// <summary>释放会话订阅资源，并在成功后清除对应启动阶段状态。</summary>
    internal ValueTask DisposeSessionAsync()
    {
        var wasStarted = sessionStarted;
        disposeSession();
        if (wasStarted)
        {
            sessionStarted = false;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>释放桌面变化源资源，并在成功后清除对应启动阶段状态。</summary>
    internal async ValueTask DisposeDesktopAsync()
    {
        var wasStarted = desktopStarted;
        await disposeDesktopAsync().ConfigureAwait(false);
        if (wasStarted)
        {
            desktopStarted = false;
        }
    }
}

/// <summary>保证组合清理失败时仍按次序清理 marker 与单实例互斥量。</summary>
internal static class ExitCleanupCoordinator
{
    /// <summary>组合清理最多尝试两次，随后无条件执行两个进程身份清理动作。</summary>
    internal static async Task<Exception?> RunAsync(
        Func<ValueTask> disposeComposition,
        Action<bool> releaseMarker,
        Action releaseSingleInstance)
    {
        var failures = new List<Exception>();
        var compositionClean = false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await disposeComposition();
                compositionClean = true;
                break;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        RunSync(() => releaseMarker(compositionClean), failures);
        RunSync(releaseSingleInstance, failures);
        return failures.Count == 0 ? null : new AggregateException("退出清理存在失败。", failures);
    }

    /// <summary>隔离单个同步身份清理错误，使后续动作仍可执行。</summary>
    private static void RunSync(Action action, List<Exception> failures)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }
}
