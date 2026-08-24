using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using DeskButler.Core.Restore;
using DeskButler.Core.Scenes;
using DeskButler.Infrastructure.Windows.Native;

namespace DeskButler.Infrastructure.Windows.Restore;

/// <summary>按当前显示器 DPI 回收窗口边界，并在最后恢复显示状态。</summary>
public sealed class WindowsWindowPositioner : IWindowPositioner
{
    private const uint DefaultDpi = 96;
    private const int MinimumVisibleWidth = 200;
    private const int MinimumVisibleHeight = 120;
    private readonly IWindowPositionNativeFacade native;

    /// <summary>创建使用真实 Win32 显示器与窗口位置 API 的适配器。</summary>
    public WindowsWindowPositioner()
        : this(new WindowPositionNativeFacade())
    {
    }

    /// <summary>创建使用可控原生 facade 的适配器。</summary>
    internal WindowsWindowPositioner(IWindowPositionNativeFacade native)
    {
        this.native = native ?? throw new ArgumentNullException(nameof(native));
    }

    /// <summary>先写入 normal bounds，再按场景应用 Normal、Maximized 或 Minimized。</summary>
    public Task PositionAsync(
        nint windowHandle,
        SceneItem sceneItem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sceneItem);
        cancellationToken.ThrowIfCancellationRequested();
        if (windowHandle == 0)
        {
            throw new ArgumentException("窗口句柄不能为空。", nameof(windowHandle));
        }

        var monitors = native.GetMonitors();
        var targetMonitor = SelectTargetMonitor(monitors, sceneItem.Monitor.DeviceName);
        var normalBounds = RecoverBounds(sceneItem.Bounds, sceneItem.Monitor, targetMonitor);
        cancellationToken.ThrowIfCancellationRequested();
        if (!native.SetNormalBounds(windowHandle, normalBounds))
        {
            throw new Win32Exception(native.GetLastError(), "设置窗口普通边界失败。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!native.SetWindowState(windowHandle, sceneItem.State))
        {
            throw new Win32Exception(native.GetLastError(), "恢复窗口显示状态失败。");
        }

        return Task.CompletedTask;
    }

    /// <summary>优先使用保存时设备名，缺失时回退 primary，再回退枚举首项。</summary>
    private static RestoreMonitor SelectTargetMonitor(
        IReadOnlyList<RestoreMonitor> monitors,
        string savedDeviceName)
    {
        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("没有可用于恢复窗口的显示器。可能是桌面会话尚未就绪。");
        }

        return monitors.FirstOrDefault(monitor =>
                   StringComparer.OrdinalIgnoreCase.Equals(monitor.DeviceName, savedDeviceName))
               ?? monitors.FirstOrDefault(monitor => monitor.IsPrimary)
               ?? monitors[0];
    }

    /// <summary>按保存与目标 DPI 比例缩放相对工作区几何，并完全约束到目标工作区。</summary>
    private static WindowBounds RecoverBounds(
        WindowBounds savedBounds,
        MonitorIdentity savedMonitor,
        RestoreMonitor targetMonitor)
    {
        var workArea = NormalizeWorkArea(targetMonitor.WorkArea);
        var scaleX = SafeDpi(targetMonitor.DpiX) / (double)SafeDpi(savedMonitor.DpiX);
        var scaleY = SafeDpi(targetMonitor.DpiY) / (double)SafeDpi(savedMonitor.DpiY);
        var relativeLeft = (double)savedBounds.Left - savedMonitor.WorkArea.Left;
        var relativeTop = (double)savedBounds.Top - savedMonitor.WorkArea.Top;
        var requestedWidth = savedBounds.Width * scaleX;
        var requestedHeight = savedBounds.Height * scaleY;

        var minimumWidth = Math.Min(MinimumVisibleWidth, workArea.Width);
        var minimumHeight = Math.Min(MinimumVisibleHeight, workArea.Height);
        var width = ClampDimension(requestedWidth, minimumWidth, workArea.Width);
        var height = ClampDimension(requestedHeight, minimumHeight, workArea.Height);
        var requestedLeft = targetMonitor.WorkArea.Left + relativeLeft * scaleX;
        var requestedTop = targetMonitor.WorkArea.Top + relativeTop * scaleY;
        // 先求非负余量再与起点相加，避免 int.MaxValue + 1 - 1 的中间表达式溢出。
        var maximumLeft = checked(workArea.Left + (workArea.Width - width));
        var maximumTop = checked(workArea.Top + (workArea.Height - height));
        var left = ClampCoordinate(requestedLeft, workArea.Left, maximumLeft);
        var top = ClampCoordinate(requestedTop, workArea.Top, maximumTop);
        return new WindowBounds(left, top, width, height);
    }

