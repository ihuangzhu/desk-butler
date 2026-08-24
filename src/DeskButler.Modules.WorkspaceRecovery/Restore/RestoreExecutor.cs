using DeskButler.Core.Capture;
using DeskButler.Core.Restore;
using DeskButler.Core.Scenes;
using DeskButler.Core.Time;
using System.Runtime.ExceptionServices;

namespace DeskButler.Modules.WorkspaceRecovery.Restore;

/// <summary>严格消费用户批准的恢复计划，并隔离单项启动、等待和定位失败。</summary>
public sealed class RestoreExecutor
{
    private static readonly TimeSpan ItemTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollingInterval = TimeSpan.FromMilliseconds(500);
    private readonly IAppLauncher launcher;
    private readonly IWindowInventory inventory;
    private readonly IWindowPositioner positioner;
    private readonly IClock clock;
    private readonly IWindowPollingTimerFactory pollingTimers;
    private readonly IHandleRegistryFactory handleRegistries;
    private Exception? lastBackgroundFailure;

    /// <summary>读取最近一次已观察的后台 work/cancel fault；并发写入以最后完成者为准。</summary>
    internal Exception? LastBackgroundFailure => Volatile.Read(ref lastBackgroundFailure);

    /// <summary>创建 production 执行器；真实轮询由 PeriodicTimer 驱动。</summary>
    public RestoreExecutor(
        IAppLauncher launcher,
        IWindowInventory inventory,
        IWindowPositioner positioner,
        IClock clock)
        : this(
            launcher,
            inventory,
            positioner,
            clock,
            new PeriodicWindowPollingTimerFactory(),
            new HandleRegistryFactory())
    {
    }

    /// <summary>创建使用可控轮询计时器的执行器。</summary>
    internal RestoreExecutor(
        IAppLauncher launcher,
        IWindowInventory inventory,
        IWindowPositioner positioner,
        IClock clock,
        IWindowPollingTimerFactory pollingTimers)
        : this(launcher, inventory, positioner, clock, pollingTimers, new HandleRegistryFactory())
    {
    }

    /// <summary>创建同时使用可控轮询和 HWND registry 的执行器。</summary>
    internal RestoreExecutor(
        IAppLauncher launcher,
        IWindowInventory inventory,
        IWindowPositioner positioner,
        IClock clock,
        IWindowPollingTimerFactory pollingTimers,
        IHandleRegistryFactory handleRegistries)
    {
        this.launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        this.positioner = positioner ?? throw new ArgumentNullException(nameof(positioner));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.pollingTimers = pollingTimers ?? throw new ArgumentNullException(nameof(pollingTimers));
        this.handleRegistries = handleRegistries ?? throw new ArgumentNullException(nameof(handleRegistries));
    }

