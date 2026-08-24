using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using DeskButler.Infrastructure.Windows.Startup;

namespace DeskButler.Desktop.Hosting;

/// <summary>向唯一 DeskButler 实例请求受控退出，并在退出后清理自己的启动项。</summary>
internal sealed class PrepareUninstallCoordinator(
    Func<IDisposable?> tryAcquireSingleInstance,
    IUninstallRequestClient requestClient,
    IProcessExitWaiter processExitWaiter,
    IStartupRegistration startupRegistration,
    TimeSpan timeout,
    bool removeStartupRegistration = true)
{
    /// <summary>执行幂等卸载准备；运行实例未按时退出时失败且不伪装成功。</summary>
    internal async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var lease = tryAcquireSingleInstance();
        if (lease is not null)
        {
            if (removeStartupRegistration)
            {
                startupRegistration.Disable();
            }
            return;
        }

        var processId = await requestClient.RequestExitAsync(timeout, cancellationToken);
        if (processId <= 0 || processId == Environment.ProcessId)
        {
            throw new InvalidOperationException("卸载退出通道返回了无效的 DeskButler 进程身份。");
        }

        if (!await processExitWaiter.WaitForExitAsync(processId, timeout, cancellationToken))
        {
            throw new TimeoutException("DeskButler 运行实例未在卸载准备超时内干净退出。");
        }

        using var exitProof = tryAcquireSingleInstance()
            ?? throw new InvalidOperationException("目标进程已退出，但 DeskButler 单实例互斥量仍被占用。");

        if (removeStartupRegistration)
        {
            startupRegistration.Disable();
        }
    }
}

/// <summary>发送唯一固定的卸载准备请求。</summary>
internal interface IUninstallRequestClient
{
    /// <summary>请求运行实例干净退出，并返回被请求实例的进程号。</summary>
    Task<int> RequestExitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>等待已明确识别的单个进程自然退出。</summary>
internal interface IProcessExitWaiter
{
    /// <summary>在限时内等待指定进程退出，绝不终止该进程。</summary>
    Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>通过当前用户专属命名管道发送固定卸载请求。</summary>
internal sealed class NamedPipeUninstallRequestClient(string pipeName, string expectedExecutablePath)
    : IUninstallRequestClient
{
    internal const string ProtocolRequest = "prepare-uninstall-v1";

    /// <inheritdoc />
    public async Task<int> RequestExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        await using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeoutSource.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverProcessId) ||
                serverProcessId == 0)
            {
                throw new InvalidDataException(
                    "无法从 Windows 命名管道句柄取得 DeskButler 服务端进程身份。",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }

            VerifyServerExecutable(checked((int)serverProcessId), expectedExecutablePath);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(ProtocolRequest.AsMemory(), timeoutSource.Token);
            var response = await reader.ReadLineAsync(timeoutSource.Token);
            var responseProcessId = int.TryParse(response, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var processId)
                ? processId
                : throw new InvalidDataException("卸载退出通道返回了无效响应。");
            if (responseProcessId != checked((int)serverProcessId))
            {
                throw new InvalidDataException("卸载退出通道响应的 PID 与 Windows 识别的服务端 PID 不一致。");
            }
            return responseProcessId;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("无法在限时内连接 DeskButler 卸载退出通道。");
        }
    }

    /// <summary>验证服务端进程的可执行文件与当前维护客户端完全相同。</summary>
    private static void VerifyServerExecutable(int processId, string expectedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPath);
        using var process = Process.GetProcessById(processId);
        var actualPath = process.MainModule?.FileName
            ?? throw new InvalidDataException("无法解析 DeskButler 管道服务端可执行文件路径。");
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetFullPath(actualPath), Path.GetFullPath(expectedPath)))
        {
            throw new InvalidDataException("命名管道服务端不是当前 DeskButler 可执行文件。");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);
}

/// <summary>为当前 Windows 用户和登录会话派生稳定且不泄露 SID 的管道名称。</summary>
internal static class UninstallPipeName
{
    /// <summary>取得当前用户当前会话的卸载管道名称。</summary>
    internal static string ForCurrentSession()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("无法取得当前 Windows 用户 SID。");
        return Create(sid, Process.GetCurrentProcess().SessionId);
    }

    /// <summary>从显式 SID 与会话号生成仅含安全字符的稳定管道名称。</summary>
    internal static string Create(string sid, int sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        ArgumentOutOfRangeException.ThrowIfNegative(sessionId);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)));
        return $"DeskButler.PrepareUninstall.v1.{hash}.{sessionId}";
    }
}

/// <summary>只等待指定 PID 的真实进程句柄，不按名称查找或终止任何进程。</summary>
internal sealed class ProcessExitWaiter : IProcessExitWaiter
{
    /// <inheritdoc />
    public async Task<bool> WaitForExitAsync(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (process)
        using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }
    }
}

/// <summary>当前用户进程内仅接收固定卸载请求的命名管道服务端。</summary>
internal sealed class UninstallRequestServer : IAsyncDisposable
{
    private readonly string pipeName;
    private readonly Action requestExit;
    private readonly TimeSpan sessionTimeout;
    private readonly CancellationTokenSource stopSource = new();
    private Task? worker;

    /// <summary>创建固定协议的卸载请求服务端。</summary>
    internal UninstallRequestServer(
        string pipeName,
        Action requestExit,
        TimeSpan? sessionTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        this.pipeName = pipeName;
        this.requestExit = requestExit ?? throw new ArgumentNullException(nameof(requestExit));
        this.sessionTimeout = sessionTimeout ?? TimeSpan.FromSeconds(2);
        if (this.sessionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionTimeout));
        }
    }

    /// <summary>开始循环等待卸载请求；重复启动会被拒绝。</summary>
    internal void Start()
    {
        if (worker is not null)
        {
            throw new InvalidOperationException("卸载请求服务端已经启动。");
        }

        worker = RunAsync(stopSource.Token);
    }

    /// <summary>停止等待并释放命名管道。</summary>
    public async ValueTask DisposeAsync()
    {
        stopSource.Cancel();
        if (worker is not null)
        {
            try
            {
                await worker;
            }
            catch (OperationCanceledException) when (stopSource.IsCancellationRequested)
            {
            }
        }
        stopSource.Dispose();
    }

    /// <summary>逐会话验证固定协议；无效或断连会话不会终止服务端。</summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        while (!cancellationToken.IsCancellationRequested)
        {
            var accepted = false;
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                accepted = await HandleSessionAsync(pipe, cancellationToken);
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // 恶意或不完整客户端只结束当前会话，同一首个服务端实例继续等待。
            }
            finally
            {
                DisconnectSession(pipe);
            }

            if (accepted)
            {
                requestExit();
                return;
            }
        }
    }

    /// <summary>处理一个客户端会话，仅合法请求返回 true 并触发受控退出。</summary>
    private async Task<bool> HandleSessionAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        using var sessionSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        sessionSource.CancelAfter(sessionTimeout);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
        string? request;
        try
        {
            request = await reader.ReadLineAsync(sessionSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        if (!StringComparer.Ordinal.Equals(request, NamedPipeUninstallRequestClient.ProtocolRequest))
        {
            return false;
        }
        await writer.WriteLineAsync(
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture).AsMemory(),
            cancellationToken);
        return true;
    }

    /// <summary>安全断开当前会话，同时保留最初创建的命名管道服务端实例。</summary>
    private static void DisconnectSession(NamedPipeServerStream pipe)
    {
        try
        {
            pipe.Disconnect();
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
