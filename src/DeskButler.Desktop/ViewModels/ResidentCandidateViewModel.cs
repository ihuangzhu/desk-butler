using System.Windows.Media;
using DeskButler.Core.ResidentApps;
using DeskButler.Desktop.Hosting;

namespace DeskButler.Desktop.ViewModels;

/// <summary>把一次发现代次中的单个常驻候选投影为可绑定状态。</summary>
public sealed class ResidentCandidateViewModel : ObservableObject
{
    private readonly ResidentAppCandidate candidate;
    private readonly IExecutablePicker picker;
    private readonly IExecutableIconProvider iconProvider;
    private readonly Action stateChanged;
    private bool isSelected;
    private string? finalLaunchPath;
    private ImageSource? icon;

    /// <summary>创建候选视图模型，并保留父级提供的代次变化通知边界。</summary>
    public ResidentCandidateViewModel(
        ResidentAppCandidate candidate,
        long generation,
        IExecutablePicker picker,
        IExecutableIconProvider iconProvider,
        Action? stateChanged = null)
    {
        this.candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        Generation = generation;
        this.picker = picker ?? throw new ArgumentNullException(nameof(picker));
        this.iconProvider = iconProvider ?? throw new ArgumentNullException(nameof(iconProvider));
        this.stateChanged = stateChanged ?? (() => { });
        finalLaunchPath = candidate.LaunchPath;
        // 路径替换必须经过显式用户确认，不能因高可信度自动覆盖已有入口。
        isSelected = candidate.Confidence == ResidentCandidateConfidence.High &&
            candidate.Kind == ResidentCandidateKind.NewApplication &&
            !string.IsNullOrWhiteSpace(candidate.LaunchPath);
        icon = iconProvider.GetIcon(finalLaunchPath);
        BrowsePathCommand = new AsyncCommand(BrowsePathAsync);
    }

    /// <summary>获取候选所属的单调发现代次。</summary>
    public long Generation { get; }

    /// <summary>获取候选稳定标识。</summary>
    public string CandidateId => candidate.CandidateId;

    /// <summary>获取用户可见的候选名称。</summary>
    public string DisplayName => candidate.DisplayName;

    /// <summary>获取候选操作类型。</summary>
    public ResidentCandidateKind Kind => candidate.Kind;

    /// <summary>获取候选的可信度。</summary>
    public ResidentCandidateConfidence Confidence => candidate.Confidence;

    /// <summary>获取可由用户修改后回传确认的启动入口。</summary>
    public string? FinalLaunchPath
    {
        get => finalLaunchPath;
        set
        {
            if (SetProperty(ref finalLaunchPath, value))
            {
                Icon = iconProvider.GetIcon(value);
                OnPropertyChanged(nameof(CanConfirm));
                OnPropertyChanged(nameof(PathReplacementText));
                stateChanged();
            }
        }
    }

    /// <summary>获取候选是否被用户选择；setter 只更新 UI 草稿，不写设置。</summary>
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (SetProperty(ref isSelected, value))
            {
                OnPropertyChanged(nameof(CanConfirm));
                stateChanged();
            }
        }
    }

    /// <summary>获取已选择候选是否有可交给命令处理器验证的非空入口。</summary>
    public bool CanConfirm => IsSelected && !string.IsNullOrWhiteSpace(FinalLaunchPath);

    /// <summary>获取路径替换时展示的旧、新入口文本。</summary>
    public string PathReplacementText => Kind == ResidentCandidateKind.PathReplacement
        ? $"旧路径：{candidate.ReplacesLaunchPath ?? "（缺失）"}{Environment.NewLine}新路径：{FinalLaunchPath ?? "（待选择）"}"
        : string.Empty;

    /// <summary>获取可供 XAML 显示的内存图标。</summary>
    public ImageSource? Icon
    {
        get => icon;
        private set => SetProperty(ref icon, value);
    }

    /// <summary>获取选择候选入口的命令。</summary>
    public AsyncCommand BrowsePathCommand { get; }

    /// <summary>显示文件选择器；取消时严格保留当前候选草稿。</summary>
    public async Task BrowsePathAsync()
    {
        var selected = await picker.PickAsync(CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            FinalLaunchPath = selected;
        }
    }

    /// <summary>只生成允许 UI 回传的候选选择快照。</summary>
    public ResidentCandidateSelection ToSelection() => new(CandidateId, FinalLaunchPath, IsSelected);
}
