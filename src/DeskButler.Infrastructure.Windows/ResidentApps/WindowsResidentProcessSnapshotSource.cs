using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DeskButler.Core.ResidentApps;
using DeskButler.Infrastructure.Windows.Native;
using DeskButler.Infrastructure.Windows.Windows;

namespace DeskButler.Infrastructure.Windows.ResidentApps;

internal interface IResidentProcessReader
{
    /// <summary>读取 DeskButler 当前 Windows Session 标识。</summary>
    int GetCurrentSessionId();

    /// <summary>枚举进程包装；每个包装及其底层句柄由调用方释放。</summary>
    IReadOnlyList<IResidentProcess> GetProcesses();
}

internal interface IResidentProcess : IDisposable
{
    bool HasExited { get; }

    int ProcessId { get; }

    int SessionId { get; }

    /// <summary>只读取主模块的绝对 exe 路径。</summary>
    string GetExecutablePath();

    /// <summary>只读取 ProductName、CompanyName 和 FileDescription。</summary>
    ResidentFileVersionInfo? GetFileVersionInfo(string executablePath);
}

/// <summary>保存允许从文件版本资源读取的三项公开文本。</summary>
internal sealed record ResidentFileVersionInfo(string? ProductName, string? CompanyName, string? FileDescription);

/// <summary>保存无标题、无窗口类名的顶层窗口分类。</summary>
internal sealed record ResidentTopLevelWindow(
    int ProcessId,
    bool IsVisible,
    bool IsOwned,
    bool IsToolWindow,
    bool IsCloaked);

internal interface IResidentWindowReader
{
    /// <summary>读取当前桌面顶层窗口的最小分类字段。</summary>
    IReadOnlyList<ResidentTopLevelWindow> Read(CancellationToken cancellationToken);
}

/// <summary>定义进程观察实际需要的最小 Win32 窗口分类调用边界。</summary>
internal interface IResidentWindowNativeApi
{
    /// <summary>枚举当前桌面顶层 HWND，callback 返回 false 时停止。</summary>
    bool EnumerateWindows(NativeMethods.EnumWindowsProc callback);

    /// <summary>读取 EnumWindows 失败时保存的 Win32 错误码。</summary>
    int GetLastError();

    /// <summary>读取窗口所属 PID；窗口已失效时返回 false。</summary>
    bool TryGetProcessId(nint windowHandle, out int processId);

    /// <summary>读取窗口可见分类。</summary>
    bool IsWindowVisible(nint windowHandle);

    /// <summary>读取 owner 分类，并消解零返回歧义。</summary>
    bool TryGetOwner(nint windowHandle, out nint owner);

    /// <summary>读取扩展样式，并消解零返回歧义。</summary>
    bool TryGetExtendedStyle(nint windowHandle, out nint extendedStyle);

    /// <summary>读取 DWM cloaked 分类；API 失败时返回 false。</summary>
    bool IsCloaked(nint windowHandle);
}

/// <summary>使用 Task 3 的 P/Invoke 与零返回消歧约定实现最小窗口分类边界。</summary>
internal sealed class WindowsResidentWindowNativeApi : IResidentWindowNativeApi
{
    private readonly NativeWindowPropertyReader windowProperties = new();
    private readonly WindowEnumerationNativeApi windowEnumeration = new();

    /// <summary>调用 Win32 EnumWindows；callback 生命周期由调用方保持到返回后。</summary>
    public bool EnumerateWindows(NativeMethods.EnumWindowsProc callback) =>
        windowEnumeration.EnumerateWindows(callback);

    /// <summary>读取最近 P/Invoke 保存的 Win32 错误码。</summary>
    public int GetLastError() => windowEnumeration.GetLastError();

    /// <summary>读取 HWND 所属 PID，零值表示窗口已失效或没有可用进程归属。</summary>
    public bool TryGetProcessId(nint windowHandle, out int processId)
    {
        processId = 0;
        if (NativeMethods.GetWindowThreadProcessId(windowHandle, out var nativeProcessId) == 0 || nativeProcessId == 0)
        {
            return false;
        }

        processId = unchecked((int)nativeProcessId);
        return processId > 0;
    }

