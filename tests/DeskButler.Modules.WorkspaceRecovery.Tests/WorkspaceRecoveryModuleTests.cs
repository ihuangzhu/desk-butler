using DeskButler.Core.Capture;
using DeskButler.Core.Persistence;
using DeskButler.Core.Scenes;
using DeskButler.Core.Settings;
using DeskButler.Modules.WorkspaceRecovery.Capture;
using DeskButler.Modules.WorkspaceRecovery.Tests.Capture;

namespace DeskButler.Modules.WorkspaceRecovery.Tests;

public sealed class WorkspaceRecoveryModuleTests
{
    /// <summary>验证启动后来自任意线程的变化信号被安全合并，停止后事件源已释放。</summary>
    [Fact]
    public async Task StartSubscribesThreadSafeDesktopChangesAndStopUnsubscribes()
    {
        var clock = new FakeClock();
        var source = new FakeDesktopChangeSource();
        var repository = new ModuleSceneRepository();
        var coordinator = CreateCoordinator(ButlerSettings.Default, repository, clock);
        await using var scheduler = new SnapshotScheduler(clock, coordinator.SaveNowAsync);
        var module = new WorkspaceRecoveryModule(source, scheduler, coordinator, clock);

        await module.StartAsync(CancellationToken.None);
        Parallel.For(0, 32, _ => source.Raise());
        await clock.AdvanceAsync(TimeSpan.FromSeconds(10));

        Assert.Single(repository.Snapshots);
        Assert.Equal("quiet-debounce", repository.Snapshots[0].CaptureReason);
        Assert.Equal(1, source.SubscriberCount);

        await module.StopAsync(CancellationToken.None);
        Assert.Equal(0, source.SubscriberCount);
    }

    /// <summary>验证停止先吞掉普通防抖批次，再以独立 module-stop 原因执行最终刷新。</summary>
    [Fact]
    public async Task StopCancelsDebounceButPerformsFinalFlush()
    {
        var clock = new FakeClock();
        var source = new FakeDesktopChangeSource();
        var repository = new ModuleSceneRepository();
        var coordinator = CreateCoordinator(ButlerSettings.Default, repository, clock);
        await using var scheduler = new SnapshotScheduler(clock, coordinator.SaveNowAsync);
        var module = new WorkspaceRecoveryModule(source, scheduler, coordinator, clock);
        await module.StartAsync(CancellationToken.None);

        source.Raise();
        await module.StopAsync(CancellationToken.None);
        await clock.AdvanceAsync(TimeSpan.FromMinutes(5));

        var snapshot = Assert.Single(repository.Snapshots);
        Assert.Equal("module-stop", snapshot.CaptureReason);
        Assert.Equal(0, source.SubscriberCount);
    }

    /// <summary>验证最终刷新超过内部时间上限时会被取消，停止不会无限阻塞。</summary>
    [Fact]
    public async Task StopFinalFlushIsBoundedByFakeClock()
    {
        var clock = new FakeClock();
        var source = new FakeDesktopChangeSource();
        var repository = new ModuleSceneRepository(blockSaveUntilCanceled: true);
        var coordinator = CreateCoordinator(ButlerSettings.Default, repository, clock);
        await using var scheduler = new SnapshotScheduler(clock, coordinator.SaveNowAsync);
        var module = new WorkspaceRecoveryModule(
            source,
            scheduler,
            coordinator,
            clock,
            TimeSpan.FromSeconds(5));
        await module.StartAsync(CancellationToken.None);

        var stopTask = module.StopAsync(CancellationToken.None);
        Assert.False(stopTask.IsCompleted);
        Assert.Equal(0, source.SubscriberCount);

        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
        await stopTask;
        await repository.SaveExited.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(repository.SaveCancellationObserved);
        Assert.IsType<TimeoutException>(module.LastFailure);
        Assert.Equal(0, source.SubscriberCount);
        Assert.Equal(0, clock.PendingDelayCount);
    }

