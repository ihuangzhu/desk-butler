using System.Diagnostics;
using System.Runtime.InteropServices;
using DeskButler.Core.Scenes;
using DeskButler.Infrastructure.Windows.Restore;

namespace DeskButler.EndToEnd;

public sealed class PhysicalDpiWindowTests
{
    /// <summary>证明物理边界读取以 left/top/right/bottom 四边表达，而非复用宽高断言。</summary>
    [WindowsFact]
    public void 物理边界值保留独立右下边缘()
    {
        var bounds = PhysicalWindowBounds.FromEdges(101, 202, 707, 909);

        Assert.Equal(101, bounds.Left);
        Assert.Equal(202, bounds.Top);
        Assert.Equal(707, bounds.Right);
        Assert.Equal(909, bounds.Bottom);
    }

    /// <summary>独立 oracle 明确验证 96→144/192 缩放，并证明未缩放结果会偏离超过八像素。</summary>
    [WindowsFact]
    public void 独立Oracle拒绝未缩放边界()
    {
        var source = new WindowBounds(100, 50, 1920, 1040);
        var target = new WindowBounds(-1600, 20, 1600, 1200);
        var saved = new WindowBounds(140, 110, 400, 240);

        var at144 = IndependentDpiOracle.Calculate(saved, source, target, 96, 144);
        var at192 = IndependentDpiOracle.Calculate(saved, source, target, 96, 192);

        Assert.Equal(PhysicalWindowBounds.FromEdges(-1540, 110, -940, 470), at144);
        Assert.Equal(PhysicalWindowBounds.FromEdges(-1520, 140, -720, 620), at192);
        Assert.Equal(600, at144.Right - at144.Left);
        Assert.Equal(800, at192.Right - at192.Left);
        Assert.True(Math.Abs((long)(saved.Left + saved.Width) - at144.Right) > 8);
    }

    /// <summary>在真实 fixture 上以注入的 144 DPI 走生产定位器，并逐边核对物理像素。</summary>
    [InteractiveWindowsFact]
    [Trait("Category", "Interactive")]
    public async Task 注入百分之一百五十缩放后真实窗口四边误差不超过八像素()
    {
        var executablePath = FindFixtureExecutable();
        var process = Process.Start(new ProcessStartInfo(executablePath) { UseShellExecute = true })
            ?? throw new InvalidOperationException("无法启动 DPI TestWindow fixture。");
        await using var fixture = FixtureProcessLease.Create(process, executablePath);
        var windowHandle = await fixture.WaitForMainWindowAsync(TestContext.Current.CancellationToken);
        var workArea = PhysicalWindowBoundsReader.ReadMonitorWorkArea(windowHandle);
        var savedMonitor = new MonitorIdentity("DPI-FIXTURE", workArea, 96, 96);
        var savedBounds = new WindowBounds(
            workArea.Left + 40,
            workArea.Top + 60,
            400,
            240);
        var expected = IndependentDpiOracle.Calculate(savedBounds, workArea, workArea, 96, 144);
        Assert.Equal(600, expected.Right - expected.Left);
        Assert.True(Math.Abs((long)(savedBounds.Left + savedBounds.Width) - expected.Right) > 8);
        var scene = new SceneItem(
            "dpi-fixture", executablePath, "FixtureWindow", null, null,
            savedBounds, SceneWindowState.Normal, savedMonitor, false);
        var native = new RealBoundsInjectedDpiFacade(
            new RestoreMonitor("DPI-FIXTURE", workArea, 144, 144, true));

        await new WindowsWindowPositioner(native).PositionAsync(
            windowHandle, scene, TestContext.Current.CancellationToken);

        var actual = PhysicalWindowBoundsReader.Read(windowHandle);
        Assert.Equal(144U, native.Monitor.DpiX);
        AssertEdgeWithinEight(expected.Left, actual.Left);
        AssertEdgeWithinEight(expected.Top, actual.Top);
        AssertEdgeWithinEight(expected.Right, actual.Right);
        AssertEdgeWithinEight(expected.Bottom, actual.Bottom);
    }

    /// <summary>断言单条物理边误差不超过 GetWindowRect 不可见边框容差。</summary>
    private static void AssertEdgeWithinEight(int expected, int actual) =>
        Assert.InRange(Math.Abs((long)actual - expected), 0, 8);

    /// <summary>定位与当前测试配置相同的项目 TestWindow apphost。</summary>
    private static string FindFixtureExecutable()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "DeskButler.slnx")))
        {
            repository = repository.Parent;
        }

        if (repository is null)
        {
            throw new DirectoryNotFoundException("无法定位 DeskButler 仓库根。");
        }

        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        return Path.Combine(repository.FullName, "tests", "DeskButler.Infrastructure.Windows.Tests", "TestApps",
            "DeskButler.TestWindow", "bin", configuration, "net10.0-windows10.0.17763.0",
            "DeskButler.TestWindow.exe");
    }

    /// <summary>注入 144 DPI/真实工作区，同时只把生产算法输出写到真实 fixture HWND。</summary>
    private sealed class RealBoundsInjectedDpiFacade(RestoreMonitor monitor) : IWindowPositionNativeFacade
    {
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;

        internal RestoreMonitor Monitor { get; } = monitor;

        /// <summary>只返回确定性的 150% 目标显示器。</summary>
        public IReadOnlyList<RestoreMonitor> GetMonitors() => [Monitor];

        /// <summary>在 PMv2 上下文中把生产算法结果写入真实窗口。</summary>
        public bool SetNormalBounds(nint windowHandle, WindowBounds bounds)
        {
            return DpiAwarenessContextScope.Run(() => SetWindowPos(
                    windowHandle, 0, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                    SwpNoZOrder | SwpNoActivate));
        }

        /// <summary>fixture 已处于 Normal；无需激活窗口即可保持该状态。</summary>
        public bool SetWindowState(nint windowHandle, SceneWindowState state) => state == SceneWindowState.Normal;

        /// <summary>返回最近一次 Win32 错误。</summary>
        public int GetLastError() => Marshal.GetLastPInvokeError();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);
    }
}
