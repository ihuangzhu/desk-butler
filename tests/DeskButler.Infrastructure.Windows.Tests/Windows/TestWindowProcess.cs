using System.Diagnostics;

namespace DeskButler.Infrastructure.Windows.Tests.Windows;

internal sealed class TestWindowProcess : IAsyncDisposable
{
    private readonly Process process;

    /// <summary>包装已启动的受控 WPF 测试进程。</summary>
    private TestWindowProcess(Process process)
    {
        this.process = process;
    }

    internal int ProcessId => process.Id;

    /// <summary>启动测试窗口并等待主 HWND 可供真实 Win32 枚举。</summary>
    internal static async Task<TestWindowProcess> StartAsync(string title)
    {
        var executablePath = FindExecutablePath();
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--title");
        startInfo.ArgumentList.Add(title);
        startInfo.ArgumentList.Add("--left");
        startInfo.ArgumentList.Add("140");
        startInfo.ArgumentList.Add("--top");
        startInfo.ArgumentList.Add("160");
        startInfo.ArgumentList.Add("--width");
        startInfo.ArgumentList.Add("720");
        startInfo.ArgumentList.Add("--height");
        startInfo.ArgumentList.Add("520");

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动受控测试窗口。");
        var wrapper = new TestWindowProcess(process);
        try
        {
            await wrapper.WaitForMainWindowAsync();
            return wrapper;
        }
        catch
        {
            await wrapper.DisposeAsync();
            throw;
        }
    }

    /// <summary>关闭测试窗口并确保进程资源和子进程均被释放。</summary>
    public async ValueTask DisposeAsync()
    {
        await DisposeLifetimeAsync(new ProcessLifetime(process));
    }

    /// <summary>关闭测试窗口，并在超时竞态中重新确认进程仍存活后才强制终止。</summary>
    internal static async Task DisposeLifetimeAsync(ITestProcessLifetime lifetime)
    {
        try
        {
            if (!lifetime.HasExited)
            {
                lifetime.CloseMainWindow();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await lifetime.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        if (!lifetime.HasExited)
                        {
                            lifetime.Kill();
                            await lifetime.WaitForExitAsync(CancellationToken.None);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // 进程可能在二次检查与 Kill 之间退出；此竞态等同于清理成功。
                    }
                }
            }
        }
        catch (InvalidOperationException)
        {
            // 读取退出状态或关闭主窗口时进程已退出，无需继续强制终止。
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    /// <summary>轮询主窗口句柄，隔离 WPF 启动时序而不依赖固定休眠。</summary>
    private async Task WaitForMainWindowAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"测试窗口过早退出，代码 {process.ExitCode}。");
            }

            process.Refresh();
            if (process.MainWindowHandle != 0)
            {
                return;
            }

            await Task.Delay(50, timeout.Token);
        }

        throw new TimeoutException("等待受控测试窗口主句柄超时。");
    }

    /// <summary>从测试输出目录定位同配置构建的 WPF 测试应用。</summary>
    private static string FindExecutablePath()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = output.Parent?.Name ?? "Debug";
        var repository = output;
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "DeskButler.slnx")))
        {
            repository = repository.Parent;
        }

        if (repository is null)
        {
            throw new DirectoryNotFoundException("无法从测试输出目录定位 DeskButler 仓库根目录。");
        }

        return Path.Combine(
            repository.FullName,
            "tests",
            "DeskButler.Infrastructure.Windows.Tests",
            "TestApps",
            "DeskButler.TestWindow",
            "bin",
            configuration,
            "net10.0-windows10.0.17763.0",
            "DeskButler.TestWindow.exe");
    }

    private sealed class ProcessLifetime : ITestProcessLifetime
    {
        private readonly Process process;

        /// <summary>包装真实测试进程的清理期操作。</summary>
        /// <param name="process">由测试 helper 拥有的进程。</param>
        internal ProcessLifetime(Process process)
        {
            this.process = process;
        }

        public bool HasExited => process.HasExited;

        /// <summary>请求 WPF 主窗口正常关闭。</summary>
        public void CloseMainWindow()
        {
            process.CloseMainWindow();
        }

        /// <summary>等待进程退出或取消。</summary>
        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            return process.WaitForExitAsync(cancellationToken);
        }

        /// <summary>强制终止测试进程树。</summary>
        public void Kill()
        {
            process.Kill(entireProcessTree: true);
        }

        /// <summary>释放 Process 持有的操作系统资源。</summary>
        public void Dispose()
        {
            process.Dispose();
        }
    }
}

internal interface ITestProcessLifetime : IDisposable
{
    bool HasExited { get; }

    /// <summary>请求主窗口正常关闭。</summary>
    void CloseMainWindow();

    /// <summary>等待进程退出或取消。</summary>
    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>强制终止测试进程。</summary>
    void Kill();
}
