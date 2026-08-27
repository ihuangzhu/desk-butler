using System.Drawing;
using DeskButler.Desktop.Tray;

namespace DeskButler.Desktop.Tests.Tray;

public sealed class BrandIconLoaderTests
{
    /// <summary>品牌资源存在时，托盘必须使用随程序发布的图标，而不是系统兜底图标。</summary>
    [Fact]
    public void LoadReturnsEmbeddedBrandIconInsteadOfFallback()
    {
        var loaded = BrandIconLoader.TryLoad(out var icon);

        Assert.True(loaded);
        Assert.NotNull(icon);
        using (icon)
        {
            Assert.True(icon.Width >= 32);
            Assert.True(icon.Height >= 32);
        }
    }
}