    /// <summary>把异常工作区收敛为至少 1x1 且右下边界不溢出的区域。</summary>
    private static WindowBounds NormalizeWorkArea(WindowBounds workArea)
    {
        var maximumWidth = workArea.Left >= 0
            ? Math.Max(1, int.MaxValue - workArea.Left)
            : int.MaxValue;
        var maximumHeight = workArea.Top >= 0
            ? Math.Max(1, int.MaxValue - workArea.Top)
            : int.MaxValue;
        var width = Math.Clamp(workArea.Width, 1, maximumWidth);
        var height = Math.Clamp(workArea.Height, 1, maximumHeight);
        return new WindowBounds(workArea.Left, workArea.Top, width, height);
    }

    /// <summary>将零或异常 DPI 回退到 96。</summary>
    private static uint SafeDpi(uint dpi) => dpi == 0 ? DefaultDpi : dpi;

    /// <summary>把尺寸四舍五入并约束在最小可见尺寸与工作区尺寸之间。</summary>
    private static int ClampDimension(double value, int minimum, int maximum)
    {
        if (!double.IsFinite(value))
        {
            return minimum;
        }

        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(rounded, minimum, maximum);
    }

    /// <summary>把坐标安全约束到可表示且完全位于工作区的区间。</summary>
    private static int ClampCoordinate(double value, int minimum, int maximum)
    {
        if (double.IsNaN(value))
        {
            return minimum;
        }

        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(rounded, minimum, maximum);
    }
}

/// <summary>保存一次恢复定位所需的当前显示器数据。</summary>
/// <param name="DeviceName">稳定设备名。</param>
/// <param name="WorkArea">当前工作区。</param>
/// <param name="DpiX">当前水平 DPI。</param>
/// <param name="DpiY">当前垂直 DPI。</param>
/// <param name="IsPrimary">是否为 primary 显示器。</param>
internal sealed record RestoreMonitor(
    string DeviceName,
    WindowBounds WorkArea,
    uint DpiX,
    uint DpiY,
    bool IsPrimary);

/// <summary>隔离显示器枚举和窗口定位 P/Invoke。</summary>
internal interface IWindowPositionNativeFacade
{
    /// <summary>枚举当前桌面的活动显示器。</summary>
    IReadOnlyList<RestoreMonitor> GetMonitors();

    /// <summary>设置普通边界，失败时返回 false 并保存 Win32 last-error。</summary>
    bool SetNormalBounds(nint windowHandle, WindowBounds bounds);

    /// <summary>在普通边界之后应用目标状态，失败时返回 false。</summary>
    bool SetWindowState(nint windowHandle, SceneWindowState state);

    /// <summary>读取最近 P/Invoke 的 Win32 last-error。</summary>
    int GetLastError();
}

/// <summary>真实 Win32 定位 facade，保持枚举 callback 生命周期并显式传播错误。</summary>
internal sealed class WindowPositionNativeFacade : IWindowPositionNativeFacade
{
    private readonly IWindowPlacementNativeApi windowPlacement;

    /// <summary>创建使用真实窗口 placement P/Invoke 的 facade。</summary>
    internal WindowPositionNativeFacade()
        : this(new WindowPlacementNativeApi())
    {
    }

    /// <summary>创建使用可控 placement P/Invoke 的 facade。</summary>
    internal WindowPositionNativeFacade(IWindowPlacementNativeApi windowPlacement)
    {
        this.windowPlacement = windowPlacement ?? throw new ArgumentNullException(nameof(windowPlacement));
    }

