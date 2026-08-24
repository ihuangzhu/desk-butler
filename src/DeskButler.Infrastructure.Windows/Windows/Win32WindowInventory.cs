using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using DeskButler.Core.Capture;
using DeskButler.Core.Scenes;
using DeskButler.Infrastructure.Windows.Native;

namespace DeskButler.Infrastructure.Windows.Windows;

/// <summary>捕获当前 Windows 会话中的普通可见主窗口。</summary>
public sealed class Win32WindowInventory : IWindowInventory
{
    private static readonly HashSet<string> SystemWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Progman",
        "WorkerW",
        "DV2ControlHost",
        "MultitaskingViewFrame"
    };

    private static readonly HashSet<string> TemporaryWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "#32768",
        "tooltips_class32",
        "SysShadow"
    };

    private readonly IWindowNativeFacade native;
    private readonly IExplorerWindowReader explorer;
    private readonly IMonitorCatalog monitors;
    private readonly int currentProcessId;

    /// <summary>创建使用真实 Win32、Shell 和显示器边界的窗口清单。</summary>
    public Win32WindowInventory()
        : this(new Win32NativeFacade(), new ExplorerWindowReader(), new MonitorCatalog(), Environment.ProcessId)
    {
    }

    /// <summary>创建使用可控平台边界的窗口清单，供确定性测试验证生产映射逻辑。</summary>
    internal Win32WindowInventory(
        IWindowNativeFacade native,
        IExplorerWindowReader explorer,
        IMonitorCatalog monitors,
        int currentProcessId)
    {
        this.native = native;
        this.explorer = explorer;
        this.monitors = monitors;
        this.currentProcessId = currentProcessId;
    }

    /// <summary>捕获并映射允许持久化的窗口候选，不读取命令行或窗口内容。</summary>
    public Task<IReadOnlyList<WindowCandidate>> CaptureAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<WindowCandidate>();
        native.EnumerateTopLevelWindows(windowHandle =>
        {
            NativeWindowClassification classification;
            try
            {
                if (!native.TryReadClassification(windowHandle, out classification))
                {
                    return;
                }
            }
            catch (Exception exception) when (IsRecoverableWindowFailure(exception))
            {
                // 明确的 Win32/COM 窗口竞态只淘汰当前 HWND；程序错误必须交回调用方。
                return;
            }

            if (!IsOrdinaryVisibleMainWindow(classification))
            {
                return;
            }

            NativeWindowDetails details;
            try
            {
                if (!native.TryReadDetails(windowHandle, classification.ProcessId, out details))
                {
                    return;
                }
            }
            catch (Exception exception) when (IsRecoverableWindowFailure(exception))
            {
                // 已通过分类的窗口仍可能在详情读取时消失；仅恢复明确的平台访问失败。
                return;
            }

            try
            {
                candidates.Add(MapCandidate(new NativeWindowSnapshot(classification, details)));
            }
            catch (Exception exception) when (IsRecoverableWindowFailure(exception))
            {
                // Explorer 或 monitor 的平台竞态只淘汰当前窗口；Task 11 前不扩展日志边界。
            }
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<WindowCandidate>>(candidates);
    }

    /// <summary>判断快照是否为非系统、非临时、非自身的普通可见主窗口。</summary>
    private bool IsOrdinaryVisibleMainWindow(NativeWindowClassification snapshot)
    {
        return snapshot.IsVisible &&
               !snapshot.IsOwned &&
               !snapshot.IsToolWindow &&
               !snapshot.IsCloaked &&
               snapshot.ProcessId != currentProcessId &&
               !SystemWindowClasses.Contains(snapshot.WindowClass) &&
               !TemporaryWindowClasses.Contains(snapshot.WindowClass);
    }

    /// <summary>把原生快照映射为 Core 候选，并仅按 HWND 关联本地 Explorer 目录。</summary>
    private WindowCandidate MapCandidate(NativeWindowSnapshot snapshot)
    {
        return new WindowCandidate(
            snapshot.Handle,
            snapshot.ProcessId,
            snapshot.ExecutablePath,
            snapshot.WindowClass,
            snapshot.Title,
            explorer.TryGetFolderPath(snapshot.Handle),
            snapshot.Bounds,
            snapshot.State,
            monitors.GetForWindow(snapshot.Handle),
            true,
            false,
            false,
            false,
            snapshot.WasElevatedOrInaccessible);
    }

    /// <summary>仅把可预期的 Win32/COM 窗口竞态解释为可恢复单项失败。</summary>
    private static bool IsRecoverableWindowFailure(Exception exception) =>
        exception is Win32Exception or COMException;
}

internal sealed class Win32NativeFacade : IWindowNativeFacade
{
    private readonly NativeWindowPropertyReader windowProperties;
    private readonly IWindowEnumerationNativeApi windowEnumeration;

    /// <summary>创建使用真实 Win32 枚举及 owner/style 读取器的 facade。</summary>
    internal Win32NativeFacade()
        : this(new NativeWindowPropertyReader(), new WindowEnumerationNativeApi())
    {
    }

