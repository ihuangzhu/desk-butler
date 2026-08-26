using DeskButler.Core.Capture;
using DeskButler.Core.Persistence;
using DeskButler.Core.Scenes;
using DeskButler.Core.Settings;
using DeskButler.Modules.WorkspaceRecovery.Capture;

namespace DeskButler.Modules.WorkspaceRecovery.Tests.Capture;

public sealed class CaptureCoordinatorTests
{
    /// <summary>验证关闭捕获时不会读取窗口清单，也不会写入快照。</summary>
    [Fact]
    public async Task CaptureDisabledSkipsInventoryAndSave()
    {
        var clock = new FakeClock();
        var repository = new InMemorySceneRepository();
        var inventory = new DelegateWindowInventory(
            _ => throw new InvalidOperationException("disabled capture must not inspect windows"));
        var settings = ButlerSettings.Default with { CaptureEnabled = false };
        var coordinator = new CaptureCoordinator(
            settings,
            inventory,
            new SceneFilter(settings),
            repository,
            clock);

        await coordinator.SaveNowAsync("manual", CancellationToken.None);

        Assert.Empty(repository.Snapshots);
    }

    /// <summary>验证手动观察在捕获暂停时仍返回普通窗口路径，但绝不写入快照。</summary>
    [Fact]
    public async Task ManualObservedCaptureDisabledReturnsPathsWithoutSaving()
    {
        var repository = new InMemorySceneRepository();
        var settings = ButlerSettings.Default;
        var coordinator = new CaptureCoordinator(
            settings,
            new DelegateWindowInventory(
                _ => throw new InvalidOperationException("manual observed capture must not enumerate again")),
            new SceneFilter(settings),
            repository,
            new FakeClock());

        var outcome = await coordinator.SaveObservedAsync(
            "manual",
            [CandidateFactory.Normal(@"C:\Apps\.\EDITOR.exe", "Draft")],
            saveEnabled: false,
            CancellationToken.None);

        Assert.False(outcome.SnapshotSaved);
        Assert.Equal(CaptureSkipReason.Disabled, outcome.SkipReason);
        Assert.Equal(@"C:\Apps\EDITOR.exe", Assert.Single(outcome.WindowExecutablePaths));
        Assert.Empty(repository.Snapshots);
    }

    /// <summary>验证启用的手动观察没有平台候选时返回 NoCandidates。</summary>
    [Fact]
    public async Task ManualObservedCaptureWithoutCandidatesReturnsNoCandidates()
    {
        var repository = new InMemorySceneRepository();
        var coordinator = CreateCoordinator([], repository);

        var outcome = await coordinator.SaveObservedAsync(
            "manual", [], saveEnabled: true, CancellationToken.None);

        Assert.False(outcome.SnapshotSaved);
        Assert.Equal(CaptureSkipReason.NoCandidates, outcome.SkipReason);
        Assert.Empty(outcome.WindowExecutablePaths);
        Assert.Empty(repository.Snapshots);
    }

    /// <summary>验证手动观察候选全部被安全规则过滤时返回 NoItems。</summary>
    [Fact]
    public async Task ManualObservedCaptureWithoutSafeItemsReturnsNoItems()
    {
        var repository = new InMemorySceneRepository();
        var coordinator = CreateCoordinator([], repository);

        var outcome = await coordinator.SaveObservedAsync(
            "manual",
            [CandidateFactory.Normal() with { IsTemporaryWindow = true }],
            saveEnabled: true,
            CancellationToken.None);

        Assert.False(outcome.SnapshotSaved);
        Assert.Equal(CaptureSkipReason.NoItems, outcome.SkipReason);
        Assert.Empty(outcome.WindowExecutablePaths);
        Assert.Empty(repository.Snapshots);
    }

