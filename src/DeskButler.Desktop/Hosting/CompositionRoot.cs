using DeskButler.Application.Commands;
using DeskButler.Application.Hosting;
using DeskButler.Application.Events;
using DeskButler.Core.Capture;
using DeskButler.Core.Persistence;
using DeskButler.Core.Scenes;
using DeskButler.Core.Settings;
using DeskButler.Core.Time;
using DeskButler.Core.Diagnostics;
using DeskButler.Desktop.Tray;
using DeskButler.Desktop.ViewModels;
using DeskButler.Desktop.Views;
using DeskButler.Infrastructure.Windows.Restore;
using DeskButler.Infrastructure.Windows.Session;
using DeskButler.Infrastructure.Windows.Startup;
using DeskButler.Infrastructure.Windows.Windows;
using DeskButler.Modules.WorkspaceRecovery;
using DeskButler.Modules.WorkspaceRecovery.Capture;
using DeskButler.Modules.WorkspaceRecovery.Restore;
using DeskButler.Persistence.Json;
using DeskButler.Persistence.Paths;
using DeskButler.Persistence.Sqlite;
using DeskButler.Persistence.Diagnostics;
using System.IO;

namespace DeskButler.Desktop.Hosting;

/// <summary>手工组合 V1 的真实持久化、捕获、恢复与桌面界面服务。</summary>
public sealed class CompositionRoot : IAsyncDisposable
{
    private readonly ModuleHost moduleHost;
    private readonly SnapshotScheduler scheduler;
    private readonly CaptureCoordinator captureCoordinator;
    private readonly SqliteSceneRepository repository;
    private readonly SettingsCoordinator settingsCoordinator;
    private readonly InventoryFingerprintChangeSource desktopChanges;
    private readonly WindowsSessionEvents sessionEvents;
    private readonly RecoveryCardFocusCoordinator recoveryCardFocus;
    private readonly BestEffortAsyncCleanup cleanup;
    private bool started;

    private CompositionRoot(
        ModuleHost moduleHost,
        SnapshotScheduler scheduler,
        CaptureCoordinator captureCoordinator,
        SqliteSceneRepository repository,
        SettingsCoordinator settingsCoordinator,
        InventoryFingerprintChangeSource desktopChanges,
        WindowsSessionEvents sessionEvents,
        RecoveryCardFocusCoordinator recoveryCardFocus,
        MainViewModel mainViewModel,
        RecoveryCardViewModel recoveryCardViewModel,
        MainWindow mainWindow,
        RecoveryCardWindow recoveryCardWindow,
        TrayIconService trayIcon,
        RollingJsonLog diagnosticLog)
    {
        this.moduleHost = moduleHost;
        this.scheduler = scheduler;
        this.captureCoordinator = captureCoordinator;
        this.repository = repository;
        this.settingsCoordinator = settingsCoordinator;
        this.desktopChanges = desktopChanges;
        this.sessionEvents = sessionEvents;
        this.recoveryCardFocus = recoveryCardFocus;
        MainViewModel = mainViewModel;
        RecoveryCardViewModel = recoveryCardViewModel;
        MainWindow = mainWindow;
        RecoveryCardWindow = recoveryCardWindow;
        TrayIcon = trayIcon;
        cleanup = new BestEffortAsyncCleanup(
        [
            new("tray", () => { TrayIcon.Dispose(); return ValueTask.CompletedTask; }),
            new("desktop changes", async () => await desktopChanges.DisposeAsync()),
            new("session events", () => { sessionEvents.Dispose(); return ValueTask.CompletedTask; }),
            new("module host", async () => { if (started) await moduleHost.StopAsync(CancellationToken.None); }),
            new("main view model", () => { MainViewModel.Dispose(); return ValueTask.CompletedTask; }),
            new("recovery view model", () => { RecoveryCardViewModel.Dispose(); return ValueTask.CompletedTask; }),
            new("recovery window", () => { RecoveryCardWindow.CloseForExit(); return ValueTask.CompletedTask; }),
            new("main window", () => { MainWindow.CloseForExit(); return ValueTask.CompletedTask; }),
            new("scheduler", async () => await scheduler.DisposeAsync()),
            new("capture coordinator", () => { captureCoordinator.Dispose(); return ValueTask.CompletedTask; }),
            new("repository", () => { repository.Dispose(); return ValueTask.CompletedTask; }),
            new("settings", () => { settingsCoordinator.Dispose(); return ValueTask.CompletedTask; }),
            new("diagnostic log", async () => await diagnosticLog.DisposeAsync())
        ]);
    }

