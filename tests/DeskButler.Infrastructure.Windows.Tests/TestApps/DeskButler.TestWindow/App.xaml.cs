using System.Globalization;
using System.Windows;

namespace DeskButler.TestWindow;

public partial class App : Application
{
    /// <summary>解析受控窗口参数并显示唯一 WPF 主窗口。</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = WindowOptions.Parse(e.Args);
        MainWindow = new MainWindow(options);
        MainWindow.Show();
    }
}

/// <summary>定义受控 WPF 测试窗口的启动参数。</summary>
/// <param name="Title">主窗口标题。</param>
/// <param name="Left">窗口左坐标。</param>
/// <param name="Top">窗口顶坐标。</param>
/// <param name="Width">窗口宽度。</param>
/// <param name="Height">窗口高度。</param>
internal sealed record WindowOptions(string Title, double Left, double Top, double Width, double Height)
{
    /// <summary>解析测试进程公开的标题和窗口矩形参数。</summary>
    internal static WindowOptions Parse(IReadOnlyList<string> arguments)
    {
        return new WindowOptions(
            ReadString(arguments, "--title", "DeskButler Test Window"),
            ReadDouble(arguments, "--left", 120),
            ReadDouble(arguments, "--top", 120),
            ReadDouble(arguments, "--width", 640),
            ReadDouble(arguments, "--height", 480));
    }

    /// <summary>读取指定字符串参数；缺失时使用默认值。</summary>
    private static string ReadString(IReadOnlyList<string> arguments, string name, string fallback)
    {
        var index = FindArgument(arguments, name);
        return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : fallback;
    }

    /// <summary>读取使用不变区域格式的数值参数；无效时使用默认值。</summary>
    private static double ReadDouble(IReadOnlyList<string> arguments, string name, double fallback)
    {
        var index = FindArgument(arguments, name);
        return index >= 0 &&
               index + 1 < arguments.Count &&
               double.TryParse(arguments[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    /// <summary>按不区分大小写方式查找参数名称。</summary>
    private static int FindArgument(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}
