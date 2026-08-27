using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using DeskButler.Core.Scenes;

namespace DeskButler.Desktop.ViewModels;

/// <summary>表示历史现场中一个可供本次恢复选择的窗口。</summary>
public sealed class SceneItemSelectionViewModel : ObservableObject
{
    private readonly Action selectionChanged;
    private bool isSelected = true;

    internal SceneItemSelectionViewModel(SceneItem item, Action selectionChanged)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        this.selectionChanged = selectionChanged ?? throw new ArgumentNullException(nameof(selectionChanged));
    }

    /// <summary>获取原始不可变窗口条目。</summary>
    public SceneItem Item { get; }

    /// <summary>获取优先使用窗口标题、否则使用程序名的显示名称。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Item.TitleHint) ? ApplicationName : Item.TitleHint;

    /// <summary>获取不含扩展名的程序名称。</summary>
    public string ApplicationName
    {
        get
        {
            var name = Path.GetFileNameWithoutExtension(Item.ExecutablePath);
            return string.IsNullOrWhiteSpace(name) ? "未知程序" : name;
        }
    }

    /// <summary>获取资源管理器目录；普通窗口或空白目录返回空。</summary>
    public string? ExplorerPath => string.IsNullOrWhiteSpace(Item.ExplorerPath) ? null : Item.ExplorerPath;

    /// <summary>获取完整可执行文件路径，供界面提示展示。</summary>
    public string ExecutablePath => Item.ExecutablePath;

    /// <summary>获取捕获时是否为管理员权限窗口。</summary>
    public bool IsElevated => Item.WasElevated;

    /// <summary>获取或设置本次恢复是否包含该窗口。</summary>
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (SetProperty(ref isSelected, value))
            {
                selectionChanged();
            }
        }
    }
}

/// <summary>表示主窗口最近现场列表中的一张可展开、可选择卡片。</summary>
public sealed class SceneSummaryViewModel : ObservableObject
{
    private bool isExpanded;

    /// <summary>使用历史快照创建卡片，窗口默认全部选中。</summary>
    public SceneSummaryViewModel(SceneSnapshot scene, bool isExpanded = false)
    {
        Scene = scene ?? throw new ArgumentNullException(nameof(scene));
        this.isExpanded = isExpanded;
        Items = new ObservableCollection<SceneItemSelectionViewModel>(
            Scene.Items.Select(item => new SceneItemSelectionViewModel(item, OnSelectionChanged)));
        SelectAllCommand = new AsyncCommand(() => SetAllSelectedAsync(true));
        SelectNoneCommand = new AsyncCommand(() => SetAllSelectedAsync(false));
    }

    /// <summary>获取原始不可变现场。</summary>
    public SceneSnapshot Scene { get; }

    /// <summary>获取本地时间显示文本。</summary>
    public string CapturedAtText => Scene.CapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    /// <summary>获取窗口数量摘要。</summary>
    public string ItemCountText => $"{Items.Count} 个窗口";

    /// <summary>获取可供本次恢复选择的窗口明细。</summary>
    public ObservableCollection<SceneItemSelectionViewModel> Items { get; }

    /// <summary>获取或设置该现场卡片是否展开。</summary>
    public bool IsExpanded
    {
        get => isExpanded;
        set => SetProperty(ref isExpanded, value);
    }

    /// <summary>获取是否至少选择了一个窗口。</summary>
    public bool HasSelectedItems => Items.Any(item => item.IsSelected);

    /// <summary>获取用户可见的选择数量摘要。</summary>
    public string SelectedItemCountText => $"已选择 {Items.Count(item => item.IsSelected)}/{Items.Count}";

    /// <summary>获取当前选择的稳定窗口标识。</summary>
    public IReadOnlyList<string> SelectedItemIds => Items
        .Where(item => item.IsSelected)
        .Select(item => item.Item.Id)
        .ToArray();

    /// <summary>获取全选本现场窗口的命令。</summary>
    public AsyncCommand SelectAllCommand { get; }

    /// <summary>获取取消选择本现场全部窗口的命令。</summary>
    public AsyncCommand SelectNoneCommand { get; }

    private Task SetAllSelectedAsync(bool selected)
    {
        foreach (var item in Items)
        {
            item.IsSelected = selected;
        }

        return Task.CompletedTask;
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelectedItems));
        OnPropertyChanged(nameof(SelectedItemCountText));
        OnPropertyChanged(nameof(SelectedItemIds));
    }
}
