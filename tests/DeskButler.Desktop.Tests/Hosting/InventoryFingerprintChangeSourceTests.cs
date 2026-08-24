using DeskButler.Core.Capture;
using DeskButler.Core.Scenes;
using DeskButler.Desktop.Hosting;
using DeskButler.Desktop.Tests.ViewModels;
using DeskButler.Modules.WorkspaceRecovery;
using DeskButler.Modules.WorkspaceRecovery.Capture;
using DeskButler.Core.Diagnostics;

namespace DeskButler.Desktop.Tests.Hosting;

public sealed class InventoryFingerprintChangeSourceTests
{
    /// <summary>建立基线和静止轮询均不得伪造桌面变化。</summary>
    [Fact]
    public async Task InitialBaselineAndUnchangedInventoryEmitNothing()
    {
        var clock = new FakeClock();
        var inventory = new SequenceInventory([Candidate(1, "A")], [Candidate(1, "A")]);
        await using var source = new InventoryFingerprintChangeSource(inventory, clock, TimeSpan.FromSeconds(2));
        var changes = 0;
        source.DesktopChanged += (_, _) => changes++;

        await source.StartAsync(CancellationToken.None);
        await clock.AdvanceAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, changes);
    }

    /// <summary>首次真实指纹变化必须在一个轮询周期内发出一次信号，随后静止不重复。</summary>
    [Fact]
    public async Task FirstChangeEmitsOnceAndThenStaysQuiet()
    {
        var clock = new FakeClock();
        var inventory = new SequenceInventory([Candidate(1, "A")], [Candidate(1, "B")], [Candidate(1, "B")]);
        await using var source = new InventoryFingerprintChangeSource(inventory, clock, TimeSpan.FromSeconds(2));
        var changes = 0;
        source.DesktopChanged += (_, _) => changes++;
        await source.StartAsync(CancellationToken.None);

        await clock.AdvanceAsync(TimeSpan.FromSeconds(2));
        await clock.AdvanceAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, changes);
    }

    /// <summary>桌面持续变化时每次新指纹都要继续发信号，供 60 秒强制检查点观察。</summary>
    [Fact]
    public async Task ContinuousChangesContinueEmittingSignals()
    {
        var clock = new FakeClock();
        var inventory = new SequenceInventory(
            [Candidate(1, "A")], [Candidate(1, "B")], [Candidate(1, "C")], [Candidate(1, "D")]);
        await using var source = new InventoryFingerprintChangeSource(inventory, clock, TimeSpan.FromSeconds(2));
        var changes = 0;
        source.DesktopChanged += (_, _) => changes++;
        await source.StartAsync(CancellationToken.None);

        await clock.AdvanceAsync(TimeSpan.FromSeconds(2));
        await clock.AdvanceAsync(TimeSpan.FromSeconds(2));
        await clock.AdvanceAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(3, changes);
    }

    /// <summary>单次枚举失败不得清空基线、伪造变化或终止后续检测，释放也必须干净完成。</summary>
    [Fact]
    public async Task PollFailureIsObservableAndNextSuccessfulChangeStillEmits()
    {
        var clock = new FakeClock();
        var inventory = new FaultingSequenceInventory(
            new WindowCandidate[] { Candidate(1, "A") },
            new InvalidOperationException("枚举失败"),
            new WindowCandidate[] { Candidate(1, "B") });
        var failures = new List<Exception>();
        var source = new InventoryFingerprintChangeSource(
            inventory, clock, TimeSpan.FromSeconds(2), failures.Add);
        var changes = 0;
        source.DesktopChanged += (_, _) => changes++;
        await source.StartAsync(CancellationToken.None);

        await clock.AdvanceAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, changes);
        Assert.Contains(failures, failure => failure.Message.Contains("枚举失败", StringComparison.Ordinal));

        await clock.AdvanceAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, changes);
        await source.DisposeAsync();
    }

    /// <summary>故障订阅者不得阻断同一变化的其他订阅者，也不得终止后续轮询。</summary>
    [Fact]
    public async Task FaultingSubscriberIsReportedAndOtherSubscriberStillRuns()
    {
        var clock = new FakeClock();
        var inventory = new SequenceInventory([Candidate(1, "A")], [Candidate(1, "B")]);
        var failures = new List<Exception>();
        await using var source = new InventoryFingerprintChangeSource(
            inventory, clock, TimeSpan.FromSeconds(2), failures.Add);
        var healthyCalls = 0;
        source.DesktopChanged += (_, _) => throw new InvalidOperationException("订阅者失败");
        source.DesktopChanged += (_, _) => healthyCalls++;
        await source.StartAsync(CancellationToken.None);

        await clock.AdvanceAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, healthyCalls);
        Assert.Contains(failures, failure => failure.Message.Contains("订阅者失败", StringComparison.Ordinal));
    }

    /// <summary>后台写入的最近故障必须能被另一线程立即观察，不能依赖偶然缓存刷新。</summary>
    [Fact]
    public async Task LastFailureIsVisibleFromAnotherThread()
    {
        var clock = new FakeClock();
        var expected = new InvalidOperationException("跨线程故障");
        var inventory = new FaultingSequenceInventory(
            new WindowCandidate[] { Candidate(1, "A") }, expected);
        await using var source = new InventoryFingerprintChangeSource(inventory, clock, TimeSpan.FromSeconds(2));
        await source.StartAsync(CancellationToken.None);

        await clock.AdvanceAsync(TimeSpan.FromSeconds(2));
        var observed = await Task.Run(() => source.LastFailure, TestContext.Current.CancellationToken);

        Assert.Same(expected, observed);
    }

    /// <summary>诊断日志自身异常不得终止变化循环，后续真实变化仍应通知健康订阅者。</summary>
    [Fact]
    public async Task DiagnosticLogFailureDoesNotKillChangeLoop()
    {
        var clock = new FakeClock();
        var inventory = new FaultingSequenceInventory(
            new WindowCandidate[] { Candidate(1, "A") },
            new InvalidOperationException("枚举失败"),
            new WindowCandidate[] { Candidate(1, "B") });
        var log = new ThrowingDiagnosticLog();
        await using var source = new InventoryFingerprintChangeSource(
            inventory, clock, TimeSpan.FromSeconds(2), reportFailure: null, log);
        var changes = 0;
        source.DesktopChanged += (_, _) => changes++;
        await source.StartAsync(CancellationToken.None);

        await clock.AdvanceAsync(TimeSpan.FromSeconds(2));
        await log.Attempted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await clock.AdvanceAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, changes);
        Assert.IsType<AggregateException>(source.LastFailure);
    }

    /// <summary>暂停设置必须在捕获边界短路，自动调度无法取得任何可保存窗口。</summary>
    [Fact]
    public async Task PausedCaptureReturnsEmptyInventoryWithoutReadingDesktop()
    {
        var inner = new CountingInventory([Candidate(1, "A")]);
        var settings = DeskButler.Core.Settings.ButlerSettings.Default with { CaptureEnabled = false };
        var inventory = new SettingsAwareWindowInventory(
            inner, new InMemorySettingsStore(settings));

        var result = await inventory.CaptureAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result);
        Assert.Equal(0, inner.CaptureCount);
    }

    /// <summary>暂停后即使真实桌面继续变化并触发调度，也不得保存空快照或覆盖历史。</summary>
    [Fact]
    public async Task PausedProductionChainDoesNotSaveWhenRawFingerprintChanges()
    {
        var clock = new FakeClock();
        var raw = new SequenceInventory([Candidate(1, "A")], [Candidate(1, "B")], [Candidate(1, "B")]);
        var settingsStore = new InMemorySettingsStore(DeskButler.Core.Settings.ButlerSettings.Default);
        var repository = new InMemorySceneRepository();
        var captureSettings = DeskButler.Core.Settings.ButlerSettings.Default;
        var capture = new CaptureCoordinator(
            captureSettings,
            new SettingsAwareWindowInventory(raw, settingsStore),
            new SceneFilter(captureSettings), repository, clock);
        await using var scheduler = new SnapshotScheduler(clock, capture.SaveNowAsync);
        await using var source = new InventoryFingerprintChangeSource(raw, clock, TimeSpan.FromSeconds(2));
        var module = new WorkspaceRecoveryModule(source, scheduler, capture, clock);
        await module.StartAsync(CancellationToken.None);
        await source.StartAsync(CancellationToken.None);
        await settingsStore.SaveAsync(captureSettings with { CaptureEnabled = false }, CancellationToken.None);

        await clock.AdvanceAsync(TimeSpan.FromSeconds(2));
        await clock.AdvanceAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(await repository.GetRecentAsync(3, CancellationToken.None));
        await module.StopAsync(CancellationToken.None);
        capture.Dispose();
    }

    private static WindowCandidate Candidate(int handle, string title) => new(
        handle, 10, @"C:\Apps\Editor.exe", "Editor", title, null,
        new WindowBounds(10, 10, 800, 600), SceneWindowState.Normal,
        new MonitorIdentity("DISPLAY1", new WindowBounds(0, 0, 1920, 1080), 96, 96),
        true, false, false, false, false);

    private sealed class SequenceInventory(params IReadOnlyList<WindowCandidate>[] snapshots) : IWindowInventory
    {
        private int index;

        /// <inheritdoc />
        public Task<IReadOnlyList<WindowCandidate>> CaptureAsync(CancellationToken cancellationToken)
        {
            var current = snapshots[Math.Min(index, snapshots.Length - 1)];
            index++;
            return Task.FromResult(current);
        }
    }

    private sealed class CountingInventory(IReadOnlyList<WindowCandidate> snapshot) : IWindowInventory
    {
        internal int CaptureCount { get; private set; }

        /// <inheritdoc />
        public Task<IReadOnlyList<WindowCandidate>> CaptureAsync(CancellationToken cancellationToken)
        {
            CaptureCount++;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class FaultingSequenceInventory(params object[] results) : IWindowInventory
    {
        private int index;

        /// <inheritdoc />
        public Task<IReadOnlyList<WindowCandidate>> CaptureAsync(CancellationToken cancellationToken)
        {
            var result = results[Math.Min(index++, results.Length - 1)];
            return result is Exception exception
                ? Task.FromException<IReadOnlyList<WindowCandidate>>(exception)
                : Task.FromResult((IReadOnlyList<WindowCandidate>)result);
        }
    }

    private sealed class ThrowingDiagnosticLog : IDiagnosticLog
    {
        internal TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public Task WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken)
        {
            Attempted.TrySetResult();
            throw new IOException("日志失败");
        }
    }
}
