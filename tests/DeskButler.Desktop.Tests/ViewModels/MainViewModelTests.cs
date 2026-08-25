using DeskButler.Desktop.Hosting;
using DeskButler.Desktop.ViewModels;
using DeskButler.Application.Commands;
using DeskButler.Core.Restore;
using DeskButler.Infrastructure.Windows.Startup;

namespace DeskButler.Desktop.Tests.ViewModels;

public sealed class MainViewModelTests
{
    /// <summary>登录启动命令启用时必须同时提交 JSON 与唯一注册值。</summary>
    [Fact]
    public async Task StartupToggleHandlerEnablesSettingsAndRegistration()
    {
        var store = new InMemorySettingsStore(DeskButler.Core.Settings.ButlerSettings.Default with
        {
            StartupEnabled = false
        });
        var registration = new FakeStartupRegistration(false);
        var handler = new SetStartupEnabledCommandHandler(store, registration);

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
        var handler = new SetStartupEnabledCommandHandler(store, registration);

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
        var handler = new SetStartupEnabledCommandHandler(store, registration);

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
        var handler = new SetStartupEnabledCommandHandler(store, registration);

        await Assert.ThrowsAsync<IOException>(
            () => handler.HandleAsync(new SetStartupEnabledCommand(false), CancellationToken.None));

        Assert.Equal(original, store.Current);
        Assert.True(registration.IsEnabled);
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

    private sealed class FakeStartupRegistration(bool enabled) : IStartupRegistration
    {
        public bool FailEnable { get; init; }
        public bool IsEnabled { get; private set; } = enabled;
        /// <inheritdoc />
        public void Enable()
        {
            if (FailEnable) throw new InvalidOperationException("注册表写入失败");
            IsEnabled = true;
        }
        /// <inheritdoc />
        public void Disable() => IsEnabled = false;
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
