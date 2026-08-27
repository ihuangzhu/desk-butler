using System.Collections.Specialized;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using DeskButler.Desktop.ViewModels;

namespace DeskButler.Desktop.Tray;

/// <summary>拥有唯一通知区图标，并把菜单操作路由到主界面模型。</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly MainViewModel viewModel;
    private readonly Action openMainWindow;
    private readonly Action requestExit;
    private readonly NotifyIcon notifyIcon;
    private readonly Icon? brandIcon;
    private readonly ToolStripMenuItem recentScenesItem;
    private readonly ToolStripMenuItem captureToggleItem;
    private readonly ToolStripMenuItem focusRecoveryCardItem;
    private readonly AsyncCommand focusRecoveryCardCommand;
    private bool disposed;

    /// <summary>创建并立即显示托盘图标及极简菜单。</summary>
    public TrayIconService(
        MainViewModel viewModel,
        Action openMainWindow,
        Func<Task> focusRecoveryCard,
        Action requestExit)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.openMainWindow = openMainWindow ?? throw new ArgumentNullException(nameof(openMainWindow));
        this.requestExit = requestExit ?? throw new ArgumentNullException(nameof(requestExit));
        focusRecoveryCardCommand = new AsyncCommand(focusRecoveryCard ?? throw new ArgumentNullException(nameof(focusRecoveryCard)));

        var menu = new ContextMenuStrip();
        menu.Items.Add(CreateActionItem("保存当前现场", (_, _) => viewModel.SaveNowCommand.Execute(null)));
        menu.Items.Add(CreateLaunchResidentsNowItem(viewModel));
        recentScenesItem = new ToolStripMenuItem("最近现场");
        menu.Items.Add(recentScenesItem);
        captureToggleItem = CreateActionItem(viewModel.CaptureToggleText, (_, _) => viewModel.ToggleCaptureCommand.Execute(null));
        menu.Items.Add(captureToggleItem);
        menu.Items.Add(new ToolStripSeparator());
        focusRecoveryCardItem = CreateActionItem("聚焦恢复卡", (_, _) => focusRecoveryCardCommand.Execute(null));
        menu.Items.Add(focusRecoveryCardItem);
        menu.Items.Add(CreateActionItem("打开管家", (_, _) => openMainWindow()));
        menu.Items.Add(CreateActionItem("退出", (_, _) => requestExit()));

        BrandIconLoader.TryLoad(out brandIcon);
        notifyIcon = new NotifyIcon
        {
            Text = "DeskButler 本地工作现场管家",
            Icon = brandIcon ?? SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        notifyIcon.DoubleClick += OnNotifyIconDoubleClick;
        viewModel.RecentScenes.CollectionChanged += OnRecentScenesChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        RebuildRecentScenesMenu();
    }

    /// <summary>隐藏图标、解除事件并释放所有 WinForms 原生资源。</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        viewModel.RecentScenes.CollectionChanged -= OnRecentScenesChanged;
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        notifyIcon.DoubleClick -= OnNotifyIconDoubleClick;
        notifyIcon.Visible = false;
        notifyIcon.ContextMenuStrip?.Dispose();
        notifyIcon.Dispose();
        brandIcon?.Dispose();
    }

    /// <summary>创建绑定单一动作的托盘菜单项。</summary>
    private static ToolStripMenuItem CreateActionItem(string text, EventHandler click)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += click;
        return item;
    }

    /// <summary>创建复用 ViewModel 手动批次命令的托盘入口，不建立第二个常驻协调器。</summary>
    internal static ToolStripMenuItem CreateLaunchResidentsNowItem(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return CreateActionItem("立即启动常驻应用", (_, _) => viewModel.LaunchResidentsNowCommand.Execute(null));
    }

    /// <summary>双击托盘图标时打开主窗口。</summary>
    private void OnNotifyIconDoubleClick(object? sender, EventArgs eventArgs) => openMainWindow();

    /// <summary>最近现场变化后同步重建最多三个菜单入口。</summary>
    private void OnRecentScenesChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs) =>
        RebuildRecentScenesMenu();

    /// <summary>捕获暂停状态变化后更新菜单文案。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MainViewModel.CaptureToggleText))
        {
            captureToggleItem.Text = viewModel.CaptureToggleText;
        }
    }

    /// <summary>以当前 ViewModel 顺序生成可手动恢复的历史菜单。</summary>
    private void RebuildRecentScenesMenu()
    {
        recentScenesItem.DropDownItems.Clear();
        // 与最近现场集合同步，避免键盘用户进入没有可操作内容的空卡。
        focusRecoveryCardItem.Enabled = viewModel.RecentScenes.Count > 0;
        foreach (var scene in viewModel.RecentScenes)
        {
            var item = new ToolStripMenuItem($"{scene.CapturedAtText} · {scene.ItemCountText}");
            item.Click += (_, _) => viewModel.RestoreSceneCommand.Execute(scene);
            recentScenesItem.DropDownItems.Add(item);
        }

        if (recentScenesItem.DropDownItems.Count == 0)
        {
            recentScenesItem.DropDownItems.Add(new ToolStripMenuItem("暂无现场") { Enabled = false });
        }
    }
}
