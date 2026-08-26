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
using DeskButler.Core.ResidentApps;
using DeskButler.Modules.WorkspaceRecovery.Capture;
using DeskButler.Modules.WorkspaceRecovery;
using System.IO;

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

/// <summary>汇总常驻页面的可替换桌面依赖，保持旧 MainViewModel 构造调用兼容。</summary>
public sealed class ResidentViewModelDependencies(
    IExecutablePicker picker,
    IExecutableIconProvider iconProvider,
    Func<string, ResidentExecutableValidation> validateExecutable,
    Func<CancellationToken, Task> launchEnabledNowAsync)
{
    /// <summary>获取选择可执行文件的 UI 边界。</summary>
    public IExecutablePicker Picker { get; } = picker ?? throw new ArgumentNullException(nameof(picker));

    /// <summary>获取不持有文件句柄的图标边界。</summary>
    public IExecutableIconProvider IconProvider { get; } = iconProvider ?? throw new ArgumentNullException(nameof(iconProvider));

    /// <summary>获取用于展示路径健康状态的预检边界；最终策略仍由命令处理器负责。</summary>
    public Func<string, ResidentExecutableValidation> ValidateExecutable { get; } =
        validateExecutable ?? throw new ArgumentNullException(nameof(validateExecutable));

    /// <summary>获取 Task 8 已建立协调器的手动启动委托。</summary>
    public Func<CancellationToken, Task> LaunchEnabledNowAsync { get; } =
        launchEnabledNowAsync ?? throw new ArgumentNullException(nameof(launchEnabledNowAsync));
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
    private readonly ResidentViewModelDependencies residentDependencies;
    private bool isCapturePaused;
    private string statusText = "就绪";
    private string healthStatusText;
    private string diagnosticPreviewText = "点击“预览诊断内容”查看即将导出的脱敏类别。";
    private bool isStartupEnabled;
    private bool isStartupToggleEnabled = true;
    private string? startupErrorMessage;
    private string moduleStatusText = "模块状态正在初始化";
    private bool residentApplicationsEnabled;
    private long residentCandidateGeneration;

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
        AutomaticCaptureGate? automaticCaptureGate = null,
        ResidentViewModelDependencies? residentDependencies = null)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.commands = commands ?? throw new ArgumentNullException(nameof(commands));
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.diagnosticPreviewLoader = diagnosticPreviewLoader ?? (_ => Task.FromResult("当前没有可预览的诊断文件。"));
        this.startupRegistration = startupRegistration;
        this.automaticCaptureGate = automaticCaptureGate;
        this.residentDependencies = residentDependencies ?? new ResidentViewModelDependencies(
            new NullExecutablePicker(),
            new FallbackExecutableIconProvider(),
            path => new ResidentExecutableValidation(false, null, ResidentExecutableRejection.ValidationFailed),
            _ => Task.CompletedTask);
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
        ConfirmResidentCandidatesCommand = new AsyncCommand(
            ConfirmResidentCandidatesAsync,
            CanConfirmResidentCandidates);
        DismissResidentCandidatesCommand = new AsyncCommand(DismissResidentCandidatesAsync, () => HasResidentCandidates);
        FindResidentCandidatesCommand = new AsyncCommand(FindResidentCandidatesAsync);
        AddResidentApplicationCommand = new AsyncCommand(AddResidentApplicationAsync);
        LaunchResidentsNowCommand = new AsyncCommand(LaunchResidentsNowAsync);
        ToggleResidentApplicationsCommand = new AsyncCommand(
            () => SetResidentApplicationsEnabledAsync(!ResidentApplicationsEnabled));
    }

    /// <summary>获取最新优先且最多三份的现场历史。</summary>
    public ObservableCollection<SceneSummaryViewModel> RecentScenes { get; } = [];

    /// <summary>获取设置中按路径排序的永久排除列表。</summary>
    public ObservableCollection<string> ExcludedExecutablePaths { get; } = [];

    /// <summary>获取等待用户确认的当前发现代候选。</summary>
    public ObservableCollection<ResidentCandidateViewModel> ResidentCandidates { get; } = [];

    /// <summary>获取按已保存顺序排列的常驻应用管理条目。</summary>
    public ObservableCollection<ResidentApplicationViewModel> ResidentApplications { get; } = [];

    /// <summary>获取当前是否存在等待确认的候选。</summary>
    public bool HasResidentCandidates => ResidentCandidates.Count > 0;

    /// <summary>获取登录后自动常驻总开关的当前设置快照。</summary>
    public bool ResidentApplicationsEnabled
    {
        get => residentApplicationsEnabled;
        private set => SetProperty(ref residentApplicationsEnabled, value);
    }

    /// <summary>仅由手动保存发现非空当前候选时触发，供后续托盘交互订阅。</summary>
    public event EventHandler? ResidentCandidatesAvailable;

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

    /// <summary>获取确认当前候选代次的命令。</summary>
    public AsyncCommand ConfirmResidentCandidatesCommand { get; }

    /// <summary>获取忽略当前候选代次的命令。</summary>
    public AsyncCommand DismissResidentCandidatesCommand { get; }

    /// <summary>获取不保存现场的独立常驻查找命令。</summary>
    public AsyncCommand FindResidentCandidatesCommand { get; }

    /// <summary>获取浏览并新增常驻应用的命令。</summary>
    public AsyncCommand AddResidentApplicationCommand { get; }

    /// <summary>获取委托 Task 8 协调器立即启动已启用应用的命令。</summary>
    public AsyncCommand LaunchResidentsNowCommand { get; }

    /// <summary>获取切换常驻总开关的命令。</summary>
    public AsyncCommand ToggleResidentApplicationsCommand { get; }

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
        ApplyResidentSettings(settings);

        RecentScenes.Clear();
        foreach (var scene in scenes)
        {
            RecentScenes.Add(new SceneSummaryViewModel(scene));
        }
    }

    /// <summary>要求捕获协调器立即保存现场并刷新历史。</summary>
    public async Task SaveNowAsync()
    {
        var result = await commands.SendAsync(new SaveSceneNowCommand(), CancellationToken.None);
        var publishedCurrentGeneration = PublishResidentCandidates(result.Discovery);
        StatusText = FormatManualSaveStatus(result);
        // 只有手动保存的当前代非空候选可以请求托盘引导；后台 SceneSaved 和 Find 都不能触发。
        if (publishedCurrentGeneration && !result.Discovery.DiscoveryFailed &&
            result.Discovery.Candidates.Count > 0 && residentCandidateGeneration == result.Discovery.Generation)
        {
            ResidentCandidatesAvailable?.Invoke(this, EventArgs.Empty);
        }

        try
        {
            await ReloadRecentScenesAsync();
        }
        catch (Exception)
        {
            // 最近现场刷新是保存后的尽力补偿；不得覆盖已经发布的常驻结果、文案或事件。
        }
    }

    /// <summary>不保存当前现场，直接请求一轮常驻候选发现。</summary>
    public async Task FindResidentCandidatesAsync()
    {
        var batch = await commands.SendAsync(new FindResidentCandidatesCommand(), CancellationToken.None);
        PublishResidentCandidates(batch);
        StatusText = batch.DiscoveryFailed ? "常驻应用发现失败" : "已完成常驻应用查找";
    }

    /// <summary>确认当前 UI 代次；成功仅清空同代候选并重新加载设置。</summary>
    public Task ConfirmResidentCandidatesAsync() =>
        ConfirmResidentCandidatesAsync(
            residentCandidateGeneration,
            ResidentCandidates
                .Where(candidate => candidate.IsSelected)
                .Select(candidate => candidate.ToSelection())
                .ToArray());

    /// <summary>确认指定代次的选择快照，迟到结果不能污染已经发布的新代候选。</summary>
    public async Task ConfirmResidentCandidatesAsync(
        long generation,
        IReadOnlyList<ResidentCandidateSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);
        if (generation != residentCandidateGeneration)
        {
            return;
        }

        // ICommand 以外的直接调用也必须遵守同一确认契约，不能向 handler 发送空入口选择。
        if (!CanConfirmResidentCandidates() || !AreConfirmSelectionsValid(selections))
        {
            return;
        }

        var confirmed = await commands.SendAsync(
            new ConfirmResidentCandidatesCommand(generation, selections),
            CancellationToken.None);
        if (!confirmed || generation != residentCandidateGeneration)
        {
            return;
        }

        await ReloadResidentSettingsAsync();
        if (generation != residentCandidateGeneration)
        {
            return;
        }

        ClearResidentCandidates();
        StatusText = "已保存常驻应用设置";
    }

    /// <summary>忽略当前代候选；过期代次不改写新候选。</summary>
    public async Task DismissResidentCandidatesAsync()
    {
        var generation = residentCandidateGeneration;
        if (!HasResidentCandidates)
        {
            return;
        }

        var dismissed = await commands.SendAsync(
            new DismissResidentCandidatesCommand(generation),
            CancellationToken.None);
        if (dismissed && generation == residentCandidateGeneration)
        {
            ClearResidentCandidates();
            StatusText = "已忽略本次常驻应用候选";
        }
    }

    /// <summary>通过 Windows 选择器新增应用；取消时不发送命令也不改变列表。</summary>
    public async Task AddResidentApplicationAsync()
    {
        var launchPath = await residentDependencies.Picker.PickAsync(CancellationToken.None);
        if (string.IsNullOrWhiteSpace(launchPath))
        {
            return;
        }

        var displayName = Path.GetFileNameWithoutExtension(launchPath);
        var result = await commands.SendAsync(
            new AddResidentApplicationCommand(launchPath, displayName),
            CancellationToken.None);
        ApplyResidentMutation(result);
    }

    /// <summary>只调用复用的 Task 8 手动启动委托；不发送命令且不创建第二协调器。</summary>
    public Task LaunchResidentsNowAsync() => residentDependencies.LaunchEnabledNowAsync(CancellationToken.None);

    /// <summary>通过类型化命令切换总开关，并以返回快照刷新绑定状态。</summary>
    public async Task SetResidentApplicationsEnabledAsync(bool enabled)
    {
        var result = await commands.SendAsync(
            new SetResidentApplicationsEnabledCommand(enabled),
            CancellationToken.None);
        ApplyResidentMutation(result);
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

    /// <summary>将设置中的常驻总开关和条目快照替换为新的可绑定集合。</summary>
    private void ApplyResidentSettings(ButlerSettings settings)
    {
        ResidentApplicationsEnabled = settings.ResidentApplicationsEnabled;
        ResidentApplications.Clear();
        foreach (var application in settings.ResidentApplications.OrderBy(application => application.LaunchOrder))
        {
            ResidentApplications.Add(new ResidentApplicationViewModel(
                application,
                residentDependencies.Picker,
                residentDependencies.IconProvider,
                residentDependencies.ValidateExecutable,
                SetResidentApplicationEnabledAsync,
                RemoveResidentApplicationAsync,
                MoveResidentApplicationAsync,
                ReplaceResidentApplicationPathAsync));
        }
    }

    /// <summary>只读取并投影常驻设置，避免候选确认和手动保存依赖场景仓库。</summary>
    private async Task ReloadResidentSettingsAsync()
    {
        var settings = await settingsStore.LoadAsync(CancellationToken.None);
        ApplyResidentSettings(settings);
    }

    /// <summary>只读取并更新最近现场列表，供不发 SceneSaved 通知的手动保存结果补偿刷新。</summary>
    private async Task ReloadRecentScenesAsync()
    {
        var scenes = await repository.GetRecentAsync(3, CancellationToken.None);
        RecentScenes.Clear();
        foreach (var scene in scenes)
        {
            RecentScenes.Add(new SceneSummaryViewModel(scene));
        }
    }

    /// <summary>以命令处理器返回的完整快照刷新列表，避免条目 setter 绕开设置事务。</summary>
    private void ApplyResidentMutation(ResidentSettingsMutationResult result)
    {
        ResidentApplicationsEnabled = result.ResidentApplicationsEnabled;
        ResidentApplications.Clear();
        foreach (var application in result.Applications.OrderBy(application => application.LaunchOrder))
        {
            ResidentApplications.Add(new ResidentApplicationViewModel(
                application,
                residentDependencies.Picker,
                residentDependencies.IconProvider,
                residentDependencies.ValidateExecutable,
                SetResidentApplicationEnabledAsync,
                RemoveResidentApplicationAsync,
                MoveResidentApplicationAsync,
                ReplaceResidentApplicationPathAsync));
        }
        StatusText = result.Error == ResidentSettingsError.None
            ? "已更新常驻应用设置"
            : MapResidentSettingsError(result.Error);
    }

    /// <summary>把条目启停请求交给父级类型化命令，再应用权威返回快照。</summary>
    private async Task SetResidentApplicationEnabledAsync(ResidentApplicationViewModel application, bool enabled)
    {
        var result = await commands.SendAsync(
            new SetResidentApplicationEnabledCommand(application.LaunchPath, enabled),
            CancellationToken.None);
        ApplyResidentMutation(result);
    }

    /// <summary>把条目删除请求交给父级类型化命令。</summary>
    private async Task RemoveResidentApplicationAsync(ResidentApplicationViewModel application)
    {
        var result = await commands.SendAsync(
            new RemoveResidentApplicationCommand(application.LaunchPath),
            CancellationToken.None);
        ApplyResidentMutation(result);
    }

    /// <summary>把相邻移动请求交给父级类型化命令。</summary>
    private async Task MoveResidentApplicationAsync(ResidentApplicationViewModel application, int offset)
    {
        var result = await commands.SendAsync(
            new MoveResidentApplicationCommand(application.LaunchPath, offset),
            CancellationToken.None);
        ApplyResidentMutation(result);
    }

    /// <summary>把浏览得到的新入口交给最终策略所在的类型化替换命令。</summary>
    private async Task ReplaceResidentApplicationPathAsync(ResidentApplicationViewModel application, string newLaunchPath)
    {
        var result = await commands.SendAsync(
            new ReplaceResidentApplicationPathCommand(application.LaunchPath, newLaunchPath),
            CancellationToken.None);
        ApplyResidentMutation(result);
    }

    /// <summary>发布新发现代次并更新确认、忽略命令的可执行状态。</summary>
    private bool PublishResidentCandidates(ResidentDiscoveryBatch batch)
    {
        // 发现器 generation 全局单调；迟到的手动结果不得覆盖已显示的新代候选。
        if (batch.Generation < residentCandidateGeneration)
        {
            return false;
        }

        residentCandidateGeneration = batch.Generation;
        ResidentCandidates.Clear();
        if (!batch.DiscoveryFailed)
        {
            foreach (var candidate in batch.Candidates)
            {
                ResidentCandidates.Add(new ResidentCandidateViewModel(
                    candidate,
                    batch.Generation,
                    residentDependencies.Picker,
                    residentDependencies.IconProvider,
                    OnResidentCandidateChanged));
            }
        }

        OnPropertyChanged(nameof(HasResidentCandidates));
        ConfirmResidentCandidatesCommand.RaiseCanExecuteChanged();
        DismissResidentCandidatesCommand.RaiseCanExecuteChanged();
        return true;
    }

    /// <summary>清空当前 UI 代次的候选，但保留代次用于拒绝迟到确认。</summary>
    private void ClearResidentCandidates()
    {
        ResidentCandidates.Clear();
        OnPropertyChanged(nameof(HasResidentCandidates));
        ConfirmResidentCandidatesCommand.RaiseCanExecuteChanged();
        DismissResidentCandidatesCommand.RaiseCanExecuteChanged();
    }

    /// <summary>候选勾选或入口草稿变化时刷新确认命令状态。</summary>
    private void OnResidentCandidateChanged() => ConfirmResidentCandidatesCommand.RaiseCanExecuteChanged();

    /// <summary>确认需至少选择一项，且每个已选候选都具备非空启动入口。</summary>
    private bool CanConfirmResidentCandidates()
    {
        var selected = ResidentCandidates.Where(candidate => candidate.IsSelected).ToArray();
        return selected.Length > 0 && selected.All(candidate => candidate.CanConfirm);
    }

    /// <summary>防御性验证准备发送给命令处理器的快照，只允许已选且入口完整的项目。</summary>
    private static bool AreConfirmSelectionsValid(IReadOnlyList<ResidentCandidateSelection> selections) =>
        selections.Count > 0 && selections.All(selection =>
            selection.IsSelected && !string.IsNullOrWhiteSpace(selection.FinalLaunchPath));

    /// <summary>精确映射手动保存与发现工作流的五种用户可见结果。</summary>
    private static string FormatManualSaveStatus(ManualSaveResult result)
    {
        if (result.Discovery.DiscoveryFailed)
        {
            return "常驻应用发现失败";
        }

        if (result.Capture.SnapshotSaved)
        {
            return "现场已保存";
        }

        return result.Capture.SkipReason switch
        {
            CaptureSkipReason.Disabled => "捕获已暂停，仍完成常驻查找",
            CaptureSkipReason.Failed => "现场保存失败，仍完成常驻查找",
            _ => "现场未变化"
        };
    }

    /// <summary>将列表处理器的稳定错误枚举转换为不含底层异常的 UI 文案。</summary>
    private static string MapResidentSettingsError(ResidentSettingsError error) => error switch
    {
        ResidentSettingsError.ExecutablePathRejected => "启动路径未通过安全验证",
        ResidentSettingsError.DuplicateLaunchPath => "启动路径已存在",
        ResidentSettingsError.KnownProcessPathConflict => "识别路径与现有应用冲突",
        ResidentSettingsError.EntryNotFound => "常驻应用已不存在",
        ResidentSettingsError.InvalidMoveOffset => "常驻应用移动请求无效",
        _ => "常驻应用设置更新失败"
    };

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

    /// <summary>兼容旧构造入口的空选择器；不会打开窗口或产生设置变更。</summary>
    private sealed class NullExecutablePicker : IExecutablePicker
    {
        /// <inheritdoc />
        public Task<string?> PickAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
