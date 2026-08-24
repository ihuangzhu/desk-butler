using DeskButler.Core.Time;

namespace DeskButler.Modules.WorkspaceRecovery.Tests.Capture;

/// <summary>为调度测试提供完全可控的 UTC 时间和异步等待。</summary>
internal sealed class FakeClock : IClock
{
    private readonly object syncRoot = new();
    private readonly List<ScheduledDelay> delays = [];

    /// <summary>初始化固定起点的虚拟时钟。</summary>
    internal FakeClock()
    {
        Start = new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);
        UtcNow = Start;
    }

    /// <summary>获取虚拟时钟起点。</summary>
    internal DateTimeOffset Start { get; }

    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; }

    /// <summary>获取当前尚未从队列移除的最早等待时刻，供时间边界测试校验。</summary>
    internal DateTimeOffset? NextDueAt
    {
        get
        {
            lock (syncRoot)
            {
                return delays.Count == 0 ? null : delays.Min(item => item.DueAt);
            }
        }
    }

    /// <summary>获取仍登记在虚拟时钟中的等待数量。</summary>
    internal int PendingDelayCount
    {
        get
        {
            lock (syncRoot)
            {
                return delays.Count;
            }
        }
    }

    /// <inheritdoc />
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (delay <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        ScheduledDelay scheduled;
        lock (syncRoot)
        {
            // 到期推进必须同步驱动等待链，否则快速推进会让线程池调度延迟伪装成生产时钟漂移。
            var completion = new TaskCompletionSource();
            scheduled = new ScheduledDelay(UtcNow + delay, completion);
            delays.Add(scheduled);
        }

        scheduled.Registration = cancellationToken.Register(
            static state =>
            {
                var cancellation = (DelayCancellation)state!;
                cancellation.Clock.CancelDelay(cancellation.Delay, cancellation.Token);
            },
            new DelayCancellation(this, scheduled, cancellationToken));
        if (scheduled.Completion.Task.IsCompleted)
        {
            scheduled.Registration.Dispose();
        }

        return scheduled.Completion.Task;
    }

    /// <summary>推进时间，完成所有已到期等待，并让其延续任务运行。</summary>
    /// <param name="duration">要推进的虚拟时长。</param>
    internal async Task AdvanceAsync(TimeSpan duration)
    {
        List<ScheduledDelay> due;
        lock (syncRoot)
        {
            UtcNow += duration;
            due = delays.Where(item => item.DueAt <= UtcNow).ToList();
            delays.RemoveAll(item => item.DueAt <= UtcNow);
        }

        foreach (var item in due)
        {
            item.Registration.Dispose();
            item.Completion.TrySetResult();
        }

        await DrainAsync();
    }

    /// <summary>在取消发生时立即移除等待并释放注册，避免已取消 delay 污染后续断言。</summary>
    /// <param name="scheduled">要取消的已登记等待。</param>
    /// <param name="cancellationToken">触发取消的原始令牌。</param>
    private void CancelDelay(ScheduledDelay scheduled, CancellationToken cancellationToken)
    {
        var removed = false;
        lock (syncRoot)
        {
            removed = delays.Remove(scheduled);
        }

        if (removed)
        {
            scheduled.Completion.TrySetCanceled(cancellationToken);
        }

        scheduled.Registration.Dispose();
    }

    /// <summary>让异步延续任务排空，不依赖真实时间等待。</summary>
    internal static async Task DrainAsync()
    {
        for (var index = 0; index < 64; index++)
        {
            await Task.Factory.StartNew(
                static () => { },
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
        }
    }

    /// <summary>表示一次等待及其取消注册。</summary>
    private sealed class ScheduledDelay
    {
        /// <summary>创建固定到期时刻的等待。</summary>
        internal ScheduledDelay(DateTimeOffset dueAt, TaskCompletionSource completion)
        {
            DueAt = dueAt;
            Completion = completion;
        }

        internal DateTimeOffset DueAt { get; }

        internal TaskCompletionSource Completion { get; }

        internal CancellationTokenRegistration Registration { get; set; }
    }

    /// <summary>绑定虚拟时钟、等待和原始令牌，供无捕获取消 callback 使用。</summary>
    /// <param name="Clock">拥有等待队列的虚拟时钟。</param>
    /// <param name="Delay">要从队列移除的等待。</param>
    /// <param name="Token">完成取消任务时使用的原始令牌。</param>
    private sealed record DelayCancellation(FakeClock Clock, ScheduledDelay Delay, CancellationToken Token);
}
