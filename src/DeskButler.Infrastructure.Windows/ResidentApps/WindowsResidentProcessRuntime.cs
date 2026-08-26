using System.ComponentModel;
using System.Diagnostics;
using DeskButler.Core.ResidentApps;

namespace DeskButler.Infrastructure.Windows.ResidentApps;

internal interface IResidentProcessInfo : IDisposable
{
    bool HasExited { get; }

    int SessionId { get; }

    string ProcessName { get; }

    /// <summary>仅在上层已按进程名筛选后读取主模块完整路径。</summary>
    string GetExecutablePath();
}

internal interface IResidentProcessCatalog
{
    /// <summary>读取 DeskButler 当前 Windows Session 标识。</summary>
    int GetCurrentSessionId();

    /// <summary>创建当前进程快照；返回对象及其句柄由调用方释放。</summary>
    IReadOnlyList<IResidentProcessInfo> GetProcesses();
}

internal interface IResidentProcessStarter
{
    /// <summary>发出进程启动请求，并把返回句柄所有权交给调用方。</summary>
    IDisposable? Start(ProcessStartInfo startInfo);
}

internal sealed class WindowsResidentProcessCatalog : IResidentProcessCatalog
{
    /// <summary>短暂持有当前 Process 包装以读取 SessionId，随后立即释放。</summary>
    public int GetCurrentSessionId()
    {
        using var current = Process.GetCurrentProcess();
        return current.SessionId;
    }

    /// <summary>包装系统进程快照，使上层可以在 finally 中统一释放所有 Process 句柄。</summary>
    public IReadOnlyList<IResidentProcessInfo> GetProcesses() =>
        Process.GetProcesses().Select(process => (IResidentProcessInfo)new WindowsResidentProcessInfo(process)).ToArray();
}

internal sealed class WindowsResidentProcessInfo(Process process) : IResidentProcessInfo
{
    public bool HasExited => process.HasExited;

    public int SessionId => process.SessionId;

    public string ProcessName => process.ProcessName;

    /// <summary>读取主模块路径；调用者已先按 Session 和文件名过滤，避免无关访问拒绝污染结果。</summary>
    public string GetExecutablePath()
    {
        using var module = process.MainModule;
        return module?.FileName
            ?? throw new Win32Exception("进程主模块路径不可用。");
    }

    /// <summary>释放枚举快照拥有的 Process 包装和底层句柄。</summary>
    public void Dispose()
    {
        process.Dispose();
    }
}

internal sealed class WindowsResidentProcessStarter : IResidentProcessStarter
{
    /// <summary>调用系统 Process.Start；返回的 Process 句柄由运行时立即释放。</summary>
    public IDisposable? Start(ProcessStartInfo startInfo) => Process.Start(startInfo);
}

public sealed class WindowsResidentProcessRuntime : IResidentProcessRuntime
{
    private readonly IResidentExecutablePolicy executablePolicy;
    private readonly IResidentProcessCatalog processCatalog;
    private readonly IResidentProcessStarter processStarter;

    /// <summary>创建使用真实 Windows 进程枚举和启动边界的运行时。</summary>
    public WindowsResidentProcessRuntime(IResidentExecutablePolicy executablePolicy)
        : this(executablePolicy, new WindowsResidentProcessCatalog(), new WindowsResidentProcessStarter())
    {
    }

    /// <summary>创建使用可控进程枚举和启动边界的运行时。</summary>
    internal WindowsResidentProcessRuntime(
        IResidentExecutablePolicy executablePolicy,
        IResidentProcessCatalog processCatalog,
        IResidentProcessStarter processStarter)
    {
        this.executablePolicy = executablePolicy;
        this.processCatalog = processCatalog;
        this.processStarter = processStarter;
    }

