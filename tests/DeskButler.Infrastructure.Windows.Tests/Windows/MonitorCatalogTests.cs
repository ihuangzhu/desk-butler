using DeskButler.Core.Scenes;
using DeskButler.Infrastructure.Windows.Windows;

namespace DeskButler.Infrastructure.Windows.Tests.Windows;

public sealed class MonitorCatalogTests
{
    /// <summary>验证显示器目录保留稳定设备名、工作区及窗口 DPI。</summary>
    [Fact]
    public void GetForWindow映射显示器身份与Dpi()
    {
        var native = new FakeMonitorNativeFacade(
            new NativeMonitorSnapshot(@"\\.\DISPLAY2", new WindowBounds(-1920, 0, 1920, 1040)),
            (144, 120));

        var monitor = new MonitorCatalog(native).GetForWindow(42);

        Assert.Equal(@"\\.\DISPLAY2", monitor.DeviceName);
        Assert.Equal(new WindowBounds(-1920, 0, 1920, 1040), monitor.WorkArea);
        Assert.Equal(144U, monitor.DpiX);
        Assert.Equal(120U, monitor.DpiY);
    }

    /// <summary>验证 DPI API 缺失或失败时使用保守 96 DPI 后备值。</summary>
    [Fact]
    public void GetForWindow的Dpi不可用时回退为96()
    {
        var native = new FakeMonitorNativeFacade(
            new NativeMonitorSnapshot(@"\\.\DISPLAY1", new WindowBounds(0, 0, 1920, 1040)),
            null);

        var monitor = new MonitorCatalog(native).GetForWindow(42);

        Assert.Equal(96U, monitor.DpiX);
        Assert.Equal(96U, monitor.DpiY);
    }

    /// <summary>创建返回指定显示器快照和 DPI 的测试 native 边界。</summary>
    /// <param name="snapshot">受控显示器快照。</param>
    /// <param name="dpi">受控 DPI 或不可用标记。</param>
    private sealed class FakeMonitorNativeFacade(
        NativeMonitorSnapshot snapshot,
        (uint DpiX, uint DpiY)? dpi) : IMonitorNativeFacade
    {
        /// <summary>返回受控显示器设备名和工作区。</summary>
        public NativeMonitorSnapshot GetMonitorForWindow(nint windowHandle) => snapshot;

        /// <summary>返回受控 DPI 或模拟 API 不可用。</summary>
        public (uint DpiX, uint DpiY)? TryGetDpiForWindow(nint windowHandle) => dpi;
    }
}
