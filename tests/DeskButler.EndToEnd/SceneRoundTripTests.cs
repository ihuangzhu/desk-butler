using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DeskButler.Core.Capture;
using DeskButler.Core.Diagnostics;
using DeskButler.Core.Scenes;
using DeskButler.Core.Settings;
using DeskButler.Core.Time;
using DeskButler.Infrastructure.Windows.Restore;
using DeskButler.Infrastructure.Windows.Windows;
using DeskButler.Modules.WorkspaceRecovery.Capture;
using DeskButler.Modules.WorkspaceRecovery.Restore;
using DeskButler.Persistence.Paths;
using DeskButler.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace DeskButler.EndToEnd;

public sealed class SceneRoundTripTests
{
    /// <summary>在显式交互门禁下验证两个项目 fixture 与唯一 Explorer 目录的真实捕获恢复往返。</summary>
    [InteractiveWindowsFact]
    [Trait("Category", "Interactive")]
    public async Task 受控窗口捕获移动关闭再恢复后回到八像素内()
    {
        await using var fixture = await RoundTripFixture.CreateAsync(TestContext.Current.CancellationToken);
        var paths = new AppDataPaths(Path.Combine(fixture.RootDirectory, "data"));
        using var repository = new SqliteSceneRepository(paths);
        using var coordinator = new CaptureCoordinator(
            ButlerSettings.Default,
            fixture.Inventory,
            new SceneFilter(ButlerSettings.Default),
            repository,
            new SystemTestClock());

        var expectedPhysicalBounds = await fixture.ReadPhysicalBoundsForFixturesAsync(TestContext.Current.CancellationToken);
        await coordinator.SaveNowAsync("e2e-round-trip", TestContext.Current.CancellationToken);
        var scene = Assert.Single(await repository.GetRecentAsync(3, TestContext.Current.CancellationToken));
        var expectedWindows = scene.Items.Where(item => fixture.ExecutablePaths.Contains(item.ExecutablePath)).ToArray();
        Assert.Equal(2, expectedWindows.Length);
        _ = Assert.Single(scene.Items, item => PathEquals(item.ExplorerPath, fixture.ExplorerPath));

        await fixture.MoveFixtureWindowsAwayAsync(TestContext.Current.CancellationToken);
        await fixture.CloseSecondFixtureProcessAsync(TestContext.Current.CancellationToken);
        var current = await fixture.Inventory.CaptureAsync(TestContext.Current.CancellationToken);
        var plan = new RestorePlanner().Build(scene, current, FailureHistory.Empty, safeMode: false);
        using var restoreTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        restoreTimeout.CancelAfter(TimeSpan.FromSeconds(45));
        var result = await new RestoreExecutor(
            new WindowsAppLauncher(),
            fixture.Inventory,
            new WindowsWindowPositioner(),
            new SystemTestClock()).ExecuteAsync(plan, restoreTimeout.Token);

        Assert.Equal(scene.Items.Count, result.Items.Count(item => item.Status == DeskButler.Core.Restore.RestoreItemStatus.Succeeded));
        var restored = await fixture.WaitForBothFixtureWindowsAsync(restoreTimeout.Token);
        foreach (var expected in expectedWindows)
        {
            var actual = Assert.Single(restored, candidate => PathEquals(candidate.ExecutablePath, expected.ExecutablePath));
            AssertWithinEightPhysicalPixels(
                expectedPhysicalBounds[Path.GetFullPath(expected.ExecutablePath!)],
                PhysicalWindowBoundsReader.Read(actual.Handle));
        }
    }

    /// <summary>逐边断言独立 Win32 物理像素误差不超过八，包含不可见边框造成的容差。</summary>
    private static void AssertWithinEightPhysicalPixels(PhysicalWindowBounds expected, PhysicalWindowBounds actual)
    {
        Assert.InRange(Math.Abs((long)actual.Left - expected.Left), 0, 8);
        Assert.InRange(Math.Abs((long)actual.Top - expected.Top), 0, 8);
        Assert.InRange(Math.Abs((long)actual.Right - expected.Right), 0, 8);
        Assert.InRange(Math.Abs((long)actual.Bottom - expected.Bottom), 0, 8);
    }