    /// <summary>按计划顺序执行；调用方取消后将当前及全部剩余项目标记为取消。</summary>
    public async Task<RestoreResult> ExecuteAsync(RestorePlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var results = new List<RestoreItemResult>(plan.Items.Length);
        var handleRegistry = handleRegistries.Create();

        for (var index = 0; index < plan.Items.Length; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                AddCancelledRemainder(plan, index, results);
                break;
            }

            var item = plan.Items[index];
            if (IsSkipped(item.Disposition))
            {
                results.Add(new RestoreItemResult(item.SceneItem.Id, RestoreItemStatus.Skipped));
                continue;
            }

            try
            {
                results.Add(await ExecuteWithinBudgetAsync(
                    item, index, plan, handleRegistry, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                AddCancelledRemainder(plan, index, results);
                break;
            }
            catch (OperationCanceledException exception)
            {
                // 依赖内部取消不代表用户取消整份计划；只隔离当前项并继续。
                results.Add(Failed(item, exception.Message));
            }
            catch (Exception exception) when (IsRecoverableItemFailure(exception))
            {
                results.Add(Failed(item, exception.Message));
            }
        }

        return new RestoreResult(results);
    }

    /// <summary>让启动、轮询和定位共享同一个 30 秒预算。</summary>
    private async Task<RestoreItemResult> ExecuteWithinBudgetAsync(
        RestorePlanItem item,
        int itemIndex,
        RestorePlan plan,
        IHandleRegistry handleRegistry,
        CancellationToken cancellationToken)
    {
        var lease = handleRegistry.Activate(itemIndex);
        var itemCancellation = new CancellationTokenSource();
        var timeoutCancellation = new CancellationTokenSource();
        using var callerSignal = CancellationSignal.Create(cancellationToken);
        var deadline = clock.UtcNow + ItemTimeout;
        Task timeout;
        Task<RestoreItemResult> work;
        try
        {
            timeout = clock.DelayAsync(ItemTimeout, timeoutCancellation.Token);
            // 平台适配器可能在返回 Task 前同步调用 Shell/PInvoke；放入默认调度器以免阻塞预算竞争和 UI 线程。
            work = Task.Run(async () =>
            {
                try
                {
                    return await ExecuteItemAsync(
                        item,
                        itemIndex,
                        plan,
                        handleRegistry,
                        lease,
                        deadline,
                        itemCancellation.Token).ConfigureAwait(false);
                }
                finally
                {
                    // Deactivate 只撤销认领权限；work 真正结束后才可释放数值 HWND gate。
                    handleRegistry.CompleteWork(lease);
                }
            }, CancellationToken.None);
        }
        catch
        {
            handleRegistry.Deactivate(lease);
            itemCancellation.Dispose();
            timeoutCancellation.Dispose();
            throw;
        }

        var winner = await Task.WhenAny(work, timeout, callerSignal.Task).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested || winner == callerSignal.Task)
        {
            handleRegistry.Deactivate(lease);
            CancelItemWorkAndTransferLifetime(itemCancellation, work);
            var timeoutOutcome = await CancelAndObserveTimeoutAsync(
                timeoutCancellation, timeout).ConfigureAwait(false);
            ThrowFatal(timeoutOutcome);
            return new RestoreItemResult(
                item.SceneItem.Id,
                RestoreItemStatus.Cancelled,
                timeoutOutcome.ErrorMessage);
        }

        if (winner == timeout)
        {
            handleRegistry.Deactivate(lease);
            var timeoutOutcome = await ObserveCompletedTimeoutAsync(
                timeoutCancellation, timeout).ConfigureAwait(false);
            CancelItemWorkAndTransferLifetime(itemCancellation, work);
            ThrowFatal(timeoutOutcome);
            return Failed(item, CombineErrors(
                "单项恢复超过 30 秒预算。",
                timeoutOutcome.ErrorMessage)!);
        }

        handleRegistry.Deactivate(lease);
        var losingTimeoutOutcome = await CancelAndObserveTimeoutAsync(
            timeoutCancellation, timeout).ConfigureAwait(false);
        try
        {
            var result = await work.ConfigureAwait(false);
            ThrowFatal(losingTimeoutOutcome);
            return result;
        }
        finally
        {
            itemCancellation.Dispose();
        }
    }

