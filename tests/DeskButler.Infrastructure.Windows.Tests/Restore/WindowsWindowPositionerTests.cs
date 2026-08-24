using System.ComponentModel;
using DeskButler.Core.Scenes;
using DeskButler.Infrastructure.Windows.Native;
using DeskButler.Infrastructure.Windows.Restore;

namespace DeskButler.Infrastructure.Windows.Tests.Restore;

public sealed class WindowsWindowPositionerTests
{
    /// <summary>验证相对保存工作区的位置与尺寸按目标显示器 DPI 比例换算。</summary>
    [Fact]
    public async Task PositionAsync按目标Dpi换算相对工作区几何()
    {
        var native = new FakeWindowPositionNativeFacade(
            new RestoreMonitor(@"\\.\DISPLAY2", new WindowBounds(-1600, 0, 1600, 1400), 144, 144, false));
        var scene = App(new WindowBounds(200, 150, 800, 600), SceneWindowState.Normal,
            new MonitorIdentity(@"\\.\DISPLAY2", new WindowBounds(100, 50, 1600, 1000), 96, 96));

        await new WindowsWindowPositioner(native).PositionAsync(42, scene, CancellationToken.None);

        Assert.Equal(new WindowBounds(-1450, 150, 1200, 900), native.NormalBounds);
    }

    /// <summary>验证原显示器缺失时回收到 primary，而不是保留离屏坐标。</summary>
    [Fact]
    public async Task PositionAsync原显示器缺失时回收至Primary工作区()
    {
        var native = new FakeWindowPositionNativeFacade(
            new RestoreMonitor(@"\\.\DISPLAY1", new WindowBounds(0, 0, 1920, 1040), 96, 96, true));
        var scene = App(new WindowBounds(-1800, 100, 1000, 700), SceneWindowState.Normal,
            new MonitorIdentity(@"\\.\MISSING", new WindowBounds(-1920, 0, 1920, 1040), 96, 96));

        await new WindowsWindowPositioner(native).PositionAsync(42, scene, CancellationToken.None);

        Assert.Equal(new WindowBounds(120, 100, 1000, 700), native.NormalBounds);
    }

    /// <summary>验证最大化总在 normal bounds 成功设置后应用。</summary>
    [Fact]
    public async Task PositionAsync先设置NormalBounds再最大化()
    {
        var native = PrimaryFacade();
        var scene = App(new WindowBounds(10, 20, 800, 600), SceneWindowState.Maximized, SavedPrimary());

        await new WindowsWindowPositioner(native).PositionAsync(42, scene, CancellationToken.None);

        Assert.Equal(["bounds", "state:Maximized"], native.Operations);
    }

    /// <summary>验证最小化同样只在 normal bounds 成功设置后应用。</summary>
    [Fact]
    public async Task PositionAsync先设置NormalBounds再最小化()
    {
        var native = PrimaryFacade();
        var scene = App(new WindowBounds(10, 20, 800, 600), SceneWindowState.Minimized, SavedPrimary());

        await new WindowsWindowPositioner(native).PositionAsync(42, scene, CancellationToken.None);

        Assert.Equal(["bounds", "state:Minimized"], native.Operations);
    }

    /// <summary>验证至少 200x120 的窗口及标题栏被约束在普通工作区内。</summary>
    [Fact]
    public async Task PositionAsync保证最小可见区域和标题栏()
    {
        var native = PrimaryFacade();
        var scene = App(new WindowBounds(3000, -500, -20, 30), SceneWindowState.Normal, SavedPrimary());

        await new WindowsWindowPositioner(native).PositionAsync(42, scene, CancellationToken.None);

        Assert.Equal(new WindowBounds(1720, 0, 200, 120), native.NormalBounds);
    }

    /// <summary>验证 primary 工作区小于最小可见要求时采用完全缩入的 best effort。</summary>
    [Fact]
    public async Task PositionAsync超小Primary工作区使用边界内BestEffort()
    {
        var native = new FakeWindowPositionNativeFacade(
            new RestoreMonitor(@"\\.\DISPLAY1", new WindowBounds(-50, 10, 100, 80), 96, 96, true));
        var scene = App(new WindowBounds(5000, 5000, 900, 700), SceneWindowState.Normal,
            new MonitorIdentity(@"\\.\MISSING", new WindowBounds(0, 0, 1920, 1040), 96, 96));

        await new WindowsWindowPositioner(native).PositionAsync(42, scene, CancellationToken.None);

        Assert.Equal(new WindowBounds(-50, 10, 100, 80), native.NormalBounds);
    }

    /// <summary>验证零 DPI 回退 96 且极端坐标不会算术溢出。</summary>
    [Fact]
    public async Task PositionAsync对零Dpi和溢出几何保守约束()
    {
        var native = new FakeWindowPositionNativeFacade(
            new RestoreMonitor(@"\\.\DISPLAY1", new WindowBounds(-100, -100, 800, 600), 0, 0, true));
        var scene = App(new WindowBounds(int.MaxValue, int.MinValue, int.MaxValue, int.MinValue),
            SceneWindowState.Normal,
            new MonitorIdentity(@"\\.\DISPLAY1", new WindowBounds(int.MinValue, int.MaxValue, 1, 1), 0, 0));

        await new WindowsWindowPositioner(native).PositionAsync(42, scene, CancellationToken.None);

        var bounds = Assert.IsType<WindowBounds>(native.NormalBounds);
        Assert.InRange(bounds.Left, -100, 700);
        Assert.InRange(bounds.Top, -100, 500);
        Assert.InRange(bounds.Width, 200, 800);
        Assert.InRange(bounds.Height, 120, 600);
    }

