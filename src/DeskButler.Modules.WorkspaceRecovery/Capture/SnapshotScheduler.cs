using DeskButler.Core.Time;

namespace DeskButler.Modules.WorkspaceRecovery.Capture;

/// <summary>把高频桌面变化合并为静止保存，并为持续变化生成强制检查点。</summary>
public sealed class SnapshotScheduler : IAsyncDisposable
{
    /// <summary>静止变化保存使用的稳定原因。</summary>
    public const string QuietDebounceReason = "quiet-debounce";

    /// <summary>持续变化强制检查点使用的稳定原因。</summary>
    public const string ContinuousCheckpointReason = "continuous-checkpoint";

    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaximumPendingPeriod = TimeSpan.FromSeconds(60);

    private readonly object syncRoot = new();
    private readonly IClock clock;
    private readonly Func<string, CancellationToken, Task> saveAsync;
    private readonly CancellationTokenSource stopSource = new();
    private TaskCompletionSource? loopCompletion;
    private CancellationTokenSource? waitSource;
    private DateTimeOffset firstChangedAt;
    private DateTimeOffset lastChangedAt;
    private bool pending;
    private bool stopped;
    private bool disposed;
    private Exception? lastFailure;

    /// <summary>创建使用单一可取消后台循环的快照调度器。</summary>
    /// <param name="clock">提供当前时间和可控等待的时钟。</param>
    /// <param name="saveAsync">按稳定原因执行保存的异步回调。</param>
    public SnapshotScheduler(IClock clock, Func<string, CancellationToken, Task> saveAsync)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
    }

    /// <summary>获取最近一次保存失败；后续成功会清除此状态。</summary>
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

    /// <summary>以线程安全方式通知调度器桌面发生变化。</summary>
    public void NotifyDesktopChanged()
    {
        var startLoop = false;
        var lifetimeToken = default(CancellationToken);
        TaskCompletionSource? completion = null;
        CancellationTokenSource? waitToCancel = null;
        lock (syncRoot)
        {
            if (stopped)
            {
                return;
            }

            var now = clock.UtcNow;
            if (!pending)
            {
                firstChangedAt = now;
                pending = true;
            }

            lastChangedAt = now;
            if (loopCompletion is null)
            {
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                loopCompletion = completion;
                startLoop = true;
                lifetimeToken = stopSource.Token;
            }
            else
            {
                // 通知不创建计时器，只取消当前唯一等待，令同一循环按新 lastChangedAt 重算。
                waitToCancel = waitSource;
            }
        }

        TryCancelWait(waitToCancel);
        if (startLoop)
        {
            _ = RunLoopAndCompleteAsync(completion!, lifetimeToken);
        }
    }

    /// <summary>取消待处理等待并等待后台循环退出，此后变化通知不会再保存。</summary>
    /// <param name="cancellationToken">限制等待循环退出的令牌。</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? runningLoop;
        CancellationTokenSource? waitToCancel = null;
        var requestStop = false;
        lock (syncRoot)
        {
            if (!stopped)
            {
                stopped = true;
                pending = false;
                requestStop = true;
                waitToCancel = waitSource;
            }

            runningLoop = loopCompletion?.Task;
        }

        if (requestStop)
        {
            stopSource.Cancel();
            TryCancelWait(waitToCancel);
        }
        if (runningLoop is not null)
        {
            await runningLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>运行调度循环并最终完成锁内预先发布的稳定完成句柄。</summary>
    /// <param name="completion">Stop 在 runner 启动前即可读取的完成句柄。</param>
    /// <param name="cancellationToken">调度器生命周期令牌。</param>
    private async Task RunLoopAndCompleteAsync(TaskCompletionSource completion, CancellationToken cancellationToken)
    {
        while (true)
        {
            var failed = false;
            try
            {
                await RunLoopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failed = true;
                SetLastFailure(exception);
            }

            var restartForLateChange = false;
            lock (syncRoot)
            {
                if (failed)
                {
                    pending = false;
                }

                restartForLateChange = pending && !stopped;
                if (!restartForLateChange)
                {
                    // 先完成稳定句柄再清字段，Stop 要么读到已完成 Task，要么确认 runner 已结束。
                    completion.TrySetResult();
                    if (ReferenceEquals(loopCompletion, completion))
                    {
                        loopCompletion = null;
                    }
                }
            }

            if (!restartForLateChange)
            {
                return;
            }
        }
    }

    /// <summary>停止调度器并释放取消与唤醒资源。</summary>
    public async ValueTask DisposeAsync()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        stopSource.Dispose();
    }

    /// <summary>运行唯一后台循环；变化只唤醒并重算到期时间，不创建逐事件计时器。</summary>
    /// <param name="cancellationToken">调度器生命周期令牌。</param>
    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var dueAt = ReadDueAt();
                if (dueAt is null)
                {
                    if (CanLoopExit())
                    {
                        return;
                    }

                    continue;
                }

                await WaitUntilDueOrChangedAsync(dueAt.Value, cancellationToken).ConfigureAwait(false);
                var reason = TryClaimDueSave();
                if (reason is null)
                {
                    continue;
                }

                try
                {
                    await saveAsync(reason, cancellationToken).ConfigureAwait(false);
                    SetLastFailure(null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    // 保存失败属于可恢复状态；记录后继续处理保存期间或之后到达的新变化。
                    SetLastFailure(exception);
                }

                if (ReadDueAt() is null)
                {
                    if (CanLoopExit())
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>读取当前批次最早应保存的时刻。</summary>
    /// <returns>静止到期与六十秒检查点中较早者；无待保存变化时为空。</returns>
    private DateTimeOffset? ReadDueAt()
    {
        lock (syncRoot)
        {
            if (!pending || stopped)
            {
                return null;
            }

            var quietDueAt = lastChangedAt + QuietPeriod;
            var checkpointDueAt = firstChangedAt + MaximumPendingPeriod;
            return quietDueAt <= checkpointDueAt ? quietDueAt : checkpointDueAt;
        }
    }

    /// <summary>等待当前到期时刻；新变化会取消本轮等待并要求循环重算。</summary>
    /// <param name="dueAt">本轮观察到的到期时刻。</param>
    /// <param name="cancellationToken">调度器生命周期令牌。</param>
    private async Task WaitUntilDueOrChangedAsync(DateTimeOffset dueAt, CancellationToken cancellationToken)
    {
        var delay = dueAt - clock.UtcNow;
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        CancellationTokenSource currentWait;
        lock (syncRoot)
        {
            currentWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitSource = currentWait;
        }

        try
        {
            await clock.DelayAsync(delay, currentWait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 新变化取消当前唯一等待，外层循环会按更新后的时间戳重算。
        }
        finally
        {
            lock (syncRoot)
            {
                if (ReferenceEquals(waitSource, currentWait))
                {
                    waitSource = null;
                }
            }

            currentWait.Dispose();
        }
    }

    /// <summary>在锁内认领已经到期的批次，并在保存前清空该批次状态。</summary>
    /// <returns>应使用的稳定保存原因；尚未到期时为空。</returns>
    private string? TryClaimDueSave()
    {
        lock (syncRoot)
        {
            if (!pending || stopped)
            {
                return null;
            }

            var now = clock.UtcNow;
            string? reason = null;
            if (now >= firstChangedAt + MaximumPendingPeriod)
            {
                reason = ContinuousCheckpointReason;
            }
            else if (now >= lastChangedAt + QuietPeriod)
            {
                reason = QuietDebounceReason;
            }

            if (reason is not null)
            {
                // 先清批次再锁外保存，使保存期间到达的变化拥有独立 first/last 时间窗。
                pending = false;
            }

            return reason;
        }
    }

    /// <summary>保存最近失败状态，供模块边界或诊断调用方观察。</summary>
    /// <param name="failure">最近异常；成功时为空。</param>
    private void SetLastFailure(Exception? failure)
    {
        lock (syncRoot)
        {
            lastFailure = failure;
        }
    }

    /// <summary>判断循环当前是否可退出；包装层会再次检查退出窗口内到达的新变化。</summary>
    private bool CanLoopExit()
    {
        lock (syncRoot)
        {
            return !pending || stopped;
        }
    }

    /// <summary>在状态锁外取消当前等待，并容忍等待恰好已完成并释放的竞态。</summary>
    /// <param name="source">要取消的本轮等待源。</param>
    private static void TryCancelWait(CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 等待完成与并发通知交错时，下一轮已按最新状态重算，无需再次取消。
        }
    }
}