    /// <summary>按 Windows 路径语义比较可选路径。</summary>
    private static bool PathEquals(string? left, string? right) =>
        left is not null && right is not null &&
        StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(left), Path.GetFullPath(right));

    /// <summary>为真实恢复执行器提供系统时间与可取消延迟。</summary>
    private sealed class SystemTestClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        /// <summary>使用真实墙钟等待窗口轮询。</summary>
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
    }

    /// <summary>拥有本轮唯一 fixture 路径、进程、HWND 和临时目录的安全测试边界。</summary>
    private sealed class RoundTripFixture : IAsyncDisposable
    {
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const uint WindowCloseMessage = 0x0010;
        private readonly Win32WindowInventory rawInventory = new();
        private readonly List<FixtureProcessLease> fixtureProcesses = [];
        private readonly FixtureProcessLease secondProcess;
        private nint explorerHandle;

        /// <summary>保存创建完毕且已精确识别的本轮资源。</summary>
        private RoundTripFixture(
            string rootDirectory,
            string explorerPath,
            IReadOnlySet<string> executablePaths,
            FixtureProcessLease firstProcess,
            FixtureProcessLease secondProcess)
        {
            RootDirectory = rootDirectory;
            ExplorerPath = explorerPath;
            ExecutablePaths = executablePaths;
            this.secondProcess = secondProcess;
            fixtureProcesses.Add(firstProcess);
            fixtureProcesses.Add(secondProcess);
            Inventory = new ScopedInventory(rawInventory, executablePaths, explorerPath);
        }

        internal string RootDirectory { get; }

        internal string ExplorerPath { get; }

        internal IReadOnlySet<string> ExecutablePaths { get; }

        internal ScopedInventory Inventory { get; }

        /// <summary>复制两份项目 TestWindow 输出，启动唯一进程与 Explorer 目录并等待三者可枚举。</summary>
        internal static async Task<RoundTripFixture> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(), $"DeskButler-RoundTrip-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            RoundTripFixture? fixture = null;
            Process? first = null;
            Process? second = null;
            FixtureProcessLease? firstLease = null;
            FixtureProcessLease? secondLease = null;
            try
            {
                var source = FindFixtureOutputDirectory();
                var firstDirectory = Path.Combine(root, "fixture-a");
                var secondDirectory = Path.Combine(root, "fixture-b");
                CopyDirectory(source, firstDirectory);
                CopyDirectory(source, secondDirectory);
                var firstExecutable = Path.Combine(firstDirectory, "DeskButler.TestWindow.exe");
                var secondExecutable = Path.Combine(secondDirectory, "DeskButler.TestWindow.exe");
                first = StartFixture(firstExecutable);
                firstLease = FixtureProcessLease.Create(first, firstExecutable);
                first = null;
                second = StartFixture(secondExecutable);
                secondLease = FixtureProcessLease.Create(second, secondExecutable);
                second = null;
                var explorerPath = Path.Combine(root, "explorer-fixture");
                Directory.CreateDirectory(explorerPath);
                fixture = new RoundTripFixture(
                    root,
                    explorerPath,
                    new HashSet<string>([firstExecutable, secondExecutable], StringComparer.OrdinalIgnoreCase),
                    firstLease,
                    secondLease);
                firstLease = null;
                secondLease = null;
                await fixture.WaitForBothFixtureWindowsAsync(cancellationToken);
                fixture.OpenExplorerDirectory();
                fixture.explorerHandle = await fixture.WaitForExplorerAsync(cancellationToken);
                return fixture;
            }
            catch
            {
                if (fixture is not null)
                {
                    await fixture.DisposeAsync();
                }
                else
                {
                    if (firstLease is not null)
                    {
                        await firstLease.DisposeAsync();
                    }

                    if (secondLease is not null)
                    {
                        await secondLease.DisposeAsync();
                    }

                    await CloseHeldProcessAsync(first);
                    await CloseHeldProcessAsync(second);
                    TryDeleteUniqueRoot(root);
                }

                throw;
            }
        }

        /// <summary>把两个受控 HWND 移到不同矩形，且不激活或提升窗口层级。</summary>
        internal async Task MoveFixtureWindowsAwayAsync(CancellationToken cancellationToken)
        {
            var windows = await WaitForBothFixtureWindowsAsync(cancellationToken);
            var rectangles = new[]
            {
                new WindowBounds(windows[0].Monitor.WorkArea.Left + 20, windows[0].Monitor.WorkArea.Top + 20, 360, 260),
                new WindowBounds(windows[1].Monitor.WorkArea.Left + 420, windows[1].Monitor.WorkArea.Top + 80, 380, 280)
            };
            for (var index = 0; index < windows.Count; index++)
            {
                if (!SetWindowPos(windows[index].Handle, 0, rectangles[index].Left, rectangles[index].Top,
                        rectangles[index].Width, rectangles[index].Height, SwpNoZOrder | SwpNoActivate | SwpShowWindow))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "移动 fixture 窗口失败。");
                }
            }
        }

        /// <summary>仅关闭创建时记录的第二个精确 PID，不按进程名查找。</summary>
        internal async Task CloseSecondFixtureProcessAsync(CancellationToken cancellationToken)
        {
            await secondProcess.CloseAsync(cancellationToken);
        }

        /// <summary>用独立 PMv2/GetWindowRect 读取本轮两个原始 HWND 的物理四边。</summary>
        internal async Task<IReadOnlyDictionary<string, PhysicalWindowBounds>> ReadPhysicalBoundsForFixturesAsync(
            CancellationToken cancellationToken)
        {
            var windows = await WaitForBothFixtureWindowsAsync(cancellationToken);
            return windows.ToDictionary(
                candidate => Path.GetFullPath(candidate.ExecutablePath!),
                candidate => PhysicalWindowBoundsReader.Read(candidate.Handle),
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>等待两个不同临时 exe 路径各出现唯一主窗口，并记录恢复产生的新 PID。</summary>
        internal async Task<IReadOnlyList<WindowCandidate>> WaitForBothFixtureWindowsAsync(CancellationToken cancellationToken)
        {
            return await WaitUntilAsync(async () =>
            {
                var candidates = (await Inventory.CaptureAsync(cancellationToken))
                    .Where(candidate => candidate.ExecutablePath is not null && ExecutablePaths.Contains(Path.GetFullPath(candidate.ExecutablePath)))
                    .ToArray();
                if (candidates.Length != 2 || candidates.Select(candidate => candidate.ExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2)
                {
                    return null;
                }

                foreach (var candidate in candidates)
                {
                    Process? discoveredProcess = null;
                    try
                    {
                        discoveredProcess = Process.GetProcessById(candidate.ProcessId);
                        var discoveredPath = discoveredProcess.MainModule?.FileName
                            ?? throw new InvalidOperationException("恢复 fixture 没有可验证的主模块路径。");
                        var discoveredStartTime = discoveredProcess.StartTime.ToUniversalTime();
                        if (!StringComparer.OrdinalIgnoreCase.Equals(
                                Path.GetFullPath(discoveredPath), Path.GetFullPath(candidate.ExecutablePath!)))
                        {
                            discoveredProcess.Dispose();
                            return null;
                        }

                        if (fixtureProcesses.Any(process => process.RepresentsRunningIdentity(
                                candidate.ProcessId, discoveredPath, discoveredStartTime)))
                        {
                            discoveredProcess.Dispose();
                            continue;
                        }

                        fixtureProcesses.Add(FixtureProcessLease.Create(discoveredProcess, candidate.ExecutablePath!));
                        discoveredProcess = null;
                    }
                    catch (ArgumentException)
                    {
                        discoveredProcess?.Dispose();
                        return null;
                    }
                    catch (InvalidOperationException)
                    {
                        discoveredProcess?.Dispose();
                        return null;
                    }
                }

                return (IReadOnlyList<WindowCandidate>)candidates;
            }, TimeSpan.FromSeconds(15), cancellationToken);
        }

        /// <summary>关闭精确 Explorer HWND 和所有由本轮唯一复制路径产生的 PID，再删唯一临时根。</summary>
        public async ValueTask DisposeAsync()
        {
            var resourceCleanup = new List<Func<ValueTask>> { CloseExplorerAsync };
            resourceCleanup.AddRange(fixtureProcesses.Select<FixtureProcessLease, Func<ValueTask>>(
                process => process.DisposeAsync));
            await BestEffortCleanup.RunAsync(
                resourceCleanup,
                [ClearFixtureDatabasePoolAsync, () => new ValueTask(DeleteUniqueRootWithRetryAsync(RootDirectory))]);
        }

        /// <summary>仅关闭仍映射到本轮唯一目录的精确 Explorer HWND。</summary>
        private async ValueTask CloseExplorerAsync()
        {
            if (explorerHandle != 0 && PathEquals(new ExplorerWindowReader().TryGetFolderPath(explorerHandle), ExplorerPath))
            {
                _ = PostMessage(explorerHandle, WindowCloseMessage, 0, 0);
                await WaitForExplorerCloseAsync(explorerHandle);
            }
        }

        /// <summary>仅清理本轮唯一 SQLite connection string 的连接池。</summary>
        private ValueTask ClearFixtureDatabasePoolAsync()
        {
            using (var connection = new SqliteConnection(
                       $"Data Source={new AppDataPaths(Path.Combine(RootDirectory, "data")).DatabasePath}"))
            {
                SqliteConnection.ClearPool(connection);
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>通过固定系统 explorer.exe 打开本轮唯一目录，不保留或终止 Shell 代理进程。</summary>
        private void OpenExplorerDirectory()
        {
            var startInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            {
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add(ExplorerPath);
            using var shellProxy = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 Explorer fixture。");
        }

        /// <summary>等待 COM/Win32 清单返回与唯一目录精确匹配的单一 Explorer HWND。</summary>
        private async Task<nint> WaitForExplorerAsync(CancellationToken cancellationToken)
        {
            return await WaitUntilAsync<nint>(async () =>
            {
                var matches = (await rawInventory.CaptureAsync(cancellationToken))
                    .Where(candidate => PathEquals(candidate.ExplorerPath, ExplorerPath))
                    .Select(candidate => candidate.Handle)
                    .Distinct()
                    .ToArray();
                return matches.Length == 1 ? matches[0] : (nint?)null;
            }, TimeSpan.FromSeconds(15), cancellationToken);
        }

        /// <summary>从仓库根定位同配置已构建的 TestWindow 输出目录。</summary>
        private static string FindFixtureOutputDirectory()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "DeskButler.slnx")))
            {
                repository = repository.Parent;
            }

            if (repository is null)
            {
                throw new DirectoryNotFoundException("无法定位 DeskButler 仓库根。");
            }

            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
            return Path.Combine(repository.FullName, "tests", "DeskButler.Infrastructure.Windows.Tests", "TestApps",
                "DeskButler.TestWindow", "bin", configuration, "net10.0-windows10.0.17763.0");
        }

        /// <summary>复制项目 fixture 的完整运行目录到本轮唯一位置。</summary>
        private static void CopyDirectory(string source, string destination)
        {
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }

            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }

        /// <summary>启动本轮唯一复制路径的单窗口 fixture。</summary>
        private static Process StartFixture(string executablePath)
        {
            return Process.Start(new ProcessStartInfo(executablePath) { UseShellExecute = true })
                ?? throw new InvalidOperationException("无法启动 TestWindow fixture。");
        }

        /// <summary>在超时内轮询条件，避免固定等待掩盖启动竞态。</summary>
        private static async Task<T> WaitUntilAsync<T>(
            Func<Task<T?>> readAsync,
            TimeSpan timeout,
            CancellationToken cancellationToken)
            where T : struct
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await readAsync() is { } value)
                {
                    return value;
                }

                await Task.Delay(50, cancellationToken);
            }

            throw new TimeoutException("等待受控 Windows fixture 超时。");
        }

        /// <summary>在超时内轮询引用类型条件。</summary>
        private static async Task<T> WaitUntilAsync<T>(
            Func<Task<T?>> readAsync,
            TimeSpan timeout,
            CancellationToken cancellationToken)
            where T : class
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await readAsync() is { } value)
                {
                    return value;
                }

                await Task.Delay(50, cancellationToken);
            }

            throw new TimeoutException("等待受控 Windows fixture 超时。");
        }

        /// <summary>仅用于构造失败窗口，直接通过仍持有的 Process 身份清理，不按 PID 重开。</summary>
        private static async Task CloseHeldProcessAsync(Process? process)
        {
            if (process is null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    _ = process.CloseMainWindow();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try
                    {
                        await process.WaitForExitAsync(timeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                            await process.WaitForExitAsync(CancellationToken.None);
                        }
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // 精确 PID 可在检查与关闭之间自然退出。
            }
            finally
            {
                process.Dispose();
            }
        }

        /// <summary>仅尝试删除以本轮 GUID 创建的精确临时根。</summary>
        private static void TryDeleteUniqueRoot(string root)
        {
            if (!Path.GetFileName(root).StartsWith("DeskButler-RoundTrip-", StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Explorer 释放目录句柄存在延迟时保留唯一 fixture 供诊断。
            }
            catch (UnauthorizedAccessException)
            {
                // 不提升权限清理临时目录。
            }
        }

        /// <summary>等待精确 Explorer HWND 从 Shell COM 映射中消失，避免目录句柄清理竞态。</summary>
        private static async Task WaitForExplorerCloseAsync(nint windowHandle)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            var reader = new ExplorerWindowReader();
            while (DateTimeOffset.UtcNow < deadline && reader.TryGetFolderPath(windowHandle) is not null)
            {
                await Task.Delay(50);
            }
        }

        /// <summary>在短暂 Shell 释放窗口内重试删除本轮唯一临时根，最终失败会使测试明确失败。</summary>
        private static async Task DeleteUniqueRootWithRetryAsync(string root)
        {
            if (!Path.GetFileName(root).StartsWith("DeskButler-RoundTrip-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("拒绝删除非本轮命名的临时根。");
            }

            Exception? lastFailure = null;
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }

                    return;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    lastFailure = exception;
                    await Task.Delay(100);
                }
            }

            throw new IOException("无法清理本轮唯一 RoundTrip 临时根。", lastFailure);
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(nint windowHandle, uint message, nint wParam, nint lParam);
    }

    /// <summary>把真实枚举限制在本轮两条唯一 exe 路径和唯一 Explorer 路径内。</summary>
    private sealed class ScopedInventory(
        IWindowInventory inner,
        IReadOnlySet<string> executablePaths,
        string explorerPath) : IWindowInventory
    {
        /// <summary>过滤掉全部非 fixture 窗口，使捕获与恢复永不操作用户现有窗口。</summary>
        public async Task<IReadOnlyList<WindowCandidate>> CaptureAsync(CancellationToken cancellationToken)
        {
            var candidates = await inner.CaptureAsync(cancellationToken);
            return candidates.Where(candidate =>
                    candidate.ExecutablePath is not null && executablePaths.Contains(Path.GetFullPath(candidate.ExecutablePath)) ||
                    PathEquals(candidate.ExplorerPath, explorerPath))
                .ToArray();
        }
    }
}
