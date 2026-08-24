using DeskButler.Modules.WorkspaceRecovery.Capture;
using DeskButler.Core.Time;

namespace DeskButler.Modules.WorkspaceRecovery.Tests.Capture;

public sealed class SnapshotSchedulerTests
{
    /// <summary>验证一次变化静止十秒后只保存一次，后续时间流逝不会复制保存。</summary>
    [Fact]
    public async Task QuietChangeSavesOnceAfterTenSeconds()
    {
        var clock = new FakeClock();
        var saves = new List<(DateTimeOffset At, string Reason)>();
        await using var scheduler = new SnapshotScheduler(
            clock,
            (reason, _) =>
            {
                saves.Add((clock.UtcNow, reason));
                return Task.CompletedTask;
            });

        scheduler.NotifyDesktopChanged();
        await clock.AdvanceAsync(TimeSpan.FromSeconds(9));
        Assert.Empty(saves);

        await clock.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal([(clock.Start + TimeSpan.FromSeconds(10), "quiet-debounce")], saves);

        await clock.AdvanceAsync(TimeSpan.FromMinutes(5));
        Assert.Single(saves);
    }

    /// <summary>验证持续变化会从首个待保存信号起满六十秒强制保存。</summary>
    [Fact]
    public async Task ContinuousChangesForceCheckpointAtSixtySeconds()
    {
        var clock = new FakeClock();
        var saves = new List<(DateTimeOffset At, string Reason)>();
        await using var scheduler = new SnapshotScheduler(
            clock,
            (reason, _) =>
            {
                saves.Add((clock.UtcNow, reason));
                return Task.CompletedTask;
            });

        for (var second = 0; second < 70; second += 5)
        {
            scheduler.NotifyDesktopChanged();
            await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Contains(
            saves,
            save => save == (clock.Start + TimeSpan.FromSeconds(60), "continuous-checkpoint"));
    }

    /// <summary>验证后续变化会重置十秒静止期限，而不会重置六十秒批次起点。</summary>
    [Fact]
    public async Task RepeatedChangeResetsQuietDeadline()
    {
        var clock = new FakeClock();
        var saveTimes = new List<DateTimeOffset>();
        await using var scheduler = new SnapshotScheduler(
            clock,
            (_, _) =>
            {
                saveTimes.Add(clock.UtcNow);
                return Task.CompletedTask;
            });

        scheduler.NotifyDesktopChanged();
        await clock.AdvanceAsync(TimeSpan.FromSeconds(9));
        scheduler.NotifyDesktopChanged();
        await clock.AdvanceAsync(TimeSpan.FromSeconds(9));
        Assert.Empty(saveTimes);

        await clock.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal([clock.Start + TimeSpan.FromSeconds(19)], saveTimes);
    }

    /// <summary>验证保存期间的新变化形成独立批次，且两个保存回调绝不重叠。</summary>
    [Fact]
    public async Task ChangeDuringSaveIsSavedLaterWithoutOverlap()
    {
        var clock = new FakeClock();
        var firstSaveRelease = new TaskCompletionSource();
        var saveCount = 0;
        var activeSaves = 0;
        var maximumActiveSaves = 0;
        await using var scheduler = new SnapshotScheduler(
            clock,
            async (_, _) =>
            {
                var active = Interlocked.Increment(ref activeSaves);
                maximumActiveSaves = Math.Max(maximumActiveSaves, active);
                var currentSave = Interlocked.Increment(ref saveCount);
                if (currentSave == 1)
                {
                    await firstSaveRelease.Task;
                }

                Interlocked.Decrement(ref activeSaves);
            });

        scheduler.NotifyDesktopChanged();
        await clock.AdvanceAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, saveCount);

        scheduler.NotifyDesktopChanged();
        await clock.AdvanceAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, saveCount);

        firstSaveRelease.SetResult();
        await FakeClock.DrainAsync();

        Assert.Equal(2, saveCount);
        Assert.Equal(1, maximumActiveSaves);
    }

    /// <summary>验证一次保存异常可被观察且不会阻止下一批变化保存。</summary>
    [Fact]
    public async Task SaveFailureIsObservableAndLaterChangeStillSaves()
    {
        var clock = new FakeClock();
        var attempts = 0;
        await using var scheduler = new SnapshotScheduler(
            clock,
            (_, _) =>
            {
                attempts++;
                return attempts == 1
                    ? Task.FromException(new InvalidOperationException("controlled failure"))
                    : Task.CompletedTask;
            });

        scheduler.NotifyDesktopChanged();
        await clock.AdvanceAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<InvalidOperationException>(scheduler.LastFailure);

        scheduler.NotifyDesktopChanged();
        await clock.AdvanceAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, attempts);
        Assert.Null(scheduler.LastFailure);
    }

    /// <summary>验证停止会丢弃普通防抖批次，之后的通知也不会产生保存。</summary>
    [Fact]
    public async Task StopCancelsPendingAndFutureSaves()
    {
        var clock = new FakeClock();
        var saveCount = 0;
        await using var scheduler = new SnapshotScheduler(
            clock,
            (_, _) =>
            {
                saveCount++;
                return Task.CompletedTask;
            });

        scheduler.NotifyDesktopChanged();
        await scheduler.StopAsync(CancellationToken.None);
        await clock.AdvanceAsync(TimeSpan.FromMinutes(5));
        scheduler.NotifyDesktopChanged();
        await clock.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(0, saveCount);
    }

    /// <summary>验证首次 runner 尚未返回可发布 Task 时，Stop 仍等待该轮 runner 完成。</summary>
    [Fact]
    public async Task StopWaitsForFirstRunnerWhenStartupIsBlockedBeforeTaskPublication()
    {
        var clock = new BlockingDelayClock();
        await using var scheduler = new SnapshotScheduler(clock, (_, _) => Task.CompletedTask);

        var notifyTask = Task.Run(scheduler.NotifyDesktopChanged, TestContext.Current.CancellationToken);
        await clock.DelayEntered.Task;

        var stopTask = scheduler.StopAsync(TestContext.Current.CancellationToken);
        var stopReturnedBeforeRunner = stopTask.IsCompleted;

        clock.ReleaseDelayCall.SetResult();
        await notifyTask;
        await stopTask;

        Assert.False(stopReturnedBeforeRunner);
        Assert.True(clock.CancellationObserved);
    }

    /// <summary>在 DelayAsync 返回 Task 前同步阻塞，用于重现首次 runner 发布竞态。</summary>
    private sealed class BlockingDelayClock : IClock
    {
        internal TaskCompletionSource DelayEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseDelayCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool CancellationObserved { get; private set; }

        /// <inheritdoc />
        public DateTimeOffset UtcNow { get; } =
            new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);

        /// <inheritdoc />
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            DelayEntered.TrySetResult();
            ReleaseDelayCall.Task.GetAwaiter().GetResult();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }

            return Task.CompletedTask;
        }
    }
}