    /// <summary>获取共享主界面模型。</summary>
    public MainViewModel MainViewModel { get; }

    /// <summary>获取恢复提示卡片模型。</summary>
    public RecoveryCardViewModel RecoveryCardViewModel { get; }

    /// <summary>获取主窗口实例。</summary>
    public MainWindow MainWindow { get; }

    /// <summary>获取恢复卡片窗口实例。</summary>
    public RecoveryCardWindow RecoveryCardWindow { get; }

    /// <summary>获取唯一托盘图标所有者。</summary>
    public TrayIconService TrayIcon { get; }

    /// <summary>使用正式当前用户数据目录创建真实服务图。</summary>
    public static Task<CompositionRoot> CreateAsync(
        AppDataPaths paths,
        Action requestExit,
        bool pauseAutomaticCapture,
        CancellationToken cancellationToken) =>
        CreateCoreAsync(
            paths, requestExit, createFixture: false, applyStartupRegistration: true,
            pauseAutomaticCapture, cancellationToken);

#if DEBUG
    /// <summary>Debug 构建可在隔离数据根创建一份无敏感内容的冒烟现场。</summary>
    public static Task<CompositionRoot> CreateDebugAsync(
        AppDataPaths paths,
        Action requestExit,
        bool createFixture,
        bool pauseAutomaticCapture,
        CancellationToken cancellationToken) =>
        CreateCoreAsync(
            paths, requestExit, createFixture, applyStartupRegistration: false,
            pauseAutomaticCapture, cancellationToken);
#endif

    /// <summary>启动工作区模块、会话关机检查点与轮询变化源。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(cleanup.IsComplete, this);
        if (started)
        {
            return;
        }

        await moduleHost.StartAsync(cancellationToken);
        sessionEvents.Start();
        await desktopChanges.StartAsync(cancellationToken);
        started = true;
    }

    /// <summary>加载最近现场并在存在时展示最新一份恢复卡片。</summary>
    public async Task<bool> ShowRecoveryCardForLatestSceneAsync()
    {
        await MainViewModel.LoadAsync();
        var latest = MainViewModel.RecentScenes.FirstOrDefault();
        if (latest is null)
        {
            return false;
        }

        await RecoveryCardViewModel.ShowAsync(latest.Scene);
        return true;
    }

    /// <summary>显示并激活主窗口，供托盘菜单随时手动调用。</summary>
    public void ShowMainWindow()
    {
        if (!MainWindow.IsVisible)
        {
            MainWindow.Show();
        }

        if (MainWindow.WindowState == System.Windows.WindowState.Minimized)
        {
            MainWindow.WindowState = System.Windows.WindowState.Normal;
        }

        MainWindow.Activate();
    }

    /// <summary>供托盘键盘菜单加载最近现场并显式聚焦恢复卡。</summary>
    public Task<bool> FocusRecoveryCardAsync() => recoveryCardFocus.FocusAsync();

#if DEBUG
    /// <summary>在 Debug 隔离数据根验证托盘宿主、主窗与卡片可创建并干净收起。</summary>
    public async Task RunDebugSmokeAsync()
    {
        ShowMainWindow();
        await MainViewModel.LoadDiagnosticsAsync();
        if (string.IsNullOrWhiteSpace(MainViewModel.HealthStatusText) ||
            string.IsNullOrWhiteSpace(MainViewModel.DiagnosticPreviewText))
        {
            throw new InvalidOperationException("Debug UI 冒烟未能加载诊断页健康状态或预览入口。");
        }

        var emptyFocused = await FocusRecoveryCardAsync();
        if (emptyFocused || RecoveryCardViewModel.IsVisible || RecoveryCardWindow.IsVisible)
        {
            throw new InvalidOperationException("Debug UI 冒烟在无现场时错误显示或聚焦了空恢复卡。");
        }

        // 同一真实对象图先验证空仓库，再注入固定安全现场验证实际窗口焦点。
        await CreateFixtureIfEmptyAsync(repository, new SystemClock(), CancellationToken.None);
        var focused = await FocusRecoveryCardAsync();
        if (!focused || !MainWindow.IsVisible || !RecoveryCardViewModel.IsVisible || !RecoveryCardWindow.IsVisible ||
            !RecoveryCardWindow.IsKeyboardFocusWithin)
        {
            throw new InvalidOperationException("Debug UI 冒烟未能显示窗口并取得恢复卡键盘焦点。");
        }

        await RecoveryCardViewModel.SkipAsync();
        if (RecoveryCardViewModel.IsVisible || RecoveryCardWindow.IsVisible)
        {
            throw new InvalidOperationException("Debug UI 冒烟未能无恢复副作用地隐藏卡片。");
        }
    }
