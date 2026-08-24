using System.Collections.ObjectModel;
using DeskButler.Application.Commands;
using DeskButler.Core.Persistence;
using DeskButler.Core.Scenes;
using DeskButler.Core.Settings;
using DeskButler.Desktop.Hosting;
using System.Globalization;

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
    private bool isCapturePaused;
    private string statusText = "就绪";

    /// <summary>使用实际仓库、命令总线和设置存储创建主界面模型。</summary>
    public MainViewModel(ISceneRepository repository, ICommandBus commands, ISettingsStore settingsStore)
        : this(repository, commands, settingsStore, new InlineUiDispatcher())
    {
    }

    /// <summary>使用显式 UI 调度边界创建主界面模型。</summary>
    internal MainViewModel(
        ISceneRepository repository,
        ICommandBus commands,
        ISettingsStore settingsStore,
        IUiDispatcher dispatcher)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.commands = commands ?? throw new ArgumentNullException(nameof(commands));
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
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

    /// <summary>获取立即保存命令。</summary>
    public AsyncCommand SaveNowCommand { get; }

    /// <summary>获取暂停或继续命令。</summary>
    public AsyncCommand ToggleCaptureCommand { get; }

    /// <summary>获取恢复指定历史现场的命令。</summary>
    public AsyncCommand RestoreSceneCommand { get; }

    /// <summary>获取刷新历史命令。</summary>
    public AsyncCommand RefreshCommand { get; }

    /// <summary>加载设置与最近三份有效现场。</summary>
    public async Task LoadAsync()
    {
        var settings = await settingsStore.LoadAsync(CancellationToken.None);
        var scenes = await repository.GetRecentAsync(3, CancellationToken.None);
        IsCapturePaused = !settings.CaptureEnabled;
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
        await commands.SendAsync(
            new RestoreSceneCommand(scene.Scene, scene.Scene.Items.Select(item => item.Id).ToArray(), safeMode),
            CancellationToken.None);
        StatusText = "恢复请求已完成";
    }

    /// <summary>切换并持久化捕获暂停状态。</summary>
    public async Task ToggleCaptureAsync()
    {
        var enable = IsCapturePaused;
        await commands.SendAsync(new SetCaptureEnabledCommand(enable), CancellationToken.None);
        IsCapturePaused = !enable;
        StatusText = enable ? "已继续捕获" : "已暂停捕获";
    }

    /// <summary>解除自动保存通知，退出后不再排队 UI 工作。</summary>
    public void Dispose()
    {
        if (notifyingRepository is not null)
        {
            notifyingRepository.SceneSaved -= OnSceneSaved;
        }
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
