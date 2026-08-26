using DeskButler.Core.Diagnostics;
using DeskButler.Core.ResidentApps;
using DeskButler.Core.Settings;
using DeskButler.Core.Time;
using DeskButler.Desktop.Hosting;

namespace DeskButler.Desktop.Tests.Hosting;

public sealed class ResidentLaunchCoordinatorTests
{
    private static readonly DateTimeOffset InitialTime = new(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlySet<string> AllowedDiagnosticProperties =
        new HashSet<string>(["displayName", "path", "result", "exceptionType"], StringComparer.Ordinal);

    /// <summary>首次自动批次必须先等待五秒，并只执行届时原子保存的固定顺序。</summary>
    [Fact]
    public async Task FirstBatchWaitsFiveSecondsAndKeepsSavedPlanOrder()
    {
        var qq = App("QQ", @"C:\Apps\qq.exe", enabled: true, order: 0);
        var wechat = App("WeChat", @"C:\Apps\wechat.exe", enabled: true, order: 1);
        var disabled = App("Disabled", @"C:\Apps\disabled.exe", enabled: false, order: 2);
        var settings = new MutableSettingsStore(Settings(qq, wechat, disabled));
        var sessions = new RecordingSessionStore();
        var clock = new ManualClock(InitialTime);
        var runtime = new RecordingRuntime();
        await using var coordinator = Create(settings, sessions, clock, runtime, "LUID-A");

        coordinator.Start();
        var completion = coordinator.Completion;
        coordinator.Start();
        await clock.WaitForDelayAsync(TimeSpan.FromSeconds(5));

        Assert.Same(completion, coordinator.Completion);
        Assert.Empty(runtime.StartedPaths);
        Assert.Null(sessions.Current);

        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
        await sessions.FirstSave.Task;
        await runtime.FirstStart.Task;
        settings.Current = Settings(
            App("Added", @"C:\Apps\added.exe", enabled: true, order: 0),
            disabled with { Enabled = true, LaunchOrder = 1 },
            wechat with { LaunchOrder = 2 },
            qq with { LaunchOrder = 3 });
        await clock.WaitForDelayAsync(TimeSpan.FromSeconds(1));
        await clock.AdvanceAsync(TimeSpan.FromSeconds(1));
        await completion;

        Assert.Equal([qq.LaunchPath, wechat.LaunchPath], runtime.StartedPaths);
        Assert.True(sessions.Current!.Completed);
        Assert.Equal(2, sessions.Current.Plan.Count);
        Assert.All(sessions.Current.Plan, item => Assert.True(item.Attempted));
    }

    /// <summary>同一登录会话的完成批次必须立即结束，不等待也不读取设置或启动程序。</summary>
    [Fact]
    public async Task CompletedCurrentLogonSessionEndsImmediately()
    {
        var settings = new MutableSettingsStore(Settings(App("QQ", @"C:\Apps\qq.exe", true, 0)));
        var sessions = new RecordingSessionStore
        {
            Current = new ResidentLaunchSession(1, "LUID-A", true, [new("opaque", true)])
        };
        var clock = new ManualClock(InitialTime);
        var runtime = new RecordingRuntime();
        await using var coordinator = Create(settings, sessions, clock, runtime, "LUID-A");

        coordinator.Start();
        await coordinator.Completion;

        Assert.Equal(0, settings.LoadCount);
        Assert.Empty(clock.RequestedDelays);
        Assert.Empty(runtime.StartedPaths);
        Assert.Equal(0, sessions.SaveCount);
    }

    /// <summary>新的登录 LUID 必须在初始延迟后用当前设置原子替换旧登录计划。</summary>
    [Fact]
    public async Task DifferentLogonSessionReplacesOldPlan()
    {
        var current = App("Current", @"C:\Apps\current.exe", true, 0);
        var settings = new MutableSettingsStore(Settings(current));
        var sessions = new RecordingSessionStore
        {
            Current = new ResidentLaunchSession(1, "LUID-OLD", false, [new("old-identity", false)])
        };
        var clock = new ManualClock(InitialTime);
        var runtime = new RecordingRuntime();
        await using var coordinator = Create(settings, sessions, clock, runtime, "LUID-NEW");

        coordinator.Start();
        await clock.WaitForDelayAsync(TimeSpan.FromSeconds(5));
        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
        await coordinator.Completion;

        Assert.Equal("LUID-NEW", sessions.Current!.LogonSessionId);
        Assert.True(sessions.Current.Completed);
        Assert.Equal(current.LaunchPath, Assert.Single(runtime.StartedPaths));
        Assert.DoesNotContain(sessions.Current.Plan, item => item.LaunchIdentity == "old-identity");
    }

    /// <summary>总开关关闭时也必须为当前 LUID 原子写入 completed 空计划。</summary>
    [Fact]
    public async Task DisabledMasterSwitchPersistsCompletedEmptyPlan()
    {
        var settings = new MutableSettingsStore(
            Settings(App("QQ", @"C:\Apps\qq.exe", true, 0)) with { ResidentApplicationsEnabled = false });
        var sessions = new RecordingSessionStore();
        var clock = new ManualClock(InitialTime);
        var runtime = new RecordingRuntime();
        await using var coordinator = Create(settings, sessions, clock, runtime, "LUID-A");

        coordinator.Start();
        await clock.WaitForDelayAsync(TimeSpan.FromSeconds(5));
        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
        await coordinator.Completion;

        Assert.Equal(new ResidentLaunchSession(1, "LUID-A", true, []), sessions.Current);
        Assert.Empty(runtime.StartedPaths);
        Assert.Equal(1, sessions.SaveCount);
    }

    /// <summary>启动边界抛错前必须已保存 attempted，DeskButler 重启后只续跑剩余项。</summary>
    [Fact]
    public async Task FailedStartIsAttemptedBeforeBoundaryAndRestartOnlyRunsRemainder()
    {
        var first = App("First", @"C:\Apps\first.exe", true, 0);
        var second = App("Second", @"C:\Apps\second.exe", true, 1);
        var settings = new MutableSettingsStore(Settings(first, second));
        var sessions = new RecordingSessionStore();
        var firstClock = new ManualClock(InitialTime);
        var failedRuntime = new RecordingRuntime
        {
            StartBehavior = _ => Task.FromException(new IOException("simulated crash boundary"))
        };
        await using (var firstCoordinator = Create(settings, sessions, firstClock, failedRuntime, "LUID-A"))
        {
            firstCoordinator.Start();
            await firstClock.WaitForDelayAsync(TimeSpan.FromSeconds(5));
            await firstClock.AdvanceAsync(TimeSpan.FromSeconds(5));
            await firstClock.WaitForDelayAsync(TimeSpan.FromSeconds(1));
            // 模拟 DeskButler 在下一项之前退出；第三方启动失败已被观察，未 attempted 项仍可续跑。
            await firstCoordinator.DisposeAsync();
        }

        Assert.False(sessions.Current!.Completed);
        Assert.True(sessions.Current.Plan[0].Attempted);
        Assert.False(sessions.Current.Plan[1].Attempted);

        var restartClock = new ManualClock(InitialTime.AddMinutes(1));
        var restartRuntime = new RecordingRuntime();
        await using var restarted = Create(settings, sessions, restartClock, restartRuntime, "LUID-A");
        restarted.Start();
        await restartClock.WaitForDelayAsync(TimeSpan.FromSeconds(5));
        await restartClock.AdvanceAsync(TimeSpan.FromSeconds(5));
        await restarted.Completion;

        Assert.Equal(second.LaunchPath, Assert.Single(restartRuntime.StartedPaths));
        Assert.True(sessions.Current.Completed);
        Assert.All(sessions.Current.Plan, item => Assert.True(item.Attempted));
    }

    /// <summary>续跑必须按 launch identity 回查最新设置，删除、禁用和换路径都只登记跳过。</summary>
    [Fact]
    public async Task ResumeUsesLatestSettingsAndNeverStartsStoredPaths()
    {
        var removed = App("Removed", @"C:\Apps\removed.exe", true, 0);
        var disabled = App("Disabled", @"C:\Apps\disabled.exe", true, 1);
        var changed = App("Changed", @"C:\Apps\old.exe", true, 2);
        var settings = new MutableSettingsStore(Settings(removed, disabled, changed));
        var sessions = new RecordingSessionStore();
        sessions.AfterFirstSave = _ =>
        {
            settings.Current = Settings(
                disabled with { Enabled = false },
                changed with { LaunchPath = @"C:\Apps\new.exe", KnownProcessPaths =
                    new HashSet<string>([@"C:\Apps\new.exe"], StringComparer.OrdinalIgnoreCase) });
        };
        var clock = new ManualClock(InitialTime);
        var runtime = new RecordingRuntime();
        await using var coordinator = Create(settings, sessions, clock, runtime, "LUID-A");

        coordinator.Start();
        await clock.WaitForDelayAsync(TimeSpan.FromSeconds(5));
        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
        await clock.WaitForDelayAsync(TimeSpan.FromSeconds(1));
        await clock.AdvanceAsync(TimeSpan.FromSeconds(1));
        await clock.WaitForDelayAsync(TimeSpan.FromSeconds(1));
        await clock.AdvanceAsync(TimeSpan.FromSeconds(1));
        await coordinator.Completion;

        Assert.Empty(runtime.StartedPaths);
        Assert.True(sessions.Current!.Completed);
        Assert.All(sessions.Current.Plan, item => Assert.True(item.Attempted));
    }

    /// <summary>Running 和 Unknown 都必须终止该项，只有 NotRunning 才能进入策略和启动边界。</summary>
    [Fact]
    public async Task RunningAndUnknownSkipWhileNotRunningUsesDeduplicatedKnownPaths()
    {
        var running = App("Running", @"C:\Apps\running.exe", true, 0);
        var unknown = App("Unknown", @"C:\Apps\unknown.exe", true, 1);
        var ready = new ResidentApplication(
            @"C:\Apps\ready.exe",
            new HashSet<string>(
                [@"C:\Apps\ready.exe", @"c:\apps\READY.exe", @"C:\Apps\helper.exe"],
                StringComparer.Ordinal),
            "Ready",
            true,
            2);
        var settings = new MutableSettingsStore(Settings(running, unknown, ready));
        var sessions = new RecordingSessionStore();
        var clock = new ManualClock(InitialTime);
        var runtime = new RecordingRuntime
        {
            CheckBehavior = paths => Task.FromResult(
                paths.Contains(running.LaunchPath)
                    ? new ResidentRunningCheck(ResidentRunningState.Running, running.LaunchPath)
                    : paths.Contains(unknown.LaunchPath)
                        ? new ResidentRunningCheck(ResidentRunningState.Unknown, null)
                        : new ResidentRunningCheck(ResidentRunningState.NotRunning, null))
        };
        var policy = new ConfigurablePolicy();
        await using var coordinator = Create(settings, sessions, clock, runtime, "LUID-A", policy);

        coordinator.Start();
        await clock.AdvanceUntilCompletedAsync(coordinator.Completion);

        Assert.Equal(3, runtime.CheckedKnownPaths.Count);
        Assert.Equal(2, runtime.CheckedKnownPaths[2].Count);
        Assert.Equal([ready.LaunchPath], policy.ValidatedPaths);
        Assert.Equal(ready.LaunchPath, Assert.Single(runtime.StartedPaths));
        Assert.All(sessions.Current!.Plan, item => Assert.True(item.Attempted));
    }

    /// <summary>检查、策略和启动的单项故障都必须先登记并隔离，后续条目仍可启动。</summary>
    [Fact]
    public async Task ItemFailuresAreAttemptedAndIsolatedFromLaterApplications()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrWhiteSpace(userProfile));
        var checkFails = App(
            "CheckFails",
            Path.Combine(userProfile, "Apps", "check-fails.exe"),
            true,
            0);
        var policyRejects = App("PolicyRejects", @"C:\Apps\policy-rejects.exe", true, 1);
        var startFails = App("StartFails", @"C:\Apps\start-fails.exe", true, 2);
        var succeeds = App("Succeeds", @"C:\Apps\succeeds.exe", true, 3);
        var settings = new MutableSettingsStore(Settings(checkFails, policyRejects, startFails, succeeds));
        var sessions = new RecordingSessionStore();
        var clock = new ManualClock(InitialTime);
        var runtime = new RecordingRuntime
        {
            Now = () => clock.UtcNow,
            CheckBehavior = paths => paths.Contains(checkFails.LaunchPath)
                ? Task.FromException<ResidentRunningCheck>(new IOException("check secret"))
                : Task.FromResult(new ResidentRunningCheck(ResidentRunningState.NotRunning, null)),
            StartBehavior = path => path.Equals(startFails.LaunchPath, StringComparison.OrdinalIgnoreCase)
                ? Task.FromException(new IOException("start secret"))
                : Task.CompletedTask
        };
        var policy = new ConfigurablePolicy(path =>
            path.Equals(policyRejects.LaunchPath, StringComparison.OrdinalIgnoreCase)
                ? new ResidentExecutableValidation(false, null, ResidentExecutableRejection.AccessDenied)
                : new ResidentExecutableValidation(true, Path.GetFullPath(path), ResidentExecutableRejection.None));
        var diagnostics = new RecordingDiagnosticLog();
        await using var coordinator = Create(
            settings, sessions, clock, runtime, "LUID-A", policy, diagnostics);

