using DeskButler.Application.Commands;
using DeskButler.Application.Hosting;
using DeskButler.Application.Events;
using DeskButler.Application.Modules;
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
using DeskButler.Infrastructure.Windows.ResidentApps;
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
    private readonly CompositionStartupCoordinator startup;
    private readonly SqliteSceneRepository repository;
    private readonly RecoveryCardFocusCoordinator recoveryCardFocus;
    private readonly ResidentCandidateFocusCoordinator residentCandidateFocus;
    private readonly ResidentLaunchCoordinator residentLaunchCoordinator;
    private readonly ResidentCandidateCoordinator residentCandidates;
    private readonly ResidentAppCommandHandlerSet residentCommands;
    private readonly Func<string, CancellationToken, Task> automaticCapture;
    private readonly Func<CancellationToken, Task> sessionEndingCapture;
    private readonly BestEffortAsyncCleanup cleanup;

    private CompositionRoot(
        CompositionStartupCoordinator startup,
        SqliteSceneRepository repository,
        RecoveryCardFocusCoordinator recoveryCardFocus,
        ResidentCandidateFocusCoordinator residentCandidateFocus,
        ResidentLaunchCoordinator residentLaunchCoordinator,
        ResidentCandidateCoordinator residentCandidates,
        ResidentAppCommandHandlerSet residentCommands,
        Func<string, CancellationToken, Task> automaticCapture,
        Func<CancellationToken, Task> sessionEndingCapture,
        MainViewModel mainViewModel,
        RecoveryCardViewModel recoveryCardViewModel,
        MainWindow mainWindow,
        RecoveryCardWindow recoveryCardWindow,
        TrayIconService trayIcon,
        BestEffortAsyncCleanup cleanup)
    {
        this.startup = startup;
        this.repository = repository;
        this.recoveryCardFocus = recoveryCardFocus;
        this.residentCandidateFocus = residentCandidateFocus;
        this.residentLaunchCoordinator = residentLaunchCoordinator;
        this.residentCandidates = residentCandidates;
        this.residentCommands = residentCommands;
        this.automaticCapture = automaticCapture;
        this.sessionEndingCapture = sessionEndingCapture;
        MainViewModel = mainViewModel;
        RecoveryCardViewModel = recoveryCardViewModel;
        MainWindow = mainWindow;
        RecoveryCardWindow = recoveryCardWindow;
        TrayIcon = trayIcon;
        this.cleanup = cleanup;
        MainViewModel.ResidentCandidatesAvailable += OnResidentCandidatesAvailable;
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

    /// <summary>供组合测试验证真实命令接线共用的候选协调器。</summary>
    internal ResidentCandidateCoordinator ResidentCandidates => residentCandidates;

    /// <summary>供组合测试验证实际注册的完整常驻命令集合。</summary>
    internal ResidentAppCommandHandlerSet ResidentCommands => residentCommands;

    /// <summary>供组合测试验证自动与手动启动只引用唯一协调器。</summary>
    internal ResidentLaunchCoordinator ResidentLaunch => residentLaunchCoordinator;

    /// <summary>调用生产 scheduler 使用的同一自动捕获委托，供组合测试验证常驻发现隔离。</summary>
    internal Task RunAutomaticCaptureForTestsAsync(string reason, CancellationToken cancellationToken) =>
        automaticCapture(reason, cancellationToken);

    /// <summary>调用生产 session-ending 使用的同一检查点委托，供组合测试验证常驻发现隔离。</summary>
    internal Task RunSessionEndingCaptureForTestsAsync(CancellationToken cancellationToken) =>
        sessionEndingCapture(cancellationToken);

    /// <summary>使用正式当前用户数据目录创建真实服务图。</summary>
    public static Task<CompositionRoot> CreateAsync(
        AppDataPaths paths,
        Action requestExit,
        bool pauseAutomaticCapture,
        CancellationToken cancellationToken) =>
        CreateCoreAsync(
            paths, requestExit, createFixture: false, applyStartupRegistration: true,
            pauseAutomaticCapture, CreateResidentPlatformServices, cancellationToken);

#if DEBUG
    /// <summary>Debug 构建可在隔离数据根创建一份无敏感内容的冒烟现场。</summary>
    public static Task<CompositionRoot> CreateDebugAsync(
        AppDataPaths paths,
        Action requestExit,
        bool createFixture,
        bool runSmoke,
        bool pauseAutomaticCapture,
        CancellationToken cancellationToken) =>
        CreateCoreAsync(
            paths, requestExit, createFixture, applyStartupRegistration: false,
            pauseAutomaticCapture,
            path => CreateResidentPlatformServices(path, disableProcessRuntime: runSmoke),
            cancellationToken);
#endif

    /// <summary>在隔离数据根注入完整 fake 平台包，同时保留真实组合根、命令和所有权接线。</summary>
    internal static Task<CompositionRoot> CreateForTestsAsync(
        AppDataPaths paths,
        Action requestExit,
        ResidentPlatformServices resident,
        bool pauseAutomaticCapture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resident);
        return CreateCoreAsync(
            paths, requestExit, createFixture: false, applyStartupRegistration: false,
            pauseAutomaticCapture, _ => resident, cancellationToken);
    }

    /// <summary>启动工作区模块、会话关机检查点与轮询变化源。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(cleanup.IsComplete, this);
        await StartCoreAsync(
            startup,
            cleanup,
            residentLaunchCoordinator.Start,
            cancellationToken);
    }

    /// <summary>在既有启动阶段全部成功后建立非阻塞常驻批次；失败时绝不越过该边界。</summary>
    internal static async Task StartCoreAsync(
        CompositionStartupCoordinator startup,
        BestEffortAsyncCleanup cleanup,
        Action startResidentBatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(cleanup);
        ArgumentNullException.ThrowIfNull(startResidentBatch);
        await startup.StartAsync(cleanup, cancellationToken);
        startResidentBatch();
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
        MainViewModel.ResidentCandidatesAvailable -= OnResidentCandidatesAvailable;
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
        Func<AppDataPaths, ResidentPlatformServices> createResidentPlatformServices,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(requestExit);
        ArgumentNullException.ThrowIfNull(createResidentPlatformServices);
        paths.EnsureRootDirectoryExists();

        return await CompositionResourceOwner.BuildAsync(async ownership =>
        {
            var clock = new SystemClock();
            var diagnosticLog = ownership.Own(
                "diagnostic log",
                new RollingJsonLog(paths.LogDirectory),
                static log => log.DisposeAsync());
            var residentDiagnostics = ownership.Own(
                "resident diagnostics",
                new ResidentDiagnosticTracker(diagnosticLog, clock),
                static tracker => tracker.DrainAsync());
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
            var settingsStore = new JsonSettingsStore(paths, residentDiagnostics.ReportNormalization);
            var persistedSettings = await settingsStore.LoadAsync(cancellationToken);
            var startupRegistration = ApplyStartupRegistration(persistedSettings, applyStartupRegistration);
            var settingsCoordinator = ownership.Own(
                "settings",
                new SettingsCoordinator(settingsStore),
                static settings =>
                {
                    settings.Dispose();
                    return ValueTask.CompletedTask;
                });
            var resident = createResidentPlatformServices(paths);
            var residentCandidates = new ResidentCandidateCoordinator(
                resident.Discovery,
                settingsStore,
                settingsCoordinator,
                residentDiagnostics.ReportDiscovery);
            var residentCommands = ResidentAppCommandHandlerSet.Create(
                residentCandidates,
                settingsCoordinator,
                resident.ExecutablePolicy);
            var residentLaunchCoordinator = ownership.Own(
                "resident launch",
                new ResidentLaunchCoordinator(
                    settingsStore,
                    resident.LaunchSessionStore,
                    resident.LogonIdentity,
                    resident.ProcessRuntime,
                    resident.ExecutablePolicy,
                    clock,
                    diagnosticLog),
                static coordinator => coordinator.DisposeAsync(),
                CleanupPhase.Early);
            var repository = ownership.Own(
                "repository",
                new SqliteSceneRepository(paths),
                static repository =>
                {
                    repository.Dispose();
                    return ValueTask.CompletedTask;
                });
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
            var captureCoordinator = ownership.Own(
                "capture coordinator",
                new CaptureCoordinator(
                    captureSettings,
                    captureInventory,
                    new SceneFilter(captureSettings),
                    notifyingRepository,
                    clock),
                static coordinator =>
                {
                    coordinator.Dispose();
                    return ValueTask.CompletedTask;
                });
            Func<string, CancellationToken, Task> automaticCapture = captureCoordinator.SaveNowAsync;
            var scheduler = ownership.Own(
                "scheduler",
                new SnapshotScheduler(clock, automaticCapture),
                static scheduler => scheduler.DisposeAsync());
            var automaticCaptureGate = new AutomaticCaptureGate(
                pauseAutomaticCapture,
                pauseAutomaticCapture ? "检测到上次运行未正常结束，自动捕获已暂停。" : null);
            ModuleHost? moduleHost = null;
            InventoryFingerprintChangeSource? desktopChanges = null;
            WindowsSessionEvents? sessionEvents = null;
            var startup = new CompositionStartupCoordinator(
                token => moduleHost!.StartAsync(token),
                token => moduleHost!.StopAsync(token),
                () => sessionEvents!.Start(),
                () => sessionEvents!.Dispose(),
                token => desktopChanges!.StartAsync(token),
                () => desktopChanges!.DisposeAsync());
            // 两秒检测上限加既有十秒静止防抖，近似用户所见十秒静止语义。
            var ownedDesktopChanges = ownership.Own(
                "desktop changes",
                new InventoryFingerprintChangeSource(
                    rawInventory, clock, TimeSpan.FromSeconds(2), reportFailure: null, diagnosticLog),
                _ => startup.DisposeDesktopAsync());
            desktopChanges = ownedDesktopChanges;
            var module = new WorkspaceRecoveryModule(
                ownedDesktopChanges, scheduler, captureCoordinator, clock, automaticCaptureGate);
            var moduleState = CreateModuleStateComposition(module, diagnosticLog, clock);
            ownership.Own(
                "module event diagnostics",
                moduleState,
                static composition => composition.DrainDiagnosticsAsync());
            moduleHost = moduleState.Host;
            ownership.Own(
                "module host",
                startup,
                static coordinator => coordinator.StopModuleIfStartedAsync());
            Func<CancellationToken, Task> sessionEndingCapture = token => automaticCaptureGate.IsPaused
                ? Task.CompletedTask
                : captureCoordinator.SaveNowAsync("session-ending", token);
            sessionEvents = ownership.Own(
                "session events",
                new WindowsSessionEvents(sessionEndingCapture),
                _ => startup.DisposeSessionAsync());

            var commandBus = new InProcessCommandBus();
            commandBus.Register(new SaveSceneNowCommandHandler(
                captureInventory, captureCoordinator, residentCandidates, diagnosticLog, clock));
            residentCommands.Register(commandBus);
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
                commandBus.Register(new SetStartupEnabledCommandHandler(settingsCoordinator, startupRegistration));
            }
            commandBus.Register(new PersistExclusionCommandHandler(settingsCoordinator));

#if DEBUG
            if (createFixture)
            {
                await CreateFixtureIfEmptyAsync(repository, clock, cancellationToken);
            }
#endif

            var mainViewModel = ownership.Own(
                "main view model",
                new MainViewModel(
                    notifyingRepository, commandBus, settingsStore,
                    new WpfUiDispatcher(System.Windows.Threading.Dispatcher.CurrentDispatcher),
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
                    moduleState.EventBus,
                    module.Descriptor,
                    automaticCaptureGate,
                    new ResidentViewModelDependencies(
                        new WindowsExecutablePicker(),
                        new WindowsExecutableIconProvider(),
                        resident.ExecutablePolicy.Validate,
                        residentLaunchCoordinator.LaunchEnabledNowAsync)),
                static viewModel =>
                {
                    viewModel.Dispose();
                    return ValueTask.CompletedTask;
                });
            await mainViewModel.LoadAsync();
            var recoveryCardViewModel = ownership.Own(
                "recovery view model",
                new RecoveryCardViewModel(
                    commandBus, clock, persistedSettings.RecoveryCardDismissSeconds, failureHistoryStore),
                static viewModel =>
                {
                    viewModel.Dispose();
                    return ValueTask.CompletedTask;
                });
            var mainWindow = ownership.Own(
                "main window",
                new MainWindow(mainViewModel),
                static window => DispatcherCleanup.RunAsync(
                    window.Dispatcher,
                    window.CloseForExit));
            var recoveryCardWindow = ownership.Own(
                "recovery window",
                new RecoveryCardWindow(recoveryCardViewModel),
                static window => DispatcherCleanup.RunAsync(
                    window.Dispatcher,
                    window.CloseForExit));
            CompositionRoot? root = null;
            var recoveryCardFocus = new RecoveryCardFocusCoordinator(
                () => root?.ShowRecoveryCardForLatestSceneAsync() ?? Task.FromResult(false),
                recoveryCardWindow.FocusForKeyboard);
            var residentCandidateFocus = new ResidentCandidateFocusCoordinator(
                () => mainWindow.IsVisible,
                () => root?.ShowMainWindow(),
                mainWindow.FocusResidentCandidateConfirmation);
            var trayIcon = ownership.Own(
                "tray",
                new TrayIconService(
                    mainViewModel,
                    () => root?.ShowMainWindow(),
                    recoveryCardFocus.FocusAsync,
                    requestExit),
                static tray =>
                {
                    tray.Dispose();
                    return ValueTask.CompletedTask;
                });
            var cleanup = ownership.PrepareCleanup();
            root = new CompositionRoot(
                startup,
                repository,
                recoveryCardFocus,
                residentCandidateFocus,
                residentLaunchCoordinator,
                residentCandidates,
                residentCommands,
                automaticCapture,
                sessionEndingCapture,
                mainViewModel,
                recoveryCardViewModel,
                mainWindow,
                recoveryCardWindow,
                trayIcon,
                cleanup);
            return root;
        }).ConfigureAwait(false);
    }

    /// <summary>只在 settings/diagnostic 已建立后创建正式 Windows 平台边界。</summary>
    private static ResidentPlatformServices CreateResidentPlatformServices(AppDataPaths paths)
        => CreateResidentPlatformServices(paths, disableProcessRuntime: false);

    /// <summary>创建正式平台边界；仅 Debug smoke 可替换为零外部调用的禁用 runtime。</summary>
    private static ResidentPlatformServices CreateResidentPlatformServices(
        AppDataPaths paths,
        bool disableProcessRuntime)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var executablePolicy = new WindowsResidentExecutablePolicy();
        return new ResidentPlatformServices(
            executablePolicy,
            new WindowsLogonSessionIdentityProvider(),
            disableProcessRuntime
                ? new DisabledResidentProcessRuntime()
                : new WindowsResidentProcessRuntime(executablePolicy),
            WindowsResidentAppDiscovery.CreateDefault(),
            new JsonResidentLaunchSessionStore(paths));
    }

    /// <summary>仅手动保存发布的候选事件触发窗口边界；SceneSaved 和自动刷新不订阅此入口。</summary>
    private void OnResidentCandidatesAvailable(object? sender, EventArgs eventArgs) => residentCandidateFocus.Focus();

    /// <summary>创建共享同一事件总线的模块宿主和可清理诊断观察边界。</summary>
    internal static ModuleStateComposition CreateModuleStateComposition(
        IModule module,
        IDiagnosticLog diagnosticLog,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(diagnosticLog);
        ArgumentNullException.ThrowIfNull(clock);
        var eventBus = new InProcessEventBus();
        var diagnostics = new ModuleEventDiagnosticTracker(
            exception => ReportModuleEventFailureAsync(diagnosticLog, clock, exception));
        return new ModuleStateComposition(
            new ModuleHost([module], eventBus, diagnostics.Report),
            eventBus,
            diagnostics.DrainAsync);
    }

    /// <summary>在两秒边界内写入不含异常消息和用户数据的模块观察故障。</summary>
    private static async Task ReportModuleEventFailureAsync(
        IDiagnosticLog diagnosticLog,
        IClock clock,
        Exception exception)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var diagnosticEvent = new DiagnosticEvent(
                clock.UtcNow,
                DiagnosticLevel.Warning,
                "module-status",
                "模块状态观察者处理失败。",
                new Dictionary<string, object?>
                {
                    ["exceptionType"] = exception.GetType().FullName
                });
            // 防止接口实现返回 Task 前同步阻塞；迟到任务的异常另有观察边界。
            var writeTask = Task.Run(
                () => diagnosticLog.WriteAsync(diagnosticEvent, timeout.Token),
                CancellationToken.None);
            ObserveLateFailure(writeTask);
            await writeTask.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // 诊断日志是最终观察边界，任何失败都不得污染模块生命周期或退出清理。
        }
    }

    /// <summary>观察超时后迟到诊断任务的异常，避免形成未观察任务故障。</summary>
    private static void ObserveLateFailure(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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

/// <summary>跟踪同步诊断回调启动的全部有界任务，供组合根退出时统一封存并等待。</summary>
internal sealed class ModuleEventDiagnosticTracker
{
    private readonly object syncRoot = new();
    private readonly List<Task> pending = [];
    private readonly Func<Exception, Task> startDiagnosticTask;
    private bool sealedForCleanup;

    /// <summary>使用只负责启动单个有界诊断任务的工厂创建 tracker。</summary>
    internal ModuleEventDiagnosticTracker(Func<Exception, Task> startDiagnosticTask)
    {
        this.startDiagnosticTask = startDiagnosticTask ??
            throw new ArgumentNullException(nameof(startDiagnosticTask));
    }

    /// <summary>在同一同步边界内检查封存状态、创建并登记诊断任务。</summary>
    internal void Report(Exception exception)
    {
        lock (syncRoot)
        {
            if (sealedForCleanup)
            {
                return;
            }

            try
            {
                // Task.Run 与登记都在锁内；委托即使立即开始，Drain 也只能在引用可见后快照。
                var task = Task.Run(
                    () => startDiagnosticTask(exception),
                    CancellationToken.None);
                pending.Add(task);
            }
            catch
            {
                // 最终诊断任务工厂自身失败也不得污染模块生命周期。
            }
        }
    }

    /// <summary>首次调用时封存 tracker，并在锁外幂等等待封存前登记的全部任务。</summary>
    internal async ValueTask DrainAsync()
    {
        Task[] snapshot;
        lock (syncRoot)
        {
            sealedForCleanup = true;
            snapshot = [.. pending];
        }

        try
        {
            await Task.WhenAll(snapshot).ConfigureAwait(false);
        }
        catch
        {
            // 最终诊断边界不得让日志任务故障污染退出清理。
        }
    }
}

/// <summary>封装生产模块宿主、共享事件总线和对应诊断清理步骤。</summary>
internal sealed class ModuleStateComposition(
    ModuleHost host,
    InProcessEventBus eventBus,
    Func<ValueTask> drainDiagnosticsAsync)
{
    /// <summary>获取使用共享生产事件总线的模块宿主。</summary>
    internal ModuleHost Host { get; } = host;

    /// <summary>获取同时供模块宿主和状态 ViewModel 使用的生产事件总线。</summary>
    internal InProcessEventBus EventBus { get; } = eventBus;

    /// <summary>等待同步诊断接收器已经启动的全部有界任务。</summary>
    internal ValueTask DrainDiagnosticsAsync() => drainDiagnosticsAsync();
}
