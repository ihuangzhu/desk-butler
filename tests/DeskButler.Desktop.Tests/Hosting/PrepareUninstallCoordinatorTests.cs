using DeskButler.Desktop.Hosting;
using DeskButler.Infrastructure.Windows.Startup;

namespace DeskButler.Desktop.Tests.Hosting;

public sealed class PrepareUninstallCoordinatorTests
{
    /// <summary>验证无运行实例时只删除自己的启动值并释放单实例租约。</summary>
    [Fact]
    public async Task ExecuteAsync无运行实例时只清理自身启动项()
    {
        var calls = new List<string>();
        var startup = new RecordingStartupRegistration(calls);
        var coordinator = new PrepareUninstallCoordinator(
            () => { calls.Add("acquire"); return new RecordingLease(calls); },
            new RecordingRequestClient(calls, 100),
            new RecordingProcessWaiter(calls, exited: true),
            startup,
            TimeSpan.FromSeconds(1));

        await coordinator.ExecuteAsync(CancellationToken.None);

        Assert.Equal(["acquire", "disable", "release"], calls);
    }

    /// <summary>验证有运行实例时严格等待目标退出后再清理启动项。</summary>
    [Fact]
    public async Task ExecuteAsync运行实例收到请求并退出后才清理启动项()
    {
        var calls = new List<string>();
        var attempts = 0;
        var coordinator = new PrepareUninstallCoordinator(
            () =>
            {
                attempts++;
                calls.Add(attempts == 1 ? "busy" : "acquire-proof");
                return attempts == 1 ? null : new RecordingLease(calls);
            },
            new RecordingRequestClient(calls, 4321),
            new RecordingProcessWaiter(calls, exited: true),
            new RecordingStartupRegistration(calls),
            TimeSpan.FromSeconds(7));

        await coordinator.ExecuteAsync(CancellationToken.None);

        Assert.Equal(["busy", "request", "wait:4321:7", "acquire-proof", "disable", "release"], calls);
    }

    /// <summary>验证退出超时会失败且不会提前删除启动项。</summary>
    [Fact]
    public async Task ExecuteAsync运行实例未在超时内退出时保留启动项并失败()
    {
        var calls = new List<string>();
        var coordinator = new PrepareUninstallCoordinator(
            () => null,
            new RecordingRequestClient(calls, 4321),
            new RecordingProcessWaiter(calls, exited: false),
            new RecordingStartupRegistration(calls),
            TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<TimeoutException>(
            () => coordinator.ExecuteAsync(CancellationToken.None));

        Assert.Equal(["request", "wait:4321:3"], calls);
    }

    /// <summary>验证升级准备保留启动项。</summary>
    [Fact]
    public async Task ExecuteAsync升级退出时保留自身启动项()
    {
        var calls = new List<string>();
        var attempts = 0;
        var coordinator = new PrepareUninstallCoordinator(
            () => ++attempts == 1 ? null : new RecordingLease(calls),
            new RecordingRequestClient(calls, 4321),
            new RecordingProcessWaiter(calls, exited: true),
            new RecordingStartupRegistration(calls),
            TimeSpan.FromSeconds(3),
            removeStartupRegistration: false);

        await coordinator.ExecuteAsync(CancellationToken.None);

        Assert.Equal(["request", "wait:4321:3", "release"], calls);
    }

    /// <summary>验证目标 PID 退出后若正式互斥量仍被占用，则失败且不删除启动项。</summary>
    [Fact]
    public async Task ExecuteAsync辅助进程退出但真实实例仍持有互斥量时失败()
    {
        var calls = new List<string>();
        var coordinator = new PrepareUninstallCoordinator(
            () => null,
            new RecordingRequestClient(calls, 4321),
            new RecordingProcessWaiter(calls, exited: true),
            new RecordingStartupRegistration(calls),
            TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ExecuteAsync(CancellationToken.None));

        Assert.Equal(["request", "wait:4321:3"], calls);
    }

    /// <summary>验证管道名由用户 SID 与 Windows 会话共同隔离。</summary>
    [Theory]
    [InlineData("S-1-5-21-100", 1, "S-1-5-21-200", 1)]
    [InlineData("S-1-5-21-100", 1, "S-1-5-21-100", 2)]
    public void PipeName不同用户或会话生成不同名称(
        string firstSid, int firstSession, string secondSid, int secondSession)
    {
        var first = UninstallPipeName.Create(firstSid, firstSession);
        var second = UninstallPipeName.Create(secondSid, secondSession);

        Assert.NotEqual(first, second);
        Assert.DoesNotContain(firstSid, first, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', first);
    }

    /// <summary>验证相同 SID 与会话始终生成同一安全名称。</summary>
    [Fact]
    public void PipeName相同用户会话稳定()
    {
        Assert.Equal(
            UninstallPipeName.Create("S-1-5-21-100", 7),
            UninstallPipeName.Create("S-1-5-21-100", 7));
    }

    /// <summary>验证当前用户命名管道只传递固定请求与服务端进程号。</summary>
    [Fact]
    public async Task NamedPipeChannel仅传递固定卸载请求和服务端进程号()
    {
        var pipeName = $"DeskButler.Tests.PrepareUninstall.{Guid.NewGuid():N}";
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new UninstallRequestServer(pipeName, () => requested.TrySetResult());
        server.Start();

        var processId = await new NamedPipeUninstallRequestClient(pipeName, Environment.ProcessPath!)
            .RequestExitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(Environment.ProcessId, processId);
    }

    /// <summary>验证响应 PID 必须等于 Windows 从管道句柄取得的真实服务端 PID。</summary>
    [Fact]
    public async Task NamedPipeClient拒绝服务端伪造的辅助进程号()
    {
        var pipeName = $"DeskButler.Tests.PrepareUninstall.{Guid.NewGuid():N}";
        using var helper = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", "/c timeout /t 5 /nobreak >nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        })!;
        var fakeServer = RunFakeServerAsync(pipeName, helper.Id);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new NamedPipeUninstallRequestClient(pipeName, Environment.ProcessPath!)
                .RequestExitAsync(TimeSpan.FromSeconds(5), CancellationToken.None));

