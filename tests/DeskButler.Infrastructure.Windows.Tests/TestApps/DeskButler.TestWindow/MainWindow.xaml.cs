using System.Windows;

namespace DeskButler.TestWindow;

public partial class MainWindow : Window
{
    /// <summary>创建具有确定标题和边界的唯一测试主窗口。</summary>
    internal MainWindow(WindowOptions options)
    {
        InitializeComponent();
        Title = options.Title;
        Left = options.Left;
        Top = options.Top;
        Width = options.Width;
        Height = options.Height;
    }
}