    /// <summary>执行 Reuse 或 Launch；绝不修改计划 disposition。</summary>
    private async Task<RestoreItemResult> ExecuteItemAsync(
        RestorePlanItem item,
        int itemIndex,
        RestorePlan plan,
        IHandleRegistry handleRegistry,
        HandleLease lease,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        if (item.Disposition == RestoreDisposition.Reuse)
        {
            if (item.TargetWindowHandle is not { } targetHandle || targetHandle == 0)
            {
                return Failed(item, "Reuse 计划缺少有效窗口句柄。");
            }

            if (!handleRegistry.TryClaim(lease, RuntimeWindowFingerprint.ForHandle(targetHandle)))
            {
                return Failed(item, "Reuse 窗口句柄已由其他计划项认领。");
            }

            await positioner.PositionAsync(targetHandle, item.SceneItem, cancellationToken).ConfigureAwait(false);
            return Succeeded(item);
        }

        if (item.Disposition != RestoreDisposition.Launch)
        {
            return Failed(item, "恢复计划包含未知处理方式。");
        }

        var reservations = handleRegistry.GetReservationSnapshots(lease);
        IReadOnlyList<WindowCandidate>? validationSnapshot = null;
        if (reservations.Count > 0)
        {
            validationSnapshot = await inventory.CaptureAsync(cancellationToken).ConfigureAwait(false);
            var validReservations = reservations
                .SelectMany(reserved => validationSnapshot
                    .Where(candidate => IsSafeWindow(candidate) &&
                                        WindowInstanceIdentity.Create(candidate) == reserved.InstanceIdentity &&
                                        FindUniqueOwner(candidate, itemIndex, plan) == itemIndex)
                    .Select(candidate => (Reserved: reserved, Current: RuntimeWindowFingerprint.Create(candidate))))
                .Distinct()
                .Take(2)
                .ToArray();

            foreach (var reserved in reservations.Where(reserved =>
                         validReservations.All(valid => valid.Reserved != reserved)))
            {
                handleRegistry.RemoveReservationIfMatches(lease, reserved);
            }

            if (validReservations.Length > 1)
            {
                return Failed(item, "当前计划项存在多个有效未来窗口 reservation，已保守跳过。");
            }

            if (validReservations.Length == 1)
            {
                var valid = validReservations[0];
                if (!handleRegistry.TryClaimReservation(lease, valid.Reserved, valid.Current))
                {
                    return Failed(item, "未来窗口 reservation 在原子认领前已失效。");
                }

                await positioner.PositionAsync(
                    valid.Current.Handle, item.SceneItem, cancellationToken).ConfigureAwait(false);
                return Succeeded(item);
            }
        }

        var baseline = validationSnapshot ??
            await inventory.CaptureAsync(cancellationToken).ConfigureAwait(false);
        var baselineIdentities = baseline.Select(WindowInstanceIdentity.Create).ToHashSet();
        await launcher.LaunchAsync(item.SceneItem, cancellationToken).ConfigureAwait(false);
        var launchedWindow = await WaitForOwnedWindowAsync(
            itemIndex,
            plan,
            baselineIdentities,
            handleRegistry,
            lease,
            deadline,
            cancellationToken).ConfigureAwait(false);

        await positioner.PositionAsync(launchedWindow.Handle, item.SceneItem, cancellationToken).ConfigureAwait(false);
        return Succeeded(item);
    }

    /// <summary>每 500ms 捕获一次窗口，只返回严格归属当前剩余计划项的新 HWND。</summary>
    private async Task<RuntimeWindowFingerprint> WaitForOwnedWindowAsync(
        int itemIndex,
        RestorePlan plan,
        HashSet<WindowInstanceIdentity> baselineIdentities,
        IHandleRegistry handleRegistry,
        HandleLease lease,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        await using var timer = pollingTimers.Create(PollingInterval);
        while (clock.UtcNow < deadline &&
               await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.UtcNow >= deadline)
            {
                break;
            }

            var candidates = await inventory.CaptureAsync(cancellationToken).ConfigureAwait(false);
            var owned = new List<RuntimeWindowFingerprint>();
            foreach (var candidate in candidates.Where(IsSafeWindow))
            {
                var fingerprint = RuntimeWindowFingerprint.Create(candidate);
                if (fingerprint.Handle == 0 || baselineIdentities.Contains(fingerprint.InstanceIdentity))
                {
                    continue;
                }

                var owner = FindUniqueOwner(candidate, itemIndex, plan);
                if (owner == itemIndex)
                {
                    owned.Add(fingerprint);
                }
                else if (owner > itemIndex)
                {
                    handleRegistry.TryReserve(lease, owner.Value, fingerprint);
                }
            }

            var currentCandidates = owned
                .Distinct()
                .Take(2)
                .ToArray();
            if (currentCandidates.Length == 1 &&
                handleRegistry.TryClaim(lease, currentCandidates[0]))
            {
                return currentCandidates[0];
            }
        }

