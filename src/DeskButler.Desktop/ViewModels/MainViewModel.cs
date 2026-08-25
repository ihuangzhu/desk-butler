using System.Collections.ObjectModel;
using DeskButler.Application.Commands;
using DeskButler.Core.Persistence;
using DeskButler.Core.Scenes;
using DeskButler.Core.Settings;
using DeskButler.Core.Restore;
using DeskButler.Desktop.Hosting;
using System.Globalization;
using DeskButler.Infrastructure.Windows.Startup;
using DeskButler.Application.Events;
using DeskButler.Application.Modules;
using DeskButler.Modules.WorkspaceRecovery;

namespace DeskButler.Desktop.ViewModels;

/// <summary>表示主窗口最近现场列表中的一行。</summary>
public sealed class SceneSummaryViewModel(SceneSnapshot scene)
{
    /// <summary>获取原始不可变现场。</summary>
    public SceneSnapshot Scene { get; } = scene ?? throw new ArgumentNullException(nameof(scene));

    /// <summary>获取本地时间显示文本。</summary>
    public string CapturedAtText => Scene.CapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    /// <summary>获取窗口数量摘要。</summary>
    public string ItemCountText => $"{Scene.Items.Count} 个窗口";
}

/// <summary>管理主窗口、托盘菜单和最近现场的共享状态。</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ISceneRepository repository;
    private readonly ICommandBus commands;
    private readonly ISettingsStore settingsStore;
    private readonly IUiDispatcher dispatcher;
    private readonly NotifyingSceneRepository? notifyingRepository;
    private readonly Func<CancellationToken, Task<string>> diagnosticPreviewLoader;
    private readonly IStartupRegistration? startupRegistration;
    private readonly IDisposable? moduleStatusSubscription;
    private readonly AutomaticCaptureGate? automaticCaptureGate;
    private bool isCapturePaused;
    private string statusText = "就绪";
    private string healthStatusText;
    private string diagnosticPreviewText = "点击“预览诊断内容”查看即将导出的脱敏类别。";
    private bool isStartupEnabled;
    private bool isStartupToggleEnabled = true;
    private string? startupErrorMessage;
    private string moduleStatusText = "模块状态正在初始化";

    /// <summary>使用实际仓库、命令总线和设置存储创建主界面模型。</summary>
    public MainViewModel(ISceneRepository repository, ICommandBus commands, ISettingsStore settingsStore)
        : this(repository, commands, settingsStore, new InlineUiDispatcher(), null, null)
    {
    }

    /// <summary>使用显式 UI 调度边界创建主界面模型。</summary>
    internal MainViewModel(
        ISceneRepository repository,
        ICommandBus commands,
        ISettingsStore settingsStore,
        IUiDispatcher dispatcher,
        string? healthWarning = null,
        Func<CancellationToken, Task<string>>? diagnosticPreviewLoader = null,
        IStartupRegistration? startupRegistration = null,
        IEventBus? eventBus = null,
        ModuleDescriptor? moduleDescriptor = null,
        AutomaticCaptureGate? automaticCaptureGate = null)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.commands = commands ?? throw new ArgumentNullException(nameof(commands));
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.diagnosticPreviewLoader = diagnosticPreviewLoader ?? (_ => Task.FromResult("当前没有可预览的诊断文件。"));
        this.startupRegistration = startupRegistration;
        this.automaticCaptureGate = automaticCaptureGate;
        if (eventBus is not null && moduleDescriptor is not null)
        {
            ModuleStatusText = $"{moduleDescriptor.DisplayName} {moduleDescriptor.Version} · 正在启动";
            moduleStatusSubscription = eventBus.Subscribe<ModuleStatusChanged>(
                "main-view-model", (status, _) => HandleModuleStatusAsync(moduleDescriptor, status));
        }
        healthStatusText = healthWarning ?? "数据库与本地服务运行正常";
        notifyingRepository = repository as NotifyingSceneRepository;
        if (notifyingRepository is not null)
        {
            notifyingRepository.SceneSaved += OnSceneSaved;
        }
        SaveNowCommand = new AsyncCommand(SaveNowAsync);
        ToggleCaptureCommand = new AsyncCommand(ToggleCaptureAsync);
        RestoreSceneCommand = new AsyncCommand(
            parameter => parameter is SceneSummaryViewModel scene
                ? RestoreSceneAsync(scene)
                : Task.CompletedTask);
        RefreshCommand = new AsyncCommand(LoadAsync);
        LoadDiagnosticsCommand = new AsyncCommand(LoadDiagnosticsAsync);
        ToggleStartupCommand = new AsyncCommand(ToggleStartupAsync, () => IsStartupToggleEnabled);
    }

    /// <summary>获取最新优先且最多三份的现场历史。</summary>
    public ObservableCollection<SceneSummaryViewModel> RecentScenes { get; } = [];

    /// <summary>获取设置中按路径排序的永久排除列表。</summary>
    public ObservableCollection<string> ExcludedExecutablePaths { get; } = [];

    /// <summary>获取捕获当前是否暂停。</summary>
    public bool IsCapturePaused
    {
        get => isCapturePaused;
        private set
        {
            if (SetProperty(ref isCapturePaused, value))
            {
                OnPropertyChanged(nameof(CaptureToggleText));
            }
        }
    }

    /// <summary>获取托盘暂停切换文案。</summary>
    public string CaptureToggleText => IsCapturePaused ? "继续捕获" : "暂停捕获";

    /// <summary>获取最近一次用户操作状态。</summary>
    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    /// <summary>获取数据库回退或正常运行的用户可见健康状态。</summary>
    public string HealthStatusText
    {
        get => healthStatusText;
        private set => SetProperty(ref healthStatusText, value);
    }

    /// <summary>获取写入 ZIP 前的脱敏诊断预览。</summary>
    public string DiagnosticPreviewText
    {
        get => diagnosticPreviewText;
        private set => SetProperty(ref diagnosticPreviewText, value);
    }

    /// <summary>获取当前权威登录启动注册状态。</summary>
    public bool IsStartupEnabled
    {
        get => isStartupEnabled;
        private set => SetProperty(ref isStartupEnabled, value);
    }

    /// <summary>获取登录启动开关的实际状态是否仍可核实并允许再次操作。</summary>
    public bool IsStartupToggleEnabled
    {
        get => isStartupToggleEnabled;
        private set
        {
            if (SetProperty(ref isStartupToggleEnabled, value))
            {
                ToggleStartupCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>获取最近一次登录启动切换错误。</summary>
    public string? StartupErrorMessage
    {
        get => startupErrorMessage;
        private set => SetProperty(ref startupErrorMessage, value);
    }

    /// <summary>获取真实模块描述与最近生命周期状态。</summary>
    public string ModuleStatusText
    {
        get => moduleStatusText;
        private set => SetProperty(ref moduleStatusText, value);
    }

    /// <summary>获取立即保存命令。</summary>
    public AsyncCommand SaveNowCommand { get; }

    /// <summary>获取暂停或继续命令。</summary>
    public AsyncCommand ToggleCaptureCommand { get; }

    /// <summary>获取恢复指定历史现场的命令。</summary>
    public AsyncCommand RestoreSceneCommand { get; }

    /// <summary>获取刷新历史命令。</summary>
    public AsyncCommand RefreshCommand { get; }

    /// <summary>获取加载诊断包脱敏预览的命令。</summary>
    public AsyncCommand LoadDiagnosticsCommand { get; }

    /// <summary>获取切换登录启动的键盘可执行命令。</summary>
    public AsyncCommand ToggleStartupCommand { get; }

    /// <summary>加载设置与最近三份有效现场。</summary>
    public async Task LoadAsync()
    {
        var settings = await settingsStore.LoadAsync(CancellationToken.None);
        var scenes = await repository.GetRecentAsync(3, CancellationToken.None);
        IsCapturePaused = !settings.CaptureEnabled || (automaticCaptureGate?.IsPaused ?? false);
        if (automaticCaptureGate?.IsPaused == true)
        {
            StatusText = automaticCaptureGate.PauseReason ?? "自动捕获因安全模式暂停";
        }
        IsStartupEnabled = startupRegistration?.IsEnabled ?? settings.StartupEnabled;
        ExcludedExecutablePaths.Clear();
        foreach (var path in settings.ExcludedExecutablePaths.Order(StringComparer.OrdinalIgnoreCase))
        {
            ExcludedExecutablePaths.Add(path);
        }

        RecentScenes.Clear();
        foreach (var scene in scenes)
        {
            RecentScenes.Add(new SceneSummaryViewModel(scene));
        }
    }

    /// <summary>要求捕获协调器立即保存现场并刷新历史。</summary>
    public async Task SaveNowAsync()
    {
        await commands.SendAsync(new SaveSceneNowCommand(), CancellationToken.None);
        await LoadAsync();
        StatusText = "现场已保存";
    }

    /// <summary>恢复用户明确选中的历史现场。</summary>
    public async Task RestoreSceneAsync(SceneSummaryViewModel scene, bool safeMode = false)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var result = await commands.SendAsync(
            new RestoreSceneCommand(scene.Scene, scene.Scene.Items.Select(item => item.Id).ToArray(), safeMode),
            CancellationToken.None);
        StatusText = RestoreResultSummary.Format(result ?? new RestoreResult([]));
    }

    /// <summary>切换并持久化捕获暂停状态。</summary>
    public async Task ToggleCaptureAsync()
    {
        var enable = IsCapturePaused;
        await commands.SendAsync(new SetCaptureEnabledCommand(enable), CancellationToken.None);
        IsCapturePaused = !enable;
        StatusText = enable ? "已继续捕获" : "已暂停捕获";
    }

    /// <summary>切换登录启动；失败时重新读取权威注册状态并显示可理解错误。</summary>
    public async Task ToggleStartupAsync()
    {
        try
        {
            IsStartupEnabled = await commands.SendAsync(
                new SetStartupEnabledCommand(!IsStartupEnabled), CancellationToken.None);
            IsStartupToggleEnabled = true;
            StartupErrorMessage = null;
            StatusText = IsStartupEnabled ? "已启用登录启动" : "已禁用登录启动";
        }
        catch (Exception exception)
        {
            try
            {
                var settings = await settingsStore.LoadAsync(CancellationToken.None);
                var actualRegistration = startupRegistration?.IsEnabled;
                IsStartupEnabled = actualRegistration ?? settings.StartupEnabled;
                IsStartupToggleEnabled = actualRegistration is not null && actualRegistration == settings.StartupEnabled;
                StartupErrorMessage = IsStartupToggleEnabled
                    ? $"登录启动设置失败：{exception.Message}"
                    : $"登录启动设置失败且无法核实实际状态：{exception.Message}";
            }
            catch (Exception)
            {
                IsStartupToggleEnabled = false;
                StartupErrorMessage = $"登录启动设置失败且无法核实实际状态：{exception.Message}";
            }

            StatusText = StartupErrorMessage;
        }
    }

    /// <summary>加载白名单诊断文件的脱敏预览，不在此步骤创建或上传 ZIP。</summary>
    public async Task LoadDiagnosticsAsync()
    {
        try
        {
            DiagnosticPreviewText = await diagnosticPreviewLoader(CancellationToken.None);
        }
        catch (Exception exception)
        {
            DiagnosticPreviewText = $"诊断预览失败：{exception.Message}";
            HealthStatusText = "诊断服务需要关注";
        }
    }

    /// <summary>解除自动保存通知，退出后不再排队 UI 工作。</summary>
    public void Dispose()
    {
        moduleStatusSubscription?.Dispose();
        if (notifyingRepository is not null)
        {
            notifyingRepository.SceneSaved -= OnSceneSaved;
        }
    }

    /// <summary>把共享事件总线的模块状态投影回 UI 线程。</summary>
    private Task HandleModuleStatusAsync(ModuleDescriptor descriptor, ModuleStatusChanged status)
    {
        if (!StringComparer.Ordinal.Equals(descriptor.Id, status.ModuleId))
        {
            return Task.CompletedTask;
        }

        dispatcher.Post(() =>
        {
            ModuleStatusText = status.State switch
            {
                ModuleRunState.Running => $"{descriptor.DisplayName} {descriptor.Version} · 运行中",
                ModuleRunState.Stopped => $"{descriptor.DisplayName} {descriptor.Version} · 已停止",
                _ => $"{descriptor.DisplayName} {descriptor.Version} · 失败：{status.ErrorMessage}"
            };
            return Task.CompletedTask;
        });
        return Task.CompletedTask;
    }

    /// <summary>将后台保存通知切回 UI Dispatcher，并隔离刷新故障。</summary>
    private void OnSceneSaved(object? sender, EventArgs eventArgs)
    {
        dispatcher.Post(async () =>
        {
            try
            {
                await LoadAsync();
            }
            catch (Exception exception)
            {
                StatusText = $"现场列表刷新失败：{exception.Message}";
            }
        });
    }
}
