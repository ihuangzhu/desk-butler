using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskButler.EndToEnd;

/// <summary>抽象已持有的原始进程句柄，使 PID 复用安全性可确定性测试。</summary>
internal interface IFixtureProcessLifetime : IDisposable
{
    bool HasExited { get; }

    int ProcessId { get; }

    string ExecutablePath { get; }

    DateTime StartTimeUtc { get; }

    nint MainWindowHandle { get; }

    void Refresh();

    void CloseMainWindow();

    Task WaitForExitAsync(CancellationToken cancellationToken);

    void TerminateHeldProcess();
}

/// <summary>拥有一个已验证唯一 exe 路径的原始 Process 与 SafeProcessHandle。</summary>
internal sealed class FixtureProcessLease : IAsyncDisposable
{
    private readonly IFixtureProcessLifetime lifetime;
    private bool disposed;

    /// <summary>注入已持有的身份对象，供 PID 复用回归测试使用。</summary>
    internal FixtureProcessLease(IFixtureProcessLifetime lifetime)
    {
        this.lifetime = lifetime;
    }

    internal int ProcessId => lifetime.ProcessId;

    /// <summary>以 PID、启动时间、exe 路径和仍存活原句柄共同判断同一进程身份。</summary>
    internal bool RepresentsRunningIdentity(int processId, string executablePath, DateTime startTimeUtc) =>
        !lifetime.HasExited &&
        lifetime.ProcessId == processId &&
        lifetime.StartTimeUtc == startTimeUtc &&
        StringComparer.OrdinalIgnoreCase.Equals(
            Path.GetFullPath(lifetime.ExecutablePath), Path.GetFullPath(executablePath));

    /// <summary>等待原始进程产生主窗口，不通过 PID 枚举其他进程。</summary>
    internal async Task<nint> WaitForMainWindowAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (lifetime.HasExited)
            {
                throw new InvalidOperationException("fixture 在主窗口出现前退出。");
            }

            lifetime.Refresh();
            if (lifetime.MainWindowHandle != 0)
            {
                return lifetime.MainWindowHandle;
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException("等待 fixture 主窗口超时。");
    }

    /// <summary>从刚启动或刚发现的 Process 立即校验 exe，并持续持有其原始句柄。</summary>
    internal static FixtureProcessLease Create(Process process, string expectedExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(process);
        var actualPath = process.MainModule?.FileName
            ?? throw new InvalidOperationException("fixture 进程没有可验证的主模块路径。");
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetFullPath(actualPath), Path.GetFullPath(expectedExecutablePath)))
        {
            process.Dispose();
            throw new InvalidOperationException("fixture 进程主模块不属于本轮唯一复制路径。");
        }

        return new FixtureProcessLease(new ProcessFixtureLifetime(
            process,
            Path.GetFullPath(actualPath),
            process.StartTime.ToUniversalTime(),
            process.SafeHandle));
    }

    /// <summary>只操作仍由原始句柄代表的进程；它已退出时不按 PID 再解析。</summary>
    internal async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (disposed || lifetime.HasExited)
        {
            return;
        }

        lifetime.CloseMainWindow();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await lifetime.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!lifetime.HasExited)
            {
                lifetime.TerminateHeldProcess();
                await lifetime.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>关闭并释放原始 Process/SafeProcessHandle，绝不重新打开 PID。</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            await CloseAsync(CancellationToken.None);
        }
        finally
        {
            disposed = true;
            lifetime.Dispose();
        }
    }

    /// <summary>封装真实进程身份；终止直接使用创建时持有的 SafeProcessHandle。</summary>
    private sealed class ProcessFixtureLifetime(
        Process process,
        string executablePath,
        DateTime startTimeUtc,
        SafeProcessHandle safeHandle) : IFixtureProcessLifetime
    {
        // 这些身份字段刻意与句柄共同存活，便于诊断且不依赖后来可能复用的 PID。
        private readonly SafeProcessHandle safeHandle = safeHandle;

        public bool HasExited => process.HasExited;

        public int ProcessId => process.Id;

        public string ExecutablePath { get; } = executablePath;

        public DateTime StartTimeUtc { get; } = startTimeUtc;

        public nint MainWindowHandle => process.MainWindowHandle;

        /// <summary>刷新原始 Process 缓存以读取刚创建的主窗口句柄。</summary>
        public void Refresh() => process.Refresh();

        /// <summary>向原始进程的主窗口请求正常退出。</summary>
        public void CloseMainWindow() => _ = process.CloseMainWindow();

        /// <summary>等待原始 Process 对象对应的内核对象退出。</summary>
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);

        /// <summary>仅通过创建时持有的 SafeProcessHandle 终止 fixture，不终止子树。</summary>
        public void TerminateHeldProcess()
        {
            if (!TerminateProcess(safeHandle, 1))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(),
                    $"无法终止 fixture {ExecutablePath}（启动于 {StartTimeUtc:O}）。");
            }
        }

        /// <summary>释放原始 Process 及其 SafeProcessHandle。</summary>
        public void Dispose() => process.Dispose();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(SafeProcessHandle processHandle, uint exitCode);
    }
}