        coordinator.Start();
        await clock.AdvanceUntilCompletedAsync(coordinator.Completion);

        Assert.Equal([startFails.LaunchPath, succeeds.LaunchPath], runtime.StartedPaths);
        Assert.Equal(succeeds.LaunchPath, Assert.Single(runtime.CompletedStartPaths));
        Assert.True(sessions.Current!.Completed);
        Assert.All(sessions.Current.Plan, item => Assert.True(item.Attempted));
        Assert.True(runtime.StartAttemptTimes[1] - runtime.StartAttemptTimes[0] >= TimeSpan.FromSeconds(1));
        Assert.All(
            diagnostics.Events.SelectMany(item => item.Properties?.Keys ?? []),
            key => Assert.Contains(key, AllowedDiagnosticProperties));
        Assert.DoesNotContain(
            diagnostics.Events.SelectMany(item => item.Properties?.Values ?? []).OfType<string>(),
            value => value.Contains("LUID", StringComparison.OrdinalIgnoreCase) ||
                     value.Contains("secret", StringComparison.OrdinalIgnoreCase));
        var checkDiagnostic = Assert.Single(diagnostics.Events, item =>
            Equals(item.Properties?["result"], "running-check-failed"));
        var redactedPath = Assert.IsType<string>(checkDiagnostic.Properties!["path"]);
        Assert.StartsWith("%USERPROFILE%", redactedPath, StringComparison.Ordinal);
        Assert.DoesNotContain(userProfile, redactedPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>损坏会话无论能否保全证据，本次进程都不得创建计划或启动应用。</summary>
    [Theory]
    [InlineData(ResidentLaunchRecoveryResult.RecoveredWithEmptyPlan)]
    [InlineData(ResidentLaunchRecoveryResult.PreservationFailedFailClosed)]
    public async Task CorruptSessionRecoveryNeverLaunchesInCurrentProcess(
        ResidentLaunchRecoveryResult recoveryResult)
    {
        var settings = new MutableSettingsStore(Settings(App("QQ", @"C:\Apps\qq.exe", true, 0)));
        var sessions = new RecordingSessionStore
        {
            LoadFailure = new InvalidDataException("corrupt evidence"),
            RecoveryResult = recoveryResult
        };
        var clock = new ManualClock(InitialTime);
        var runtime = new RecordingRuntime();
        await using var coordinator = Create(settings, sessions, clock, runtime, "LUID-A");

        coordinator.Start();
        await coordinator.Completion;

        Assert.Equal(1, sessions.RecoveryCount);
        Assert.Equal(0, sessions.SaveCount);
        Assert.Equal(0, settings.LoadCount);
        Assert.Empty(clock.RequestedDelays);
        Assert.Empty(runtime.StartedPaths);
    }

    /// <summary>手动双击必须加入同一批次，每轮读取最新 enabled 设置且从不读写登录会话。</summary>
    [Fact]
    public async Task ManualLaunchIsIndependentSingleFlightUsingLatestSettingsWithoutSessionWrites()
    {
        var stale = App("Stale", @"C:\Apps\stale.exe", true, 0);
        var first = App("First", @"C:\Apps\first.exe", true, 0);
        var disabled = App("Disabled", @"C:\Apps\disabled.exe", false, 1);
        var latest = App("Latest", @"C:\Apps\latest.exe", true, 0);
        var settings = new BlockingLatestSettingsStore(Settings(stale));
        var sessions = new RecordingSessionStore();
        var clock = new ManualClock(InitialTime);
        var runtime = new RecordingRuntime();
        await using var coordinator = Create(settings, sessions, clock, runtime, "LUID-A");

        var firstClick = coordinator.LaunchEnabledNowAsync(CancellationToken.None);
        await settings.FirstLoadStarted.Task;
        settings.Current = Settings(first, disabled) with { ResidentApplicationsEnabled = false };
        var secondClick = coordinator.LaunchEnabledNowAsync(CancellationToken.None);
        Assert.Same(firstClick, secondClick);
        settings.ReleaseFirstLoad();
        await firstClick;

        Assert.Equal(first.LaunchPath, Assert.Single(runtime.StartedPaths));
        Assert.Empty(clock.RequestedDelays);
        Assert.Equal(0, sessions.LoadCount);
        Assert.Equal(0, sessions.SaveCount);

        settings.Current = Settings(latest);
        var nextBatch = coordinator.LaunchEnabledNowAsync(CancellationToken.None);
        Assert.NotSame(firstClick, nextBatch);
        await clock.WaitForDelayAsync(TimeSpan.FromSeconds(1));
        await clock.AdvanceAsync(TimeSpan.FromSeconds(1));
        await nextBatch;

        Assert.Equal([first.LaunchPath, latest.LaunchPath], runtime.StartedPaths);
        Assert.Equal(0, sessions.LoadCount);
        Assert.Equal(0, sessions.SaveCount);
    }

    /// <summary>并发 Dispose 必须共享一次清理，并取消仍处于五秒等待的自动批次。</summary>
    [Fact]
    public async Task ConcurrentDisposeDuringInitialDelayCancelsBatchExactlyOnce()
    {
        var settings = new MutableSettingsStore(Settings(App("QQ", @"C:\Apps\qq.exe", true, 0)));
        var sessions = new RecordingSessionStore();
        var clock = new ManualClock(InitialTime);
        var runtime = new RecordingRuntime();
        var coordinator = Create(settings, sessions, clock, runtime, "LUID-A");
        coordinator.Start();
        await clock.WaitForDelayAsync(TimeSpan.FromSeconds(5));

        var firstDispose = coordinator.DisposeAsync().AsTask();
        var secondDispose = coordinator.DisposeAsync().AsTask();
        await Task.WhenAll(firstDispose, secondDispose);

        Assert.Same(firstDispose, secondDispose);
        Assert.Equal(1, clock.CanceledDelayCount);
        Assert.Empty(runtime.StartedPaths);
        Assert.Throws<ObjectDisposedException>(coordinator.Start);
        await coordinator.DisposeAsync();
    }

    /// <summary>项目间隔中退出只取消尚未开始的项目，不对已经启动的第三方进程做终止动作。</summary>
    [Fact]
    public async Task DisposeDuringItemIntervalStopsRemainderWithoutUndoingStartedProcess()
    {
        var first = App("First", @"C:\Apps\first.exe", true, 0);
        var second = App("Second", @"C:\Apps\second.exe", true, 1);
        var settings = new MutableSettingsStore(Settings(first, second));
        var sessions = new RecordingSessionStore();
        var clock = new ManualClock(InitialTime);
        var runtime = new RecordingRuntime();
        var coordinator = Create(settings, sessions, clock, runtime, "LUID-A");
        coordinator.Start();
        await clock.AdvanceUntilAsync(runtime.FirstStart.Task);
        await clock.WaitForDelayAsync(TimeSpan.FromSeconds(1));

        await coordinator.DisposeAsync();

        Assert.Equal(first.LaunchPath, Assert.Single(runtime.CompletedStartPaths));
        Assert.Equal(1, clock.CanceledDelayCount);
        Assert.False(sessions.Current!.Completed);
        Assert.True(sessions.Current.Plan[0].Attempted);
        Assert.False(sessions.Current.Plan[1].Attempted);
    }

    /// <summary>启动请求越过边界后迟到失败必须被协调器观察，双 Dispose 等待同一结果且不外泄异常。</summary>
    [Fact]
    public async Task LateStartFailureIsObservedDuringSharedDispose()
    {
        var app = App("Late", @"C:\Apps\late.exe", true, 0);
        var settings = new MutableSettingsStore(Settings(app));
        var sessions = new RecordingSessionStore();
        var clock = new ManualClock(InitialTime);
        var lateStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new RecordingRuntime { StartBehavior = _ => lateStart.Task };
        var diagnostics = new RecordingDiagnosticLog();
        var coordinator = Create(settings, sessions, clock, runtime, "LUID-A", diagnosticLog: diagnostics);
        coordinator.Start();
        await clock.AdvanceUntilAsync(runtime.FirstStart.Task);

        var firstDispose = coordinator.DisposeAsync().AsTask();
        var secondDispose = coordinator.DisposeAsync().AsTask();
        Assert.Same(firstDispose, secondDispose);
        Assert.False(firstDispose.IsCompleted);
        lateStart.TrySetException(new IOException("late secret"));
        await Task.WhenAll(firstDispose, secondDispose);

        var failure = Assert.Single(diagnostics.Events, item =>
            Equals(item.Properties?["result"], "start-failed"));
        Assert.Equal(typeof(IOException).FullName, failure.Properties!["exceptionType"]);
        Assert.DoesNotContain("late secret", string.Join('|', failure.Properties.Values));
    }

    /// <summary>自动与手动批次并发时也必须共享实际 Start 边界的全局一秒节流。</summary>
    [Fact]
    public async Task AutomaticAndManualStartAttemptsAreGloballySpaced()
    {
        var app = App("Shared", @"C:\Apps\shared.exe", true, 0);
        var settings = new MutableSettingsStore(Settings(app));
        var sessions = new RecordingSessionStore();
        var clock = new ManualClock(InitialTime);
        var releaseAutomaticCheck = new TaskCompletionSource<ResidentRunningCheck>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var automaticCheckStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var checkCount = 0;
        var runtime = new RecordingRuntime
        {
            Now = () => clock.UtcNow,
            CheckBehavior = _ =>
            {
                if (Interlocked.Increment(ref checkCount) == 1)
                {
                    automaticCheckStarted.TrySetResult();
                    return releaseAutomaticCheck.Task;
                }

                return Task.FromResult(
                    new ResidentRunningCheck(ResidentRunningState.NotRunning, null));
            }
        };
        await using var coordinator = Create(settings, sessions, clock, runtime, "LUID-A");
        coordinator.Start();
        await clock.AdvanceUntilAsync(automaticCheckStarted.Task);

        var manual = coordinator.LaunchEnabledNowAsync(CancellationToken.None);
        await runtime.FirstStart.Task;
        releaseAutomaticCheck.TrySetResult(
            new ResidentRunningCheck(ResidentRunningState.NotRunning, null));
        var pacingDelay = clock.WaitForDelayAsync(TimeSpan.FromSeconds(1));
        var boundary = await Task.WhenAny(coordinator.Completion, pacingDelay);

        Assert.Same(pacingDelay, boundary);
        Assert.Single(runtime.StartAttemptTimes);
        await clock.AdvanceAsync(TimeSpan.FromSeconds(1));
        await Task.WhenAll(coordinator.Completion, manual);

        Assert.Equal(2, runtime.StartAttemptTimes.Count);
        Assert.True(runtime.StartAttemptTimes[1] - runtime.StartAttemptTimes[0] >= TimeSpan.FromSeconds(1));
    }

    private static ResidentLaunchCoordinator Create(
        ISettingsStore settings,
        IResidentLaunchSessionStore sessions,
        IClock clock,
        IResidentProcessRuntime runtime,
        string luid,
        IResidentExecutablePolicy? policy = null,
        IDiagnosticLog? diagnosticLog = null) =>
        new(
            settings,
            sessions,
            new FixedLogonSessionIdentityProvider(luid),
            runtime,
            policy ?? new AllowingPolicy(),
            clock,
            diagnosticLog ?? new RecordingDiagnosticLog());

    private static ButlerSettings Settings(params ResidentApplication[] applications) =>
        ButlerSettings.Default with { ResidentApplications = applications };

    private static ResidentApplication App(string name, string path, bool enabled, int order) =>
        new(
            path,
            new HashSet<string>([path], StringComparer.OrdinalIgnoreCase),
            name,
            enabled,
            order);

    private sealed class FixedLogonSessionIdentityProvider(string luid) : ILogonSessionIdentityProvider
    {
        public string GetCurrent() => luid;
    }

    private sealed class MutableSettingsStore(ButlerSettings current) : ISettingsStore
    {
        internal ButlerSettings Current { get; set; } = current;

        internal int LoadCount { get; private set; }

        public Task<ButlerSettings> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            return Task.FromResult(Current);
        }

        public Task SaveAsync(ButlerSettings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingLatestSettingsStore(ButlerSettings current) : ISettingsStore
    {
        private readonly TaskCompletionSource releaseFirstLoad =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int loadCount;

        internal ButlerSettings Current { get; set; } = current;

        internal TaskCompletionSource FirstLoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void ReleaseFirstLoad() => releaseFirstLoad.TrySetResult();

        public async Task<ButlerSettings> LoadAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref loadCount) == 1)
            {
                FirstLoadStarted.TrySetResult();
                await releaseFirstLoad.Task.WaitAsync(cancellationToken);
            }

            return Current;
        }

