using System.Runtime.ExceptionServices;

namespace DeskButler.Desktop.Hosting;

/// <summary>表示一个可独立重试的异步清理步骤。</summary>
internal sealed record CleanupStep(string Name, Func<ValueTask> RunAsync);

/// <summary>逐步尽力清理；成功步骤只执行一次，失败步骤可在后续调用重试。</summary>
internal sealed class BestEffortAsyncCleanup(IEnumerable<CleanupStep> steps)
{
    private readonly CleanupState[] states = steps.Select(step => new CleanupState(step)).ToArray();

    /// <summary>获取所有步骤是否均已成功完成。</summary>
    internal bool IsComplete => states.All(state => state.Completed);

    /// <summary>执行全部未完成步骤，并在最后汇总本轮错误。</summary>
    internal async ValueTask RunAsync()
    {
        var failures = new List<Exception>();
        foreach (var state in states.Where(state => !state.Completed))
        {
            try
            {
                await state.Step.RunAsync();
                state.Completed = true;
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

    private sealed class CleanupState(CleanupStep step)
    {
        internal CleanupStep Step { get; } = step;

        internal bool Completed { get; set; }
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
            var result = await build(owner).ConfigureAwait(false);
            owner.Transfer();
            return result;
        }
        catch (Exception constructionFailure)
        {
            try
            {
                await owner.DisposeAsync().ConfigureAwait(false);
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
            await PrepareCleanup().RunAsync().ConfigureAwait(false);
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
            await startModuleAsync(cancellationToken).ConfigureAwait(false);
            moduleStarted = true;
            startSession();
            sessionStarted = true;
            await startDesktopAsync(cancellationToken).ConfigureAwait(false);
            desktopStarted = true;
        }
        catch (Exception startFailure)
        {
            startupFailed = true;
            try
            {
                await cleanup.RunAsync().ConfigureAwait(false);
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
