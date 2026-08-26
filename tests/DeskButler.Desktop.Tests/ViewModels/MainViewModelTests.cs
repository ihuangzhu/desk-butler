using DeskButler.Desktop.Hosting;
using DeskButler.Desktop.ViewModels;
using DeskButler.Application.Commands;
using DeskButler.Core.ResidentApps;
using DeskButler.Core.Restore;
using DeskButler.Core.Persistence;
using DeskButler.Core.Scenes;
using DeskButler.Core.Settings;
using DeskButler.Infrastructure.Windows.Startup;
using DeskButler.Modules.WorkspaceRecovery.Capture;
using System.Windows.Media;

namespace DeskButler.Desktop.Tests.ViewModels;

public sealed class MainViewModelTests
{
    /// <summary>手动保存必须准确投影捕获与发现的五类稳定结果文案。</summary>
    [Theory]
    [MemberData(nameof(ManualSaveCases))]
    public async Task SaveNowAsyncMapsManualCaptureAndDiscoveryResults(
        CaptureSkipReason reason,
        bool snapshotSaved,
        bool discoveryFailed,
        string expectedStatus)
    {
        var batch = new ResidentDiscoveryBatch(41, [], discoveryFailed);
        var commands = new ResidentCommandBus(new ManualSaveResult(
            new CaptureOutcome(snapshotSaved, reason, new HashSet<string>(StringComparer.OrdinalIgnoreCase)), batch));
        var vm = CreateResidentViewModel(commands);

        await vm.SaveNowAsync();

        Assert.Equal(expectedStatus, vm.StatusText);
    }

    /// <summary>手动保存发现当前代候选时才发出可被托盘消费的事件。</summary>
    [Fact]
    public async Task SaveNowAsyncPublishesCandidatesAndRaisesManualAvailabilityEvent()
    {
        var batch = new ResidentDiscoveryBatch(42, [CreateCandidate("candidate-manual")], false);
        var commands = new ResidentCommandBus(new ManualSaveResult(
            new CaptureOutcome(true, CaptureSkipReason.None, new HashSet<string>(StringComparer.OrdinalIgnoreCase)), batch));
        var vm = CreateResidentViewModel(commands);
        var events = 0;
        vm.ResidentCandidatesAvailable += (_, _) => events++;

        await vm.SaveNowAsync();

        Assert.Equal(1, events);
        Assert.Single(vm.ResidentCandidates);
        Assert.True(vm.HasResidentCandidates);
        Assert.True(vm.ConfirmResidentCandidatesCommand.CanExecute(null));
    }

