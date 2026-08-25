using System.Collections.ObjectModel;
using DeskButler.Application.Commands;
using DeskButler.Core.Scenes;
using DeskButler.Core.Time;
using DeskButler.Core.Restore;
using DeskButler.Core.Diagnostics;
using DeskButler.Desktop.Hosting;
using System.IO;

namespace DeskButler.Desktop.ViewModels;

/// <summary>表示恢复卡片中的一个可勾选场景项目。</summary>
public sealed class RecoveryItemViewModel(SceneItem item, bool failureProtected = false) : ObservableObject
{
    private bool isSelected = !failureProtected;

    /// <summary>获取原始不可变场景项目。</summary>
    public SceneItem Item { get; } = item ?? throw new ArgumentNullException(nameof(item));

    /// <summary>获取便于用户识别的窗口标题。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Item.TitleHint)
        ? Path.GetFileNameWithoutExtension(Item.ExecutablePath)
        : Item.TitleHint;

    /// <summary>获取连续失败保护原因；未受该保护时为空。</summary>
    public string? ProtectionReason => failureProtected ? "连续失败 3 次，已默认取消；可手动重新勾选重试。" : null;

    /// <summary>获取该项显示时是否因连续失败保护而默认取消。</summary>
    internal bool WasFailureProtected => failureProtected;

    /// <summary>获取或设置本次恢复是否包含该项。</summary>
    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }
}

/// <summary>管理非自动恢复卡片的可见性、选择和显式操作。</summary>
public sealed class RecoveryCardViewModel : ObservableObject, IDisposable
{
    private readonly ICommandBus commands;
    private readonly IClock clock;
    private readonly TimeSpan dismissDelay;
    private readonly IFailureHistoryStore? failureHistoryStore;
    // 所有卡片动作共享此门，避免独立 ICommand 防重入边界互相穿透。
    private readonly SemaphoreSlim actionGate = new(1, 1);
    // 每次显示拥有独立代次；旧倒计时不得隐藏后来显示的新现场。
    private CancellationTokenSource? dismissSource;
    private SceneSnapshot? scene;
    private bool isVisible;
    private string? errorMessage;
    private string lastRestoreSummary = "尚未执行恢复";

    /// <summary>使用命令总线和可控时钟创建恢复卡片。</summary>
    public RecoveryCardViewModel(ICommandBus commands, IClock clock, int dismissSeconds)
        : this(commands, clock, dismissSeconds, null)
    {
    }

