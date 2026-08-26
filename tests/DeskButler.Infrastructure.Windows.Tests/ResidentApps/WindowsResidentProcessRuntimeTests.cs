using System.ComponentModel;
using System.Diagnostics;
using DeskButler.Core.ResidentApps;
using DeskButler.Infrastructure.Windows.ResidentApps;

namespace DeskButler.Infrastructure.Windows.Tests.ResidentApps;

public sealed class WindowsResidentProcessRuntimeTests
{
    private const string MarkerFileName = "DeskButler.ResidentFixture.started";

    /// <summary>当前 Session 中真实 fixture 的完整路径匹配时返回 Running。</summary>
    [WindowsFact]
    public async Task CheckRunningAsyncFindsOwnedFixtureInCurrentSession()
    {
        await using var fixture = await ResidentFixtureWorkspace.CreateAsync();
        await fixture.StartWaitingAsync();
        var runtime = new WindowsResidentProcessRuntime(new WindowsResidentExecutablePolicy());

        var result = await WaitForRunningAsync(runtime, fixture.ExecutablePath);

        Assert.Equal(ResidentRunningState.Running, result.State);
        Assert.Equal(Path.GetFullPath(fixture.ExecutablePath), result.MatchedPath);
    }

    /// <summary>可能匹配目标名称的进程路径无法读取时必须返回 Unknown。</summary>
    [WindowsFact]
    public async Task CheckRunningAsyncReturnsUnknownForMatchingNameAccessFailure()
    {
        var process = new FakeResidentProcessInfo(
            "target",
            sessionId: 7,
            executablePath: () => throw new Win32Exception(5));
        var runtime = CreateRuntime(new FakeResidentProcessCatalog(7, process));

        var result = await runtime.CheckRunningAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Apps\target.exe" },
            CancellationToken.None);

