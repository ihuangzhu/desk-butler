using System.Windows.Media;

namespace DeskButler.Desktop.Hosting;

/// <summary>定义供常驻条目展示图标的可替换边界。</summary>
public interface IExecutableIconProvider
{
    /// <summary>返回可绑定图标；无法提供时返回空值。</summary>
    ImageSource? GetIcon(string? executablePath);
}