    /// <summary>较旧的手动保存结果不能覆盖已经发布的更新发现代次或触发托盘事件。</summary>
    [Fact]
    public async Task SaveNowAsyncDoesNotPublishStaleManualCandidateGeneration()
    {
        var commands = new ResidentCommandBus(
            manualSaveResult: new ManualSaveResult(
                new CaptureOutcome(true, CaptureSkipReason.None, new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
                new ResidentDiscoveryBatch(47, [CreateCandidate("stale-manual")], false)),
            findResult: new ResidentDiscoveryBatch(48, [CreateCandidate("new-find")], false));
        var vm = CreateResidentViewModel(commands);
        var events = 0;
        vm.ResidentCandidatesAvailable += (_, _) => events++;
        await vm.FindResidentCandidatesAsync();

        await vm.SaveNowAsync();

        Assert.Equal("new-find", Assert.Single(vm.ResidentCandidates).CandidateId);
        Assert.Equal(0, events);
    }

    /// <summary>混合选择中只要有已选空入口，确认命令与方法都必须拒绝发送。</summary>
    [Fact]
    public async Task ConfirmResidentCandidatesRequiresEverySelectedCandidateToHavePath()
    {
        var valid = CreateCandidate("valid");
        var pathless = valid with { CandidateId = "pathless", LaunchPath = null };
        var commands = new ResidentCommandBus(
            findResult: new ResidentDiscoveryBatch(49, [valid, pathless], false),
            confirmResult: false);
        var vm = CreateResidentViewModel(commands);
        await vm.FindResidentCandidatesAsync();
        var emptyPath = vm.ResidentCandidates.Single(candidate => candidate.CandidateId == "pathless");
        emptyPath.IsSelected = true;

        Assert.False(vm.ConfirmResidentCandidatesCommand.CanExecute(null));
        await vm.ConfirmResidentCandidatesAsync();
        Assert.DoesNotContain(commands.SentCommands, command => command is ConfirmResidentCandidatesCommand);

        emptyPath.IsSelected = false;
        Assert.True(vm.ConfirmResidentCandidatesCommand.CanExecute(null));
        await vm.ConfirmResidentCandidatesAsync();

        var confirm = Assert.IsType<ConfirmResidentCandidatesCommand>(
            Assert.Single(commands.SentCommands, command => command is ConfirmResidentCandidatesCommand));
        var selection = Assert.Single(confirm.Selections);
        Assert.Equal("valid", selection.CandidateId);
        Assert.True(selection.IsSelected);
        Assert.False(string.IsNullOrWhiteSpace(selection.FinalLaunchPath));
    }

    /// <summary>现场仓库刷新失败不能阻止手动保存结果中的候选、文案和事件投影。</summary>
    [Fact]
    public async Task SaveNowAsyncPublishesResidentResultWhenSceneRefreshFails()
    {
        var commands = new ResidentCommandBus(new ManualSaveResult(
            new CaptureOutcome(true, CaptureSkipReason.None, new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            new ResidentDiscoveryBatch(50, [CreateCandidate("save-without-scenes")], false)));
        var repository = new ThrowingSceneRepository();
        var vm = CreateResidentViewModel(commands, sceneRepository: repository);
        var eventCount = 0;
        vm.ResidentCandidatesAvailable += (_, _) => eventCount++;

        await vm.SaveNowAsync();

        Assert.Single(vm.ResidentCandidates);
        Assert.Equal("现场已保存", vm.StatusText);
        Assert.Equal(1, eventCount);
        Assert.Equal(1, repository.GetRecentCallCount);
    }

    /// <summary>没有 SceneSaved 通知时，手动保存后仍须主动刷新最近现场列表。</summary>
    [Fact]
    public async Task SaveNowAsyncRefreshesRecentScenesWithoutRepositoryNotification()
    {
        var oldScene = SceneFactory.Create(
            "00000000-0000-0000-0000-000000000071", DateTimeOffset.UtcNow.AddMinutes(-1), @"C:\Apps\Old.exe");
        var savedScene = SceneFactory.Create(
            "00000000-0000-0000-0000-000000000072", DateTimeOffset.UtcNow, @"C:\Apps\Saved.exe");
        var repository = new InMemorySceneRepository(oldScene);
        var commands = new ResidentCommandBus(
            manualSaveResult: new ManualSaveResult(
                new CaptureOutcome(true, CaptureSkipReason.None, new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
                new ResidentDiscoveryBatch(53, [], false)),
            saveSideEffect: () => repository.SaveAsync(savedScene, CancellationToken.None));
        var vm = CreateResidentViewModel(commands, sceneRepository: repository);
        await vm.LoadAsync();

        await vm.SaveNowAsync();

        Assert.Equal(savedScene.Id, vm.RecentScenes[0].Scene.Id);
    }

    /// <summary>独立查找只发送查找命令，不能把它误接到保存现场的工作流或托盘事件。</summary>
    [Fact]
    public async Task FindResidentCandidatesAsyncDoesNotSaveSceneOrRaiseManualEvent()
    {
        var commands = new ResidentCommandBus(
            findResult: new ResidentDiscoveryBatch(43, [CreateCandidate("candidate-find")], false));
        var vm = CreateResidentViewModel(commands);
        var events = 0;
        vm.ResidentCandidatesAvailable += (_, _) => events++;

        await vm.FindResidentCandidatesAsync();

        Assert.DoesNotContain(commands.SentCommands, command => command is SaveSceneNowCommand);
        Assert.Contains(commands.SentCommands, command => command is FindResidentCandidatesCommand);
        Assert.Equal(0, events);
        Assert.Single(vm.ResidentCandidates);
    }

    /// <summary>旧代确认即使返回成功也不能清掉新代候选或重新加载设置。</summary>
    [Fact]
    public async Task ConfirmResidentCandidatesAsyncIgnoresExpiredGeneration()
    {
        var commands = new ResidentCommandBus(
            findResult: new ResidentDiscoveryBatch(44, [CreateCandidate("old")], false),
            confirmResult: true);
        var settings = new CountingSettingsStore(ButlerSettings.Default);
        var vm = CreateResidentViewModel(commands, settingsStore: settings);
        await vm.FindResidentCandidatesAsync();
        var oldCandidate = Assert.Single(vm.ResidentCandidates);
        oldCandidate.IsSelected = true;
        commands.FindResult = new ResidentDiscoveryBatch(45, [CreateCandidate("new")], false);
        await vm.FindResidentCandidatesAsync();

        await vm.ConfirmResidentCandidatesAsync(44, [oldCandidate.ToSelection()]);

        Assert.Equal("new", Assert.Single(vm.ResidentCandidates).CandidateId);
        Assert.Equal(0, settings.LoadCount);
    }

    /// <summary>同代确认成功清空候选并使用最新设置重新投影常驻列表。</summary>
    [Fact]
    public async Task ConfirmResidentCandidatesAsyncClearsSameGenerationAndReloadsSettings()
    {
        var application = new ResidentApplication(@"C:\Apps\Persisted.exe", new HashSet<string>(), "Persisted", true, 0);
        var commands = new ResidentCommandBus(
            findResult: new ResidentDiscoveryBatch(46, [CreateCandidate("same")], false),
            confirmResult: true);
        var settings = new CountingSettingsStore(ButlerSettings.Default with { ResidentApplications = [application] });
        var vm = CreateResidentViewModel(commands, settingsStore: settings);
        await vm.FindResidentCandidatesAsync();

        await vm.ConfirmResidentCandidatesAsync();

        Assert.Empty(vm.ResidentCandidates);
        Assert.Single(vm.ResidentApplications);
        Assert.Equal(1, settings.LoadCount);
    }

    /// <summary>确认成功仅重载 resident settings，不应访问场景仓库。</summary>
    [Fact]
    public async Task ConfirmResidentCandidatesAsyncReloadsSettingsWithoutReadingScenes()
    {
        var application = new ResidentApplication(@"C:\Apps\SettingsOnly.exe", new HashSet<string>(), "SettingsOnly", true, 0);
        var commands = new ResidentCommandBus(
            findResult: new ResidentDiscoveryBatch(51, [CreateCandidate("settings-only")], false),
            confirmResult: true);
        var settings = new CountingSettingsStore(ButlerSettings.Default with { ResidentApplications = [application] });
        var repository = new ThrowingSceneRepository();
        var vm = CreateResidentViewModel(commands, settingsStore: settings, sceneRepository: repository);
        await vm.FindResidentCandidatesAsync();

        await vm.ConfirmResidentCandidatesAsync();

        Assert.Empty(vm.ResidentCandidates);
        Assert.Single(vm.ResidentApplications);
        Assert.Equal(1, settings.LoadCount);
        Assert.Equal(0, repository.GetRecentCallCount);
    }

    /// <summary>确认后的 resident settings 重载失败时，当前代候选必须保留供用户重试。</summary>
    [Fact]
    public async Task ConfirmResidentCandidatesAsyncKeepsCandidatesWhenResidentSettingsReloadFails()
    {
        var commands = new ResidentCommandBus(
            findResult: new ResidentDiscoveryBatch(52, [CreateCandidate("reload-failure")], false),
            confirmResult: true);
        var vm = CreateResidentViewModel(commands, settingsStore: new ThrowingSettingsStore());
        await vm.FindResidentCandidatesAsync();

        await Assert.ThrowsAsync<IOException>(vm.ConfirmResidentCandidatesAsync);

        Assert.Equal("reload-failure", Assert.Single(vm.ResidentCandidates).CandidateId);
    }

    /// <summary>立即启动只委托给任务八的手动启动边界，不发送其他命令。</summary>
    [Fact]
    public async Task LaunchResidentsNowAsyncOnlyInvokesManualLaunchDelegate()
    {
        var commands = new ResidentCommandBus();
        var launchCalls = 0;
        var vm = CreateResidentViewModel(commands, launch: _ =>
        {
            launchCalls++;
            return Task.CompletedTask;
        });

        await vm.LaunchResidentsNowAsync();

        Assert.Equal(1, launchCalls);
        Assert.Empty(commands.SentCommands);
    }

    /// <summary>浏览取消时不得发送新增命令或修改常驻列表。</summary>
    [Fact]
    public async Task AddResidentApplicationAsyncCancellationDoesNotMutate()
    {
        var commands = new ResidentCommandBus();
        var vm = CreateResidentViewModel(commands, picker: new FakeExecutablePicker());

        await vm.AddResidentApplicationAsync();

        Assert.DoesNotContain(commands.SentCommands, command => command is AddResidentApplicationCommand);
        Assert.Empty(vm.ResidentApplications);
    }

    /// <summary>常驻总开关的 UI 交互必须经类型化命令返回权威快照，而不是通过属性 setter 直接持久化。</summary>
    [Fact]
    public async Task ToggleResidentApplicationsCommandAppliesTypedMutationSnapshot()
    {
        var commands = new ResidentCommandBus(
            mutationResult: new ResidentSettingsMutationResult(true, ResidentSettingsError.None, [], false));
        var vm = CreateResidentViewModel(
            commands,
            ButlerSettings.Default with { ResidentApplicationsEnabled = true });
        await vm.LoadAsync();

        await vm.SetResidentApplicationsEnabledAsync(false);

        var command = Assert.IsType<SetResidentApplicationsEnabledCommand>(Assert.Single(commands.SentCommands));
        Assert.False(command.IsEnabled);
        Assert.False(vm.ResidentApplicationsEnabled);
    }

    /// <summary>条目开关、删除和移动只能走父级的类型化命令，再由返回快照更新列表。</summary>
    [Fact]
    public async Task ResidentApplicationActionsSendTypedCommandsThroughParent()
    {
        var app = new ResidentApplication(@"C:\Apps\Managed.exe", new HashSet<string>(), "Managed", true, 0);
        var commands = new ResidentCommandBus(
            mutationResult: new ResidentSettingsMutationResult(
                true, ResidentSettingsError.None, [app with { Enabled = false }], false));
        var vm = CreateResidentViewModel(commands, ButlerSettings.Default with { ResidentApplications = [app] });
        await vm.LoadAsync();
        var item = Assert.Single(vm.ResidentApplications);

        await item.SetEnabledAsync(false);
        await item.RemoveAsync();
        await item.MoveAsync(1);

        Assert.Contains(commands.SentCommands, command => command is SetResidentApplicationEnabledCommand);
        Assert.Contains(commands.SentCommands, command => command is RemoveResidentApplicationCommand);
        Assert.Contains(commands.SentCommands, command => command is MoveResidentApplicationCommand);
    }

    public static IEnumerable<object[]> ManualSaveCases =>
    [
        [CaptureSkipReason.None, true, false, "现场已保存"],
        [CaptureSkipReason.Disabled, false, false, "捕获已暂停，仍完成常驻查找"],
        [CaptureSkipReason.Unchanged, false, false, "现场未变化"],
        [CaptureSkipReason.Failed, false, false, "现场保存失败，仍完成常驻查找"],
        [CaptureSkipReason.None, true, true, "常驻应用发现失败"]
    ];

    /// <summary>登录启动、捕获与排除并发修改时必须保留三个字段的最终提交值。</summary>
    [Fact]
    public async Task ConcurrentStartupCaptureAndExclusionPreserveAllFields()
    {
        var store = new FirstLoadBarrierSettingsStore(
            DeskButler.Core.Settings.ButlerSettings.Default with { StartupEnabled = false });
        var registration = new FakeStartupRegistration(false);
        using var settings = new SettingsCoordinator(store);
        var startup = new SetStartupEnabledCommandHandler(settings, registration);
        var capture = new SetCaptureEnabledCommandHandler(settings);
        var exclusion = new PersistExclusionCommandHandler(settings);

        var startupTask = startup.HandleAsync(new SetStartupEnabledCommand(true), CancellationToken.None);
        await store.FirstLoadStarted.Task;
        var captureTask = capture.HandleAsync(new SetCaptureEnabledCommand(false), CancellationToken.None);
        var exclusionTask = exclusion.HandleAsync(
            new PersistExclusionCommand(@"C:\Apps\Editor.exe"), CancellationToken.None);
        store.ReleaseFirstLoad.TrySetResult();

        await Task.WhenAll(startupTask, captureTask, exclusionTask);

        Assert.True(store.Current.StartupEnabled);
        Assert.False(store.Current.CaptureEnabled);
        Assert.Contains(@"C:\Apps\Editor.exe", store.Current.ExcludedExecutablePaths);
    }

    /// <summary>目标保存失败后必须依次尝试两个补偿，并把原始失败保留在聚合异常首位。</summary>
    [Fact]
    public async Task StartupRollbackAttemptsSettingsAndRegistryAndPreservesOriginalFailure()
    {
        var calls = new List<string>();
        var originalSaveFailure = new IOException("目标设置保存失败");
        var settingsRollbackFailure = new IOException("设置补偿失败");
        var registrationRollbackFailure = new InvalidOperationException("注册补偿失败");
        var store = new CompensationFailingSettingsStore(
            DeskButler.Core.Settings.ButlerSettings.Default with { StartupEnabled = false },
            calls,
            originalSaveFailure,
            settingsRollbackFailure);
        var registration = new FakeStartupRegistration(false, calls)
        {
            DisableFailure = registrationRollbackFailure
        };
        using var settings = new SettingsCoordinator(store);
        var handler = new SetStartupEnabledCommandHandler(settings, registration);

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => handler.HandleAsync(new SetStartupEnabledCommand(true), CancellationToken.None));

        Assert.Same(originalSaveFailure, error.InnerExceptions[0]);
        Assert.Contains(registrationRollbackFailure, error.InnerExceptions);
        Assert.Contains(settingsRollbackFailure, error.InnerExceptions);
        Assert.Equal(3, error.InnerExceptions.Count);
        Assert.True(store.RollbackAttempted);
        Assert.True(registration.RollbackAttempted);
        Assert.Equal(
            ["registration:enable", "settings:target", "registration:disable", "settings:rollback"],
            calls);
    }

    /// <summary>登录启动命令启用时必须同时提交 JSON 与唯一注册值。</summary>
    [Fact]
    public async Task StartupToggleHandlerEnablesSettingsAndRegistration()
    {
        var store = new InMemorySettingsStore(DeskButler.Core.Settings.ButlerSettings.Default with
        {
            StartupEnabled = false
        });
        var registration = new FakeStartupRegistration(false);
        using var settings = new SettingsCoordinator(store);
        var handler = new SetStartupEnabledCommandHandler(settings, registration);

        Assert.True(await handler.HandleAsync(new SetStartupEnabledCommand(true), CancellationToken.None));
        Assert.True(store.Current.StartupEnabled);
        Assert.True(registration.IsEnabled);
    }

    /// <summary>注册失败时必须补偿恢复 JSON、注册状态与 UI 可用的返回错误边界。</summary>
    [Fact]
    public async Task StartupToggleHandlerRollsBackSettingsWhenRegistrationFails()
    {
        var store = new InMemorySettingsStore(DeskButler.Core.Settings.ButlerSettings.Default with
        {
            StartupEnabled = false
        });
        var registration = new FakeStartupRegistration(false) { FailEnable = true };
        using var settings = new SettingsCoordinator(store);
        var handler = new SetStartupEnabledCommandHandler(settings, registration);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(new SetStartupEnabledCommand(true), CancellationToken.None));

        Assert.False(store.Current.StartupEnabled);
        Assert.False(registration.IsEnabled);
    }

    /// <summary>登录启动命令禁用时必须同步提交 JSON 与注册值。</summary>
    [Fact]
    public async Task StartupToggleHandlerDisablesSettingsAndRegistration()
    {
        var store = new InMemorySettingsStore(DeskButler.Core.Settings.ButlerSettings.Default);
        var registration = new FakeStartupRegistration(true);
        using var settings = new SettingsCoordinator(store);
        var handler = new SetStartupEnabledCommandHandler(settings, registration);

        Assert.False(await handler.HandleAsync(new SetStartupEnabledCommand(false), CancellationToken.None));
        Assert.False(store.Current.StartupEnabled);
        Assert.False(registration.IsEnabled);
    }

    /// <summary>设置保存失败时注册表与原 JSON 状态必须保持不变。</summary>
    [Fact]
    public async Task StartupToggleHandlerKeepsRegistrationWhenSettingsSaveFails()
    {
        var original = DeskButler.Core.Settings.ButlerSettings.Default;
        var store = new FailOnceSettingsStore(original);
        var registration = new FakeStartupRegistration(true);
        using var settings = new SettingsCoordinator(store);
        var handler = new SetStartupEnabledCommandHandler(settings, registration);

        await Assert.ThrowsAsync<IOException>(
            () => handler.HandleAsync(new SetStartupEnabledCommand(false), CancellationToken.None));

        Assert.Equal(original, store.Current);
        Assert.True(registration.IsEnabled);
    }

    /// <summary>登录启动失败但实际状态可核实时，UI 必须恢复真实值并保持开关可用。</summary>
    [Fact]
    public async Task StartupToggleFailureRestoresVerifiedStateAndKeepsToggleEnabled()
    {
        var store = new FailOnceSettingsStore(
            DeskButler.Core.Settings.ButlerSettings.Default with { StartupEnabled = false });
        var registration = new FakeStartupRegistration(false);
        using var settings = new SettingsCoordinator(store);
        var bus = new InProcessCommandBus();
        bus.Register(new SetStartupEnabledCommandHandler(settings, registration));
        var vm = new MainViewModel(
            new InMemorySceneRepository(), bus, store, new InlineUiDispatcher(),
            startupRegistration: registration);
        await vm.LoadAsync();

        await vm.ToggleStartupAsync();

        Assert.False(vm.IsStartupEnabled);
        Assert.True(vm.IsStartupToggleEnabled);
        Assert.True(vm.ToggleStartupCommand.CanExecute(null));
        Assert.Contains("登录启动设置失败", vm.StartupErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>登录启动失败后无法重新加载设置时，UI 必须禁用开关并保留可见错误。</summary>
    [Fact]
    public async Task StartupToggleFailureDisablesToggleWhenActualSettingsCannotBeLoaded()
    {
        var store = new FailAfterFirstLoadSettingsStore(
            DeskButler.Core.Settings.ButlerSettings.Default with { StartupEnabled = false });
        var vm = new MainViewModel(
            new InMemorySceneRepository(), new FailingCommandBus(new IOException("命令失败")), store);
        await vm.LoadAsync();

        await vm.ToggleStartupAsync();

        Assert.False(vm.IsStartupToggleEnabled);
        Assert.False(vm.ToggleStartupCommand.CanExecute(null));
        Assert.Contains("无法核实实际状态", vm.StartupErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>登录启动失败后 JSON 与注册状态不一致时，UI 必须以注册状态显示并禁用开关。</summary>
    [Fact]
    public async Task StartupToggleFailureDisablesToggleWhenSettingsAndRegistrationDisagree()
    {
        var initial = DeskButler.Core.Settings.ButlerSettings.Default with { StartupEnabled = false };
        var store = new SettingsSequenceStore(initial, initial with { StartupEnabled = true });
        var registration = new FakeStartupRegistration(false);
        var vm = new MainViewModel(
            new InMemorySceneRepository(), new FailingCommandBus(new IOException("命令失败")), store,
            new InlineUiDispatcher(), startupRegistration: registration);
        await vm.LoadAsync();

        await vm.ToggleStartupAsync();

        Assert.False(vm.IsStartupEnabled);
        Assert.False(vm.IsStartupToggleEnabled);
        Assert.False(vm.ToggleStartupCommand.CanExecute(null));
        Assert.Contains("无法核实实际状态", vm.StartupErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>命令失败后缺少注册边界时不得仅凭 JSON 启用再次切换。</summary>
    [Fact]
    public async Task StartupToggleFailureDisablesToggleWhenRegistrationBoundaryIsUnavailable()
    {
        var store = new InMemorySettingsStore(
            DeskButler.Core.Settings.ButlerSettings.Default with { StartupEnabled = false });
        var commands = new FailingCommandBus(new IOException("命令失败"));
        var vm = new MainViewModel(new InMemorySceneRepository(), commands, store);
        await vm.LoadAsync();

        await vm.ToggleStartupAsync();
        vm.ToggleStartupCommand.Execute(null);

        Assert.False(vm.IsStartupEnabled);
        Assert.False(vm.IsStartupToggleEnabled);
        Assert.False(vm.ToggleStartupCommand.CanExecute(null));
        Assert.Equal(1, commands.SendCount);
        Assert.Contains("无法核实实际状态", vm.StartupErrorMessage, StringComparison.Ordinal);
    }

    private sealed class FailOnceSettingsStore(DeskButler.Core.Settings.ButlerSettings initial)
        : DeskButler.Core.Settings.ISettingsStore
    {
        private int failuresRemaining = 1;
        internal DeskButler.Core.Settings.ButlerSettings Current { get; private set; } = initial;

        /// <inheritdoc />
        public Task<DeskButler.Core.Settings.ButlerSettings> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Current);

        /// <inheritdoc />
        public Task SaveAsync(
            DeskButler.Core.Settings.ButlerSettings settings, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref failuresRemaining, 0) == 1)
            {
                Current = settings;
                throw new IOException("设置写入失败");
            }

            Current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStartupRegistration(bool enabled, List<string>? calls = null) : IStartupRegistration
    {
        public bool FailEnable { get; init; }
        public Exception? DisableFailure { get; init; }
        public bool RollbackAttempted { get; private set; }
        public bool IsEnabled { get; private set; } = enabled;
        /// <inheritdoc />
        public void Enable()
        {
            calls?.Add("registration:enable");
            if (FailEnable) throw new InvalidOperationException("注册表写入失败");
            IsEnabled = true;
        }
        /// <inheritdoc />
        public void Disable()
        {
            calls?.Add("registration:disable");
            RollbackAttempted = true;
            if (DisableFailure is not null)
            {
                throw DisableFailure;
            }

            IsEnabled = false;
        }
    }

    private sealed class FirstLoadBarrierSettingsStore(DeskButler.Core.Settings.ButlerSettings initial)
        : DeskButler.Core.Settings.ISettingsStore
    {
        private int loadCount;
        internal TaskCompletionSource FirstLoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFirstLoad { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal DeskButler.Core.Settings.ButlerSettings Current { get; private set; } = initial;

        /// <inheritdoc />
        public async Task<DeskButler.Core.Settings.ButlerSettings> LoadAsync(CancellationToken cancellationToken)
        {
            var snapshot = Current;
            if (Interlocked.Increment(ref loadCount) == 1)
            {
                FirstLoadStarted.TrySetResult();
                await ReleaseFirstLoad.Task.WaitAsync(cancellationToken);
            }

            return snapshot;
        }

        /// <inheritdoc />
        public Task SaveAsync(
            DeskButler.Core.Settings.ButlerSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class CompensationFailingSettingsStore(
        DeskButler.Core.Settings.ButlerSettings initial,
        List<string> calls,
        Exception originalSaveFailure,
        Exception rollbackFailure) : DeskButler.Core.Settings.ISettingsStore
    {
        private int saveCount;
        internal bool RollbackAttempted { get; private set; }

        /// <inheritdoc />
        public Task<DeskButler.Core.Settings.ButlerSettings> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(initial);

        /// <inheritdoc />
        public Task SaveAsync(
            DeskButler.Core.Settings.ButlerSettings settings,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref saveCount) == 1)
            {
                calls.Add("settings:target");
                return Task.FromException(originalSaveFailure);
            }

            calls.Add("settings:rollback");
            RollbackAttempted = true;
            return Task.FromException(rollbackFailure);
        }
    }

    private sealed class FailingCommandBus(Exception failure) : ICommandBus
    {
        internal int SendCount { get; private set; }

        /// <inheritdoc />
        public Task<TResponse> SendAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromException<TResponse>(failure);
        }
    }

    private sealed class FailAfterFirstLoadSettingsStore(DeskButler.Core.Settings.ButlerSettings initial)
        : DeskButler.Core.Settings.ISettingsStore
    {
        private int loadCount;

        /// <inheritdoc />
        public Task<DeskButler.Core.Settings.ButlerSettings> LoadAsync(CancellationToken cancellationToken) =>
            Interlocked.Increment(ref loadCount) == 1
                ? Task.FromResult(initial)
                : Task.FromException<DeskButler.Core.Settings.ButlerSettings>(new IOException("设置重新加载失败"));

        /// <inheritdoc />
        public Task SaveAsync(
            DeskButler.Core.Settings.ButlerSettings settings,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SettingsSequenceStore(params DeskButler.Core.Settings.ButlerSettings[] settings)
        : DeskButler.Core.Settings.ISettingsStore
    {
        private int loadCount;

        /// <inheritdoc />
        public Task<DeskButler.Core.Settings.ButlerSettings> LoadAsync(CancellationToken cancellationToken)
        {
            var index = Math.Min(Interlocked.Increment(ref loadCount) - 1, settings.Length - 1);
            return Task.FromResult(settings[index]);
        }

        /// <inheritdoc />
        public Task SaveAsync(
            DeskButler.Core.Settings.ButlerSettings updated,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>首页恢复必须消费逐项结果并保留部分失败原因，而不是报告无条件完成。</summary>
    [Fact]
    public async Task RestoreSceneAsyncSummarizesPartialFailureWithReason()
    {
        var scene = SceneFactory.Create("00000000-0000-0000-0000-000000000031", DateTimeOffset.UtcNow,
            @"C:\Apps\Editor.exe", @"C:\Apps\Browser.exe");
        var result = new RestoreResult([
            new(scene.Items[0].Id, RestoreItemStatus.Succeeded),
            new(scene.Items[1].Id, RestoreItemStatus.Failed, "窗口定位失败")]);
        var vm = new MainViewModel(new InMemorySceneRepository(scene), new RestoreResultCommandBus(result),
            new InMemorySettingsStore(DeskButler.Core.Settings.ButlerSettings.Default));

        await vm.RestoreSceneAsync(new SceneSummaryViewModel(scene));

        Assert.Contains("成功 1", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("失败 1", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("窗口定位失败", vm.StatusText, StringComparison.Ordinal);
    }

    private sealed class RestoreResultCommandBus(RestoreResult result) : ICommandBus
    {
        /// <inheritdoc />
        public Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken) =>
            Task.FromResult((TResponse)(object)result);
    }

    /// <summary>初始化只加载最近三份现场，并保留仓库的最新优先顺序。</summary>
    [Fact]
    public async Task LoadAsyncExposesOnlyThreeMostRecentScenes()
    {
        var now = new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);
        var scenes = Enumerable.Range(1, 4)
            .Select(index => SceneFactory.Create($"00000000-0000-0000-0000-00000000000{index}", now.AddMinutes(-index),
                @"C:\Apps\Editor.exe"))
            .ToArray();
        var vm = new MainViewModel(new InMemorySceneRepository(scenes), new RecordingCommandBus(),
            new InMemorySettingsStore(DeskButler.Core.Settings.ButlerSettings.Default));

        await vm.LoadAsync();

        Assert.Equal(3, vm.RecentScenes.Count);
        Assert.Equal(scenes.Take(3).Select(scene => scene.Id), vm.RecentScenes.Select(scene => scene.Scene.Id));
    }

    /// <summary>用户可手动选择较旧现场恢复，命令必须携带该现场而不是默认最新现场。</summary>
    [Fact]
    public async Task RestoreSceneAsyncSendsTheSelectedOlderScene()
    {
        var now = new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);
        var newest = SceneFactory.Create("00000000-0000-0000-0000-000000000011", now, @"C:\Apps\Editor.exe");
        var older = SceneFactory.Create("00000000-0000-0000-0000-000000000012", now.AddMinutes(-10), @"C:\Apps\Browser.exe");
        var commands = new RecordingCommandBus();
        var vm = new MainViewModel(new InMemorySceneRepository(newest, older), commands,
            new InMemorySettingsStore(DeskButler.Core.Settings.ButlerSettings.Default));
        await vm.LoadAsync();

        await vm.RestoreSceneAsync(vm.RecentScenes[1]);

        var command = Assert.IsType<RestoreSceneCommand>(Assert.Single(commands.SentCommands));
        Assert.Equal(older.Id, command.Scene.Id);
        Assert.Equal(older.Items.Select(item => item.Id), command.SelectedItemIds);
    }

    /// <summary>暂停捕获必须持久化并更新可观察状态，供托盘菜单切换为“继续”。</summary>
    [Fact]
    public async Task ToggleCaptureAsyncPersistsPausedState()
    {
        var store = new InMemorySettingsStore(DeskButler.Core.Settings.ButlerSettings.Default);
        var bus = new DeskButler.Application.Commands.InProcessCommandBus();
        bus.Register(new SetCaptureEnabledCommandHandler(store));
        var vm = new MainViewModel(new InMemorySceneRepository(), bus, store);
        await vm.LoadAsync();

        await vm.ToggleCaptureAsync();

        Assert.True(vm.IsCapturePaused);
        Assert.False(store.Current.CaptureEnabled);
    }

    /// <summary>设置页必须展示已经持久化的永久排除路径。</summary>
    [Fact]
    public async Task LoadAsyncExposesPersistedExclusionPaths()
    {
        var settings = DeskButler.Core.Settings.ButlerSettings.Default with
        {
            ExcludedExecutablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                @"C:\Apps\Editor.exe",
                @"C:\Apps\Browser.exe"
            }
        };
        var vm = new MainViewModel(
            new InMemorySceneRepository(), new RecordingCommandBus(), new InMemorySettingsStore(settings));

        await vm.LoadAsync();

        Assert.Equal([@"C:\Apps\Browser.exe", @"C:\Apps\Editor.exe"], vm.ExcludedExecutablePaths);
    }

    /// <summary>诊断页必须显示数据库健康警告，并由用户操作加载写 ZIP 前的脱敏预览。</summary>
    [Fact]
    public async Task DiagnosticsEntryLoadsHealthWarningAndPreviewOnDemand()
    {
        var calls = 0;
        var vm = new MainViewModel(
            new InMemorySceneRepository(), new RecordingCommandBus(),
            new InMemorySettingsStore(DeskButler.Core.Settings.ButlerSettings.Default),
            new InlineUiDispatcher(), "数据库已从故障现场回退。",
            _ =>
            {
                calls++;
                return Task.FromResult("[deskbutler.jsonl] 已脱敏预览");
            });

        Assert.Equal("数据库已从故障现场回退。", vm.HealthStatusText);
        Assert.Equal(0, calls);
        await vm.LoadDiagnosticsAsync();

        Assert.Equal(1, calls);
        Assert.Equal("[deskbutler.jsonl] 已脱敏预览", vm.DiagnosticPreviewText);
    }

    private static MainViewModel CreateResidentViewModel(
        ResidentCommandBus commands,
        ButlerSettings? settings = null,
        IExecutablePicker? picker = null,
        Func<CancellationToken, Task>? launch = null,
        ISettingsStore? settingsStore = null,
        ISceneRepository? sceneRepository = null) =>
        new(
            sceneRepository ?? new InMemorySceneRepository(),
            commands,
            settingsStore ?? new CountingSettingsStore(settings ?? ButlerSettings.Default),
            new InlineUiDispatcher(),
            residentDependencies: new ResidentViewModelDependencies(
                picker ?? new FakeExecutablePicker(),
                new FakeExecutableIconProvider(),
                _ => new ResidentExecutableValidation(true, @"C:\Apps\Allowed.exe", ResidentExecutableRejection.None),
                launch ?? (_ => Task.CompletedTask)));

    private static ResidentAppCandidate CreateCandidate(string id) =>
        new(
            id,
            "Agent",
            @"C:\Apps\Agent.exe",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ResidentCandidateConfidence.High,
            ResidentCandidateKind.NewApplication,
            null);

    private sealed class FakeExecutablePicker(string? result = null) : IExecutablePicker
    {
        public Task<string?> PickAsync(CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class FakeExecutableIconProvider : IExecutableIconProvider
    {
        public ImageSource? GetIcon(string? executablePath) => null;
    }

    private sealed class CountingSettingsStore(ButlerSettings current) : ISettingsStore
    {
        internal int LoadCount { get; private set; }

        public Task<ButlerSettings> LoadAsync(CancellationToken cancellationToken)
        {
            LoadCount++;
            return Task.FromResult(current);
        }

        public Task SaveAsync(ButlerSettings settings, CancellationToken cancellationToken)
        {
            current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSettingsStore : ISettingsStore
    {
        public Task<ButlerSettings> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromException<ButlerSettings>(new IOException("常驻设置重新加载失败"));

        public Task SaveAsync(ButlerSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowingSceneRepository : ISceneRepository
    {
        internal int GetRecentCallCount { get; private set; }

        public Task SaveAsync(SceneSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<SceneSnapshot>> GetRecentAsync(int maximumCount, CancellationToken cancellationToken)
        {
            GetRecentCallCount++;
            return Task.FromException<IReadOnlyList<SceneSnapshot>>(new IOException("场景仓库不可用"));
        }

        public Task MarkInvalidAsync(Guid snapshotId, string reason, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ResidentCommandBus(
        ManualSaveResult? manualSaveResult = null,
        ResidentDiscoveryBatch? findResult = null,
        bool confirmResult = false,
        ResidentSettingsMutationResult? mutationResult = null,
        Func<Task>? saveSideEffect = null) : ICommandBus
    {
        internal List<object> SentCommands { get; } = [];

        internal ResidentDiscoveryBatch? FindResult { get; set; } = findResult;

        public async Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken)
        {
            SentCommands.Add(command);
            if (command is SaveSceneNowCommand && saveSideEffect is not null)
            {
                await saveSideEffect();
            }
            object? response = command switch
            {
                SaveSceneNowCommand => manualSaveResult,
                FindResidentCandidatesCommand => FindResult,
                ConfirmResidentCandidatesCommand => confirmResult,
                DismissResidentCandidatesCommand => true,
                SetResidentApplicationsEnabledCommand or SetResidentApplicationEnabledCommand or
                    RemoveResidentApplicationCommand or MoveResidentApplicationCommand or
                    AddResidentApplicationCommand or ReplaceResidentApplicationPathCommand => mutationResult,
                _ => throw new InvalidOperationException($"未预期的命令类型：{command.GetType().Name}")
            };
            return (TResponse)response!;
        }
    }
}