    /// <summary>读取窗口可见样式，不读取窗口标题或内容。</summary>
    public bool IsWindowVisible(nint windowHandle) => NativeMethods.IsWindowVisible(windowHandle);

    /// <summary>通过既有 reader 读取 owner，保持借用句柄和零返回规则。</summary>
    public bool TryGetOwner(nint windowHandle, out nint owner) => windowProperties.TryGetOwner(windowHandle, out owner);

    /// <summary>通过既有 reader 读取扩展样式，保持零返回规则。</summary>
    public bool TryGetExtendedStyle(nint windowHandle, out nint extendedStyle) =>
        windowProperties.TryGetExtendedStyle(windowHandle, out extendedStyle);

    /// <summary>读取 DWM cloaked 标记；失败时保守地不把窗口标记为 cloaked。</summary>
    public bool IsCloaked(nint windowHandle) =>
        NativeMethods.DwmGetWindowAttribute(
            windowHandle,
            NativeMethods.DwmwaCloaked,
            out var cloaked,
            sizeof(int)) == 0 && cloaked != 0;
}

internal sealed class WindowsResidentProcessReader : IResidentProcessReader
{
    /// <summary>短暂持有当前 Process 包装以读取当前 Windows Session。</summary>
    public int GetCurrentSessionId()
    {
        using var process = Process.GetCurrentProcess();
        return process.SessionId;
    }

    /// <summary>包装系统进程快照，使上层能统一释放自身拥有的 Process 句柄。</summary>
    public IReadOnlyList<IResidentProcess> GetProcesses() =>
        Process.GetProcesses().Select(process => (IResidentProcess)new WindowsResidentProcess(process)).ToArray();
}

internal sealed class WindowsResidentProcess(Process process) : IResidentProcess
{
    public bool HasExited => process.HasExited;

    public int ProcessId => process.Id;

    public int SessionId => process.SessionId;

    /// <summary>读取 Process.MainModule 的文件名，并立即释放临时模块包装。</summary>
    public string GetExecutablePath()
    {
        using var module = process.MainModule;
        return module?.FileName ?? throw new InvalidOperationException("进程主模块路径不可用。");
    }

    /// <summary>从已经读取的 exe 文件只提取三项公开版本元数据。</summary>
    public ResidentFileVersionInfo? GetFileVersionInfo(string executablePath)
    {
        var information = FileVersionInfo.GetVersionInfo(executablePath);
        return new ResidentFileVersionInfo(information.ProductName, information.CompanyName, information.FileDescription);
    }

    /// <summary>释放 Process.GetProcesses 返回、由本包装拥有的进程句柄。</summary>
    public void Dispose()
    {
        process.Dispose();
    }
}

internal sealed class WindowsResidentWindowReader : IResidentWindowReader
{
    private readonly IResidentWindowNativeApi native;

    /// <summary>创建使用真实 Win32 顶层窗口枚举的读取器。</summary>
    internal WindowsResidentWindowReader()
        : this(new WindowsResidentWindowNativeApi())
    {
    }

    /// <summary>创建使用可控最小原生边界的读取器，供测试验证窗口分类映射。</summary>
    internal WindowsResidentWindowReader(IResidentWindowNativeApi native)
    {
        this.native = native;
    }

    /// <summary>枚举顶层窗口并只保留进程关联与可见、owner、tool、cloaked 分类。</summary>
    public IReadOnlyList<ResidentTopLevelWindow> Read(CancellationToken cancellationToken)
    {
        var windows = new List<ResidentTopLevelWindow>();
        var boundary = new NativeWindowCallbackBoundary(windowHandle =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryRead(windowHandle, out var window))
            {
                windows.Add(window);
            }
        });
        var callback = new NativeMethods.EnumWindowsProc((windowHandle, _) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            return boundary.Visit(windowHandle);
        });

        var completed = native.EnumerateWindows(callback);
        GC.KeepAlive(callback);
        cancellationToken.ThrowIfCancellationRequested();
        boundary.ThrowIfCaptured();
        if (!completed)
        {
            throw new Win32Exception(native.GetLastError(), "枚举顶层窗口失败。");
        }

        return windows;
    }

    /// <summary>读取单一 HWND 的最小分类字段；消失或零返回歧义时仅跳过该窗口。</summary>
    private bool TryRead(nint windowHandle, out ResidentTopLevelWindow window)
    {
        window = default!;
        if (!native.TryGetProcessId(windowHandle, out var processId) ||
            !native.TryGetOwner(windowHandle, out var owner) ||
            !native.TryGetExtendedStyle(windowHandle, out var extendedStyle))
        {
            return false;
        }

        window = new ResidentTopLevelWindow(
            (int)processId,
            native.IsWindowVisible(windowHandle),
            owner != 0,
            ((long)extendedStyle & NativeMethods.WsExToolWindow) != 0,
            native.IsCloaked(windowHandle));
        return true;
    }
}