        throw new TimeoutException("未在 30 秒内找到严格匹配的新窗口。");
    }

    /// <summary>按完整剩余 Launch 计划计算候选的最佳匹配，并要求当前项是唯一最佳归属。</summary>
    private static int? FindUniqueOwner(WindowCandidate candidate, int currentIndex, RestorePlan plan)
    {
        var matches = plan.Items
            .Select((item, index) => (Item: item, Index: index))
            .Where(entry => entry.Index >= currentIndex && entry.Item.Disposition == RestoreDisposition.Launch)
            .Select(entry => (entry.Index, Rank: MatchRank(entry.Item.SceneItem, candidate)))
            .Where(entry => entry.Rank is not null)
            .ToArray();
        if (matches.Length == 0)
        {
            return null;
        }

        var bestRank = matches.Min(entry => entry.Rank!.Value);
        var bestOwners = matches.Where(entry => entry.Rank == bestRank).Take(2).ToArray();
        return bestOwners.Length == 1 ? bestOwners[0].Index : null;
    }

    /// <summary>计算 Explorer 精确路径、完整普通窗口身份和唯一 exe 回退的匹配优先级。</summary>
    private static int? MatchRank(SceneItem scene, WindowCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(scene.ExplorerPath))
        {
            return PathEquals(scene.ExplorerPath, candidate.ExplorerPath) ? 0 : null;
        }

        if (!PathEquals(scene.ExecutablePath, candidate.ExecutablePath))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(scene.TitleHint) &&
            StringComparer.Ordinal.Equals(scene.WindowClass, candidate.WindowClass) &&
            StringComparer.OrdinalIgnoreCase.Equals(scene.TitleHint.Trim(), candidate.Title?.Trim()))
        {
            return 1;
        }

        return 2;
    }

    /// <summary>规范化并按 Windows 语义比较本地路径；畸形候选只是不匹配。</summary>
    private static bool PathEquals(string? left, string? right)
    {
        var normalizedLeft = NormalizePath(left);
        var normalizedRight = NormalizePath(right);
        return normalizedLeft is not null && normalizedRight is not null &&
               StringComparer.OrdinalIgnoreCase.Equals(normalizedLeft, normalizedRight);
    }

    /// <summary>将绝对路径规范化；不允许异常逃逸中断其余候选。</summary>
    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>仅接受普通可见、可访问且非系统/临时/自身的窗口候选。</summary>
    private static bool IsSafeWindow(WindowCandidate candidate) =>
        candidate.IsVisibleMainWindow &&
        !candidate.IsSystemWindow &&
        !candidate.IsTemporaryWindow &&
        !candidate.IsDeskButlerWindow &&
        !candidate.WasElevatedOrInaccessible;

    /// <summary>判断 disposition 是否必须保持为跳过且绝不执行。</summary>
    private static bool IsSkipped(RestoreDisposition disposition) =>
        disposition is RestoreDisposition.SkipAmbiguous or RestoreDisposition.SkipUnsafe or RestoreDisposition.MissingPath;

    /// <summary>把当前索引到计划末尾统一标记为调用方取消。</summary>
    private static void AddCancelledRemainder(
        RestorePlan plan,
        int startIndex,
        List<RestoreItemResult> results)
    {
        for (var index = startIndex; index < plan.Items.Length; index++)
        {
            results.Add(new RestoreItemResult(
                plan.Items[index].SceneItem.Id, RestoreItemStatus.Cancelled));
        }
    }

    /// <summary>创建成功结果。</summary>
    private static RestoreItemResult Succeeded(RestorePlanItem item) =>
        new(item.SceneItem.Id, RestoreItemStatus.Succeeded);

    /// <summary>创建失败结果。</summary>
    private static RestoreItemResult Failed(RestorePlanItem item, string message) =>
        new(item.SceneItem.Id, RestoreItemStatus.Failed, message);

    /// <summary>先安装 work/cancel fault observer，再后台取消；CTS 随两个任务直至 completion。</summary>
    private void CancelItemWorkAndTransferLifetime(
        CancellationTokenSource cancellation,
        Task work)
    {
        ObserveFault(work);
        var startCancellation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationTask = Task.Run(async () =>
        {
            await startCancellation.Task.ConfigureAwait(false);
            cancellation.Cancel();
        });
        ObserveFault(cancellationTask);
        DisposeSourceWhenTasksComplete(cancellation, work, cancellationTask);
        startCancellation.TrySetResult();
    }

    /// <summary>取消败方 timeout，等待并观察其最终状态后释放 timer CTS。</summary>
    private static async Task<CancellationOutcome> CancelAndObserveTimeoutAsync(
        CancellationTokenSource timeoutCancellation,
        Task timeout)
    {
        var cancelOutcome = CancelSource(timeoutCancellation);
        var taskOutcome = await ObserveTaskAsync(
            timeout, timeoutCancellation.IsCancellationRequested).ConfigureAwait(false);
        timeoutCancellation.Dispose();
        return CancellationOutcome.Combine(cancelOutcome, taskOutcome);
    }

    /// <summary>观察已经赢得 WhenAny 的 timeout 并释放其 CTS。</summary>
    private static async Task<CancellationOutcome> ObserveCompletedTimeoutAsync(
        CancellationTokenSource timeoutCancellation,
        Task timeout)
    {
        var outcome = await ObserveTaskAsync(timeout, expectedCancellation: false).ConfigureAwait(false);
        timeoutCancellation.Dispose();
        return outcome;
    }

    /// <summary>显式读取败方 task 异常；预期取消不记为清理失败。</summary>
    private static async Task<CancellationOutcome> ObserveTaskAsync(
        Task task,
        bool expectedCancellation)
    {
        try
        {
            await task.ConfigureAwait(false);
            return CancellationOutcome.None;
        }
        catch (OperationCanceledException) when (expectedCancellation)
        {
            return CancellationOutcome.None;
        }
        catch (Exception exception)
        {
            return CancellationOutcome.FromException(exception);
        }
    }

    /// <summary>在 Cancel 前安装只读取 fault 的 continuation，避免迟到异常成为未观察任务。</summary>
    private void ObserveFault(Task work)
    {
        _ = work.ContinueWith(
            static (completed, state) =>
            {
                var exception = completed.Exception;
                if (exception is not null)
                {
                    ((RestoreExecutor)state!).RecordBackgroundFailure(exception);
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    /// <summary>以原子交换保存最近后台故障，同时完成对 Task.Exception 的观察。</summary>
    private void RecordBackgroundFailure(Exception exception) =>
        Interlocked.Exchange(ref lastBackgroundFailure, exception);

    /// <summary>让 CTS 与 work/cancel 两个任务一同保留；两者完成时最终释放。</summary>
    private static void DisposeSourceWhenTasksComplete(
        CancellationTokenSource cancellation,
        Task work,
        Task cancellationTask)
    {
        var lifetime = Task.WhenAll(work, cancellationTask);
        _ = lifetime.ContinueWith(
            static (completed, state) =>
            {
                _ = completed.Exception;
                ((CancellationTokenSource)state!).Dispose();
            },
            cancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>执行 CancellationTokenSource.Cancel，并把 Aggregate 展开为普通错误或首个 fatal。</summary>
    private static CancellationOutcome CancelSource(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
            return CancellationOutcome.None;
        }
        catch (Exception exception)
        {
            return CancellationOutcome.FromException(exception);
        }
    }

    /// <summary>任一取消清理包含 fatal 时按原异常类型和堆栈继续传播。</summary>
    private static void ThrowFatal(params CancellationOutcome[] outcomes)
    {
        var fatal = outcomes.Select(outcome => outcome.FatalException).FirstOrDefault(exception => exception is not null);
        if (fatal is not null)
        {
            ExceptionDispatchInfo.Capture(fatal).Throw();
        }
    }

    /// <summary>拼接非空清理错误，供 Failed 或 Cancelled 结果保留可观察信息。</summary>
    private static string? CombineErrors(params string?[] messages)
    {
        var values = messages.Where(message => !string.IsNullOrWhiteSpace(message)).Distinct().ToArray();
        return values.Length == 0 ? null : string.Join("；", values);
    }

    /// <summary>仅把普通单项故障转换为 Failed；取消及进程级致命故障继续传播。</summary>
    private static bool IsRecoverableItemFailure(Exception exception) =>
        exception is not OperationCanceledException
            and not OutOfMemoryException
            and not AccessViolationException
            and not StackOverflowException
            and not ThreadAbortException
            and not System.Runtime.InteropServices.SEHException;

    /// <summary>保存取消/败方清理的普通摘要或首个不可吞 fatal。</summary>
    private sealed record CancellationOutcome(string? ErrorMessage, Exception? FatalException)
    {
        internal static CancellationOutcome None { get; } = new(null, null);

        /// <summary>递归展开 Aggregate，区分普通清理失败和 fatal。</summary>
        internal static CancellationOutcome FromException(Exception exception)
        {
            var exceptions = Flatten(exception).ToArray();
            var fatal = exceptions.FirstOrDefault(IsFatalException);
            var ordinaryMessages = exceptions
                .Where(candidate => !IsFatalException(candidate) && candidate is not OperationCanceledException)
                .Select(candidate => candidate.Message)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct()
                .ToArray();
            return new CancellationOutcome(
                ordinaryMessages.Length == 0 ? null : string.Join("；", ordinaryMessages),
                fatal);
        }

        /// <summary>合并 item 与 timeout 两个取消边界的清理结果。</summary>
        internal static CancellationOutcome Combine(params CancellationOutcome[] outcomes) =>
            new(
                CombineErrors(outcomes.Select(outcome => outcome.ErrorMessage).ToArray()),
                outcomes.Select(outcome => outcome.FatalException).FirstOrDefault(exception => exception is not null));

        /// <summary>递归展开任意层 AggregateException。</summary>
        private static IEnumerable<Exception> Flatten(Exception exception)
        {
            if (exception is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Flatten))
                {
                    yield return inner;
                }

                yield break;
            }

            yield return exception;
        }

        /// <summary>识别不允许被取消清理降级的进程级 fatal。</summary>
        private static bool IsFatalException(Exception exception) =>
            exception is OutOfMemoryException
                or AccessViolationException
                or StackOverflowException
                or ThreadAbortException
                or System.Runtime.InteropServices.SEHException;
    }

    /// <summary>把 CancellationToken 转换为可参与 Task.WhenAny 的一次性信号。</summary>
    private sealed class CancellationSignal : IDisposable
    {
        private readonly CancellationTokenRegistration registration;

        /// <summary>注册一次取消信号。</summary>
        private CancellationSignal(CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task = completion.Task;
            registration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(), completion);
        }

        internal Task Task { get; }

        /// <summary>为调用方令牌创建取消信号。</summary>
        internal static CancellationSignal Create(CancellationToken cancellationToken) => new(cancellationToken);

        /// <summary>释放令牌注册。</summary>
        public void Dispose() => registration.Dispose();
    }
}

/// <summary>创建固定周期窗口轮询计时器。</summary>
internal interface IWindowPollingTimerFactory
{
    /// <summary>创建指定周期的计时器。</summary>
    IWindowPollingTimer Create(TimeSpan interval);
}

/// <summary>提供一个可替换的异步周期 tick。</summary>
internal interface IWindowPollingTimer : IAsyncDisposable
{
    /// <summary>等待下一次 tick，计时器结束时返回 false。</summary>
    ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken);
}

/// <summary>production 使用的 PeriodicTimer 工厂。</summary>
internal sealed class PeriodicWindowPollingTimerFactory : IWindowPollingTimerFactory
{
    /// <summary>创建真实 PeriodicTimer 包装器。</summary>
    public IWindowPollingTimer Create(TimeSpan interval) => new PeriodicWindowPollingTimer(interval);

    private sealed class PeriodicWindowPollingTimer(TimeSpan interval) : IWindowPollingTimer
    {
        private readonly PeriodicTimer timer = new(interval);

        /// <summary>等待真实周期 tick。</summary>
        public ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken) =>
            timer.WaitForNextTickAsync(cancellationToken);

        /// <summary>释放 PeriodicTimer。</summary>
        public ValueTask DisposeAsync()
        {
            timer.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
