using System.Windows;
using DeskButler.Desktop.Diagnostics;
using DeskButler.Desktop.Hosting;
using DeskButler.Persistence.Paths;

namespace DeskButler.Desktop;

/// <summary>管理单实例、崩溃标记和托盘优先桌面宿主生命周期。</summary>
public partial class App : System.Windows.Application, IDisposable
{
    private SingleInstanceGuard? singleInstance;
    private CrashSentinel? crashSentinel;
    private CompositionRoot? composition;
    private int exitRequested;

    /// <summary>取得单实例后创建对象图；默认不显示主窗口。</summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if (!SingleInstanceGuard.TryAcquire(out singleInstance))
            {
                Shutdown();
                return;
            }

            var paths = ResolveAppDataPaths(e.Args, out var createFixture, out var runSmoke);
            crashSentinel = new CrashSentinel(paths.RootDirectory);
#if DEBUG
            composition = await CompositionRoot.CreateDebugAsync(
                paths, () => _ = RequestExitAsync(), createFixture, CancellationToken.None);
#else
            composition = await CompositionRoot.CreateAsync(
                paths, () => _ = RequestExitAsync(), CancellationToken.None);
#endif
            await composition.StartAsync(CancellationToken.None);
#if DEBUG
            if (runSmoke)
            {
                await composition.RunDebugSmokeAsync();
                await RequestExitAsync();
                return;
            }
#endif
            if (crashSentinel.IsPreviousRunUnclean)
            {
                await composition.ShowRecoveryCardForLatestSceneAsync();
            }
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"DeskButler 启动失败：{exception.Message}", "DeskButler",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _ = await CleanupAfterControlledExitAsync();
            Shutdown(1);
        }
    }

    /// <summary>托盘退出先停止模块并清理 marker，最后释放线程关联的互斥量。</summary>
    private async Task RequestExitAsync()
    {
        if (Interlocked.Exchange(ref exitRequested, 1) != 0)
        {
            return;
        }

        var failure = await CleanupAfterControlledExitAsync();
        Shutdown(failure is null ? 0 : 1);
    }

    /// <summary>执行可控退出的完整清理顺序。</summary>
    private async Task<Exception?> CleanupAfterControlledExitAsync()
    {
        var currentComposition = composition;
        var currentSentinel = crashSentinel;
        var currentSingleInstance = singleInstance;
        var failure = await ExitCleanupCoordinator.RunAsync(
            () => currentComposition?.DisposeAsync() ?? ValueTask.CompletedTask,
            clean =>
            {
                if (clean)
                {
                    currentSentinel?.MarkCleanExit();
                }
                else
                {
                    currentSentinel?.Dispose();
                }
            },
            () => currentSingleInstance?.Dispose());
        composition = null;
        crashSentinel = null;
        singleInstance = null;
        return failure;
    }

    /// <summary>解析数据根；Release 构建始终使用正式 LocalAppData 目录。</summary>
    private static AppDataPaths ResolveAppDataPaths(
        string[] args,
        out bool createFixture,
        out bool runSmoke)
    {
        createFixture = false;
        runSmoke = false;
#if DEBUG
        string? explicitRoot = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(args[index], "--fixture"))
            {
                createFixture = true;
            }
            else if (StringComparer.OrdinalIgnoreCase.Equals(args[index], "--smoke"))
            {
                runSmoke = true;
            }
            else if (StringComparer.OrdinalIgnoreCase.Equals(args[index], "--data-root"))
            {
                if (++index >= args.Length)
                {
                    throw new ArgumentException("--data-root 需要目录参数。");
                }

                explicitRoot = args[index];
            }
        }

        return explicitRoot is null ? new AppDataPaths() : new AppDataPaths(explicitRoot);
#else
        _ = args;
        return new AppDataPaths();
#endif
    }

    /// <summary>异常退出时释放持有句柄但保留 run.lock，供下一次启动识别。</summary>
    public void Dispose()
    {
        crashSentinel?.Dispose();
        crashSentinel = null;
        singleInstance?.Dispose();
        singleInstance = null;
        GC.SuppressFinalize(this);
    }
}