/// <summary>捕获当前交互 Session 的进程公开元数据和顶层窗口分类。</summary>
internal sealed class WindowsResidentProcessSnapshotSource : IResidentProcessSnapshotSource
{
    private readonly IResidentProcessReader processReader;
    private readonly IResidentWindowReader windowReader;

    /// <summary>创建使用真实进程和 Win32 窗口边界的观察源。</summary>
    internal WindowsResidentProcessSnapshotSource()
        : this(new WindowsResidentProcessReader(), new WindowsResidentWindowReader())
    {
    }

    /// <summary>创建使用可控进程和窗口边界的观察源。</summary>
    internal WindowsResidentProcessSnapshotSource(
        IResidentProcessReader processReader,
        IResidentWindowReader windowReader)
    {
        this.processReader = processReader;
        this.windowReader = windowReader;
    }

    /// <summary>只观察当前非 Session 0 的进程，单项异常降级为无敏感负载的分类诊断。</summary>
    public Task<ResidentProcessSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var traitsByProcess = CollectWindowTraits(windowReader.Read(cancellationToken), cancellationToken);
        var currentSessionId = processReader.GetCurrentSessionId();
        if (currentSessionId == 0)
        {
            // 服务 Session 没有交互用户；即使当前进程运行其中也绝不扩大观察范围。
            return Task.FromResult(EmptySnapshot());
        }

