using DeskButler.Core.Scenes;
using DeskButler.Infrastructure.Windows.Restore;

namespace DeskButler.EndToEnd;

public sealed class MonitorFallbackTests
{
    /// <summary>确定性覆盖负坐标、DPI 缩放、超大与极小保存边界的主屏回收。</summary>
    [Theory]
    [InlineData(-2600, -900, 800, 600, 96u, 96u)]
    [InlineData(-1400, 120, 720, 480, 144u, 192u)]
    [InlineData(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue, 96u, 96u)]
    [InlineData(5000, 5000, 1, 1, 240u, 240u)]
    public async Task 缺失显示器时窗口完全约束到负坐标主工作区且保持可见内容(
        int left,
        int top,
        int width,
        int height,
        uint savedDpiX,
        uint savedDpiY)
    {
        var primary = new RestoreMonitor(@"\\.\DISPLAY1", new WindowBounds(-1920, -200, 1600, 900), 144, 144, true);
        var facade = new RecordingWindowPositionFacade(primary);
        var savedMonitor = new MonitorIdentity(@"\\.\MISSING", new WindowBounds(-3840, -1080, 1920, 1080), savedDpiX, savedDpiY);
        var item = new SceneItem("fixture", @"C:\Fixture\app.exe", "FixtureClass", "Fixture", null,
            new WindowBounds(left, top, width, height), SceneWindowState.Normal, savedMonitor, false);

        await new WindowsWindowPositioner(facade).PositionAsync(42, item, CancellationToken.None);

        var actual = Assert.IsType<WindowBounds>(facade.AppliedBounds);
        Assert.InRange(actual.Left, -1920, -320);
        Assert.InRange(actual.Top, -200, 700);
        Assert.InRange(actual.Width, 200, 1600);
        Assert.InRange(actual.Height, 120, 900);
        Assert.True((long)actual.Left + actual.Width <= -320, "窗口右边界必须留在主工作区内。");
        Assert.True((long)actual.Top + actual.Height <= 700, "标题栏和窗口下边界必须留在主工作区内。");
        Assert.Equal(SceneWindowState.Normal, facade.AppliedState);
    }

    /// <summary>记录 Windows 定位适配器最终请求，不调用真实用户窗口。</summary>
    private sealed class RecordingWindowPositionFacade(params RestoreMonitor[] monitors) : IWindowPositionNativeFacade
    {
        internal WindowBounds? AppliedBounds { get; private set; }

        internal SceneWindowState? AppliedState { get; private set; }

        /// <summary>返回固定的当前显示器布局。</summary>
        public IReadOnlyList<RestoreMonitor> GetMonitors() => monitors;

        /// <summary>记录即将写入的物理窗口矩形。</summary>
        public bool SetNormalBounds(nint windowHandle, WindowBounds bounds)
        {
            AppliedBounds = bounds;
            return true;
        }

        /// <summary>记录 normal bounds 之后应用的窗口状态。</summary>
        public bool SetWindowState(nint windowHandle, SceneWindowState state)
        {
            AppliedState = state;
            return true;
        }

        /// <summary>成功测试不产生 Win32 错误。</summary>
        public int GetLastError() => 0;
    }
}