        await fakeServer;
    }

    /// <summary>验证无效协议只关闭当前会话，随后合法客户端仍可触发退出。</summary>
    [Fact]
    public async Task UninstallRequestServer无效客户端后继续接受合法请求()
    {
        var pipeName = $"DeskButler.Tests.PrepareUninstall.{Guid.NewGuid():N}";
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new UninstallRequestServer(pipeName, () => requested.TrySetResult());
        server.Start();
        await SendInvalidRequestAsync(pipeName, "not-the-protocol");

        var processId = await new NamedPipeUninstallRequestClient(pipeName, Environment.ProcessPath!)
            .RequestExitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(Environment.ProcessId, processId);
    }

    /// <summary>验证多个断连客户端不会让服务端停止或泄漏唯一实例。</summary>
    [Fact]
    public async Task UninstallRequestServer多次断连后仍接受合法请求()
    {
        var pipeName = $"DeskButler.Tests.PrepareUninstall.{Guid.NewGuid():N}";
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new UninstallRequestServer(pipeName, () => requested.TrySetResult());
        server.Start();
        await ConnectAndDisconnectAsync(pipeName);
        await ConnectAndDisconnectAsync(pipeName);

        _ = await new NamedPipeUninstallRequestClient(pipeName, Environment.ProcessPath!)
            .RequestExitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    /// <summary>验证连接后不发送换行的客户端超时断开，合法客户端仍可随后退出。</summary>
    [Fact]
    public async Task UninstallRequestServer恶意客户端不换行超时后仍接受合法请求()
    {
        var pipeName = $"DeskButler.Tests.PrepareUninstall.{Guid.NewGuid():N}";
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new UninstallRequestServer(
            pipeName, () => requested.TrySetResult(), TimeSpan.FromMilliseconds(150));
        server.Start();
        await using var stalledClient = await ConnectClientAsync(pipeName);

        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        var processId = await new NamedPipeUninstallRequestClient(pipeName, Environment.ProcessPath!)
            .RequestExitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(Environment.ProcessId, processId);
    }

    /// <summary>验证多个无效会话之间始终持有首个服务端实例，无法抢建同名管道。</summary>
    [Fact]
    public async Task UninstallRequestServer无效会话之间不释放FirstPipeInstance()
    {
        var pipeName = $"DeskButler.Tests.PrepareUninstall.{Guid.NewGuid():N}";
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new UninstallRequestServer(
            pipeName, () => requested.TrySetResult(), TimeSpan.FromMilliseconds(150));
        server.Start();

        await SendInvalidRequestAsync(pipeName, "invalid-one");
        AssertCompetingFirstPipeCannotStart(pipeName);
        await SendInvalidRequestAsync(pipeName, "invalid-two");
        AssertCompetingFirstPipeCannotStart(pipeName);

        _ = await new NamedPipeUninstallRequestClient(pipeName, Environment.ProcessPath!)
            .RequestExitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    /// <summary>验证宿主正常退出时可以取消尚未连接的命名管道等待。</summary>
    [Fact]
    public async Task UninstallRequestServer无客户端时可干净停止()
    {
        var pipeName = $"DeskButler.Tests.PrepareUninstall.{Guid.NewGuid():N}";
        var server = new UninstallRequestServer(pipeName, () => { });
        server.Start();

        var exception = await Record.ExceptionAsync(() => server.DisposeAsync().AsTask());

        Assert.Null(exception);
    }

    private sealed class RecordingLease(List<string> calls) : IDisposable
    {
        /// <summary>记录租约释放顺序。</summary>
        public void Dispose() => calls.Add("release");
    }

    private sealed class RecordingRequestClient(List<string> calls, int processId) : IUninstallRequestClient
    {
        /// <summary>记录固定退出请求。</summary>
        public Task<int> RequestExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            calls.Add("request");
            return Task.FromResult(processId);
        }
    }

    private sealed class RecordingProcessWaiter(List<string> calls, bool exited) : IProcessExitWaiter
    {
        /// <summary>记录目标进程和等待期限。</summary>
        public Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
        {
            calls.Add($"wait:{processId}:{timeout.TotalSeconds:0}");
            return Task.FromResult(exited);
        }
    }

    private sealed class RecordingStartupRegistration(List<string> calls) : IStartupRegistration
    {
        public bool IsEnabled => true;

        /// <summary>本测试不允许启用启动项。</summary>
        public void Enable() => throw new NotSupportedException();

        /// <summary>记录精确启动项清理。</summary>
        public void Disable() => calls.Add("disable");
    }

    /// <summary>创建返回任意 PID 的最小恶意管道服务端。</summary>
    private static async Task RunFakeServerAsync(string pipeName, int responseProcessId)
    {
        await using var pipe = new System.IO.Pipes.NamedPipeServerStream(
            pipeName, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly |
            System.IO.Pipes.PipeOptions.FirstPipeInstance);
        await pipe.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        _ = await reader.ReadLineAsync(TestContext.Current.CancellationToken);
        await writer.WriteLineAsync(responseProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>连接后发送无效协议并等待服务端关闭当前会话。</summary>
    private static async Task SendInvalidRequestAsync(string pipeName, string request)
    {
        await using var pipe = new System.IO.Pipes.NamedPipeClientStream(
            ".", pipeName, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(request);
    }

    /// <summary>连接后立即断开，模拟不完整或恶意会话。</summary>
    private static async Task ConnectAndDisconnectAsync(string pipeName)
    {
        await using var pipe = new System.IO.Pipes.NamedPipeClientStream(
            ".", pipeName, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    /// <summary>连接并保持一个不发送数据的客户端。</summary>
    private static async Task<System.IO.Pipes.NamedPipeClientStream> ConnectClientAsync(string pipeName)
    {
        var pipe = new System.IO.Pipes.NamedPipeClientStream(
            ".", pipeName, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        return pipe;
    }

    /// <summary>断言服务端整个生命周期持续占有 FirstPipeInstance。</summary>
    private static void AssertCompetingFirstPipeCannotStart(string pipeName)
    {
        Assert.Throws<IOException>(() => new System.IO.Pipes.NamedPipeServerStream(
            pipeName, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly |
            System.IO.Pipes.PipeOptions.FirstPipeInstance));
    }
}