    /// <summary>只读取当前 Session 中名称可能匹配目标的进程路径，并显式区分 Unknown。</summary>
    public Task<ResidentRunningCheck> CheckRunningAsync(
        IReadOnlySet<string> knownProcessPaths,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var targets = NormalizeTargets(knownProcessPaths);
        if (targets.Paths.Count == 0)
        {
            return Task.FromResult(new ResidentRunningCheck(ResidentRunningState.NotRunning, null));
        }

        var currentSessionId = processCatalog.GetCurrentSessionId();
        var processes = processCatalog.GetProcesses();
        var unknown = false;
        try
        {
            foreach (var process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string processName;
                try
                {
                    if (process.HasExited || process.SessionId != currentSessionId)
                    {
                        continue;
                    }

                    // Process.ProcessName 已按契约去掉 .exe；保留产品名中的点，避免再次截断。
                    processName = process.ProcessName;
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                {
                    // 尚未证实名称可能匹配时，退出或访问失败的进程不能污染目标可靠性。
                    continue;
                }

                if (!targets.FileNames.Contains(processName))
                {
                    continue;
                }

                try
                {
                    var observedPath = Path.GetFullPath(process.GetExecutablePath());
                    if (targets.Paths.TryGetValue(observedPath, out var matchedPath))
                    {
                        return Task.FromResult(
                            new ResidentRunningCheck(ResidentRunningState.Running, matchedPath));
                    }
                }
                catch (Exception exception) when (
                    exception is UnauthorizedAccessException or Win32Exception or InvalidOperationException or IOException)
                {
                    // 只有已按文件名确认可能匹配的进程，路径读取失败才会把结论降级为 Unknown。
                    if (!HasExitedAfterPathFailure(process))
                    {
                        unknown = true;
                    }
                }
            }

            return Task.FromResult(
                new ResidentRunningCheck(
                    unknown ? ResidentRunningState.Unknown : ResidentRunningState.NotRunning,
                    null));
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>启动前重新执行同一安全策略，无参数发起启动并立即释放自身持有的 Process 句柄。</summary>
    public Task StartAsync(string executablePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 再次解析最终路径并检查 manifest，缩小设置保存到实际 Process.Start 之间的 TOCTOU 窗口。
        var validation = executablePolicy.Validate(executablePath);
        if (!validation.IsAllowed || string.IsNullOrWhiteSpace(validation.NormalizedPath) ||
            !Path.IsPathFullyQualified(validation.NormalizedPath))
        {
            throw new InvalidOperationException($"常驻应用启动路径未通过安全验证：{validation.Reason}。");
        }

        var normalizedPath = Path.GetFullPath(validation.NormalizedPath);
        var workingDirectory = Path.GetDirectoryName(normalizedPath)
            ?? throw new InvalidOperationException("常驻应用启动路径没有工作目录。");
        var startInfo = new ProcessStartInfo(normalizedPath)
        {
            UseShellExecute = true,
            WorkingDirectory = workingDirectory
        };

        cancellationToken.ThrowIfCancellationRequested();
        using var startedProcess = processStarter.Start(startInfo)
            ?? throw new InvalidOperationException("系统未返回常驻应用进程句柄。");
        // 只释放 DeskButler 自己拥有的 Process 包装；绝不等待、关闭窗口或终止第三方进程。
        return Task.CompletedTask;
    }

    /// <summary>正规化已知完整路径，并独立建立允许触发 MainModule 读取的文件名集合。</summary>
    private static ResidentProcessTargets NormalizeTargets(IReadOnlySet<string> knownProcessPaths)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in knownProcessPaths)
        {
            try
            {
                if (!Path.IsPathFullyQualified(path))
                {
                    continue;
                }

                var normalized = Path.GetFullPath(path);
                var fileName = Path.GetFileNameWithoutExtension(normalized);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                paths.TryAdd(normalized, normalized);
                fileNames.Add(fileName);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Core contract 之外的畸形路径不会扩大枚举范围，也不会读取任何进程模块。
            }
        }

        return new ResidentProcessTargets(paths, fileNames);
    }

    /// <summary>路径读取竞态失败后重新确认进程是否已退出；无法确认仍按 Unknown 处理。</summary>
    private static bool HasExitedAfterPathFailure(IResidentProcessInfo process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private sealed record ResidentProcessTargets(
        Dictionary<string, string> Paths,
        HashSet<string> FileNames);
}
