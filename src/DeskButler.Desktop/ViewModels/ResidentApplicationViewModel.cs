using System.Windows.Media;
using DeskButler.Core.ResidentApps;
using DeskButler.Desktop.Hosting;

namespace DeskButler.Desktop.ViewModels;

/// <summary>把已保存常驻条目投影为不直接写设置的可绑定管理项。</summary>
public sealed class ResidentApplicationViewModel : ObservableObject
{
    private readonly IExecutablePicker picker;
    private readonly IExecutableIconProvider iconProvider;
    private readonly Func<string, ResidentExecutableValidation> validateExecutable;
    private readonly Func<ResidentApplicationViewModel, bool, Task> setEnabledAsync;
    private readonly Func<ResidentApplicationViewModel, Task> removeAsync;
    private readonly Func<ResidentApplicationViewModel, int, Task> moveAsync;
    private readonly Func<ResidentApplicationViewModel, string, Task> replacePathAsync;
    private readonly ResidentExecutableValidation validation;
    private ImageSource? icon;

    /// <summary>创建条目视图模型；所有更改请求均回交父级发送类型化命令。</summary>
    public ResidentApplicationViewModel(
        ResidentApplication application,
        IExecutablePicker picker,
        IExecutableIconProvider iconProvider,
        Func<string, ResidentExecutableValidation> validateExecutable,
        Func<ResidentApplicationViewModel, bool, Task> setEnabledAsync,
        Func<ResidentApplicationViewModel, Task> removeAsync,
        Func<ResidentApplicationViewModel, int, Task> moveAsync,
        Func<ResidentApplicationViewModel, string, Task> replacePathAsync)
    {
        Application = application ?? throw new ArgumentNullException(nameof(application));
        this.picker = picker ?? throw new ArgumentNullException(nameof(picker));
        this.iconProvider = iconProvider ?? throw new ArgumentNullException(nameof(iconProvider));
        this.validateExecutable = validateExecutable ?? throw new ArgumentNullException(nameof(validateExecutable));
        this.setEnabledAsync = setEnabledAsync ?? throw new ArgumentNullException(nameof(setEnabledAsync));
        this.removeAsync = removeAsync ?? throw new ArgumentNullException(nameof(removeAsync));
        this.moveAsync = moveAsync ?? throw new ArgumentNullException(nameof(moveAsync));
        this.replacePathAsync = replacePathAsync ?? throw new ArgumentNullException(nameof(replacePathAsync));
        validation = this.validateExecutable(Application.LaunchPath);
        // 图标提取是同步文件/Shell I/O，只允许策略接受后的正规化路径越过该边界。
        icon = validation.IsAllowed && !string.IsNullOrWhiteSpace(validation.NormalizedPath)
            ? iconProvider.GetIcon(validation.NormalizedPath)
            : null;
        EnableCommand = new AsyncCommand(
            parameter => TryGetEnabledState(parameter, out var enabled) ? SetEnabledAsync(enabled) : Task.CompletedTask,
            parameter => TryGetEnabledState(parameter, out var enabled) && (enabled ? CanEnable : IsEnabled));
        RemoveCommand = new AsyncCommand(RemoveAsync);
        MoveUpCommand = new AsyncCommand(() => MoveAsync(-1));
        MoveDownCommand = new AsyncCommand(() => MoveAsync(1));
        ReplacePathCommand = new AsyncCommand(BrowseReplacementPathAsync);
    }

    /// <summary>获取本条目对应的不可变设置快照。</summary>
    public ResidentApplication Application { get; }

    /// <summary>获取启动入口；路径替换后由父级刷新整个设置快照。</summary>
    public string LaunchPath => Application.LaunchPath;

    /// <summary>获取显示名称。</summary>
    public string DisplayName => Application.DisplayName;

    /// <summary>获取当前启用状态；不允许 setter 绕过父级类型化命令。</summary>
    public bool IsEnabled => Application.Enabled;

    /// <summary>获取当前列表顺序。</summary>
    public int LaunchOrder => Application.LaunchOrder;

    /// <summary>获取路径是否可安全启用；最终安全校验仍由命令处理器执行。</summary>
    public bool CanEnable => validation.IsAllowed && !string.IsNullOrWhiteSpace(validation.NormalizedPath);

    /// <summary>获取不泄漏底层异常的路径状态说明。</summary>
    public string PathStatusText => validation.Reason switch
    {
        ResidentExecutableRejection.None when CanEnable => "启动路径可用",
        ResidentExecutableRejection.FileNotFound => "启动路径不存在",
        ResidentExecutableRejection.AccessDenied or ResidentExecutableRejection.ValidationFailed => "启动路径无法访问",
        _ => "启动路径已被拒绝"
    };

    /// <summary>获取条目的内存图标。</summary>
    public ImageSource? Icon
    {
        get => icon;
        private set => SetProperty(ref icon, value);
    }

    /// <summary>获取启停命令；启用受 CanEnable 约束，停用始终可用。</summary>
    public AsyncCommand EnableCommand { get; }

    /// <summary>获取删除命令。</summary>
    public AsyncCommand RemoveCommand { get; }

    /// <summary>获取向前移动命令。</summary>
    public AsyncCommand MoveUpCommand { get; }

    /// <summary>获取向后移动命令。</summary>
    public AsyncCommand MoveDownCommand { get; }

    /// <summary>获取替换启动路径命令。</summary>
    public AsyncCommand ReplacePathCommand { get; }

    /// <summary>请求父级通过类型化命令更新启停状态，不在属性 setter 中写设置。</summary>
    public Task SetEnabledAsync(bool enabled) => setEnabledAsync(this, enabled);

    /// <summary>请求父级删除当前条目。</summary>
    public Task RemoveAsync() => removeAsync(this);

    /// <summary>请求父级按相邻偏移移动当前条目。</summary>
    public Task MoveAsync(int offset) => moveAsync(this, offset);

    /// <summary>选择新路径；取消时不修改条目或发送设置命令。</summary>
    public async Task BrowseReplacementPathAsync()
    {
        var selected = await picker.PickAsync(CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            await replacePathAsync(this, selected);
        }
    }

    /// <summary>兼容 WPF 标记中的布尔命令参数，保持 ViewModel 对直接 bool 调用的类型化契约。</summary>
    private static bool TryGetEnabledState(object? parameter, out bool enabled)
    {
        if (parameter is bool boolean)
        {
            enabled = boolean;
            return true;
        }

        return bool.TryParse(parameter as string, out enabled);
    }
}
