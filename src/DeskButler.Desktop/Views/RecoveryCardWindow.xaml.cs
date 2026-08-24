using System.ComponentModel;
using System.Windows;
using DeskButler.Desktop.ViewModels;

namespace DeskButler.Desktop.Views;

/// <summary>将恢复卡片 ViewModel 的可见性同步为非激活顶层窗口。</summary>
public partial class RecoveryCardWindow : Window
{
    private readonly RecoveryCardViewModel viewModel;
    private bool allowClose;

    /// <summary>创建绑定恢复卡片模型的窗口。</summary>
    public RecoveryCardWindow(RecoveryCardViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>允许应用最终退出时解除订阅并真正关闭窗口。</summary>
    public void CloseForExit()
    {
        allowClose = true;
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Close();
    }

    /// <summary>由托盘键盘入口显式激活卡片并聚焦首个操作按钮。</summary>
    public void FocusForKeyboard()
    {
        if (!IsVisible)
        {
            Show();
        }

        Activate();
        FirstActionButton.Focus();
        System.Windows.Input.Keyboard.Focus(FirstActionButton);
    }

    /// <summary>普通关闭等价于显式跳过，不结束托盘宿主。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!allowClose)
        {
            e.Cancel = true;
            _ = viewModel.SkipAsync();
            Hide();
        }

        base.OnClosing(e);
    }

    /// <summary>只响应 IsVisible 属性，倒计时从不触发恢复操作。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(RecoveryCardViewModel.IsVisible))
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            if (viewModel.IsVisible)
            {
                Show();
            }
            else
            {
                Hide();
            }
        });
    }
}
