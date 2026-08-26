using DeskButler.Application.Commands;
using DeskButler.Core.Capture;
using DeskButler.Core.Diagnostics;
using DeskButler.Core.Persistence;
using DeskButler.Core.ResidentApps;
using DeskButler.Core.Scenes;
using DeskButler.Core.Settings;
using DeskButler.Desktop.Hosting;
using DeskButler.Desktop.Tests.ViewModels;
using DeskButler.Modules.WorkspaceRecovery.Capture;

namespace DeskButler.Desktop.Tests.Hosting;

public sealed class ResidentCandidateCoordinatorTests
{
    /// <summary>较早开始但迟到的发现结果不得覆盖较新的已发布候选代次。</summary>
    [Fact]
    public async Task LatestDiscoveryWinsWhenOlderCompletesLate()
    {
        var discovery = new FirstCallBlockingDiscovery(
            Candidate("old", "Old", @"C:\Apps\old.exe"),
            Candidate("new", "New", @"C:\Apps\new.exe"));
        var store = new InMemorySettingsStore(ButlerSettings.Default);
        using var settings = new SettingsCoordinator(store);
        var coordinator = new ResidentCandidateCoordinator(discovery, store, settings);

        var oldTask = coordinator.DiscoverAsync(
            new HashSet<string>([@"C:\Apps\ordinary-old.exe"], StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);
        await discovery.FirstStarted.Task;
        var latest = await coordinator.DiscoverAsync(
            new HashSet<string>([@"C:\Apps\ordinary-new.exe"], StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);
        discovery.ReleaseFirst();
        await oldTask;

        Assert.Equal(latest.Generation, coordinator.Current.Generation);
        Assert.Equal("New", Assert.Single(coordinator.Current.Candidates).DisplayName);
    }

    /// <summary>旧代次忽略无副作用，当前忽略只清内存且下一轮仍可发现同一候选。</summary>
    [Fact]
    public async Task DismissOnlyClearsCurrentGenerationWithoutPersistingBlacklist()
    {
        var store = new InMemorySettingsStore(ButlerSettings.Default);
        using var settings = new SettingsCoordinator(store);
        var coordinator = new ResidentCandidateCoordinator(
            new StaticDiscovery(Candidate("same", "Same", @"C:\Apps\same.exe")),
            store,
            settings);
        var first = await coordinator.DiscoverAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);

        Assert.False(coordinator.Dismiss(first.Generation - 1));
        Assert.Single(coordinator.Current.Candidates);
        Assert.True(coordinator.Dismiss(first.Generation));
        Assert.Empty(coordinator.Current.Candidates);
        Assert.Empty(store.Current.ResidentApplications);

        var second = await coordinator.DiscoverAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);

        Assert.True(second.Generation > first.Generation);
        Assert.Equal("Same", Assert.Single(second.Candidates).DisplayName);
    }

    /// <summary>有效确认只信任 UI 选择字段，并从当前候选复制其余持久化属性。</summary>
    [Fact]
    public async Task ConfirmAddsNormalizedApplicationFromCurrentCandidate()
    {
        var candidate = Candidate("chat", "Trusted Chat", @"C:\Apps\Chat\chat.exe") with
        {
            KnownProcessPaths = new HashSet<string>(
                [@"C:\Apps\Chat\chat.exe", @"C:\Apps\Chat\helper.exe"],
                StringComparer.OrdinalIgnoreCase)
        };
        var store = new InMemorySettingsStore(
            ButlerSettings.Default with { CaptureEnabled = false, StartupEnabled = false });
        using var settings = new SettingsCoordinator(store);
        var coordinator = new ResidentCandidateCoordinator(new StaticDiscovery(candidate), store, settings);
        var batch = await coordinator.DiscoverAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);

        var confirmed = await coordinator.ConfirmAsync(
            batch.Generation,
            [new ResidentCandidateSelection("chat", @"C:\Chosen\.\chat-main.exe", true)],
            CancellationToken.None);

