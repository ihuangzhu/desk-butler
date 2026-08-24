using DeskButler.Core.Capture;
using DeskButler.Core.Restore;
using DeskButler.Core.Scenes;
using DeskButler.Desktop.Hosting;
using DeskButler.Modules.WorkspaceRecovery.Restore;
using DeskButler.Desktop.Tests.ViewModels;
using DeskButler.Core.Diagnostics;

namespace DeskButler.Desktop.Tests.Hosting;

public sealed class RestoreSceneCommandHandlerTests
{
    /// <summary>永久排除必须在统一恢复边界生效，旧快照也不能重新启动该程序。</summary>
    [Fact]
    public async Task PermanentlyExcludedExecutableIsRemovedBeforePlanning()
    {
        var scene = SceneFactory.Create(
            "00000000-0000-0000-0000-000000000021",
            new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero),
            @"C:\Apps\Editor.exe");
        var settings = DeskButler.Core.Settings.ButlerSettings.Default with
        {
            ExcludedExecutablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                @"C:\Apps\Editor.exe"
            }
        };
        var inventory = new EmptyInventory();
        var executor = new RestoreExecutor(
            new FailingLauncher(), inventory, new FailingPositioner(), new FakeClock());
        var handler = new RestoreSceneCommandHandler(
            inventory, new RestorePlanner(_ => true), executor, new InMemorySettingsStore(settings));

        var result = await handler.HandleAsync(
            new RestoreSceneCommand(scene, [scene.Items[0].Id], SafeMode: false),
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Items);
    }

    /// <summary>生产恢复边界必须加载既有失败历史参与规划，并把本次真实结果写回同一存储。</summary>
    [Fact]
    public async Task FailureHistoryIsLoadedForPlanningAndResultIsRecorded()
    {
        var scene = SceneFactory.Create(
            "00000000-0000-0000-0000-000000000022",
            new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero),
            @"C:\Apps\Editor.exe");
        var history = new RecordingHistoryStore(new FailureHistory(
            new Dictionary<string, int> { [scene.Items[0].Id] = 3 }));
        var inventory = new EmptyInventory();
        var handler = new RestoreSceneCommandHandler(
            inventory,
            new RestorePlanner(_ => true),
            new RestoreExecutor(new FailingLauncher(), inventory, new FailingPositioner(), new FakeClock()),
            new InMemorySettingsStore(DeskButler.Core.Settings.ButlerSettings.Default),
            history);

        var result = await handler.HandleAsync(
            new RestoreSceneCommand(scene, [scene.Items[0].Id], SafeMode: false),
            TestContext.Current.CancellationToken);

        Assert.Equal(RestoreItemStatus.Skipped, Assert.Single(result.Items).Status);
        Assert.Same(result, history.Recorded);
    }

    /// <summary>原用户令牌在执行结束时已取消，完成结果仍必须用独立令牌写入历史并返回。</summary>
    [Fact]
    public async Task CompletedResultIsRecordedAfterOriginalTokenWasCancelled()
    {
        var scene = SceneFactory.Create(
            "00000000-0000-0000-0000-000000000023",
            new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero),
            @"C:\Apps\Editor.exe");
        using var cancellation = new CancellationTokenSource();
        var history = new CancellingHistoryStore(cancellation);
        var inventory = new EmptyInventory();
        var handler = new RestoreSceneCommandHandler(
            inventory, new RestorePlanner(_ => true),
            new RestoreExecutor(new FailingLauncher(), inventory, new FailingPositioner(), new FakeClock()),
            new InMemorySettingsStore(DeskButler.Core.Settings.ButlerSettings.Default), history);

        var result = await handler.HandleAsync(
            new RestoreSceneCommand(scene, [scene.Items[0].Id], SafeMode: false), cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(RestoreItemStatus.Cancelled, Assert.Single(result.Items).Status);
        Assert.Same(result, history.Recorded);
        Assert.False(history.RecordTokenWasCancelled);
    }

    /// <summary>历史落库失败必须写诊断并仍返回已经完成的恢复结果。</summary>
    [Fact]
    public async Task HistoryPersistenceFailureIsLoggedWithoutLosingRestoreResult()
    {
        var scene = SceneFactory.Create(
            "00000000-0000-0000-0000-000000000024",
            new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero),
            @"C:\Apps\Editor.exe");
        var inventory = new EmptyInventory();
        var history = new FailingRecordHistoryStore(scene.Items[0].Id);
        var log = new RecordingDiagnosticLog();
        var handler = new RestoreSceneCommandHandler(
            inventory, new RestorePlanner(_ => true),
            new RestoreExecutor(new FailingLauncher(), inventory, new FailingPositioner(), new FakeClock()),
            new InMemorySettingsStore(DeskButler.Core.Settings.ButlerSettings.Default), history, log);

        var result = await handler.HandleAsync(
            new RestoreSceneCommand(scene, [scene.Items[0].Id], SafeMode: false),
            TestContext.Current.CancellationToken);

        Assert.Equal(RestoreItemStatus.Skipped, Assert.Single(result.Items).Status);
        Assert.Equal("failure-history", Assert.Single(log.Events).Category);
    }

    /// <summary>历史存储和诊断日志都忽略取消且永不结束时，恢复结果仍受真实等待上限保护。</summary>
    [Fact]
    public async Task HangingHistoryAndDiagnosticTasksCannotDelayRestoreResultIndefinitely()
    {
        var scene = SceneFactory.Create(
            "00000000-0000-0000-0000-000000000025",
            new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero),
            @"C:\Apps\Editor.exe");
        var inventory = new EmptyInventory();
        var history = new HangingHistoryStore(scene.Items[0].Id);
        var log = new HangingDiagnosticLog();
        var handler = new RestoreSceneCommandHandler(
            inventory, new RestorePlanner(_ => true),
            new RestoreExecutor(new FailingLauncher(), inventory, new FailingPositioner(), new FakeClock()),
            new InMemorySettingsStore(DeskButler.Core.Settings.ButlerSettings.Default), history, log,
            TimeSpan.FromMilliseconds(40));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await handler.HandleAsync(
            new RestoreSceneCommand(scene, [scene.Items[0].Id], SafeMode: false),
            TestContext.Current.CancellationToken);

        Assert.Equal(RestoreItemStatus.Skipped, Assert.Single(result.Items).Status);
        Assert.True(history.RecordCalled);
        Assert.True(log.WriteCalled);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    /// <summary>接口在返回 Task 前同步阻塞时，线程池调度边界仍必须按上限返回恢复结果。</summary>
    [Fact]
    public async Task SynchronouslyBlockingPersistenceCallsAreBounded()
    {
        var scene = SceneFactory.Create(
            "00000000-0000-0000-0000-000000000026", DateTimeOffset.UtcNow, @"C:\Apps\Editor.exe");
        using var release = new ManualResetEventSlim();
        var inventory = new EmptyInventory();
        var history = new BlockingHistoryStore(scene.Items[0].Id, release);
        var log = new BlockingDiagnosticLog(release);
        var handler = new RestoreSceneCommandHandler(
            inventory, new RestorePlanner(_ => true),
            new RestoreExecutor(new FailingLauncher(), inventory, new FailingPositioner(), new FakeClock()),
            new InMemorySettingsStore(DeskButler.Core.Settings.ButlerSettings.Default), history, log,
            TimeSpan.FromMilliseconds(50));
        try
        {
            var result = await handler.HandleAsync(
                new RestoreSceneCommand(scene, [scene.Items[0].Id], false), TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            Assert.Equal(RestoreItemStatus.Skipped, Assert.Single(result.Items).Status);
        }
        finally
        {
            release.Set();
        }
    }

    private sealed class EmptyInventory : IWindowInventory
    {
        /// <inheritdoc />
        public Task<IReadOnlyList<WindowCandidate>> CaptureAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WindowCandidate>>([]);
    }

    private sealed class FailingLauncher : IAppLauncher
    {
        /// <inheritdoc />
        public Task LaunchAsync(SceneItem sceneItem, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("永久排除项不应到达启动器。");
    }

    private sealed class FailingPositioner : IWindowPositioner
    {
        /// <inheritdoc />
        public Task PositionAsync(nint windowHandle, SceneItem sceneItem, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("永久排除项不应到达定位器。");
    }

    private sealed class RecordingHistoryStore(FailureHistory current) : IFailureHistoryStore
    {
        internal RestoreResult? Recorded { get; private set; }

        /// <inheritdoc />
        public Task<FailureHistory> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(current);

        /// <inheritdoc />
        public Task RecordAsync(RestoreResult result, CancellationToken cancellationToken)
        {
            Recorded = result;
            return Task.CompletedTask;
        }
    }

    private sealed class CancellingHistoryStore(CancellationTokenSource source) : IFailureHistoryStore
    {
        internal RestoreResult? Recorded { get; private set; }

        internal bool RecordTokenWasCancelled { get; private set; }

        /// <inheritdoc />
        public Task<FailureHistory> LoadAsync(CancellationToken cancellationToken)
        {
            source.Cancel();
            return Task.FromResult(FailureHistory.Empty);
        }

        /// <inheritdoc />
        public Task RecordAsync(RestoreResult result, CancellationToken cancellationToken)
        {
            Recorded = result;
            RecordTokenWasCancelled = cancellationToken.IsCancellationRequested;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingRecordHistoryStore(string itemId) : IFailureHistoryStore
    {
        /// <inheritdoc />
        public Task<FailureHistory> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new FailureHistory(new Dictionary<string, int> { [itemId] = 3 }));

        /// <inheritdoc />
        public Task RecordAsync(RestoreResult result, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("history failed"));
    }

    private sealed class RecordingDiagnosticLog : IDiagnosticLog
    {
        internal List<DiagnosticEvent> Events { get; } = [];

        /// <inheritdoc />
        public Task WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken)
        {
            Events.Add(diagnosticEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class HangingHistoryStore(string itemId) : IFailureHistoryStore
    {
        internal bool RecordCalled { get; private set; }

        /// <inheritdoc />
        public Task<FailureHistory> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new FailureHistory(new Dictionary<string, int> { [itemId] = 3 }));

        /// <inheritdoc />
        public Task RecordAsync(RestoreResult result, CancellationToken cancellationToken)
        {
            RecordCalled = true;
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }
    }

    private sealed class HangingDiagnosticLog : IDiagnosticLog
    {
        internal bool WriteCalled { get; private set; }

        /// <inheritdoc />
        public Task WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken)
        {
            WriteCalled = true;
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }
    }

    private sealed class BlockingHistoryStore(string itemId, ManualResetEventSlim release) : IFailureHistoryStore
    {
        /// <inheritdoc />
        public Task<FailureHistory> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new FailureHistory(new Dictionary<string, int> { [itemId] = 3 }));

        /// <inheritdoc />
        public Task RecordAsync(RestoreResult result, CancellationToken cancellationToken)
        {
            release.Wait(CancellationToken.None);
            throw new IOException("late history failure");
        }
    }

    private sealed class BlockingDiagnosticLog(ManualResetEventSlim release) : IDiagnosticLog
    {
        /// <inheritdoc />
        public Task WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken)
        {
            release.Wait(CancellationToken.None);
            throw new IOException("late log failure");
        }
    }
}