        var observations = ImmutableArray.CreateBuilder<ResidentProcessObservation>();
        var diagnostics = ImmutableArray.CreateBuilder<ResidentDiscoveryDiagnostic>();
        var processes = processReader.GetProcesses();
        foreach (var process in processes)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                CaptureProcess(process, currentSessionId, traitsByProcess, observations, diagnostics, cancellationToken);
            }
            finally
            {
                // 仅释放 Process.GetProcesses 返回、由本 source 持有的包装；不操作第三方进程。
                process.Dispose();
            }
        }

        return Task.FromResult(new ResidentProcessSnapshot(
            observations
                .OrderBy(observation => observation.ProcessId)
                .ThenBy(observation => observation.ExecutablePath, StringComparer.Ordinal)
                .ToImmutableArray(),
            diagnostics.OrderBy(diagnostic => diagnostic.Kind).ToImmutableArray()));
    }

    /// <summary>把按 HWND 观察的顶层窗口分类按 PID 聚合，不保留句柄或窗口内容。</summary>
    private static Dictionary<int, ResidentWindowTraits> CollectWindowTraits(
        IReadOnlyList<ResidentTopLevelWindow> windows,
        CancellationToken cancellationToken)
    {
        var traits = new Dictionary<int, ResidentWindowTraits>();
        foreach (var window in windows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (window.ProcessId <= 0)
            {
                continue;
            }

            traits.TryGetValue(window.ProcessId, out var current);
            traits[window.ProcessId] = (current ?? ResidentWindowTraits.None).Include(window);
        }

        return traits;
    }

    /// <summary>读取单个候选进程；预期访问与退出竞态只影响当前进程。</summary>
    private static void CaptureProcess(
        IResidentProcess process,
        int currentSessionId,
        Dictionary<int, ResidentWindowTraits> traitsByProcess,
        ImmutableArray<ResidentProcessObservation>.Builder observations,
        ImmutableArray<ResidentDiscoveryDiagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        int processId;
        try
        {
            if (process.HasExited)
            {
                diagnostics.Add(new ResidentDiscoveryDiagnostic(ResidentDiscoveryIssue.ProcessExited));
                return;
            }

            processId = process.ProcessId;
            var sessionId = process.SessionId;
            if (processId <= 0 || sessionId == 0 || sessionId != currentSessionId)
            {
                return;
            }
        }
        catch (Exception exception) when (IsAccessDenied(exception))
        {
            diagnostics.Add(new ResidentDiscoveryDiagnostic(ResidentDiscoveryIssue.AccessDenied));
            return;
        }
        catch (Exception exception) when (IsExited(exception, process))
        {
            diagnostics.Add(new ResidentDiscoveryDiagnostic(ResidentDiscoveryIssue.ProcessExited));
            return;
        }

        string executablePath;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            executablePath = NormalizeExecutablePath(process.GetExecutablePath());
        }
        catch (Exception exception) when (IsAccessDenied(exception))
        {
            diagnostics.Add(new ResidentDiscoveryDiagnostic(ResidentDiscoveryIssue.AccessDenied));
            return;
        }
        catch (Exception exception) when (IsExited(exception, process))
        {
            diagnostics.Add(new ResidentDiscoveryDiagnostic(ResidentDiscoveryIssue.ProcessExited));
            return;
        }
        catch (Exception exception) when (IsInvalidPath(exception))
        {
            diagnostics.Add(new ResidentDiscoveryDiagnostic(ResidentDiscoveryIssue.InvalidPath));
            return;
        }

        ResidentFileVersionInfo? versionInfo = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            versionInfo = process.GetFileVersionInfo(executablePath);
        }
        catch (Exception exception) when (IsAccessDenied(exception))
        {
            diagnostics.Add(new ResidentDiscoveryDiagnostic(ResidentDiscoveryIssue.AccessDenied));
        }
        catch (Exception exception) when (IsExited(exception, process))
        {
            diagnostics.Add(new ResidentDiscoveryDiagnostic(ResidentDiscoveryIssue.ProcessExited));
        }
        catch (Exception exception) when (IsMetadataUnavailable(exception))
        {
            diagnostics.Add(new ResidentDiscoveryDiagnostic(ResidentDiscoveryIssue.MetadataUnavailable));
        }

        observations.Add(new ResidentProcessObservation(
            processId,
            executablePath,
            versionInfo?.ProductName,
            versionInfo?.CompanyName,
            versionInfo?.FileDescription,
            traitsByProcess.TryGetValue(processId, out var traits) ? traits : ResidentWindowTraits.None));
    }

    /// <summary>只接受绝对 exe 路径，避免把相对或畸形进程模块路径交给后续发现层。</summary>
    private static string NormalizeExecutablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new IOException("进程主模块路径不可用。");
        }

        return Path.GetFullPath(path);
    }

    /// <summary>判断访问拒绝这一可预期的单进程隔离边界。</summary>
    private static bool IsAccessDenied(Exception exception) =>
        exception is UnauthorizedAccessException or Win32Exception { NativeErrorCode: 5 };

    /// <summary>把 Process 竞态映射为退出诊断，不让其污染其他进程。</summary>
    private static bool IsExited(Exception exception, IResidentProcess process)
    {
        if (exception is Win32Exception { NativeErrorCode: 6 })
        {
            return true;
        }

        if (exception is not InvalidOperationException and not ArgumentException)
        {
            return false;
        }

        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Win32Exception probeException)
        {
            // 仅 ERROR_INVALID_HANDLE 明确表示进程包装已无效；其它 Win32 失败必须由原调用传播。
            return probeException.NativeErrorCode == 6;
        }
    }

    /// <summary>将路径格式相关失败局限为当前进程的无载荷诊断。</summary>
    private static bool IsInvalidPath(Exception exception) =>
        exception is IOException or ArgumentException or NotSupportedException or PathTooLongException;

    /// <summary>将 PE 版本资源不可用映射为公开元数据缺失，而非读取更多内容补偿。</summary>
    private static bool IsMetadataUnavailable(Exception exception) =>
        exception is InvalidOperationException or IOException or BadImageFormatException or ArgumentException or NotSupportedException;

    /// <summary>创建不含进程条目和异常负载的空快照。</summary>
    private static ResidentProcessSnapshot EmptySnapshot() => new([], []);
}
