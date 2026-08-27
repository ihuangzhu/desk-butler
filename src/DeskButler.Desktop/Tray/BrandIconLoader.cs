using System.Drawing;

namespace DeskButler.Desktop.Tray;

/// <summary>从桌面程序集读取随程序发布的品牌图标。</summary>
internal static class BrandIconLoader
{
    private const string ResourceName = "DeskButler.Desktop.Assets.DeskButler.ico";

    /// <summary>加载独立拥有的图标实例；资源缺失或损坏时返回 false。</summary>
    internal static bool TryLoad(out Icon? icon)
    {
        icon = null;
        try
        {
            using var stream = typeof(BrandIconLoader).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return false;
            }

            using var embeddedIcon = new Icon(stream);
            icon = (Icon)embeddedIcon.Clone();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