    /// <summary>枚举显示器，单个 DPI API 不可用时只回退该显示器为 96。</summary>
    public IReadOnlyList<RestoreMonitor> GetMonitors()
    {
        var monitors = new List<RestoreMonitor>();
        ExceptionDispatchInfo? callbackFailure = null;
        var callback = new NativeMethods.MonitorEnumProc((monitorHandle, _, _, _) =>
        {
            try
            {
                var info = new MonitorInfo
                {
                    Size = (uint)Marshal.SizeOf<MonitorInfo>(),
                    DeviceName = string.Empty
                };
                if (!NativeMethods.GetMonitorInfo(monitorHandle, ref info))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "读取显示器工作区失败。");
                }

                var dpi = TryGetMonitorDpi(monitorHandle);
                monitors.Add(new RestoreMonitor(
                    info.DeviceName,
                    info.WorkArea.ToBounds(),
                    dpi.DpiX,
                    dpi.DpiY,
                    (info.Flags & NativeMethods.MonitorInfoPrimary) != 0));
                return true;
            }
            catch (Exception exception)
            {
                // 托管异常绝不跨越 reverse P/Invoke；在 EnumDisplayMonitors 返回后恢复原堆栈。
                callbackFailure = ExceptionDispatchInfo.Capture(exception);
                return false;
            }
        });

        var completed = NativeMethods.EnumDisplayMonitors(0, 0, callback, 0);
        GC.KeepAlive(callback);
        callbackFailure?.Throw();
        if (!completed)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "枚举显示器失败。");
        }

        return monitors;
    }

    /// <summary>先恢复 normal，再以虚拟桌面屏幕坐标写入普通边界。</summary>
    public bool SetNormalBounds(nint windowHandle, WindowBounds bounds)
    {
        var placement = new WindowPlacement { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
        if (!windowPlacement.GetWindowPlacement(windowHandle, ref placement))
        {
            return false;
        }

        // WPF_RESTORETOMAXIMIZED 等旧 flags 不得污染 normal 或最终状态写入。
        placement.Flags = 0;
        placement.ShowCommand = NativeMethods.SwShowNormal;
        if (!windowPlacement.SetWindowPlacement(windowHandle, ref placement))
        {
            return false;
        }

        return windowPlacement.SetWindowPos(windowHandle, bounds);
    }

    /// <summary>在保留 SetWindowPos 写入的 normal bounds 同时应用最终显示状态。</summary>
    public bool SetWindowState(nint windowHandle, SceneWindowState state)
    {
        var placement = new WindowPlacement { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
        if (!windowPlacement.GetWindowPlacement(windowHandle, ref placement))
        {
            return false;
        }

        placement.Flags = 0;
        placement.ShowCommand = state switch
        {
            SceneWindowState.Maximized => NativeMethods.SwShowMaximized,
            SceneWindowState.Minimized => NativeMethods.SwShowMinimized,
            _ => NativeMethods.SwShowNormal
        };
        return windowPlacement.SetWindowPlacement(windowHandle, ref placement);
    }

    /// <summary>读取最近 P/Invoke 保存的错误码。</summary>
    public int GetLastError() => windowPlacement.GetLastError();

    /// <summary>读取目标显示器 DPI；旧系统缺少 shcore 时回退 96。</summary>
    private static (uint DpiX, uint DpiY) TryGetMonitorDpi(nint monitorHandle)
    {
        try
        {
            var result = NativeMethods.GetDpiForMonitor(
                monitorHandle, NativeMethods.MonitorDpiTypeEffective, out var dpiX, out var dpiY);
            return result == 0 && dpiX > 0 && dpiY > 0 ? (dpiX, dpiY) : (96, 96);
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            return (96, 96);
        }
    }
}

/// <summary>隔离窗口 placement P/Invoke，使 flags 与调用顺序可确定性验证。</summary>
internal interface IWindowPlacementNativeApi
{
    /// <summary>读取当前 placement。</summary>
    bool GetWindowPlacement(nint windowHandle, ref WindowPlacement placement);

    /// <summary>写入 normal 或最终 placement。</summary>
    bool SetWindowPlacement(nint windowHandle, ref WindowPlacement placement);

    /// <summary>以虚拟桌面屏幕坐标写入窗口边界。</summary>
    bool SetWindowPos(nint windowHandle, WindowBounds bounds);

    /// <summary>读取最近 P/Invoke 的 last-error。</summary>
    int GetLastError();
}

/// <summary>真实窗口 placement P/Invoke 适配器。</summary>
internal sealed class WindowPlacementNativeApi : IWindowPlacementNativeApi
{
    /// <summary>调用 GetWindowPlacement。</summary>
    public bool GetWindowPlacement(nint windowHandle, ref WindowPlacement placement) =>
        NativeMethods.GetWindowPlacement(windowHandle, ref placement);

    /// <summary>调用 SetWindowPlacement。</summary>
    public bool SetWindowPlacement(nint windowHandle, ref WindowPlacement placement) =>
        NativeMethods.SetWindowPlacement(windowHandle, ref placement);

    /// <summary>调用 SetWindowPos 并保持 Z 序与激活状态。</summary>
    public bool SetWindowPos(nint windowHandle, WindowBounds bounds) =>
        NativeMethods.SetWindowPos(
            windowHandle,
            0,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);

    /// <summary>读取最近 P/Invoke 保存的 last-error。</summary>
    public int GetLastError() => Marshal.GetLastPInvokeError();
}