        Assert.True(confirmed);
        var application = Assert.Single(store.Current.ResidentApplications);
        Assert.Equal(@"C:\Chosen\chat-main.exe", application.LaunchPath);
        Assert.Equal("Trusted Chat", application.DisplayName);
        Assert.True(application.Enabled);
        Assert.Equal(0, application.LaunchOrder);
        Assert.Contains(@"C:\Apps\Chat\helper.exe", application.KnownProcessPaths);
        Assert.Contains(@"C:\Chosen\chat-main.exe", application.KnownProcessPaths);
        Assert.False(store.Current.CaptureEnabled);
        Assert.False(store.Current.StartupEnabled);
        Assert.Empty(coordinator.Current.Candidates);
    }

    /// <summary>路径替换只针对候选声明的当前旧入口，并保留原条目启用状态和顺序。</summary>
    [Fact]
    public async Task ConfirmReplacesMatchingCurrentLaunchPath()
    {
        var existing = new ResidentApplication(
            @"C:\Apps\Chat\old.exe",
            new HashSet<string>([@"C:\Apps\Chat\old.exe"], StringComparer.OrdinalIgnoreCase),
            "Old Chat",
            false,
            0);
        var candidate = Candidate("replacement", "Trusted Chat", @"C:\Apps\Chat\new.exe") with
        {
            KnownProcessPaths = new HashSet<string>(
                [@"C:\Apps\Chat\new.exe", @"C:\Apps\Chat\helper.exe"],
                StringComparer.OrdinalIgnoreCase),
            Kind = ResidentCandidateKind.PathReplacement,
            ReplacesLaunchPath = @"C:\Apps\Chat\old.exe"
        };
        var store = new InMemorySettingsStore(
            ButlerSettings.Default with { ResidentApplications = [existing] });
        using var settings = new SettingsCoordinator(store);
        var coordinator = new ResidentCandidateCoordinator(new StaticDiscovery(candidate), store, settings);
        var batch = await coordinator.DiscoverAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);

        var confirmed = await coordinator.ConfirmAsync(
            batch.Generation,
            [new ResidentCandidateSelection("replacement", @"C:\Apps\Chat\.\new.exe", true)],
            CancellationToken.None);

        Assert.True(confirmed);
        var application = Assert.Single(store.Current.ResidentApplications);
        Assert.Equal(@"C:\Apps\Chat\new.exe", application.LaunchPath);
        Assert.Equal("Trusted Chat", application.DisplayName);
        Assert.False(application.Enabled);
        Assert.Equal(0, application.LaunchOrder);
        Assert.Contains(@"C:\Apps\Chat\helper.exe", application.KnownProcessPaths);
    }

    /// <summary>平台级发现异常降级为失败批次并替换旧候选，而不是向手动保存调用方传播。</summary>
    [Fact]
    public async Task DiscoverySystemFailurePublishesFailedEmptyGeneration()
    {
        var failure = new IOException("system enumeration failed");
        var store = new InMemorySettingsStore(ButlerSettings.Default);
        using var settings = new SettingsCoordinator(store);
        var discovery = new SequenceDiscovery(
            new ResidentDiscoveryResult([Candidate("old", "Old", @"C:\Apps\old.exe")], []),
            failure);
        var coordinator = new ResidentCandidateCoordinator(discovery, store, settings);
        await coordinator.DiscoverAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);

        var failed = await coordinator.DiscoverAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);

        Assert.True(failed.DiscoveryFailed);
        Assert.Empty(failed.Candidates);
        Assert.Equal(failed, coordinator.Current);
    }

    /// <summary>捕获暂停时手动命令仍复用同一批窗口路径发现一次，且绝不保存快照。</summary>
    [Fact]
    public async Task ManualSaveDiscoversOnceWhenCaptureIsDisabled()
    {
        var settingsValue = ButlerSettings.Default with { CaptureEnabled = false };
        var settingsStore = new InMemorySettingsStore(settingsValue);
        var rawInventory = new StaticWindowInventory(
            ManualCandidate(@"C:\Apps\.\ordinary.exe"));
        var manualInventory = new SettingsAwareWindowInventory(rawInventory, settingsStore);
        var repository = new InMemorySceneRepository();
        using var capture = new CaptureCoordinator(
            ButlerSettings.Default,
            manualInventory,
            new SceneFilter(ButlerSettings.Default),
            repository,
            new FakeClock());
        var discovery = new RecordingDiscovery();
        using var settings = new SettingsCoordinator(settingsStore);
        var residents = new ResidentCandidateCoordinator(discovery, settingsStore, settings);
        var handler = new SaveSceneNowCommandHandler(manualInventory, capture, residents);

        var result = await handler.HandleAsync(
            new SaveSceneNowCommand(), CancellationToken.None);

        Assert.Equal(CaptureSkipReason.Disabled, result.Capture.SkipReason);
        Assert.False(result.Capture.SnapshotSaved);
        Assert.Equal(1, discovery.CallCount);
        Assert.Equal(@"C:\Apps\ordinary.exe", Assert.Single(discovery.LastOrdinaryWindowPaths!));
        Assert.Empty(await repository.GetRecentAsync(1, CancellationToken.None));
    }

    /// <summary>可恢复的手动观察失败写脱敏诊断，并以 Failed 和空路径继续发现一次。</summary>
    [Fact]
    public async Task ManualSaveRecoversObservationFailureAndStillDiscoversOnce()
    {
        var settingsStore = new InMemorySettingsStore(ButlerSettings.Default);
        var manualInventory = new SettingsAwareWindowInventory(
            new ThrowingWindowInventory(new IOException("private path must not be logged")),
            settingsStore);
        using var capture = new CaptureCoordinator(
            ButlerSettings.Default,
            manualInventory,
            new SceneFilter(ButlerSettings.Default),
            new InMemorySceneRepository(),
            new FakeClock());
        var discovery = new RecordingDiscovery();
        using var settings = new SettingsCoordinator(settingsStore);
        var residents = new ResidentCandidateCoordinator(discovery, settingsStore, settings);
        var log = new RecordingDiagnosticLog();
        var handler = new SaveSceneNowCommandHandler(
            manualInventory, capture, residents, log, new FakeClock());

        var result = await handler.HandleAsync(
            new SaveSceneNowCommand(), CancellationToken.None);

        Assert.Equal(CaptureSkipReason.Failed, result.Capture.SkipReason);
        Assert.Empty(result.Capture.WindowExecutablePaths);
        Assert.Equal(1, discovery.CallCount);
        Assert.Empty(discovery.LastOrdinaryWindowPaths!);
        var diagnostic = Assert.Single(log.Events);
        Assert.Equal("manual-capture", diagnostic.Category);
        Assert.DoesNotContain("private path", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>设置保存失败返回 false 并保留同代候选，重试成功后才清空。</summary>
    [Fact]
    public async Task ConfirmSaveFailureRetainsSameGenerationForRetry()
    {
        var store = new FailNextSaveSettingsStore(ButlerSettings.Default);
        using var settings = new SettingsCoordinator(store);
        var coordinator = new ResidentCandidateCoordinator(
            new StaticDiscovery(Candidate("retry", "Retry", @"C:\Apps\retry.exe")),
            store,
            settings);
        var batch = await coordinator.DiscoverAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);
        var selections = new[]
        {
            new ResidentCandidateSelection("retry", @"C:\Apps\retry.exe", true)
        };
        store.FailNextSave();

        var first = await coordinator.ConfirmAsync(
            batch.Generation, selections, CancellationToken.None);

        Assert.False(first);
        Assert.Single(coordinator.Current.Candidates);
        Assert.Empty(store.Current.ResidentApplications);

        var retried = await coordinator.ConfirmAsync(
            batch.Generation, selections, CancellationToken.None);

        Assert.True(retried);
        Assert.Empty(coordinator.Current.Candidates);
        Assert.Single(store.Current.ResidentApplications);
    }

    /// <summary>确认尚未取得设置门时发布的新代次必须使旧确认在线性化点 no-op。</summary>
    [Fact]
    public async Task DiscoveryBeforeConfirmGetsSettingsGateRejectsOldConfirmation()
    {
        var store = new BlockingNextSaveSettingsStore(ButlerSettings.Default);
        using var settings = new SettingsCoordinator(store);
        var discovery = new SequenceDiscovery(
            new ResidentDiscoveryResult([Candidate("old", "Old", @"C:\Apps\old.exe")], []),
            new ResidentDiscoveryResult([Candidate("new", "New", @"C:\Apps\new.exe")], []));
        var coordinator = new ResidentCandidateCoordinator(discovery, store, settings);
        var old = await coordinator.DiscoverAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);
        store.BlockNextSave();
        var gateHolder = settings.UpdateAsync(
            current => current with { CaptureEnabled = false }, CancellationToken.None);
        await store.BlockedSaveStarted.Task;

        var confirmation = coordinator.ConfirmAsync(
            old.Generation,
            [new ResidentCandidateSelection("old", @"C:\Apps\old.exe", true)],
            CancellationToken.None);
        var latest = await coordinator.DiscoverAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);
        store.ReleaseBlockedSave();
        await gateHolder;
        var confirmed = await confirmation;

        Assert.False(confirmed);
        Assert.Empty(store.Current.ResidentApplications);
        Assert.Equal(latest.Generation, coordinator.Current.Generation);
        Assert.Equal("New", Assert.Single(coordinator.Current.Candidates).DisplayName);
    }

    /// <summary>update 回调线性化后发布的新代次不撤销提交，但设置保存完成后也不能被旧确认清空。</summary>
    [Fact]
    public async Task DiscoveryAfterConfirmationLinearizesSurvivesOldSaveCompletion()
    {
        var store = new BlockingNextSaveSettingsStore(ButlerSettings.Default);
        using var settings = new SettingsCoordinator(store);
        var discovery = new SequenceDiscovery(
            new ResidentDiscoveryResult([Candidate("old", "Old", @"C:\Apps\old.exe")], []),
            new ResidentDiscoveryResult([Candidate("new", "New", @"C:\Apps\new.exe")], []));
        var coordinator = new ResidentCandidateCoordinator(discovery, store, settings);
        var old = await coordinator.DiscoverAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);
        store.BlockNextSave();

        var confirmation = coordinator.ConfirmAsync(
            old.Generation,
            [new ResidentCandidateSelection("old", @"C:\Apps\old.exe", true)],
            CancellationToken.None);
        await store.BlockedSaveStarted.Task;
        var latest = await coordinator.DiscoverAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);
        store.ReleaseBlockedSave();
        var confirmed = await confirmation;

        Assert.True(confirmed);
        Assert.Equal("Old", Assert.Single(store.Current.ResidentApplications).DisplayName);
        Assert.Equal(latest.Generation, coordinator.Current.Generation);
        Assert.Equal("New", Assert.Single(coordinator.Current.Candidates).DisplayName);
    }

    /// <summary>旧 generation 与未知 CandidateId 均不得写设置或清除当前候选。</summary>
    [Fact]
    public async Task StaleGenerationAndUnknownCandidateConfirmationAreNoOp()
    {
        var store = new InMemorySettingsStore(ButlerSettings.Default);
        using var settings = new SettingsCoordinator(store);
        var coordinator = new ResidentCandidateCoordinator(
            new StaticDiscovery(Candidate("current", "Current", @"C:\Apps\current.exe")),
            store,
            settings);
        var batch = await coordinator.DiscoverAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);

        var stale = await coordinator.ConfirmAsync(
            batch.Generation - 1,
            [new ResidentCandidateSelection("current", @"C:\Apps\current.exe", true)],
            CancellationToken.None);
        var unknown = await coordinator.ConfirmAsync(
            batch.Generation,
            [new ResidentCandidateSelection("unknown", @"C:\Apps\spoofed.exe", true)],
            CancellationToken.None);

        Assert.False(stale);
        Assert.False(unknown);
        Assert.Empty(store.Current.ResidentApplications);
        Assert.Equal("Current", Assert.Single(coordinator.Current.Candidates).DisplayName);
    }

    /// <summary>替换候选的旧入口已被并发修改时，旧路径修正必须 no-op 并保留候选。</summary>
    [Fact]
    public async Task ReplacementTargetChangedBeforeLinearizationIsNoOp()
    {
        var oldApplication = new ResidentApplication(
            @"C:\Apps\old.exe",
            new HashSet<string>([@"C:\Apps\old.exe"], StringComparer.OrdinalIgnoreCase),
            "Old",
            true,
            0);
        var replacement = Candidate("replace", "Replacement", @"C:\Apps\new.exe") with
        {
            Kind = ResidentCandidateKind.PathReplacement,
            ReplacesLaunchPath = @"C:\Apps\old.exe"
        };
        var store = new InMemorySettingsStore(
            ButlerSettings.Default with { ResidentApplications = [oldApplication] });
        using var settings = new SettingsCoordinator(store);
        var coordinator = new ResidentCandidateCoordinator(new StaticDiscovery(replacement), store, settings);
        var batch = await coordinator.DiscoverAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);
        await settings.UpdateAsync(
            current => current with
            {
                ResidentApplications =
                [
                    oldApplication with
                    {
                        LaunchPath = @"C:\Apps\already-updated.exe",
                        KnownProcessPaths = new HashSet<string>(
                            [@"C:\Apps\already-updated.exe"], StringComparer.OrdinalIgnoreCase)
                    }
                ]
            },
            CancellationToken.None);

        var confirmed = await coordinator.ConfirmAsync(
            batch.Generation,
            [new ResidentCandidateSelection("replace", @"C:\Apps\new.exe", true)],
            CancellationToken.None);

        Assert.False(confirmed);
        Assert.Equal(@"C:\Apps\already-updated.exe", Assert.Single(store.Current.ResidentApplications).LaunchPath);
        Assert.Single(coordinator.Current.Candidates);
    }

    /// <summary>NoCandidates、NoItems 与 Unchanged 均继续且仅继续一次候选发现。</summary>
    [Fact]
    public async Task ManualSaveDiscoversOnceForEveryNormalSkipReason()
    {
        await AssertManualSkipStillDiscoversOnceAsync(CaptureSkipReason.NoCandidates, []);
        await AssertManualSkipStillDiscoversOnceAsync(
            CaptureSkipReason.NoItems,
            [ManualCandidate(@"C:\Apps\temporary.exe") with { IsTemporaryWindow = true }]);
        await AssertManualSkipStillDiscoversOnceAsync(
            CaptureSkipReason.Unchanged,
            [ManualCandidate(@"C:\Apps\unchanged.exe")],
            seedSnapshot: true);
    }

    /// <summary>发现系统失败不得回滚或改报已经成功保存的普通窗口现场。</summary>
    [Fact]
    public async Task DiscoveryFailurePreservesSuccessfulManualCapture()
    {
        var settingsStore = new InMemorySettingsStore(ButlerSettings.Default);
        var manualInventory = new SettingsAwareWindowInventory(
            new StaticWindowInventory(ManualCandidate(@"C:\Apps\ordinary.exe")),
            settingsStore);
        var repository = new InMemorySceneRepository();
        using var capture = new CaptureCoordinator(
            ButlerSettings.Default,
            manualInventory,
            new SceneFilter(ButlerSettings.Default),
            repository,
            new FakeClock());
        using var settings = new SettingsCoordinator(settingsStore);
        var residents = new ResidentCandidateCoordinator(
            new SequenceDiscovery(new IOException("resident discovery failed")),
            settingsStore,
            settings);
        var log = new RecordingDiagnosticLog();
        var handler = new SaveSceneNowCommandHandler(
            manualInventory, capture, residents, log, new FakeClock());

        var result = await handler.HandleAsync(new SaveSceneNowCommand(), CancellationToken.None);

        Assert.True(result.Capture.SnapshotSaved);
        Assert.Equal(CaptureSkipReason.None, result.Capture.SkipReason);
        Assert.True(result.Discovery.DiscoveryFailed);
        Assert.Single(await repository.GetRecentAsync(1, CancellationToken.None));
        Assert.Equal("resident-discovery", Assert.Single(log.Events).Category);
    }

    /// <summary>发现发布后外部修改候选集合不得篡改确认时可信的 KnownProcessPaths。</summary>
    [Fact]
    public async Task PublishedCandidateMetadataIsDetachedFromDiscoveryOwnedCollections()
    {
        var knownPaths = new HashSet<string>([@"C:\Apps\trusted.exe"], StringComparer.OrdinalIgnoreCase);
        var candidate = Candidate("trusted", "Trusted", @"C:\Apps\trusted.exe") with
        {
            KnownProcessPaths = knownPaths
        };
        var store = new InMemorySettingsStore(ButlerSettings.Default);
        using var settings = new SettingsCoordinator(store);
        var coordinator = new ResidentCandidateCoordinator(new StaticDiscovery(candidate), store, settings);
        var batch = await coordinator.DiscoverAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);
        knownPaths.Add(@"C:\Injected\untrusted.exe");

        var confirmed = await coordinator.ConfirmAsync(
            batch.Generation,
            [new ResidentCandidateSelection("trusted", @"C:\Apps\trusted.exe", true)],
            CancellationToken.None);

        Assert.True(confirmed);
        var application = Assert.Single(store.Current.ResidentApplications);
        Assert.DoesNotContain(@"C:\Injected\untrusted.exe", application.KnownProcessPaths);
    }

    /// <summary>窗口观察成功但快照保存失败时仍以 Failed 和空路径继续一次发现。</summary>
    [Fact]
    public async Task ManualSnapshotSaveFailureStillDiscoversOnce()
    {
        var settingsStore = new InMemorySettingsStore(ButlerSettings.Default);
        var manualInventory = new SettingsAwareWindowInventory(
            new StaticWindowInventory(ManualCandidate(@"C:\Apps\ordinary.exe")),
            settingsStore);
        using var capture = new CaptureCoordinator(
            ButlerSettings.Default,
            manualInventory,
            new SceneFilter(ButlerSettings.Default),
            new ThrowingSceneRepository(),
            new FakeClock());
        var discovery = new RecordingDiscovery();
        using var settings = new SettingsCoordinator(settingsStore);
        var residents = new ResidentCandidateCoordinator(discovery, settingsStore, settings);
        var log = new RecordingDiagnosticLog();
        var handler = new SaveSceneNowCommandHandler(
            manualInventory, capture, residents, log, new FakeClock());

        var result = await handler.HandleAsync(new SaveSceneNowCommand(), CancellationToken.None);

        Assert.Equal(CaptureSkipReason.Failed, result.Capture.SkipReason);
        Assert.Empty(result.Capture.WindowExecutablePaths);
        Assert.Equal(1, discovery.CallCount);
        Assert.Empty(discovery.LastOrdinaryWindowPaths!);
        Assert.Equal("manual-capture", Assert.Single(log.Events).Category);
    }

    /// <summary>查找、忽略和确认命令必须路由到同一个 latest-wins 候选协调器。</summary>
    [Fact]
    public async Task ResidentCandidateCommandsShareCoordinatorState()
    {
        var store = new InMemorySettingsStore(ButlerSettings.Default);
        using var settings = new SettingsCoordinator(store);
        var coordinator = new ResidentCandidateCoordinator(
            new StaticDiscovery(Candidate("shared", "Shared", @"C:\Apps\shared.exe")),
            store,
            settings);
        var bus = new InProcessCommandBus();
        bus.Register(new FindResidentCandidatesCommandHandler(coordinator));
        bus.Register(new ConfirmResidentCandidatesCommandHandler(coordinator));
        bus.Register(new DismissResidentCandidatesCommandHandler(coordinator));

        var dismissedBatch = await bus.SendAsync(
            new FindResidentCandidatesCommand(), CancellationToken.None);
        var dismissed = await bus.SendAsync(
            new DismissResidentCandidatesCommand(dismissedBatch.Generation), CancellationToken.None);
        var confirmedBatch = await bus.SendAsync(
            new FindResidentCandidatesCommand(), CancellationToken.None);
        var confirmed = await bus.SendAsync(
            new ConfirmResidentCandidatesCommand(
                confirmedBatch.Generation,
                [new ResidentCandidateSelection("shared", @"C:\Apps\shared.exe", true)]),
            CancellationToken.None);

        Assert.True(dismissed);
        Assert.True(confirmed);
        Assert.Single(store.Current.ResidentApplications);
        Assert.Empty(coordinator.Current.Candidates);
    }

    /// <summary>自动捕获直接使用既有入口，成功保存也绝不调用常驻发现器。</summary>
    [Fact]
    public async Task AutomaticCaptureNeverInvokesResidentDiscovery()
    {
        var settingsStore = new InMemorySettingsStore(ButlerSettings.Default);
        var inventory = new SettingsAwareWindowInventory(
            new StaticWindowInventory(ManualCandidate(@"C:\Apps\ordinary.exe")), settingsStore);
        var repository = new InMemorySceneRepository();
        using var capture = new CaptureCoordinator(
            ButlerSettings.Default,
            inventory,
            new SceneFilter(ButlerSettings.Default),
            repository,
            new FakeClock());
        var discovery = new RecordingDiscovery();

        await capture.SaveNowAsync("automatic", CancellationToken.None);

        Assert.Equal(0, discovery.CallCount);
        Assert.Single(await repository.GetRecentAsync(1, CancellationToken.None));
    }

    /// <summary>诊断日志自身的非用户取消故障不得阻断捕获失败后的常驻发现。</summary>
    [Fact]
    public async Task DiagnosticCancellationUnrelatedToRequestDoesNotBlockDiscovery()
    {
        var settingsStore = new InMemorySettingsStore(ButlerSettings.Default);
        var manualInventory = new SettingsAwareWindowInventory(
            new ThrowingWindowInventory(new IOException("capture failed")), settingsStore);
        using var capture = new CaptureCoordinator(
            ButlerSettings.Default,
            manualInventory,
            new SceneFilter(ButlerSettings.Default),
            new InMemorySceneRepository(),
            new FakeClock());
        var discovery = new RecordingDiscovery();
        using var settings = new SettingsCoordinator(settingsStore);
        var residents = new ResidentCandidateCoordinator(discovery, settingsStore, settings);
        var handler = new SaveSceneNowCommandHandler(
            manualInventory, capture, residents, new CancellingDiagnosticLog(), new FakeClock());

        var result = await handler.HandleAsync(new SaveSceneNowCommand(), CancellationToken.None);

        Assert.Equal(CaptureSkipReason.Failed, result.Capture.SkipReason);
        Assert.Equal(1, discovery.CallCount);
    }

    /// <summary>验证一个常规跳过结果仍携带同批路径执行且只执行一次发现。</summary>
    private static async Task AssertManualSkipStillDiscoversOnceAsync(
        CaptureSkipReason expectedReason,
        IReadOnlyList<WindowCandidate> candidates,
        bool seedSnapshot = false)
    {
        var settingsStore = new InMemorySettingsStore(ButlerSettings.Default);
        var manualInventory = new SettingsAwareWindowInventory(
            new StaticWindowInventory([.. candidates]), settingsStore);
        var repository = new InMemorySceneRepository();
        using var capture = new CaptureCoordinator(
            ButlerSettings.Default,
            manualInventory,
            new SceneFilter(ButlerSettings.Default),
            repository,
            new FakeClock());
        if (seedSnapshot)
        {
            var seeded = await capture.SaveObservedAsync(
                "seed", candidates, saveEnabled: true, CancellationToken.None);
            Assert.True(seeded.SnapshotSaved);
        }

        var discovery = new RecordingDiscovery();
        using var settings = new SettingsCoordinator(settingsStore);
        var residents = new ResidentCandidateCoordinator(discovery, settingsStore, settings);
        var handler = new SaveSceneNowCommandHandler(manualInventory, capture, residents);

        var result = await handler.HandleAsync(new SaveSceneNowCommand(), CancellationToken.None);

        Assert.Equal(expectedReason, result.Capture.SkipReason);
        Assert.Equal(1, discovery.CallCount);
    }

    /// <summary>构造具有手写稳定字段的发现候选。</summary>
    private static ResidentAppCandidate Candidate(string id, string name, string launchPath) => new(
        id,
        name,
        launchPath,
        new HashSet<string>([launchPath], StringComparer.OrdinalIgnoreCase),
        ResidentCandidateConfidence.High,
        ResidentCandidateKind.NewApplication,
        null);

    /// <summary>构造手动保存测试使用的普通窗口候选。</summary>
    private static WindowCandidate ManualCandidate(string executablePath) => new(
        (nint)1,
        10,
        executablePath,
        "WindowClass",
        "Window title",
        null,
        new WindowBounds(10, 10, 800, 600),
        SceneWindowState.Normal,
        new MonitorIdentity("DISPLAY1", new WindowBounds(0, 0, 1920, 1080), 96, 96),
        true,
        false,
        false,
        false,
        false);

    private sealed class FirstCallBlockingDiscovery(
        ResidentAppCandidate first,
        ResidentAppCandidate second) : IResidentAppDiscovery
    {
        private int calls;
        private readonly TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>释放第一次发现，使旧结果在新结果之后到达。</summary>
        internal void ReleaseFirst() => releaseFirst.TrySetResult();

        /// <inheritdoc />
        public async Task<ResidentDiscoveryResult> DiscoverAsync(
            IReadOnlySet<string> ordinaryWindowPaths,
            IReadOnlyList<ResidentApplication> existing,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                FirstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return new ResidentDiscoveryResult([first], []);
            }

            return new ResidentDiscoveryResult([second], []);
        }
    }

    private sealed class StaticDiscovery(params ResidentAppCandidate[] candidates) : IResidentAppDiscovery
    {
        /// <inheritdoc />
        public Task<ResidentDiscoveryResult> DiscoverAsync(
            IReadOnlySet<string> ordinaryWindowPaths,
            IReadOnlyList<ResidentApplication> existing,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ResidentDiscoveryResult(candidates, []));
    }

    private sealed class SequenceDiscovery(params object[] results) : IResidentAppDiscovery
    {
        private int index;

        /// <inheritdoc />
        public Task<ResidentDiscoveryResult> DiscoverAsync(
            IReadOnlySet<string> ordinaryWindowPaths,
            IReadOnlyList<ResidentApplication> existing,
            CancellationToken cancellationToken)
        {
            var result = results[Math.Min(index++, results.Length - 1)];
            return result is Exception exception
                ? Task.FromException<ResidentDiscoveryResult>(exception)
                : Task.FromResult((ResidentDiscoveryResult)result);
        }
    }

    private sealed class RecordingDiscovery : IResidentAppDiscovery
    {
        internal int CallCount { get; private set; }

        internal IReadOnlySet<string>? LastOrdinaryWindowPaths { get; private set; }

        /// <inheritdoc />
        public Task<ResidentDiscoveryResult> DiscoverAsync(
            IReadOnlySet<string> ordinaryWindowPaths,
            IReadOnlyList<ResidentApplication> existing,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastOrdinaryWindowPaths = new HashSet<string>(ordinaryWindowPaths, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new ResidentDiscoveryResult([], []));
        }
    }

    private sealed class StaticWindowInventory(params WindowCandidate[] candidates) : IWindowInventory
    {
        /// <inheritdoc />
        public Task<IReadOnlyList<WindowCandidate>> CaptureAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WindowCandidate>>(candidates);
    }

    private sealed class ThrowingWindowInventory(Exception failure) : IWindowInventory
    {
        /// <inheritdoc />
        public Task<IReadOnlyList<WindowCandidate>> CaptureAsync(CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<WindowCandidate>>(failure);
    }

    private sealed class ThrowingSceneRepository : ISceneRepository
    {
        /// <inheritdoc />
        public Task SaveAsync(SceneSnapshot snapshot, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("scene save failed"));

        /// <inheritdoc />
        public Task<IReadOnlyList<SceneSnapshot>> GetRecentAsync(
            int maximumCount,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SceneSnapshot>>([]);

        /// <inheritdoc />
        public Task MarkInvalidAsync(Guid snapshotId, string reason, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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

    private sealed class CancellingDiagnosticLog : IDiagnosticLog
    {
        /// <inheritdoc />
        public Task WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken) =>
            Task.FromException(new OperationCanceledException(CancellationToken.None));
    }

    private sealed class FailNextSaveSettingsStore(ButlerSettings initial) : ISettingsStore
    {
        private bool failNextSave;

        internal ButlerSettings Current { get; private set; } = initial;

        /// <summary>让下一次设置保存发生可恢复故障。</summary>
        internal void FailNextSave() => failNextSave = true;

        /// <inheritdoc />
        public Task<ButlerSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Current);

        /// <inheritdoc />
        public Task SaveAsync(ButlerSettings settings, CancellationToken cancellationToken)
        {
            if (failNextSave)
            {
                failNextSave = false;
                return Task.FromException(new IOException("settings save failed"));
            }

            Current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingNextSaveSettingsStore(ButlerSettings initial) : ISettingsStore
    {
        private readonly object sync = new();
        private TaskCompletionSource releaseBlockedSave =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool blockNextSave;
        private ButlerSettings current = initial;

        internal TaskCompletionSource BlockedSaveStarted { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ButlerSettings Current
        {
            get
            {
                lock (sync)
                {
                    return current;
                }
            }
        }

        /// <summary>让下一次 SaveAsync 在已收到目标设置后阻塞，供事务边界测试。</summary>
        internal void BlockNextSave()
        {
            lock (sync)
            {
                blockNextSave = true;
                BlockedSaveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                releaseBlockedSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        /// <summary>释放当前被阻塞的设置保存。</summary>
        internal void ReleaseBlockedSave() => releaseBlockedSave.TrySetResult();

        /// <inheritdoc />
        public Task<ButlerSettings> LoadAsync(CancellationToken cancellationToken)
        {
            lock (sync)
            {
                return Task.FromResult(current);
            }
        }

        /// <inheritdoc />
        public async Task SaveAsync(ButlerSettings settings, CancellationToken cancellationToken)
        {
            Task? release = null;
            lock (sync)
            {
                if (blockNextSave)
                {
                    blockNextSave = false;
                    release = releaseBlockedSave.Task;
                    BlockedSaveStarted.TrySetResult();
                }
            }

            if (release is not null)
            {
                await release.WaitAsync(cancellationToken);
            }

            lock (sync)
            {
                current = settings;
            }
        }
    }
}