    /// <summary>创建使用可控枚举边界和真实 owner/style 读取器的 facade。</summary>
    /// <param name="windowEnumeration">提供可控 HWND callback 的枚举边界。</param>
    internal Win32NativeFacade(IWindowEnumerationNativeApi windowEnumeration)
        : this(new NativeWindowPropertyReader(), windowEnumeration)
    {
    }

    /// <summary>创建使用可控枚举及 owner/style 读取器的 facade。</summary>
    /// <param name="windowProperties">消解 native 零返回歧义的读取器。</param>
    /// <param name="windowEnumeration">提供 HWND callback 的枚举边界。</param>
    internal Win32NativeFacade(
        NativeWindowPropertyReader windowProperties,
        IWindowEnumerationNativeApi windowEnumeration)
    {
        this.windowProperties = windowProperties;
        this.windowEnumeration = windowEnumeration;
    }

    /// <summary>枚举 HWND；托管异常在 callback 内暂存，返回托管边界后按原堆栈重新抛出。</summary>
    public void EnumerateTopLevelWindows(Action<nint> visitor, CancellationToken cancellationToken)
    {
        var boundary = new NativeWindowCallbackBoundary(visitor);
        var callback = new NativeMethods.EnumWindowsProc((windowHandle, _) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            return boundary.Visit(windowHandle);
        });

        var completed = windowEnumeration.EnumerateWindows(callback);
        GC.KeepAlive(callback);
        cancellationToken.ThrowIfCancellationRequested();
        boundary.ThrowIfCaptured();
        if (!completed)
        {
            throw new Win32Exception(windowEnumeration.GetLastError(), "枚举顶层窗口失败。");
        }
    }

    /// <summary>读取过滤所需的轻量原生字段；窗口消失时返回失败。</summary>
    public bool TryReadClassification(nint windowHandle, out NativeWindowClassification classification)
    {
        classification = default!;
        if (NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId) == 0 || processId == 0)
        {
            return false;
        }

        if (!windowProperties.TryGetOwner(windowHandle, out var owner) ||
            !windowProperties.TryGetExtendedStyle(windowHandle, out var extendedStyle))
        {
            return false;
        }

        classification = new NativeWindowClassification(
            windowHandle,
            (int)processId,
            ReadClassName(windowHandle),
            NativeMethods.IsWindowVisible(windowHandle),
            owner != 0,
            ((long)extendedStyle & NativeMethods.WsExToolWindow) != 0,
            IsCloaked(windowHandle));
        return true;
    }

    /// <summary>仅为通过分类的窗口读取进程、标题、边界和状态详情。</summary>
    public bool TryReadDetails(nint windowHandle, int processId, out NativeWindowDetails details)
    {
        details = default!;
        if (!NativeMethods.GetWindowRect(windowHandle, out var rectangle))
        {
            return false;
        }

        var placement = new WindowPlacement { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
        var state = NativeMethods.GetWindowPlacement(windowHandle, ref placement)
            ? MapState(placement.ShowCommand)
            : SceneWindowState.Normal;
        var process = ReadProcess(processId);
        details = new NativeWindowDetails(
            process.ExecutablePath,
            ReadTitleHint(windowHandle),
            rectangle.ToBounds(),
            state,
            process.WasElevatedOrInaccessible);
        return true;
    }

    /// <summary>读取进程路径并明确释放 Process；访问拒绝和退出竞态仅标记当前候选。</summary>
    private static ProcessSnapshot ReadProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var executablePath = process.MainModule?.FileName;
            return new ProcessSnapshot(executablePath, executablePath is null);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or Win32Exception)
        {
            return new ProcessSnapshot(null, true);
        }
    }

    /// <summary>读取固定上限的顶层标题提示，避免采集窗口内容。</summary>
    private static string? ReadTitleHint(nint windowHandle)
    {
        var text = new char[512];
        var length = NativeMethods.GetWindowText(windowHandle, text, text.Length);
        return length > 0 ? new string(text, 0, length) : null;
    }

    /// <summary>读取用于分类的窗口类名。</summary>
    private static string ReadClassName(nint windowHandle)
    {
        var text = new char[256];
        var length = NativeMethods.GetClassName(windowHandle, text, text.Length);
        return length > 0 ? new string(text, 0, length) : string.Empty;
    }

    /// <summary>读取 DWM cloaked 标记；API 失败时不把窗口误判为隐藏。</summary>
    private static bool IsCloaked(nint windowHandle)
    {
        return NativeMethods.DwmGetWindowAttribute(windowHandle, NativeMethods.DwmwaCloaked, out var value, sizeof(int)) == 0 && value != 0;
    }

    /// <summary>把 Win32 show command 映射为可恢复场景状态。</summary>
    private static SceneWindowState MapState(uint showCommand)
    {
        return showCommand switch
        {
            2 or 6 or 7 or 11 => SceneWindowState.Minimized,
            3 => SceneWindowState.Maximized,
            _ => SceneWindowState.Normal
        };
    }

    /// <summary>保存一次进程路径读取结果及其安全降级状态。</summary>
    /// <param name="ExecutablePath">成功读取的可执行文件路径。</param>
    /// <param name="WasElevatedOrInaccessible">进程是否不可访问或已退出。</param>
    private sealed record ProcessSnapshot(string? ExecutablePath, bool WasElevatedOrInaccessible);
}

