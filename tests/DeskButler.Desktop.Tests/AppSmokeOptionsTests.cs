using DeskButler.Core.ResidentApps;
using DeskButler.Core.Settings;
using DeskButler.Desktop.Hosting;
using DeskButler.Persistence.Json;
using DeskButler.Persistence.Paths;
using System.Windows.Threading;

namespace DeskButler.Desktop.Tests;

public sealed class AppSmokeOptionsTests
{
#if DEBUG
    /// <summary>Debug smoke 的完整组合根必须注入禁用 runtime，并使常驻入口零外部进程调用。</summary>
    [Fact]
    public Task DebugSmokeCompositionNeverBuildsOrCallsRealProcessRuntime() =>
        RunOnStaDispatcherAsync(async () =>
        {
            var rootPath = Path.Combine(
                Path.GetTempPath(), "DeskButler.Tests", Guid.NewGuid().ToString("N"));
            var paths = new AppDataPaths(rootPath);
            var settingsStore = new JsonSettingsStore(paths);
            await settingsStore.SaveAsync(
                ButlerSettings.Default with
                {
                    ResidentApplications =
                    [
                        new ResidentApplication(
                            @"C:\Apps\smoke-resident.exe",
                            new HashSet<string>([@"C:\Apps\smoke-resident.exe"], StringComparer.OrdinalIgnoreCase),
                            "Smoke Resident",
                            true,
                            0)
                    ]
                },
                CancellationToken.None);
            CompositionRoot? root = null;
            try
            {
                root = await CompositionRoot.CreateDebugAsync(
                    paths,
                    static () => { },
                    createFixture: false,
                    runSmoke: true,
                    pauseAutomaticCapture: false,
                    cancellationToken: CancellationToken.None);
                var runtime = Assert.IsType<DisabledResidentProcessRuntime>(
                    root.ResidentLaunch.ProcessRuntime);

                await root.MainViewModel.LaunchResidentsNowAsync();

                Assert.Equal(1, runtime.CheckCallCount);
                Assert.Equal(0, runtime.StartCallCount);
            }
            finally
            {
                if (root is not null)
                {
                    await root.DisposeAsync();
                }

                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
        });
#endif

    /// <summary>首实例完成启动后无论上次是否干净都必须查询最新有效快照。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartedFirstInstanceAlwaysQueriesRecoveryCard(bool previousRunUnclean)
    {
        Assert.True(App.ShouldQueryRecoveryCard(firstInstanceStarted: true, previousRunUnclean));
    }

    /// <summary>smoke 未显式指定隔离 data-root 时必须在对象图启动前拒绝。</summary>
    [Fact]
    public void SmokeWithoutExplicitDataRootIsRejected()
    {
        var paths = App.ResolveAppDataPaths(["--smoke"], out _, out _);

        Assert.Throws<InvalidOperationException>(() => App.PrepareSmokeRoot(paths, ["--smoke"]));
    }

#if DEBUG
    /// <summary>正式验收使用的 --smoke-ui 必须进入隔离 Debug smoke，而不是普通常驻宿主。</summary>
    [Fact]
    public void SmokeUiAliasEnablesDebugSmoke()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeskButler.Tests", Guid.NewGuid().ToString("N"));

        var paths = App.ResolveAppDataPaths(
            ["--smoke-ui", "--data-root", root],
            out var createFixture,
            out var runSmoke);

        Assert.Equal(Path.GetFullPath(root), paths.RootDirectory);
        Assert.False(createFixture);
        Assert.True(runSmoke);
    }

    /// <summary>Debug 调用者不能重新引入任意 marker 文件名入口。</summary>
    [Fact]
    public void CallerProvidedSmokeMarkerNameIsRejected()
    {
        Assert.Throws<ArgumentException>(() => App.ResolveAppDataPaths(
            ["--smoke", "--data-root", Path.GetTempPath(), "--smoke-success-marker", "chosen"],
            out _, out _));
    }
#else
    /// <summary>Release 必须忽略全部 Debug 专属参数并固定使用正式数据根。</summary>
    [Fact]
    public void ReleaseIgnoresDebugOnlySmokeArguments()
    {
        var paths = App.ResolveAppDataPaths(
            ["--smoke", "--data-root", Path.GetTempPath(), "--smoke-success-marker", "chosen"],
            out var createFixture,
            out var runSmoke);

        Assert.Equal(new AppDataPaths().RootDirectory, paths.RootDirectory);
        Assert.False(createFixture);
        Assert.False(runSmoke);
    }
#endif

#if DEBUG
    /// <summary>Debug 合法隔离根只返回固定 marker，并在开始前删除旧成功证据。</summary>
    [Fact]
    public void IsolatedSmokeRootUsesFixedFreshMarker()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeskButler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var oldMarker = Path.Combine(root, "smoke-success.marker");
            File.WriteAllText(oldMarker, "old");
            var args = new[] { "--smoke", "--data-root", root };
            var paths = App.ResolveAppDataPaths(args, out _, out _);

            var marker = App.PrepareSmokeRoot(paths, args);

            Assert.Equal(oldMarker, marker);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
#else
    /// <summary>Release 即使收到隔离根参数也必须拒绝进入 Debug smoke 准备流程。</summary>
    [Fact]
    public void ReleaseCannotPrepareDebugSmokeRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeskButler.Tests", Guid.NewGuid().ToString("N"));
        var args = new[] { "--smoke", "--data-root", root };
        var paths = App.ResolveAppDataPaths(args, out _, out _);

        Assert.Throws<InvalidOperationException>(() => App.PrepareSmokeRoot(paths, args));
    }
#endif

    /// <summary>隔离根已含正式数据文件时必须拒绝，避免 smoke 覆盖用户选择的目录。</summary>
    [Fact]
    public void SmokeRootContainingDatabaseIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeskButler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "deskbutler.db"), "existing");
            var args = new[] { "--smoke", "--data-root", root };
            var paths = App.ResolveAppDataPaths(args, out _, out _);

            Assert.Throws<InvalidOperationException>(() => App.PrepareSmokeRoot(paths, args));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>指向正式数据根的 junction 别名必须按重解析点拒绝。</summary>
    [Fact]
    public void JunctionAliasToProductionRootIsRejected()
    {
        var parent = Path.Combine(Path.GetTempPath(), "DeskButler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        var junction = Path.Combine(parent, "alias");
        var productionRoot = new AppDataPaths().RootDirectory;
        Directory.CreateDirectory(productionRoot);
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{junction}\" \"{productionRoot}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        try
        {
            var args = new[] { "--smoke", "--data-root", junction };
            var paths = App.ResolveAppDataPaths(args, out _, out _);
            Assert.Throws<InvalidOperationException>(() => App.PrepareSmokeRoot(paths, args));
        }
        finally
        {
            Directory.Delete(junction);
            Directory.Delete(parent);
        }
    }

    /// <summary>在专用 STA Dispatcher 上运行完整 WPF 组合，并确定性结束消息循环。</summary>
    private static async Task RunOnStaDispatcherAsync(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await test();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            await completion.Task;
        }
        finally
        {
            thread.Join();
        }
    }
}