    /// <summary>验证工作区起点位于 int 上界时右下边界计算不会先发生整数溢出。</summary>
    [Fact]
    public async Task PositionAsync极端正坐标工作区不溢出()
    {
        var native = new FakeWindowPositionNativeFacade(
            new RestoreMonitor(@"\\.\DISPLAY1", new WindowBounds(int.MaxValue, int.MaxValue, 1, 1), 96, 96, true));
        var scene = App(new WindowBounds(0, 0, 800, 600), SceneWindowState.Normal,
            new MonitorIdentity(@"\\.\DISPLAY1", new WindowBounds(0, 0, 1920, 1040), 96, 96));

        await new WindowsWindowPositioner(native).PositionAsync(42, scene, CancellationToken.None);

        Assert.Equal(new WindowBounds(int.MaxValue, int.MaxValue, 1, 1), native.NormalBounds);
    }

    /// <summary>验证设置 bounds 或状态的 P/Invoke 失败均以 Win32Exception 可观察。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PositionAsync原生失败可观察(bool failBounds)
    {
        var native = PrimaryFacade();
        native.FailBounds = failBounds;
        native.FailState = !failBounds;
        var scene = App(new WindowBounds(10, 20, 800, 600), SceneWindowState.Normal, SavedPrimary());

        var exception = await Assert.ThrowsAsync<Win32Exception>(() =>
            new WindowsWindowPositioner(native).PositionAsync(42, scene, CancellationToken.None));

        Assert.Equal(5, exception.NativeErrorCode);
    }

    /// <summary>验证旧 WPF_RESTORETOMAXIMIZED flags 在 normal 与最终状态两次 placement 写入前均清零。</summary>
    [Fact]
    public void NativeFacade清除旧PlacementFlags且保持Bounds到State顺序()
    {
        var native = new FakeWindowPlacementNativeApi(new WindowPlacement
        {
            Length = 44,
            Flags = 2,
            ShowCommand = 3
        });
        var facade = new WindowPositionNativeFacade(native);
        var bounds = new WindowBounds(10, 20, 800, 600);

        Assert.True(facade.SetNormalBounds(42, bounds));
        Assert.True(facade.SetWindowState(42, SceneWindowState.Maximized));

        Assert.Equal(
            ["placement:1:flags=0", "bounds", "placement:3:flags=0"],
            native.Operations);
        Assert.All(native.WrittenPlacements, placement => Assert.Equal(0U, placement.Flags));
    }

    /// <summary>创建 primary 显示器 facade。</summary>
    private static FakeWindowPositionNativeFacade PrimaryFacade() => new(
        new RestoreMonitor(@"\\.\DISPLAY1", new WindowBounds(0, 0, 1920, 1040), 96, 96, true));

    /// <summary>创建保存时的 primary 身份。</summary>
    private static MonitorIdentity SavedPrimary() => new(
        @"\\.\DISPLAY1", new WindowBounds(0, 0, 1920, 1040), 96, 96);

    /// <summary>创建场景项目。</summary>
    private static SceneItem App(WindowBounds bounds, SceneWindowState state, MonitorIdentity monitor) => new(
        "item", @"C:\Apps\tool.exe", "ToolClass", null, null, bounds, state, monitor, false);

    private sealed class FakeWindowPositionNativeFacade(params RestoreMonitor[] monitors) : IWindowPositionNativeFacade
    {
        internal WindowBounds? NormalBounds { get; private set; }

        internal List<string> Operations { get; } = [];

        internal bool FailBounds { get; set; }

        internal bool FailState { get; set; }

        /// <summary>返回受控显示器快照。</summary>
        public IReadOnlyList<RestoreMonitor> GetMonitors() => monitors;

        /// <summary>记录 normal bounds 或模拟失败。</summary>
        public bool SetNormalBounds(nint windowHandle, WindowBounds bounds)
        {
            Operations.Add("bounds");
            NormalBounds = bounds;
            return !FailBounds;
        }

        /// <summary>记录状态或模拟失败。</summary>
        public bool SetWindowState(nint windowHandle, SceneWindowState state)
        {
            Operations.Add($"state:{state}");
            return !FailState;
        }

        /// <summary>返回固定 Win32 错误码。</summary>
        public int GetLastError() => 5;
    }

    private sealed class FakeWindowPlacementNativeApi(WindowPlacement initialPlacement) : IWindowPlacementNativeApi
    {
        private WindowPlacement placement = initialPlacement;

        internal List<string> Operations { get; } = [];

        internal List<WindowPlacement> WrittenPlacements { get; } = [];

        /// <summary>返回最近一次写入的 placement。</summary>
        public bool GetWindowPlacement(nint windowHandle, ref WindowPlacement value)
        {
            value = placement;
            return true;
        }

        /// <summary>记录 placement flags/show command 并更新后续读取值。</summary>
        public bool SetWindowPlacement(nint windowHandle, ref WindowPlacement value)
        {
            placement = value;
            WrittenPlacements.Add(value);
            Operations.Add($"placement:{value.ShowCommand}:flags={value.Flags}");
            return true;
        }

        /// <summary>记录屏幕坐标 bounds 写入顺序。</summary>
        public bool SetWindowPos(nint windowHandle, WindowBounds bounds)
        {
            Operations.Add("bounds");
            return true;
        }

        /// <summary>返回固定错误码。</summary>
        public int GetLastError() => 5;
    }
}