    /// <summary>使用失败历史加载边界创建可应用三次失败默认保护的恢复卡片。</summary>
    public RecoveryCardViewModel(
        ICommandBus commands,
        IClock clock,
        int dismissSeconds,
        IFailureHistoryStore? failureHistoryStore)
    {
        this.commands = commands ?? throw new ArgumentNullException(nameof(commands));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dismissSeconds, 0);
        dismissDelay = TimeSpan.FromSeconds(dismissSeconds);
        this.failureHistoryStore = failureHistoryStore;
        RestoreImmediatelyCommand = new AsyncCommand(RestoreImmediatelyAsync);
        RestoreSafelyCommand = new AsyncCommand(RestoreSafelyAsync);
        SkipCommand = new AsyncCommand(SkipAsync);
        ExcludePermanentlyCommand = new AsyncCommand(
            parameter => parameter is RecoveryItemViewModel item
                ? ExcludePermanentlyAsync(item)
                : Task.CompletedTask);
    }

    /// <summary>获取当前场景的可选择项目。</summary>
    public ObservableCollection<RecoveryItemViewModel> Items { get; } = [];

    /// <summary>获取卡片当前是否应显示。</summary>
    public bool IsVisible
    {
        get => isVisible;
        private set => SetProperty(ref isVisible, value);
    }

    /// <summary>获取最近一次卡片操作错误；成功重试后清空。</summary>
    public string? ErrorMessage
    {
        get => errorMessage;
        private set => SetProperty(ref errorMessage, value);
    }

    /// <summary>获取最近一次逐项恢复结果摘要，供卡片和辅助技术持续呈现。</summary>
    public string LastRestoreSummary
    {
        get => lastRestoreSummary;
        private set => SetProperty(ref lastRestoreSummary, value);
    }

    /// <summary>获取立即恢复命令。</summary>
    public AsyncCommand RestoreImmediatelyCommand { get; }

    /// <summary>获取安全恢复命令。</summary>
    public AsyncCommand RestoreSafelyCommand { get; }

    /// <summary>获取跳过命令。</summary>
    public AsyncCommand SkipCommand { get; }

    /// <summary>获取永久排除指定项目的命令。</summary>
    public AsyncCommand ExcludePermanentlyCommand { get; }

    /// <summary>展示指定现场并启动只负责隐藏的倒计时。</summary>
    public async Task ShowAsync(SceneSnapshot snapshot)
    {
        scene = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        var failureHistory = failureHistoryStore is null
            ? FailureHistory.Empty
            : await failureHistoryStore.LoadAsync(CancellationToken.None);
        ErrorMessage = null;
        CancelDismissTimer();
        Items.Clear();
        foreach (var item in snapshot.Items)
        {
            Items.Add(new RecoveryItemViewModel(item, failureHistory.CountFor(item.Id) >= 3));
        }

        IsVisible = true;
        StartDismissTimer();
    }

    /// <summary>显式请求普通恢复当前选中项目。</summary>
    public Task RestoreImmediatelyAsync() => RestoreAsync(safeMode: false);

    /// <summary>显式请求安全恢复当前选中项目。</summary>
    public Task RestoreSafelyAsync() => RestoreAsync(safeMode: true);

    /// <summary>跳过本次提示，仅隐藏并取消倒计时。</summary>
    public async Task SkipAsync()
    {
        await actionGate.WaitAsync();
        try
        {
            Hide();
        }
        finally
        {
            actionGate.Release();
        }
    }

    /// <summary>将项目路径永久加入排除设置，并从本次恢复选择中移除。</summary>
    public async Task ExcludePermanentlyAsync(RecoveryItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        // 先兑现用户意图；并发恢复读取选择时已无法带入此项。
        item.IsSelected = false;
        await actionGate.WaitAsync();
        try
        {
            await commands.SendAsync(new PersistExclusionCommand(item.Item.ExecutablePath), CancellationToken.None);
            ErrorMessage = null;
        }
        catch (Exception exception)
        {
            ErrorMessage = $"永久排除失败：{exception.Message}";
        }
        finally
        {
            actionGate.Release();
        }
    }

    /// <summary>取消任何未到期倒计时并释放资源。</summary>
    public void Dispose()
    {
        CancelDismissTimer();
        GC.SuppressFinalize(this);
    }

    /// <summary>在共享串行边界内发送恢复命令，仅成功后隐藏卡片。</summary>
    private async Task RestoreAsync(bool safeMode)
    {
        if (scene is null)
        {
            return;
        }

        // 用户开始恢复后旧期限即失效；慢命令不得在后台被旧计时器隐藏。
        CancelDismissTimer();
        await actionGate.WaitAsync();
        try
        {
            var currentScene = scene;
            var selectedIds = Items.Where(item => item.IsSelected).Select(item => item.Item.Id).ToArray();
            ErrorMessage = null;
            var explicitRetries = Items
                .Where(item => item.WasFailureProtected && item.IsSelected)
                .Select(item => item.Item.Id)
                .ToHashSet(StringComparer.Ordinal);
            var result = await commands.SendAsync(
                new RestoreSceneCommand(currentScene, selectedIds, safeMode)
                {
                    ExplicitFailureRetryItemIds = explicitRetries
                }, CancellationToken.None);
            result ??= new RestoreResult([]);
            LastRestoreSummary = RestoreResultSummary.Format(result);
            var needsRetry = result.Items.Any(
                item => item.Status is RestoreItemStatus.Failed or RestoreItemStatus.Cancelled);
            if (needsRetry)
            {
                ErrorMessage = LastRestoreSummary;
                IsVisible = true;
            }
            else
            {
                Hide();
            }
        }
        catch (Exception exception)
        {
            ErrorMessage = $"恢复失败：{exception.Message}";
            IsVisible = true;
            StartDismissTimer();
        }
        finally
        {
            actionGate.Release();
        }
    }

    /// <summary>为当前可见状态创建新的完整隐藏期限。</summary>
    private void StartDismissTimer()
    {
        CancelDismissTimer();
        dismissSource = new CancellationTokenSource();
        _ = DismissAfterDelayAsync(dismissSource);
    }

    /// <summary>等待固定时长后仅改变可见性；取消属于正常的代次切换或用户操作。</summary>
    private async Task DismissAfterDelayAsync(CancellationTokenSource source)
    {
        try
        {
            await clock.DelayAsync(dismissDelay, source.Token);
            if (ReferenceEquals(dismissSource, source))
            {
                IsVisible = false;
                dismissSource = null;
                source.Dispose();
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            // 用户操作或新卡片替换旧卡片时，旧计时器静默退出。
        }
    }

    /// <summary>隐藏卡片并终止当前倒计时。</summary>
    private void Hide()
    {
        IsVisible = false;
        CancelDismissTimer();
    }

    /// <summary>安全取消并释放当前隐藏倒计时。</summary>
    private void CancelDismissTimer()
    {
        var source = dismissSource;
        dismissSource = null;
        if (source is null)
        {
            return;
        }

        source.Cancel();
        source.Dispose();
    }
}