        public Task SaveAsync(ButlerSettings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSessionStore : IResidentLaunchSessionStore
    {
        internal ResidentLaunchSession? Current { get; set; }

        internal int SaveCount { get; private set; }

        internal int LoadCount { get; private set; }

        internal TaskCompletionSource FirstSave { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Action<ResidentLaunchSession>? AfterFirstSave { get; set; }

        internal Exception? LoadFailure { get; set; }

        internal ResidentLaunchRecoveryResult RecoveryResult { get; set; } =
            ResidentLaunchRecoveryResult.RecoveredWithEmptyPlan;

        internal int RecoveryCount { get; private set; }

        public Task<ResidentLaunchSession?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (LoadFailure is not null)
            {
                return Task.FromException<ResidentLaunchSession?>(LoadFailure);
            }

            LoadCount++;
            return Task.FromResult(Current);
        }

        public Task SaveAsync(ResidentLaunchSession session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Current = session with { Plan = session.Plan.ToArray() };
            SaveCount++;
            FirstSave.TrySetResult();
            if (SaveCount == 1)
            {
                AfterFirstSave?.Invoke(Current);
            }

            return Task.CompletedTask;
        }

        public Task<ResidentLaunchRecoveryResult> RecoverCorruptAsync(
            string currentLogonSessionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecoveryCount++;
            return Task.FromResult(RecoveryResult);
        }
    }

    private sealed class RecordingRuntime : IResidentProcessRuntime
    {
        internal List<string> StartedPaths { get; } = [];

        internal List<string> CompletedStartPaths { get; } = [];

        internal List<IReadOnlySet<string>> CheckedKnownPaths { get; } = [];

        internal List<DateTimeOffset> StartAttemptTimes { get; } = [];

        internal Func<string, Task>? StartBehavior { get; set; }

        internal Func<IReadOnlySet<string>, Task<ResidentRunningCheck>>? CheckBehavior { get; set; }

        internal Func<DateTimeOffset>? Now { get; set; }

        internal TaskCompletionSource FirstStart { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ResidentRunningCheck> CheckRunningAsync(
            IReadOnlySet<string> knownProcessPaths,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = new HashSet<string>(knownProcessPaths, StringComparer.OrdinalIgnoreCase);
            CheckedKnownPaths.Add(snapshot);
            return CheckBehavior?.Invoke(snapshot) ??
                   Task.FromResult(new ResidentRunningCheck(ResidentRunningState.NotRunning, null));
        }

        public async Task StartAsync(string executablePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartedPaths.Add(executablePath);
            StartAttemptTimes.Add(Now?.Invoke() ?? InitialTime);
            FirstStart.TrySetResult();
            await (StartBehavior?.Invoke(executablePath) ?? Task.CompletedTask);
            CompletedStartPaths.Add(executablePath);
        }
    }

    private sealed class AllowingPolicy : IResidentExecutablePolicy
    {
        public ResidentExecutableValidation Validate(string path) =>
            new(true, Path.GetFullPath(path), ResidentExecutableRejection.None);
    }

    private sealed class ConfigurablePolicy(
        Func<string, ResidentExecutableValidation>? behavior = null) : IResidentExecutablePolicy
    {
        internal List<string> ValidatedPaths { get; } = [];

        public ResidentExecutableValidation Validate(string path)
        {
            ValidatedPaths.Add(path);
            return behavior?.Invoke(path) ??
                   new ResidentExecutableValidation(
                       true,
                       Path.GetFullPath(path),
                       ResidentExecutableRejection.None);
        }
    }

    private sealed class RecordingDiagnosticLog : IDiagnosticLog
    {
        internal List<DiagnosticEvent> Events { get; } = [];

        public Task WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken)
        {
            Events.Add(diagnosticEvent);
            return Task.CompletedTask;
        }
    }

    /// <summary>只由测试显式推进的假时钟；任何等待都不消耗真实时间。</summary>
    private sealed class ManualClock(DateTimeOffset initial) : IClock
    {
        private readonly object syncRoot = new();
        private readonly List<DelayWaiter> waiters = [];
        private readonly List<DelayObserver> observers = [];
        private DateTimeOffset utcNow = initial;
        private int canceledDelayCount;

        public DateTimeOffset UtcNow
        {
            get
            {
                lock (syncRoot)
                {
                    return utcNow;
                }
            }
        }

        internal List<TimeSpan> RequestedDelays { get; } = [];

        internal int CanceledDelayCount => Volatile.Read(ref canceledDelayCount);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (syncRoot)
            {
                RequestedDelays.Add(delay);
                var waiter = new DelayWaiter(utcNow + delay, delay, completion);
                waiter.Registration = cancellationToken.Register(
                    () =>
                    {
                        if (completion.TrySetCanceled(cancellationToken))
                        {
                            Interlocked.Increment(ref canceledDelayCount);
                        }
                    });
                waiters.Add(waiter);
                foreach (var observer in observers.Where(observer => observer.Delay is null || observer.Delay == delay).ToArray())
                {
                    observer.Completion.TrySetResult();
                    observers.Remove(observer);
                }
            }

            return AwaitAndDisposeAsync(completion.Task, completion, waiters);
        }

        internal Task WaitForDelayAsync(TimeSpan delay)
        {
            lock (syncRoot)
            {
                if (waiters.Any(waiter => waiter.Delay == delay && !waiter.Completion.Task.IsCompleted))
                {
                    return Task.CompletedTask;
                }

                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                observers.Add(new DelayObserver(delay, completion));
                return completion.Task;
            }
        }

        internal async Task AdvanceAsync(TimeSpan by)
        {
            DelayWaiter[] ready;
            lock (syncRoot)
            {
                utcNow += by;
                ready = waiters.Where(waiter => waiter.Due <= utcNow).ToArray();
            }

            foreach (var waiter in ready)
            {
                waiter.Completion.TrySetResult();
            }

            await Task.Yield();
        }

        /// <summary>反复推进已登记的最近等待，直到目标任务结束；不使用真实计时器。</summary>
        internal async Task AdvanceUntilCompletedAsync(Task completion)
        {
            for (var iteration = 0; iteration < 100 && !completion.IsCompleted; iteration++)
            {
                DelayWaiter? next;
                Task? waitForRegistration = null;
                lock (syncRoot)
                {
                    next = waiters
                        .Where(waiter => !waiter.Completion.Task.IsCompleted)
                        .OrderBy(waiter => waiter.Due)
                        .FirstOrDefault();
                    if (next is null)
                    {
                        var registered = new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        observers.Add(new DelayObserver(null, registered));
                        waitForRegistration = registered.Task;
                    }
                }

                if (next is null)
                {
                    await Task.WhenAny(completion, waitForRegistration!);
                    continue;
                }

                await AdvanceAsync(next.Due - UtcNow);
            }

            await completion;
        }

        /// <summary>推进所有已登记等待，直到目标观察边界完成，但不要求批次本身结束。</summary>
        internal async Task AdvanceUntilAsync(Task target)
        {
            for (var iteration = 0; iteration < 100 && !target.IsCompleted; iteration++)
            {
                DelayWaiter? next;
                Task? waitForRegistration = null;
                lock (syncRoot)
                {
                    next = waiters
                        .Where(waiter => !waiter.Completion.Task.IsCompleted)
                        .OrderBy(waiter => waiter.Due)
                        .FirstOrDefault();
                    if (next is null)
                    {
                        var registered = new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        observers.Add(new DelayObserver(null, registered));
                        waitForRegistration = registered.Task;
                    }
                }

                if (next is null)
                {
                    await Task.WhenAny(target, waitForRegistration!);
                    continue;
                }

                await AdvanceAsync(next.Due - UtcNow);
            }

            await target;
        }

        private async Task AwaitAndDisposeAsync(
            Task task,
            TaskCompletionSource completion,
            List<DelayWaiter> owner)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            finally
            {
                lock (syncRoot)
                {
                    var waiter = owner.Single(item => ReferenceEquals(item.Completion, completion));
                    waiter.Registration.Dispose();
                    owner.Remove(waiter);
                }
            }
        }

        private sealed record DelayObserver(TimeSpan? Delay, TaskCompletionSource Completion);

        private sealed record DelayWaiter(
            DateTimeOffset Due,
            TimeSpan Delay,
            TaskCompletionSource Completion)
        {
            internal CancellationTokenRegistration Registration { get; set; }
        }
    }
}