    /// <summary>验证手动观察与最新现场相同时返回 Unchanged 且保留普通窗口路径。</summary>
    [Fact]
    public async Task ManualObservedCaptureMatchingLatestReturnsUnchanged()
    {
        var candidate = CandidateFactory.Normal(@"C:\Apps\editor.exe", "Draft");
        var repository = new InMemorySceneRepository();
        var coordinator = CreateCoordinator([candidate], repository);
        await coordinator.SaveNowAsync("initial", CancellationToken.None);

        var outcome = await coordinator.SaveObservedAsync(
            "manual", [candidate], saveEnabled: true, CancellationToken.None);

        Assert.False(outcome.SnapshotSaved);
        Assert.Equal(CaptureSkipReason.Unchanged, outcome.SkipReason);
        Assert.Equal(@"C:\Apps\editor.exe", Assert.Single(outcome.WindowExecutablePaths));
        Assert.Single(repository.Snapshots);
    }

    /// <summary>验证有效手动观察保存新快照并返回 None 和同批正规化路径。</summary>
    [Fact]
    public async Task ManualObservedCaptureSavesSnapshotAndReturnsPaths()
    {
        var repository = new InMemorySceneRepository();
        var coordinator = CreateCoordinator([], repository);

        var outcome = await coordinator.SaveObservedAsync(
            "manual",
            [CandidateFactory.Normal(@"C:\Apps\.\EDITOR.exe", "Draft")],
            saveEnabled: true,
            CancellationToken.None);

        Assert.True(outcome.SnapshotSaved);
        Assert.Equal(CaptureSkipReason.None, outcome.SkipReason);
        Assert.Equal(@"C:\Apps\EDITOR.exe", Assert.Single(outcome.WindowExecutablePaths));
        Assert.Equal("manual", Assert.Single(repository.Snapshots).CaptureReason);
    }

    /// <summary>验证平台返回空候选时跳过保存，避免瞬时捕获失败侵蚀历史。</summary>
    [Fact]
    public async Task EmptyInventorySkipsSave()
    {
        var repository = new InMemorySceneRepository();
        var coordinator = CreateCoordinator([], repository);

        await coordinator.SaveNowAsync("quiet-debounce", CancellationToken.None);

        Assert.Empty(repository.Snapshots);
    }

    /// <summary>验证候选全部被安全规则过滤时跳过保存。</summary>
    [Fact]
    public async Task FullyFilteredInventorySkipsSave()
    {
        var repository = new InMemorySceneRepository();
        var coordinator = CreateCoordinator(
            [CandidateFactory.Normal() with { IsTemporaryWindow = true }],
            repository);

        await coordinator.SaveNowAsync("quiet-debounce", CancellationToken.None);

        Assert.Empty(repository.Snapshots);
    }