#endif

    /// <summary>停止后台模块并按安全顺序释放托盘、窗口和持久化资源。</summary>
    public async ValueTask DisposeAsync()
    {
        if (!cleanup.IsComplete)
        {
            await cleanup.RunAsync();
        }
    }

    /// <summary>构建不依赖反射容器的完整对象图。</summary>
    private static async Task<CompositionRoot> CreateCoreAsync(
        AppDataPaths paths,
        Action requestExit,
        bool createFixture,
        bool applyStartupRegistration,
        bool pauseAutomaticCapture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(requestExit);
        paths.EnsureRootDirectoryExists();

        var clock = new SystemClock();
        var diagnosticLog = new RollingJsonLog(paths.LogDirectory);
        try
        {
            var databaseRecovery = new DatabaseRecovery(
                paths, new SqliteConnectionLifecycle(), new DatabaseMigrator(paths));
            var databaseHealth = await databaseRecovery.InitializeAsync(cancellationToken);
            if (databaseHealth.HealthWarning is not null)
            {
                await diagnosticLog.WriteAsync(
                    new DiagnosticEvent(
                        clock.UtcNow, DiagnosticLevel.Warning, "database-recovery",
                        databaseHealth.HealthWarning,
                        new Dictionary<string, object?>
                        {
                            ["backupName"] = Path.GetFileName(databaseHealth.BackupDirectory)
                        }),
                    cancellationToken);
            }

            var diagnosticExporter = new DiagnosticBundleExporter(
                paths.LogDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ["deskbutler.jsonl", "deskbutler.1.jsonl", "deskbutler.2.jsonl"]);
            var settingsStore = new JsonSettingsStore(paths);
            var persistedSettings = await settingsStore.LoadAsync(cancellationToken);
            var startupRegistration = ApplyStartupRegistration(persistedSettings, applyStartupRegistration);
            var settingsCoordinator = new SettingsCoordinator(settingsStore);
            var repository = new SqliteSceneRepository(paths);
            var failureHistoryStore = new SqliteFailureHistoryStore(paths);
            var notifyingRepository = new NotifyingSceneRepository(repository);
            var rawInventory = new Win32WindowInventory();
            var captureInventory = new SettingsAwareWindowInventory(rawInventory, settingsStore);
            // 动态装饰器负责捕获开关与排除；协调器保持启用，因而暂停后仍可在本次进程中继续。
            var captureSettings = persistedSettings with
            {
                CaptureEnabled = true,
                ExcludedExecutablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            };
            var captureCoordinator = new CaptureCoordinator(
                captureSettings,
                captureInventory,
                new SceneFilter(captureSettings),
                notifyingRepository,
                clock);
            var scheduler = new SnapshotScheduler(clock, captureCoordinator.SaveNowAsync);
            var automaticCaptureGate = new AutomaticCaptureGate(
                pauseAutomaticCapture,
                pauseAutomaticCapture ? "检测到上次运行未正常结束，自动捕获已暂停。" : null);
            // 两秒检测上限加既有十秒静止防抖，近似用户所见十秒静止语义。
            var desktopChanges = new InventoryFingerprintChangeSource(
                rawInventory, clock, TimeSpan.FromSeconds(2), reportFailure: null, diagnosticLog);
            var module = new WorkspaceRecoveryModule(
                desktopChanges, scheduler, captureCoordinator, clock, automaticCaptureGate);
            var eventBus = new InProcessEventBus();
            var moduleHost = new ModuleHost([module], eventBus);
            var sessionEvents = new WindowsSessionEvents(
                token => automaticCaptureGate.IsPaused
                    ? Task.CompletedTask
                    : captureCoordinator.SaveNowAsync("session-ending", token));

            var commandBus = new InProcessCommandBus();
            commandBus.Register(new SaveSceneNowCommandHandler(captureCoordinator));
            commandBus.Register(new RestoreSceneCommandHandler(
                rawInventory,
                new RestorePlanner(),
                new RestoreExecutor(
                    new WindowsAppLauncher(), rawInventory, new WindowsWindowPositioner(), clock),
                    settingsStore,
                    failureHistoryStore,
                    diagnosticLog));
            commandBus.Register(new SetCaptureEnabledCommandHandler(settingsCoordinator, automaticCaptureGate));
            if (startupRegistration is not null)
            {
                commandBus.Register(new SetStartupEnabledCommandHandler(settingsStore, startupRegistration));
            }
            commandBus.Register(new PersistExclusionCommandHandler(settingsCoordinator));

#if DEBUG
            if (createFixture)
            {
                await CreateFixtureIfEmptyAsync(repository, clock, cancellationToken);
            }
#endif

            var mainViewModel = new MainViewModel(
                notifyingRepository, commandBus, settingsStore,
                new WpfUiDispatcher(System.Windows.Application.Current.Dispatcher),
                databaseHealth.HealthWarning,
                async token =>
                {
                    var manifest = await diagnosticExporter.CreateManifestAsync(token);
                    return manifest.Files.Count == 0
                        ? "当前没有可预览的诊断文件。"
                        : string.Join(
                            Environment.NewLine + Environment.NewLine,
                            manifest.Files.Select(file =>
                                $"[{file.ArchiveName}] {file.ByteCount} 字节{Environment.NewLine}" +
                                file.Preview[..Math.Min(file.Preview.Length, 4000)]));
                },
                startupRegistration,
                eventBus,
                module.Descriptor,
                automaticCaptureGate);
            await mainViewModel.LoadAsync();
            var recoveryCardViewModel = new RecoveryCardViewModel(
                commandBus, clock, persistedSettings.RecoveryCardDismissSeconds, failureHistoryStore);
            var mainWindow = new MainWindow(mainViewModel);
            var recoveryCardWindow = new RecoveryCardWindow(recoveryCardViewModel);
            CompositionRoot? root = null;
            var recoveryCardFocus = new RecoveryCardFocusCoordinator(
                () => root?.ShowRecoveryCardForLatestSceneAsync() ?? Task.FromResult(false),
                recoveryCardWindow.FocusForKeyboard);
            var trayIcon = new TrayIconService(
                mainViewModel,
                () => root?.ShowMainWindow(),
                recoveryCardFocus.FocusAsync,
                requestExit);
            root = new CompositionRoot(
                moduleHost,
                scheduler,
                captureCoordinator,
                repository,
                settingsCoordinator,
                desktopChanges,
                sessionEvents,
                recoveryCardFocus,
                mainViewModel,
                recoveryCardViewModel,
                mainWindow,
                recoveryCardWindow,
                trayIcon,
                diagnosticLog);
            return root;
        }
        catch
        {
            // 对象图尚未交给 CompositionRoot 时，由工厂释放独占日志写者锁。
            await diagnosticLog.DisposeAsync();
            throw;
        }
    }

    /// <summary>仅为正式宿主同步当前用户 HKCU 登录启动设置；Debug 注入路径不触碰注册表。</summary>
    private static RegistryStartupRegistration? ApplyStartupRegistration(
        ButlerSettings settings, bool applyStartupRegistration)
    {
        if (!applyStartupRegistration)
        {
            return null;
        }

        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定 DeskButler 可执行文件路径。");
        var startup = new RegistryStartupRegistration(executablePath);
        if (settings.StartupEnabled)
        {
            startup.Enable();
        }
        else
        {
            startup.Disable();
        }

        return startup;
    }

#if DEBUG
    /// <summary>仅在 Debug 冒烟模式且数据库为空时创建一份安全固定现场。</summary>
    private static async Task CreateFixtureIfEmptyAsync(
        SqliteSceneRepository repository,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if ((await repository.GetRecentAsync(1, cancellationToken)).Count > 0)
        {
            return;
        }

        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var executablePath = Path.Combine(systemDirectory, "notepad.exe");
        var monitor = new MonitorIdentity("DISPLAY1", new WindowBounds(100, 100, 1280, 720), 96, 96);
        var item = new SceneItem(
            "debug-fixture-notepad", executablePath, "Notepad", "Debug 冒烟记事本", null,
            new WindowBounds(180, 140, 760, 520), SceneWindowState.Normal, monitor, false);
        await repository.SaveAsync(
            new SceneSnapshot(Guid.NewGuid(), 1, clock.UtcNow, "debug-fixture", [item]), cancellationToken);
    }
#endif

    /// <summary>使用系统 UTC 时间与 Task.Delay 的生产时钟。</summary>
    private sealed class SystemClock : IClock
    {
        /// <inheritdoc />
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        /// <inheritdoc />
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(delay, cancellationToken);
    }

}