internal sealed class NativeWindowCallbackBoundary
{
    private readonly Action<nint> visitor;
    private ExceptionDispatchInfo? capturedException;

    /// <summary>创建不会让托管异常越过 reverse P/Invoke 的 callback 边界。</summary>
    /// <param name="visitor">在托管层访问单个 HWND 的委托。</param>
    internal NativeWindowCallbackBoundary(Action<nint> visitor)
    {
        this.visitor = visitor;
    }

    /// <summary>访问一个 HWND；异常时捕获原异常并要求 EnumWindows 立即停止。</summary>
    internal bool Visit(nint windowHandle)
    {
        try
        {
            visitor(windowHandle);
            return true;
        }
        catch (Exception exception)
        {
            // 任何托管异常都不能越过 native callback；原异常在 EnumWindows 返回后重新抛出。
            capturedException = ExceptionDispatchInfo.Capture(exception);
            return false;
        }
    }

    /// <summary>在纯托管边界重新抛出 callback 捕获的原异常并保留其堆栈。</summary>
    internal void ThrowIfCaptured() => capturedException?.Throw();
}

internal interface IWindowNativeFacade
{
    /// <summary>依次访问当前桌面的顶层 HWND。</summary>
    void EnumerateTopLevelWindows(Action<nint> visitor, CancellationToken cancellationToken);

    /// <summary>尝试读取窗口过滤所需的轻量分类字段。</summary>
    bool TryReadClassification(nint windowHandle, out NativeWindowClassification classification);

    /// <summary>尝试读取通过分类后才允许访问的窗口详情。</summary>
    bool TryReadDetails(nint windowHandle, int processId, out NativeWindowDetails details);
}

/// <summary>保存过滤窗口所需且不涉及进程路径或标题的字段。</summary>
/// <param name="Handle">借用的顶层窗口句柄。</param>
/// <param name="ProcessId">窗口所属的瞬时进程标识。</param>
/// <param name="WindowClass">窗口类名。</param>
/// <param name="IsVisible">窗口是否具有可见样式。</param>
/// <param name="IsOwned">窗口是否具有所有者。</param>
/// <param name="IsToolWindow">窗口是否为工具窗口。</param>
/// <param name="IsCloaked">窗口是否被 DWM 隐藏。</param>
internal sealed record NativeWindowClassification(
    nint Handle,
    int ProcessId,
    string WindowClass,
    bool IsVisible,
    bool IsOwned,
    bool IsToolWindow,
    bool IsCloaked);

/// <summary>保存通过过滤后允许读取的窗口详情。</summary>
/// <param name="ExecutablePath">可执行文件路径。</param>
/// <param name="Title">固定上限的标题提示。</param>
/// <param name="Bounds">窗口边界。</param>
/// <param name="State">窗口显示状态。</param>
/// <param name="WasElevatedOrInaccessible">进程路径是否因权限或竞态不可访问。</param>
internal sealed record NativeWindowDetails(
    string? ExecutablePath,
    string? Title,
    WindowBounds Bounds,
    SceneWindowState State,
    bool WasElevatedOrInaccessible);

/// <summary>保存 native 边界在单一时刻可安全读取的窗口字段。</summary>
/// <param name="Handle">借用的顶层窗口句柄。</param>
/// <param name="ProcessId">窗口所属的瞬时进程标识。</param>
/// <param name="ExecutablePath">可执行文件路径。</param>
/// <param name="WindowClass">窗口类名。</param>
/// <param name="Title">固定上限的标题提示。</param>
/// <param name="Bounds">窗口边界。</param>
/// <param name="State">窗口显示状态。</param>
/// <param name="IsVisible">窗口是否具有可见样式。</param>
/// <param name="IsOwned">窗口是否具有所有者。</param>
/// <param name="IsToolWindow">窗口是否为工具窗口。</param>
/// <param name="IsCloaked">窗口是否被 DWM 隐藏。</param>
/// <param name="WasElevatedOrInaccessible">进程路径是否因权限或竞态不可访问。</param>
internal sealed record NativeWindowSnapshot(
    nint Handle,
    int ProcessId,
    string? ExecutablePath,
    string WindowClass,
    string? Title,
    WindowBounds Bounds,
    SceneWindowState State,
    bool IsVisible,
    bool IsOwned,
    bool IsToolWindow,
    bool IsCloaked,
    bool WasElevatedOrInaccessible)
{
    /// <summary>把两阶段 native 读取结果组合为仅供 production mapper 使用的快照。</summary>
    internal NativeWindowSnapshot(NativeWindowClassification classification, NativeWindowDetails details)
        : this(
            classification.Handle,
            classification.ProcessId,
            details.ExecutablePath,
            classification.WindowClass,
            details.Title,
            details.Bounds,
            details.State,
            classification.IsVisible,
            classification.IsOwned,
            classification.IsToolWindow,
            classification.IsCloaked,
            details.WasElevatedOrInaccessible)
    {
    }
}
