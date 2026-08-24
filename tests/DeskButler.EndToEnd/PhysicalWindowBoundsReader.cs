using System.ComponentModel;
using System.Runtime.InteropServices;
using DeskButler.Core.Scenes;

namespace DeskButler.EndToEnd;

/// <summary>表示由测试侧独立 Win32 API 读取的物理窗口四边。</summary>
internal readonly record struct PhysicalWindowBounds(int Left, int Top, int Right, int Bottom)
{
    /// <summary>显式按四边构造，避免把 width/height 误当 right/bottom。</summary>
    internal static PhysicalWindowBounds FromEdges(int left, int top, int right, int bottom) =>
        new(left, top, right, bottom);
}

/// <summary>临时切换调用线程为 PMv2 后用 GetWindowRect 独立读取物理像素边界。</summary>
internal static class PhysicalWindowBoundsReader
{
    /// <summary>读取四边并在 finally 恢复调用线程原 DPI 上下文。</summary>
    internal static PhysicalWindowBounds Read(nint windowHandle) =>
        DpiAwarenessContextScope.Run(() =>
        {
            if (!GetWindowRect(windowHandle, out var rectangle))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法读取 fixture 的物理窗口边界。");
            }

            return PhysicalWindowBounds.FromEdges(
                rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
        });

    /// <summary>以相同 PMv2 线程上下文读取窗口所在显示器的物理工作区。</summary>
    internal static WindowBounds ReadMonitorWorkArea(nint windowHandle) =>
        DpiAwarenessContextScope.Run(() =>
        {
            var monitor = MonitorFromWindow(windowHandle, 2);
            var info = new NativeMonitorInfo { Size = (uint)Marshal.SizeOf<NativeMonitorInfo>() };
            if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法读取 fixture 显示器工作区。");
            }

            return new WindowBounds(
                info.WorkArea.Left,
                info.WorkArea.Top,
                info.WorkArea.Right - info.WorkArea.Left,
                info.WorkArea.Bottom - info.WorkArea.Top);
        });

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        internal uint Size;
        internal NativeRectangle Monitor;
        internal NativeRectangle WorkArea;
        internal uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitorHandle, ref NativeMonitorInfo monitorInfo);
}