    /// <summary>验证在途自动保存不协作取消时，整个模块停止仍受同一虚拟时间上限约束。</summary>
    [Fact]
    public async Task StopIsBoundedWhenInFlightAutomaticSaveIgnoresCancellation()
    {
        var clock = new FakeClock();
        var source = new FakeDesktopChangeSource();
        var automaticSaveEntered = new TaskCompletionSource();
        var automaticSaveRelease = new TaskCompletionSource();
        var repository = new ModuleSceneRepository();
        var coordinator = CreateCoordinator(ButlerSettings.Default, repository, clock);
        await using var scheduler = new SnapshotScheduler(
            clock,
            async (_, _) =>
            {
                automaticSaveEntered.SetResult();
                await automaticSaveRelease.Task;
            });
        var module = new WorkspaceRecoveryModule(
            source,
            scheduler,
            coordinator,
            clock,
            TimeSpan.FromSeconds(5));
        await module.StartAsync(CancellationToken.None);
        source.Raise();
        await clock.AdvanceAsync(TimeSpan.FromSeconds(10));
        await automaticSaveEntered.Task;

        var stopTask = module.StopAsync(CancellationToken.None);
        Assert.Equal(clock.Start + TimeSpan.FromSeconds(15), clock.NextDueAt);
        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
        await FakeClock.DrainAsync();
        var stoppedWithinLimit = stopTask.IsCompleted;

        automaticSaveRelease.SetResult();
        await stopTask;
        Assert.True(stoppedWithinLimit);
        Assert.IsType<TimeoutException>(module.LastFailure);
    }

    /// <summary>验证关闭捕获时模块停止不会写入最终快照。</summary>
    [Fact]
    public async Task CaptureDisabledPreventsStopFlushSave()
    {
        var clock = new FakeClock();
        var source = new FakeDesktopChangeSource();
        var repository = new ModuleSceneRepository();
        var settings = ButlerSettings.Default with { CaptureEnabled = false };
        var coordinator = CreateCoordinator(settings, repository, clock);
        await using var scheduler = new SnapshotScheduler(clock, coordinator.SaveNowAsync);
        var module = new WorkspaceRecoveryModule(source, scheduler, coordinator, clock);

        await module.StartAsync(CancellationToken.None);
        source.Raise();
        await clock.AdvanceAsync(TimeSpan.FromSeconds(10));
        await module.StopAsync(CancellationToken.None);

        Assert.Empty(repository.Snapshots);
    }

    /// <summary>验证两个并发 Stop 调用等待同一个清理任务，第二个不会因停止标志提前返回。</summary>
    [Fact]
    public async Task ConcurrentStopsWaitForSameCoreCleanup()
    {
        var clock = new FakeClock();
        var source = new FakeDesktopChangeSource();
        var repository = new ModuleSceneRepository(blockSaveUntilCanceled: true);
        var coordinator = CreateCoordinator(ButlerSettings.Default, repository, clock);
        await using var scheduler = new SnapshotScheduler(clock, coordinator.SaveNowAsync);
        var module = new WorkspaceRecoveryModule(source, scheduler, coordinator, clock, TimeSpan.FromSeconds(5));
        await module.StartAsync(TestContext.Current.CancellationToken);

        var firstStop = module.StopAsync(TestContext.Current.CancellationToken);
        var secondStop = module.StopAsync(TestContext.Current.CancellationToken);
        var secondReturnedBeforeCleanup = secondStop.IsCompleted;

        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(firstStop, secondStop);
        await repository.SaveExited.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(secondReturnedBeforeCleanup);
        Assert.True(repository.SaveCancellationObserved);
    }

    /// <summary>验证首次 caller 取消只取消自身等待，全局清理继续且后续 Stop 可等待最终保存。</summary>
    [Fact]
    public async Task FirstCallerCancellationDoesNotAbandonSharedStopCore()
    {
        var clock = new FakeClock();
        var source = new FakeDesktopChangeSource();
        var saveRelease = new TaskCompletionSource();
        var repository = new ModuleSceneRepository(saveRelease: saveRelease.Task);
        var coordinator = CreateCoordinator(ButlerSettings.Default, repository, clock);
        await using var scheduler = new SnapshotScheduler(clock, coordinator.SaveNowAsync);
        var module = new WorkspaceRecoveryModule(source, scheduler, coordinator, clock, TimeSpan.FromSeconds(5));
        await module.StartAsync(TestContext.Current.CancellationToken);
        using var firstCallerSource = new CancellationTokenSource();

        var firstStop = module.StopAsync(firstCallerSource.Token);
        await repository.SaveEntered.Task;
        firstCallerSource.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstStop);
        Assert.Null(module.LastFailure);

