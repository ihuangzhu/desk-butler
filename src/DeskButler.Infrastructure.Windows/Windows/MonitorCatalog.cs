using System.Runtime.InteropServices;
using DeskButler.Core.Scenes;
using DeskButler.Infrastructure.Windows.Native;

namespace DeskButler.Infrastructure.Windows.Windows;

/// <summary>读取窗口所属显示器的可恢复身份。</summary>
public sealed class MonitorCatalog : IMonitorCatalog
{
    private const uint DefaultDpi = 96;
    private readonly IMonitorNativeFacade native;

    /// <summary>创建使用真实 Win32 显示器边界的目录。</summary>
    public MonitorCatalog()
        : this(new Win32MonitorNativeFacade())
    {
    }

    /// <summary>创建使用可控显示器边界的目录。</summary>
    internal MonitorCatalog(IMonitorNativeFacade native)
    {
        this.native = native;
    }

    /// <summary>读取显示器设备名、工作区和 DPI，并在 DPI 不可用时回退为 96。</summary>
    public MonitorIdentity GetForWindow(nint windowHandle)
    {
        var monitor = native.GetMonitorForWindow(windowHandle);
        var dpi = native.TryGetDpiForWindow(windowHandle) ?? (DefaultDpi, DefaultDpi);
        return new MonitorIdentity(monitor.DeviceName, monitor.WorkArea, dpi.DpiX, dpi.DpiY);
    }
}

internal sealed class Win32MonitorNativeFacade : IMonitorNativeFacade
{
    /// <summary>读取窗口最近显示器的稳定设备名和工作区。</summary>
    public NativeMonitorSnapshot GetMonitorForWindow(nint windowHandle)
    {
        var monitorHandle = NativeMethods.MonitorFromWindow(windowHandle, NativeMethods.MonitorDefaultToNearest);
        var info = new MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>(),
            DeviceName = string.Empty
        };

        if (monitorHandle == 0 || !NativeMethods.GetMonitorInfo(monitorHandle, ref info))
        {
            return new NativeMonitorSnapshot("UNKNOWN", default);
        }

        return new NativeMonitorSnapshot(info.DeviceName, info.WorkArea.ToBounds());
    }

    /// <summary>尝试读取窗口 DPI；API 缺失或返回零时通知上层采用后备值。</summary>
    public (uint DpiX, uint DpiY)? TryGetDpiForWindow(nint windowHandle)
    {
        try
        {
            var dpi = NativeMethods.GetDpiForWindow(windowHandle);
            return dpi == 0 ? null : (dpi, dpi);
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }
}

internal interface IMonitorCatalog
{
    /// <summary>获取窗口所在显示器的身份。</summary>
    MonitorIdentity GetForWindow(nint windowHandle);
}

internal interface IMonitorNativeFacade
{
    /// <summary>读取窗口最近显示器的设备名和工作区快照。</summary>
    NativeMonitorSnapshot GetMonitorForWindow(nint windowHandle);

    /// <summary>尝试读取窗口 DPI；不可用时返回空。</summary>
    (uint DpiX, uint DpiY)? TryGetDpiForWindow(nint windowHandle);
}

/// <summary>保存显示器设备名与工作区的原生快照。</summary>
/// <param name="DeviceName">稳定设备名。</param>
/// <param name="WorkArea">可用工作区。</param>
internal sealed record NativeMonitorSnapshot(string DeviceName, WindowBounds WorkArea);
