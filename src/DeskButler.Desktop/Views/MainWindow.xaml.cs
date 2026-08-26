using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using DeskButler.Desktop.ViewModels;

namespace DeskButler.Desktop.Views;

/// <summary>承载五个极简功能页，并在关闭时退回托盘。</summary>
public partial class MainWindow : Window
{
    private bool allowClose;

    /// <summary>创建绑定共享主界面模型的窗口。</summary>
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    /// <summary>允许应用最终退出时真正关闭窗口。</summary>
    public void CloseForExit()
    {
        allowClose = true;
        Close();
    }

    /// <summary>在 Dispatcher 队列中聚焦具名确认区，确保 Show 完成布局后才移动键盘焦点。</summary>
    public void FocusResidentCandidateConfirmation()
    {
        Dispatcher.BeginInvoke(() =>
        {
            MainTabControl.SelectedItem = HomeTabItem;
            MainTabControl.UpdateLayout();
            ResidentCandidateConfirmationPanel.Focus();
            Keyboard.Focus(ResidentCandidateConfirmationPanel);
        });
    }

    /// <summary>普通关闭仅隐藏窗口，维持托盘宿主运行。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!allowClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}