        var secondStop = module.StopAsync(TestContext.Current.CancellationToken);
        var secondReturnedBeforeCleanup = secondStop.IsCompleted;
        saveRelease.SetResult();
        await secondStop;

        Assert.False(secondReturnedBeforeCleanup);
        Assert.Single(repository.Snapshots);
        Assert.Null(module.LastFailure);
        Assert.Equal(0, clock.PendingDelayCount);
    }

    /// <summary>验证内部超时后的迟到成功不会清除已记录的 TimeoutException。</summary>
    [Fact]
    public async Task LateSuccessAfterTimeoutPreservesTimeoutFailure()
    {
        var clock = new FakeClock();
        var source = new FakeDesktopChangeSource();
        var saveRelease = new TaskCompletionSource();
        var repository = new ModuleSceneRepository(
            saveRelease: saveRelease.Task,
            ignoreCancellationAfterRelease: true);
        var coordinator = CreateCoordinator(ButlerSettings.Default, repository, clock);
        await using var scheduler = new SnapshotScheduler(clock, coordinator.SaveNowAsync);
        var module = new WorkspaceRecoveryModule(source, scheduler, coordinator, clock, TimeSpan.FromSeconds(5));
        await module.StartAsync(TestContext.Current.CancellationToken);

        var stopTask = module.StopAsync(TestContext.Current.CancellationToken);
        await repository.SaveEntered.Task;
        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
        await stopTask;
        Assert.IsType<TimeoutException>(module.LastFailure);

        saveRelease.SetResult();
        await repository.SaveExited.Task;
        await FakeClock.DrainAsync();

        Assert.IsType<TimeoutException>(module.LastFailure);
    }

    /// <summary>验证内部超时后的迟到 fault 与 TimeoutException 聚合后可观察。</summary>
    [Fact]
    public async Task LateFaultAfterTimeoutAggregatesTimeoutAndRootFailure()
    {
        var clock = new FakeClock();
        var source = new FakeDesktopChangeSource();
        var saveRelease = new TaskCompletionSource();
        var rootFailure = new InvalidOperationException("controlled late failure");
        var repository = new ModuleSceneRepository(
            saveRelease: saveRelease.Task,
            ignoreCancellationAfterRelease: true,
            saveFailure: rootFailure);
        var coordinator = CreateCoordinator(ButlerSettings.Default, repository, clock);
        await using var scheduler = new SnapshotScheduler(clock, coordinator.SaveNowAsync);
        var module = new WorkspaceRecoveryModule(source, scheduler, coordinator, clock, TimeSpan.FromSeconds(5));
        await module.StartAsync(TestContext.Current.CancellationToken);

        var stopTask = module.StopAsync(TestContext.Current.CancellationToken);
        await repository.SaveEntered.Task;
        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
        await stopTask;
        Assert.IsType<TimeoutException>(module.LastFailure);

        saveRelease.SetResult();
        await repository.SaveExited.Task;
        await FakeClock.DrainAsync();

        var aggregate = Assert.IsType<AggregateException>(module.LastFailure);
        Assert.Contains("停止超时后后台任务又失败", aggregate.Message, StringComparison.Ordinal);
        Assert.Contains(aggregate.InnerExceptions, exception => exception is TimeoutException);
        Assert.Contains(aggregate.InnerExceptions, exception => ReferenceEquals(exception, rootFailure));
    }

    /// <summary>验证正常停止成功时会取消并移除未使用的虚拟超时等待。</summary>
    [Fact]
    public async Task SuccessfulStopRemovesUnusedTimeoutDelay()
    {
        var clock = new FakeClock();
        var source = new FakeDesktopChangeSource();
        var repository = new ModuleSceneRepository();
        var coordinator = CreateCoordinator(ButlerSettings.Default, repository, clock);
        await using var scheduler = new SnapshotScheduler(clock, coordinator.SaveNowAsync);
        var module = new WorkspaceRecoveryModule(source, scheduler, coordinator, clock, TimeSpan.FromSeconds(5));
        await module.StartAsync(TestContext.Current.CancellationToken);

        await module.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, clock.PendingDelayCount);
        Assert.Single(repository.Snapshots);
    }

    /// <summary>创建返回固定有效候选的真实捕获协调器。</summary>
    /// <param name="settings">捕获开关设置。</param>
    /// <param name="repository">记录模块保存结果的仓库。</param>
    /// <param name="clock">虚拟时钟。</param>
    /// <returns>连接既有过滤器的协调器。</returns>
    private static CaptureCoordinator CreateCoordinator(
        ButlerSettings settings,
        ModuleSceneRepository repository,
        FakeClock clock)
    {
        return new CaptureCoordinator(
            settings,
            new ModuleWindowInventory(),
            new SceneFilter(settings),
            repository,
            clock);
    }

    /// <summary>允许测试触发并检查订阅生命周期的最小桌面变化事件源。</summary>
    private sealed class FakeDesktopChangeSource : IDesktopChangeSource
    {
        private EventHandler? desktopChanged;

        /// <inheritdoc />
        public event EventHandler? DesktopChanged
        {
            add => desktopChanged += value;
            remove => desktopChanged -= value;
        }

        internal int SubscriberCount => desktopChanged?.GetInvocationList().Length ?? 0;

        /// <summary>从当前线程同步发出一次桌面变化。</summary>
        internal void Raise()
        {
            desktopChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>返回一个完整普通窗口的固定清单。</summary>
    private sealed class ModuleWindowInventory : IWindowInventory
    {
        /// <inheritdoc />
        public Task<IReadOnlyList<WindowCandidate>> CaptureAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<WindowCandidate> candidates = [CandidateFactory.Normal()];
            return Task.FromResult(candidates);
        }
    }

    /// <summary>记录模块快照，并可模拟直到取消才结束的保存。</summary>
    private sealed class ModuleSceneRepository : ISceneRepository
    {
        private readonly bool blockSaveUntilCanceled;
        private readonly Task? saveRelease;
        private readonly bool ignoreCancellationAfterRelease;
        private readonly Exception? saveFailure;

        /// <summary>创建普通或受控阻塞的内存仓库。</summary>
        /// <param name="blockSaveUntilCanceled">保存是否等待取消令牌。</param>
        internal ModuleSceneRepository(
            bool blockSaveUntilCanceled = false,
            Task? saveRelease = null,
            bool ignoreCancellationAfterRelease = false,
            Exception? saveFailure = null)
        {
            this.blockSaveUntilCanceled = blockSaveUntilCanceled;
            this.saveRelease = saveRelease;
            this.ignoreCancellationAfterRelease = ignoreCancellationAfterRelease;
            this.saveFailure = saveFailure;
        }

        internal List<SceneSnapshot> Snapshots { get; } = [];

        internal bool SaveCancellationObserved { get; private set; }

        internal TaskCompletionSource SaveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource SaveExited { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public async Task SaveAsync(SceneSnapshot snapshot, CancellationToken cancellationToken)
        {
            try
            {
                SaveEntered.TrySetResult();
                if (blockSaveUntilCanceled)
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        SaveCancellationObserved = true;
                        throw;
                    }
                }

                if (saveRelease is not null)
                {
                    await saveRelease;
                }

                if (!ignoreCancellationAfterRelease)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (saveFailure is not null)
                {
                    throw saveFailure;
                }

                Snapshots.Add(snapshot);
            }
            finally
            {
                SaveExited.TrySetResult();
            }
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<SceneSnapshot>> GetRecentAsync(int maximumCount, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<SceneSnapshot> snapshots = Snapshots.TakeLast(maximumCount).Reverse().ToArray();
            return Task.FromResult(snapshots);
        }

        /// <inheritdoc />
        public Task MarkInvalidAsync(Guid snapshotId, string reason, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