        Assert.Equal(ResidentRunningState.Unknown, result.State);
        Assert.Null(result.MatchedPath);
        Assert.Equal(1, process.PathReadCount);
        Assert.True(process.IsDisposed);
    }

    /// <summary>无关进程即使路径访问拒绝也不得污染目标的 NotRunning 结果。</summary>
    [WindowsFact]
    public async Task CheckRunningAsyncDoesNotReadUnrelatedProcessPath()
    {
        var process = new FakeResidentProcessInfo(
            "unrelated-system-process",
            sessionId: 7,
            executablePath: () => throw new Win32Exception(5));
        var runtime = CreateRuntime(new FakeResidentProcessCatalog(7, process));

        var result = await runtime.CheckRunningAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Apps\target.exe" },
            CancellationToken.None);

        Assert.Equal(ResidentRunningState.NotRunning, result.State);
        Assert.Equal(0, process.PathReadCount);
        Assert.True(process.IsDisposed);
    }

    /// <summary>已退出的同名进程不得读取路径或参与匹配。</summary>
    [WindowsFact]
    public async Task CheckRunningAsyncIgnoresExitedMatchingProcess()
    {
        var process = new FakeResidentProcessInfo(
            "target",
            sessionId: 7,
            executablePath: () => @"C:\Apps\target.exe",
            hasExited: true);
        var runtime = CreateRuntime(new FakeResidentProcessCatalog(7, process));

        var result = await runtime.CheckRunningAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Apps\target.exe" },
            CancellationToken.None);

        Assert.Equal(ResidentRunningState.NotRunning, result.State);
        Assert.Equal(0, process.PathReadCount);
    }

    /// <summary>其他 Windows Session 的同名进程不得读取路径或参与匹配。</summary>
    [WindowsFact]
    public async Task CheckRunningAsyncIgnoresMatchingProcessFromOtherSession()
    {
        var process = new FakeResidentProcessInfo(
            "target",
            sessionId: 8,
            executablePath: () => @"C:\Apps\target.exe");
        var runtime = CreateRuntime(new FakeResidentProcessCatalog(7, process));

        var result = await runtime.CheckRunningAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Apps\target.exe" },
            CancellationToken.None);

        Assert.Equal(ResidentRunningState.NotRunning, result.State);
        Assert.Equal(0, process.PathReadCount);
    }

    /// <summary>同名但完整路径不同的可读进程不得被误判为目标正在运行。</summary>
    [WindowsFact]
    public async Task CheckRunningAsyncRequiresFullPathMatch()
    {
        var process = new FakeResidentProcessInfo(
            "target",
            sessionId: 7,
            executablePath: () => @"C:\Other\target.exe");
        var runtime = CreateRuntime(new FakeResidentProcessCatalog(7, process));

        var result = await runtime.CheckRunningAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Apps\target.exe" },
            CancellationToken.None);

        Assert.Equal(ResidentRunningState.NotRunning, result.State);
        Assert.Null(result.MatchedPath);
    }

    /// <summary>启动前必须重验，并以无参数、无 runas 的 shell start 立即释放自有句柄。</summary>
    [WindowsFact]
    public async Task StartAsyncRevalidatesAndDisposesOwnedProcessHandle()
    {
        var normalized = @"C:\SafeApps\target.exe";
        var policy = new FakeExecutablePolicy(
            new ResidentExecutableValidation(true, normalized, ResidentExecutableRejection.None));
        var starter = new FakeResidentProcessStarter();
        var runtime = new WindowsResidentProcessRuntime(
            policy,
            new FakeResidentProcessCatalog(7),
            starter);

        await runtime.StartAsync(@"C:\Requested\target.exe", CancellationToken.None);

        Assert.Equal(@"C:\Requested\target.exe", Assert.Single(policy.ValidatedPaths));
        var startInfo = Assert.Single(starter.StartInfos);
        Assert.Equal(normalized, startInfo.FileName);
        Assert.Equal(@"C:\SafeApps", startInfo.WorkingDirectory);
        Assert.True(startInfo.UseShellExecute);
        Assert.Empty(startInfo.Arguments);
        Assert.Empty(startInfo.ArgumentList);
        Assert.Empty(startInfo.Verb);
        Assert.True(starter.ReturnedHandle.IsDisposed);
    }

    /// <summary>重验拒绝时不得触达 Process.Start 边界。</summary>
    [WindowsFact]
    public async Task StartAsyncDoesNotStartRejectedPath()
    {
        var policy = new FakeExecutablePolicy(
            new ResidentExecutableValidation(false, null, ResidentExecutableRejection.ReparsePoint));
        var starter = new FakeResidentProcessStarter();
        var runtime = new WindowsResidentProcessRuntime(
            policy,
            new FakeResidentProcessCatalog(7),
            starter);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.StartAsync(@"C:\Requested\target.exe", CancellationToken.None));

        Assert.Empty(starter.StartInfos);
        Assert.False(starter.ReturnedHandle.IsDisposed);
    }

    /// <summary>生产启动边界对隔离 fixture 不传参数，并让 fixture 在自身目录写固定 marker。</summary>
    [WindowsFact]
    public async Task StartAsyncLaunchesArgumentFreeFixtureWithoutRunas()
    {
        await using var fixture = await ResidentFixtureWorkspace.CreateAsync();
        var runtime = new WindowsResidentProcessRuntime(new WindowsResidentExecutablePolicy());

        await runtime.StartAsync(fixture.ExecutablePath, CancellationToken.None);
        await fixture.WaitForMarkerAsync();

        Assert.Equal("started", File.ReadAllText(Path.Combine(fixture.DirectoryPath, MarkerFileName)));
    }

    /// <summary>轮询真实枚举，避免用固定睡眠猜测 fixture 启动时序。</summary>
    private static async Task<ResidentRunningCheck> WaitForRunningAsync(
        WindowsResidentProcessRuntime runtime,
        string executablePath)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var result = await runtime.CheckRunningAsync(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { executablePath },
                timeout.Token);
            if (result.State == ResidentRunningState.Running)
            {
                return result;
            }

            await Task.Delay(25, timeout.Token);
        }
    }

    /// <summary>构造只测试进程枚举、不触达真实启动的运行时。</summary>
    private static WindowsResidentProcessRuntime CreateRuntime(IResidentProcessCatalog catalog) =>
        new(
            new FakeExecutablePolicy(
                new ResidentExecutableValidation(false, null, ResidentExecutableRejection.ValidationFailed)),
            catalog,
            new FakeResidentProcessStarter());

    private sealed class FakeExecutablePolicy(ResidentExecutableValidation result) : IResidentExecutablePolicy
    {
        internal List<string> ValidatedPaths { get; } = [];

        /// <summary>记录重验输入并返回测试指定结果。</summary>
        public ResidentExecutableValidation Validate(string path)
        {
            ValidatedPaths.Add(path);
            return result;
        }
    }

    private sealed class FakeResidentProcessCatalog(
        int currentSessionId,
        params IResidentProcessInfo[] processes) : IResidentProcessCatalog
    {
        /// <summary>返回测试指定的当前 Windows Session。</summary>
        public int GetCurrentSessionId() => currentSessionId;

        /// <summary>返回测试拥有的受控进程观察对象。</summary>
        public IReadOnlyList<IResidentProcessInfo> GetProcesses() => processes;
    }

    private sealed class FakeResidentProcessInfo(
        string processName,
        int sessionId,
        Func<string> executablePath,
        bool hasExited = false) : IResidentProcessInfo
    {
        internal int PathReadCount { get; private set; }

        internal bool IsDisposed { get; private set; }

        public bool HasExited => hasExited;

        public int SessionId => sessionId;

        public string ProcessName => processName;

        /// <summary>读取测试指定的路径或抛出指定访问异常。</summary>
        public string GetExecutablePath()
        {
            PathReadCount++;
            return executablePath();
        }

        /// <summary>记录运行时已释放自己拥有的进程观察句柄。</summary>
        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class FakeResidentProcessStarter : IResidentProcessStarter
    {
        internal List<ProcessStartInfo> StartInfos { get; } = [];

        internal DisposeOnlyHandle ReturnedHandle { get; } = new();

        /// <summary>保存启动边界输入并返回仅可释放的测试句柄。</summary>
        public IDisposable? Start(ProcessStartInfo startInfo)
        {
            var copy = new ProcessStartInfo(startInfo.FileName)
            {
                UseShellExecute = startInfo.UseShellExecute,
                WorkingDirectory = startInfo.WorkingDirectory,
                Arguments = startInfo.Arguments,
                Verb = startInfo.Verb
            };
            foreach (var argument in startInfo.ArgumentList)
            {
                copy.ArgumentList.Add(argument);
            }

            StartInfos.Add(copy);
            return ReturnedHandle;
        }
    }

    private sealed class DisposeOnlyHandle : IDisposable
    {
        internal bool IsDisposed { get; private set; }

        /// <summary>记录 DeskButler 已立即释放自己持有的启动进程句柄。</summary>
        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class ResidentFixtureWorkspace : IAsyncDisposable
    {
        private Process? waitingProcess;

        /// <summary>记录测试独占目录及其中的 fixture 可执行文件。</summary>
        private ResidentFixtureWorkspace(string directoryPath, string executablePath)
        {
            DirectoryPath = directoryPath;
            ExecutablePath = executablePath;
        }

        internal string DirectoryPath { get; }

        internal string ExecutablePath { get; }

        /// <summary>复制 fixture 的 exe 及运行依赖到测试输出下的唯一目录。</summary>
        internal static Task<ResidentFixtureWorkspace> CreateAsync()
        {
            var sourceExecutable = FindFixtureExecutablePath();
            var sourceDirectory = Path.GetDirectoryName(sourceExecutable)!;
            var directory = Path.Combine(AppContext.BaseDirectory, "ResidentRuntimeFixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            foreach (var source in Directory.EnumerateFiles(sourceDirectory))
            {
                File.Copy(source, Path.Combine(directory, Path.GetFileName(source)));
            }

            return Task.FromResult(new ResidentFixtureWorkspace(
                directory,
                Path.Combine(directory, Path.GetFileName(sourceExecutable))));
        }

        /// <summary>以 --wait 启动仅由测试拥有、仅供运行识别的 fixture。</summary>
        internal Task StartWaitingAsync()
        {
            var startInfo = new ProcessStartInfo(ExecutablePath)
            {
                UseShellExecute = false,
                WorkingDirectory = DirectoryPath
            };
            startInfo.ArgumentList.Add("--wait");
            waitingProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动专属常驻 fixture。");
            return Task.CompletedTask;
        }

        /// <summary>轮询固定 marker，避免固定睡眠并给启动失败设置上限。</summary>
        internal async Task WaitForMarkerAsync()
        {
            var marker = Path.Combine(DirectoryPath, MarkerFileName);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(marker))
            {
                await Task.Delay(25, timeout.Token);
            }
        }

        /// <summary>只终止本测试启动的 fixture，并在退出后删除唯一目录。</summary>
        public async ValueTask DisposeAsync()
        {
            if (waitingProcess is not null)
            {
                try
                {
                    if (!waitingProcess.HasExited)
                    {
                        waitingProcess.Kill(entireProcessTree: true);
                        await waitingProcess.WaitForExitAsync();
                    }
                }
                catch (InvalidOperationException)
                {
                    // fixture 可能在检查与清理之间退出；这等同于测试清理成功。
                }
                finally
                {
                    waitingProcess.Dispose();
                }
            }

            await WaitUntilFilesAreReleasedAsync();
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }

        /// <summary>等待无参数 fixture 自行退出并释放映像文件，不接触任何第三方进程。</summary>
        private async Task WaitUntilFilesAreReleasedAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                if (AreAllFilesReleased())
                {
                    return;
                }

                await Task.Delay(25, timeout.Token);
            }
        }

        /// <summary>确认 exe、managed DLL 等 fixture 文件都不再由刚退出的专属进程占用。</summary>
        private bool AreAllFilesReleased()
        {
            foreach (var path in Directory.EnumerateFiles(DirectoryPath))
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>从仓库根定位与当前测试配置一致的 fixture exe。</summary>
        private static string FindFixtureExecutablePath()
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
                "DeskButler.ResidentFixture",
                "bin",
                configuration,
                "net10.0-windows10.0.17763.0",
                "DeskButler.ResidentFixture.exe");
        }
    }
}
