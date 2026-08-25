using DeskButler.Desktop.Hosting;
using DeskButler.Desktop.ViewModels;
using DeskButler.Application.Commands;
using DeskButler.Core.Restore;
using DeskButler.Infrastructure.Windows.Startup;

namespace DeskButler.Desktop.Tests.ViewModels;

public sealed class MainViewModelTests
{
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
}
