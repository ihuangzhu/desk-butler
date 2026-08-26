using System.Windows.Media;

namespace DeskButler.Desktop.Hosting;

/// <summary>提供不访问目标文件、不会持有文件句柄的内存回退图标。</summary>
public sealed class FallbackExecutableIconProvider : IExecutableIconProvider
{
    private static readonly DrawingImage FallbackIcon = CreateFallbackIcon();

    /// <summary>始终返回内存绘制的回退图标；真实 Windows 图标提取由后续任务替换。</summary>
    public ImageSource GetIcon(string? executablePath) => FallbackIcon;

    /// <summary>构造可跨线程绑定的冻结几何图标，全程不打开可执行文件。</summary>
    private static DrawingImage CreateFallbackIcon()
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            context.DrawRoundedRectangle(
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
                new System.Windows.Media.Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)), 1),
                new System.Windows.Rect(1, 1, 14, 14),
                2,
                2);
        }

        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
