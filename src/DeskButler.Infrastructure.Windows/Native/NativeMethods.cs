using System.Runtime.InteropServices;
using DeskButler.Core.Scenes;

namespace DeskButler.Infrastructure.Windows.Native;

internal static class NativeMethods
{
    internal const int GwlExStyle = -20;
    internal const long WsExToolWindow = 0x00000080L;
    internal const uint GwOwner = 4;
    internal const uint DwmwaCloaked = 14;
    internal const uint MonitorDefaultToNearest = 2;
    internal const uint MonitorInfoPrimary = 1;
    internal const int MonitorDpiTypeEffective = 0;
    internal const uint SwShowNormal = 1;
    internal const uint SwShowMinimized = 2;
    internal const uint SwShowMaximized = 3;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;

    internal delegate bool EnumWindowsProc(nint windowHandle, nint parameter);
    internal delegate bool MonitorEnumProc(nint monitorHandle, nint deviceContext, nint rectangle, nint parameter);

    /// <summary>枚举当前桌面的所有顶层窗口。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    /// <summary>判断指定窗口当前是否可见。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint windowHandle);

    /// <summary>读取指定窗口的线程和进程标识。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    /// <summary>读取窗口在虚拟桌面中的外接矩形。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint windowHandle, out NativeRect rectangle);

    /// <summary>读取窗口的最小化、最大化及普通位置状态。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowPlacement(nint windowHandle, ref WindowPlacement placement);

    /// <summary>设置窗口普通边界和显示状态。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPlacement(nint windowHandle, [In] ref WindowPlacement placement);

    /// <summary>在虚拟桌面屏幕坐标中设置窗口边界且不改变 Z 序或激活状态。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int left,
        int top,
        int width,
        int height,
        uint flags);

    /// <summary>读取窗口类名，不跨进程读取窗口内存。</summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(nint windowHandle, [Out] char[] className, int maximumCount);

    /// <summary>读取顶层窗口公开标题提示。</summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(nint windowHandle, [Out] char[] text, int maximumCount);

    /// <summary>读取桌面窗口管理器维护的窗口属性。</summary>
    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(nint windowHandle, uint attribute, out int value, uint valueSize);

    /// <summary>查找与窗口相交面积最大的显示器。</summary>
    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    /// <summary>读取显示器设备名与工作区。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitorHandle, ref MonitorInfo monitorInfo);

    /// <summary>枚举当前桌面的活动显示器。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(
        nint deviceContext,
        nint clippingRectangle,
        MonitorEnumProc callback,
        nint parameter);

    /// <summary>读取指定显示器的有效 DPI。</summary>
    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(
        nint monitorHandle,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    /// <summary>读取窗口当前有效 DPI；旧系统缺少入口点时由调用方回退。</summary>
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint windowHandle);

    /// <summary>读取窗口所有者，用于排除 owned window。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetWindow(nint windowHandle, uint command);

    /// <summary>读取 64 位窗口扩展样式。</summary>
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint windowHandle, int index);

    /// <summary>读取 32 位窗口扩展样式。</summary>
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint windowHandle, int index);

    /// <summary>按当前进程位宽读取窗口扩展样式。</summary>
    internal static nint GetWindowLongPtr(nint windowHandle, int index)
    {
        return nint.Size == 8 ? GetWindowLongPtr64(windowHandle, index) : GetWindowLong32(windowHandle, index);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativeRect
{
    internal readonly int Left;
    internal readonly int Top;
    internal readonly int Right;
    internal readonly int Bottom;

    /// <summary>把 Win32 RECT 转换为平台无关窗口边界。</summary>
    internal WindowBounds ToBounds() => new(Left, Top, Right - Left, Bottom - Top);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    internal int X;
    internal int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowPlacement
{
    internal uint Length;
    internal uint Flags;
    internal uint ShowCommand;
    internal NativePoint MinimumPosition;
    internal NativePoint MaximumPosition;
    internal NativeRect NormalPosition;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MonitorInfo
{
    internal uint Size;
    internal NativeRect MonitorArea;
    internal NativeRect WorkArea;
    internal uint Flags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    internal string DeviceName;
}

internal interface IWindowEnumerationNativeApi
{
    /// <summary>通过受控 callback 枚举当前桌面的顶层窗口。</summary>
    bool EnumerateWindows(NativeMethods.EnumWindowsProc callback);

    /// <summary>读取 EnumWindows 失败时保存的 Win32 last-error。</summary>
    int GetLastError();
}

internal sealed class WindowEnumerationNativeApi : IWindowEnumerationNativeApi
{
    /// <summary>调用 Win32 EnumWindows；callback 生命周期由上层保持到调用返回。</summary>
    public bool EnumerateWindows(NativeMethods.EnumWindowsProc callback) => NativeMethods.EnumWindows(callback, 0);

    /// <summary>读取最近 P/Invoke 保存的 Win32 last-error。</summary>
    public int GetLastError() => Marshal.GetLastPInvokeError();
}

internal interface IWindowPropertyNativeApi
{
    /// <summary>设置当前线程的 last-error，供零返回歧义消解。</summary>
    void SetLastError(int errorCode);

    /// <summary>读取最近 P/Invoke 保存的 last-error。</summary>
    int GetLastError();

    /// <summary>读取窗口 owner；合法无 owner 与失败都可能返回零。</summary>
    nint GetOwner(nint windowHandle);

    /// <summary>读取窗口扩展样式；合法无样式与失败都可能返回零。</summary>
    nint GetExtendedStyle(nint windowHandle);
}

internal sealed class WindowPropertyNativeApi : IWindowPropertyNativeApi
{
    /// <summary>设置当前线程 Win32 last-error。</summary>
    public void SetLastError(int errorCode) => Marshal.SetLastPInvokeError(errorCode);

    /// <summary>读取最近 P/Invoke 保存的 Win32 last-error。</summary>
    public int GetLastError() => Marshal.GetLastPInvokeError();

    /// <summary>调用 GetWindow(GW_OWNER) 读取借用 owner 句柄。</summary>
    public nint GetOwner(nint windowHandle) => NativeMethods.GetWindow(windowHandle, NativeMethods.GwOwner);

    /// <summary>按进程位宽读取窗口扩展样式。</summary>
    public nint GetExtendedStyle(nint windowHandle) => NativeMethods.GetWindowLongPtr(windowHandle, NativeMethods.GwlExStyle);
}

internal sealed class NativeWindowPropertyReader
{
    private readonly IWindowPropertyNativeApi native;

    /// <summary>创建使用真实 Win32 owner/style 调用的读取器。</summary>
    internal NativeWindowPropertyReader()
        : this(new WindowPropertyNativeApi())
    {
    }

    /// <summary>创建使用可控低层 API 的读取器。</summary>
    /// <param name="native">提供可歧义零返回与 last-error 的低层边界。</param>
    internal NativeWindowPropertyReader(IWindowPropertyNativeApi native)
    {
        this.native = native;
    }

    /// <summary>区分合法无 owner 与 GetWindow 失败。</summary>
    internal bool TryGetOwner(nint windowHandle, out nint owner)
    {
        native.SetLastError(0);
        owner = native.GetOwner(windowHandle);
        return owner != 0 || native.GetLastError() == 0;
    }

    /// <summary>区分合法零扩展样式与 GetWindowLongPtr 失败。</summary>
    internal bool TryGetExtendedStyle(nint windowHandle, out nint extendedStyle)
    {
        native.SetLastError(0);
        extendedStyle = native.GetExtendedStyle(windowHandle);
        return extendedStyle != 0 || native.GetLastError() == 0;
    }
}