    /// <summary>验证有效候选映射为不含进程身份的场景条目，并保留调用方原因。</summary>
    [Fact]
    public async Task ValidCandidateSavesMappedScene()
    {
        var clock = new FakeClock();
        var repository = new InMemorySceneRepository();
        var candidate = CandidateFactory.Normal(@"C:\Apps\editor.exe", "Draft") with
        {
            ExplorerPath = @"C:\Work",
            WasElevatedOrInaccessible = true
        };
        var coordinator = CreateCoordinator([candidate], repository, clock);

        await coordinator.SaveNowAsync("module-stop", CancellationToken.None);

        var snapshot = Assert.Single(repository.Snapshots);
        Assert.Equal(clock.Start, snapshot.CapturedAt);
        Assert.Equal("module-stop", snapshot.CaptureReason);
        Assert.Equal(1, snapshot.FormatVersion);
        var item = Assert.Single(snapshot.Items);
        Assert.Equal(@"C:\Apps\editor.exe", item.ExecutablePath);
        Assert.Equal("ExampleWindowClass", item.WindowClass);
        Assert.Equal("Draft", item.TitleHint);
        Assert.Equal(@"C:\Work", item.ExplorerPath);
        Assert.Equal(candidate.Bounds, item.Bounds);
        Assert.Equal(candidate.State, item.State);
        Assert.Equal(candidate.Monitor, item.Monitor);
        Assert.True(item.WasElevated);
        Assert.DoesNotContain(candidate.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), item.Id, StringComparison.Ordinal);
        Assert.DoesNotContain(candidate.Handle.ToString(System.Globalization.CultureInfo.InvariantCulture), item.Id, StringComparison.Ordinal);
    }

    /// <summary>验证窗口内容未变化时忽略快照元数据与原生枚举顺序，不重复写入。</summary>
    [Fact]
    public async Task IdenticalNormalizedSceneSkipsDuplicateSave()
    {
        var first = CandidateFactory.Normal(@"C:\Apps\a.exe", "A");
        var second = CandidateFactory.Normal(@"C:\Apps\b.exe", "B") with { Handle = (nint)99, ProcessId = 999 };
        var repository = new InMemorySceneRepository();
        var firstCoordinator = CreateCoordinator([first, second], repository);
        var secondCoordinator = CreateCoordinator([second, first], repository);

        await firstCoordinator.SaveNowAsync("quiet-debounce", CancellationToken.None);
        await secondCoordinator.SaveNowAsync("continuous-checkpoint", CancellationToken.None);

        var snapshot = Assert.Single(repository.Snapshots);
        Assert.Equal("quiet-debounce", snapshot.CaptureReason);
    }

    /// <summary>验证并发保存串行覆盖完整捕获事务，不会重叠读取或写入。</summary>
    [Fact]
    public async Task ConcurrentSaveNowCallsAreSerialized()
    {
        var firstCaptureEntered = new TaskCompletionSource();
        var firstCaptureRelease = new TaskCompletionSource();
        var activeCaptures = 0;
        var maximumActiveCaptures = 0;
        var captureCount = 0;
        var inventory = new DelegateWindowInventory(
            async cancellationToken =>
            {
                var active = Interlocked.Increment(ref activeCaptures);
                maximumActiveCaptures = Math.Max(maximumActiveCaptures, active);
                var current = Interlocked.Increment(ref captureCount);
                if (current == 1)
                {
                    firstCaptureEntered.SetResult();
                    await firstCaptureRelease.Task.WaitAsync(cancellationToken);
                }

                Interlocked.Decrement(ref activeCaptures);
                return [CandidateFactory.Normal() with { Title = $"Scene {current}" }];
            });
        var settings = ButlerSettings.Default;
        var repository = new InMemorySceneRepository();
        var coordinator = new CaptureCoordinator(
            settings,
            inventory,
            new SceneFilter(settings),
            repository,
            new FakeClock());

        var firstSave = coordinator.SaveNowAsync("first", CancellationToken.None);
        await firstCaptureEntered.Task;
        var secondSave = coordinator.SaveNowAsync("second", CancellationToken.None);
        await FakeClock.DrainAsync();
        Assert.Equal(1, captureCount);

        firstCaptureRelease.SetResult();
        await Task.WhenAll(firstSave, secondSave);

        Assert.Equal(2, repository.Snapshots.Count);
        Assert.Equal(1, maximumActiveCaptures);
    }

    /// <summary>验证排队保存被取消后不会在前一保存完成时迟到执行。</summary>
    [Fact]
    public async Task CanceledQueuedSaveDoesNotRunLater()
    {
        var firstCaptureEntered = new TaskCompletionSource();
        var firstCaptureRelease = new TaskCompletionSource();
        var captureCount = 0;
        var inventory = new DelegateWindowInventory(
            async cancellationToken =>
            {
                var current = Interlocked.Increment(ref captureCount);
                if (current == 1)
                {
                    firstCaptureEntered.SetResult();
                    await firstCaptureRelease.Task.WaitAsync(cancellationToken);
                }

                return [CandidateFactory.Normal()];
            });
        var settings = ButlerSettings.Default;
        var repository = new InMemorySceneRepository();
        var coordinator = new CaptureCoordinator(
            settings,
            inventory,
            new SceneFilter(settings),
            repository,
            new FakeClock());

        var firstSave = coordinator.SaveNowAsync("first", CancellationToken.None);
        await firstCaptureEntered.Task;
        using var cancellationSource = new CancellationTokenSource();
        var canceledSave = coordinator.SaveNowAsync("second", cancellationSource.Token);
        cancellationSource.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledSave);

        firstCaptureRelease.SetResult();
        await firstSave;

        Assert.Equal(1, captureCount);
        Assert.Single(repository.Snapshots);
    }

    /// <summary>验证基础身份忽略瞬时进程、句柄、位置和状态，并正规化 Windows 路径写法。</summary>
    [Fact]
    public async Task StableIdentityIgnoresEphemeralWindowData()
    {
        var firstRepository = new InMemorySceneRepository();
        var secondRepository = new InMemorySceneRepository();
        var first = CandidateFactory.Normal(@"C:\Apps\.\EDITOR.exe", "Draft");
        var second = first with
        {
            Handle = (nint)999,
            ProcessId = 4321,
            ExecutablePath = @"c:\apps\editor.exe",
            Bounds = new WindowBounds(500, 300, 1200, 900),
            State = SceneWindowState.Maximized
        };

        await CreateCoordinator([first], firstRepository).SaveNowAsync("first", CancellationToken.None);
        await CreateCoordinator([second], secondRepository).SaveNowAsync("second", CancellationToken.None);

        Assert.Equal(
            Assert.Single(firstRepository.Snapshots).Items.Single().Id,
            Assert.Single(secondRepository.Snapshots).Items.Single().Id);
    }

    /// <summary>验证重复基础身份的序号基于稳定字段排序，不依赖平台枚举顺序。</summary>
    [Fact]
    public async Task DuplicateIdentityOrdinalIsIndependentOfEnumerationOrder()
    {
        var left = CandidateFactory.Normal(@"C:\Apps\editor.exe", "Draft") with
        {
            Bounds = new WindowBounds(10, 10, 800, 600)
        };
        var right = left with
        {
            Handle = (nint)500,
            ProcessId = 500,
            Bounds = new WindowBounds(900, 10, 800, 600)
        };
        var firstRepository = new InMemorySceneRepository();
        var secondRepository = new InMemorySceneRepository();

        await CreateCoordinator([left, right], firstRepository).SaveNowAsync("first", CancellationToken.None);
        await CreateCoordinator([right, left], secondRepository).SaveNowAsync("second", CancellationToken.None);

        var firstItems = Assert.Single(firstRepository.Snapshots).Items.ToDictionary(item => item.Bounds);
        var secondItems = Assert.Single(secondRepository.Snapshots).Items.ToDictionary(item => item.Bounds);
        Assert.Equal(firstItems[left.Bounds].Id, secondItems[left.Bounds].Id);
        Assert.Equal(firstItems[right.Bounds].Id, secondItems[right.Bounds].Id);
        Assert.NotEqual(firstItems[left.Bounds].Id, firstItems[right.Bounds].Id);
    }

    /// <summary>验证 Windows 路径仅大小写、冗余段或尾分隔符不同不会重复保存同一场景。</summary>
    [Fact]
    public async Task CanonicallyEquivalentWindowsPathsSkipDuplicateSave()
    {
        var first = CandidateFactory.Normal(@"C:\Apps\EDITOR.exe", "Draft") with
        {
            ExplorerPath = @"C:\Work\"
        };
        var second = first with
        {
            ExecutablePath = @"c:\apps\.\editor.exe",
            ExplorerPath = @"c:\work"
        };
        var repository = new InMemorySceneRepository();

        await CreateCoordinator([first], repository).SaveNowAsync("first", TestContext.Current.CancellationToken);
        await CreateCoordinator([second], repository).SaveNowAsync("second", TestContext.Current.CancellationToken);

        var snapshot = Assert.Single(repository.Snapshots);
        Assert.Equal("first", snapshot.CaptureReason);
    }

    /// <summary>验证单项畸形 executable 或 Explorer 路径被隔离，其他有效窗口仍可保存。</summary>
    [Fact]
    public async Task MalformedCandidatePathsAreSkippedWithoutLosingValidWindows()
    {
        var malformedExecutable = CandidateFactory.Normal("C:\\bad\0app.exe", "Bad executable");
        var malformedExplorer = CandidateFactory.Normal(@"C:\Apps\explorer.exe", "Bad explorer") with
        {
            ExplorerPath = "C:\\bad\0folder"
        };
        var valid = CandidateFactory.Normal(@"C:\Apps\editor.exe", "Valid");
        var repository = new InMemorySceneRepository();

        await CreateCoordinator(
            [malformedExecutable, malformedExplorer, valid],
            repository).SaveNowAsync("mixed", TestContext.Current.CancellationToken);

        var snapshot = Assert.Single(repository.Snapshots);
        var item = Assert.Single(snapshot.Items);
        Assert.Equal("Valid", item.TitleHint);
    }

    /// <summary>验证仅大小写不同的 monitor 名也有 Ordinal tie-break，重复身份序号不依赖枚举顺序。</summary>
    [Fact]
    public async Task DuplicateIdentityMonitorCaseUsesStrictTotalOrder()
    {
        var lowerCaseMonitor = CandidateFactory.Normal(@"C:\Apps\editor.exe", "Draft") with
        {
            Monitor = new MonitorIdentity("display1", new WindowBounds(0, 0, 1920, 1080), 96, 96)
        };
        var upperCaseMonitor = lowerCaseMonitor with
        {
            Handle = (nint)500,
            ProcessId = 500,
            Monitor = lowerCaseMonitor.Monitor with { DeviceName = "DISPLAY1" }
        };
        var firstRepository = new InMemorySceneRepository();
        var secondRepository = new InMemorySceneRepository();

        await CreateCoordinator(
            [lowerCaseMonitor, upperCaseMonitor],
            firstRepository).SaveNowAsync("first", TestContext.Current.CancellationToken);
        await CreateCoordinator(
            [upperCaseMonitor, lowerCaseMonitor],
            secondRepository).SaveNowAsync("second", TestContext.Current.CancellationToken);

        var firstItems = Assert.Single(firstRepository.Snapshots).Items
            .ToDictionary(item => item.Monitor.DeviceName, StringComparer.Ordinal);
        var secondItems = Assert.Single(secondRepository.Snapshots).Items
            .ToDictionary(item => item.Monitor.DeviceName, StringComparer.Ordinal);
        Assert.Equal(firstItems["display1"].Id, secondItems["display1"].Id);
        Assert.Equal(firstItems["DISPLAY1"].Id, secondItems["DISPLAY1"].Id);
    }

    /// <summary>创建使用默认启用设置和固定候选的协调器。</summary>
    /// <param name="candidates">捕获时返回的候选项。</param>
    /// <param name="repository">内存快照仓库。</param>
    /// <param name="clock">可选虚拟时钟。</param>
    /// <returns>可直接执行保存的协调器。</returns>
    private static CaptureCoordinator CreateCoordinator(
        IReadOnlyList<WindowCandidate> candidates,
        InMemorySceneRepository repository,
        FakeClock? clock = null)
    {
        var settings = ButlerSettings.Default;
        return new CaptureCoordinator(
            settings,
            new DelegateWindowInventory(_ => Task.FromResult(candidates)),
            new SceneFilter(settings),
            repository,
            clock ?? new FakeClock());
    }

    /// <summary>通过委托提供可控窗口候选的测试清单。</summary>
    private sealed class DelegateWindowInventory : IWindowInventory
    {
        private readonly Func<CancellationToken, Task<IReadOnlyList<WindowCandidate>>> captureAsync;

        /// <summary>创建使用给定捕获委托的测试清单。</summary>
        internal DelegateWindowInventory(Func<CancellationToken, Task<IReadOnlyList<WindowCandidate>>> captureAsync)
        {
            this.captureAsync = captureAsync;
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<WindowCandidate>> CaptureAsync(CancellationToken cancellationToken) =>
            captureAsync(cancellationToken);
    }

    /// <summary>保留完整保存结果并实现最新优先读取的内存仓库。</summary>
    private sealed class InMemorySceneRepository : ISceneRepository
    {
        internal List<SceneSnapshot> Snapshots { get; } = [];

        /// <inheritdoc />
        public Task SaveAsync(SceneSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshots.Add(snapshot);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<SceneSnapshot>> GetRecentAsync(int maximumCount, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<SceneSnapshot> recent = Snapshots
                .OrderByDescending(snapshot => snapshot.CapturedAt)
                .ThenByDescending(snapshot => snapshot.Id)
                .Take(maximumCount)
                .ToArray();
            return Task.FromResult(recent);
        }

        /// <inheritdoc />
        public Task MarkInvalidAsync(Guid snapshotId, string reason, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
