using System.Windows;
using DeskButler.Desktop.Diagnostics;
using DeskButler.Desktop.Hosting;
using DeskButler.Persistence.Paths;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskButler.Desktop;

/// <summary>管理单实例、崩溃标记和托盘优先桌面宿主生命周期。</summary>
public partial class App : System.Windows.Application, IDisposable
{
    private SingleInstanceGuard? singleInstance;
    private CrashSentinel? crashSentinel;
    private CompositionRoot? composition;
    private SafeFileHandle? smokeRootHandle;
    private int exitRequested;

    /// <summary>取得单实例后创建对象图；默认不显示主窗口。</summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
#if DEBUG
        var isSmokeRequest = e.Args.Any(argument =>
            StringComparer.OrdinalIgnoreCase.Equals(argument, "--smoke"));
#else
        const bool isSmokeRequest = false;
#endif
        try
        {
            var paths = ResolveAppDataPaths(e.Args, out var createFixture, out var runSmoke);
#if DEBUG
            var smokeSuccessMarker = runSmoke
                ? PrepareSmokeRoot(paths, e.Args, out smokeRootHandle)
                : null;
#endif
            if (!SingleInstanceGuard.TryAcquire(out singleInstance))
            {
                Shutdown(isSmokeRequest ? 2 : 0);
                return;
            }

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
                var cleanupFailure = await CleanupAfterControlledExitAsync(preserveSmokeRootLease: true);
                if (cleanupFailure is not null)
                {
                    throw new InvalidOperationException("Debug smoke 退出清理失败。", cleanupFailure);
                }

                WriteSmokeSuccessMarker(paths, smokeSuccessMarker);
                smokeRootHandle?.Dispose();
                smokeRootHandle = null;
                Shutdown(0);
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
            if (!isSmokeRequest)
            {
                System.Windows.MessageBox.Show(
                    $"DeskButler 启动失败：{exception.Message}", "DeskButler",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
    private async Task<Exception?> CleanupAfterControlledExitAsync(bool preserveSmokeRootLease = false)
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
        if (!preserveSmokeRootLease)
        {
            smokeRootHandle?.Dispose();
            smokeRootHandle = null;
        }
        return failure;
    }

    /// <summary>解析数据根；Release 构建始终使用正式 LocalAppData 目录。</summary>
    internal static AppDataPaths ResolveAppDataPaths(
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
            else if (StringComparer.OrdinalIgnoreCase.Equals(args[index], "--smoke-success-marker"))
            {
                throw new ArgumentException("Debug smoke marker 文件名固定，不能由命令行指定。");
            }
        }

        return explicitRoot is null ? new AppDataPaths() : new AppDataPaths(explicitRoot);
#else
        _ = args;
        return new AppDataPaths();
#endif
    }

    /// <summary>验证 smoke 使用全新隔离根，并在启动对象图前清除唯一固定成功标记。</summary>
    internal static string PrepareSmokeRoot(AppDataPaths paths, string[] args)
    {
        var marker = PrepareSmokeRoot(paths, args, out var rootHandle);
        rootHandle.Dispose();
        return marker;
    }

    /// <summary>验证并返回持续持有的目录身份句柄，防止检查后替换隔离根。</summary>
    private static string PrepareSmokeRoot(
        AppDataPaths paths,
        string[] args,
        out SafeFileHandle rootHandle)
    {
        var hasExplicitRoot = args.Any(argument =>
            StringComparer.OrdinalIgnoreCase.Equals(argument, "--data-root"));
        var productionRoot = new AppDataPaths().RootDirectory;
        if (!hasExplicitRoot || StringComparer.OrdinalIgnoreCase.Equals(paths.RootDirectory, productionRoot))
        {
            throw new InvalidOperationException("Debug smoke 必须显式使用非正式目录的 --data-root。");
        }

        if (Directory.Exists(paths.RootDirectory) &&
            (File.GetAttributes(paths.RootDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Debug smoke data-root 不能是符号链接或 junction。");
        }

        Directory.CreateDirectory(paths.RootDirectory);
        rootHandle = OpenDirectoryHandle(paths.RootDirectory);
        var candidateFinalPath = GetFinalPath(rootHandle);
        if (Directory.Exists(productionRoot))
        {
            using var productionHandle = OpenDirectoryHandle(productionRoot);
            if (StringComparer.OrdinalIgnoreCase.Equals(candidateFinalPath, GetFinalPath(productionHandle)))
            {
                rootHandle.Dispose();
                throw new InvalidOperationException("Debug smoke data-root 不能解析到正式数据根。");
            }
        }

        var marker = Path.Combine(paths.RootDirectory, "smoke-success.marker");
        foreach (var entry in Directory.EnumerateFileSystemEntries(paths.RootDirectory))
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(entry), marker))
            {
                throw new InvalidOperationException("Debug smoke data-root 必须为空，最多只能含旧成功 marker。");
            }
        }

        File.Delete(marker);
        return marker;
    }

    /// <summary>以拒绝删除共享打开目录，持有其身份直到 smoke 完整退出。</summary>
    private static SafeFileHandle OpenDirectoryHandle(string path)
    {
        var handle = CreateFileW(
            path, 0, FileShare.Read | FileShare.Write, IntPtr.Zero, FileMode.Open,
            FileAttributes.Normal | (FileAttributes)0x02000000, IntPtr.Zero);
        return handle.IsInvalid
            ? throw new Win32Exception(Marshal.GetLastWin32Error(), "无法锁定 Debug smoke data-root。")
            : handle;
    }

    /// <summary>从已打开目录句柄取得 Windows 最终规范路径。</summary>
    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[32768];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法解析 Debug smoke data-root 身份。");
        }

        return new string(buffer, 0, checked((int)length))
            .Replace("\\\\?\\UNC\\", "\\\\", StringComparison.OrdinalIgnoreCase)
            .Replace("\\\\?\\", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd(Path.DirectorySeparatorChar);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes,
        FileMode creationDisposition, FileAttributes flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file, [Out] char[] filePath, uint filePathSize, uint flags);

#if DEBUG
    /// <summary>只在真实建窗、诊断加载和完整退出清理后，于隔离数据根原子写入成功证据。</summary>
    private static void WriteSmokeSuccessMarker(AppDataPaths paths, string? markerPath)
    {
        if (string.IsNullOrWhiteSpace(markerPath))
        {
            throw new ArgumentException("Debug smoke 必须提供成功 marker 路径。");
        }

        var fullMarker = Path.GetFullPath(markerPath);
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetDirectoryName(fullMarker), Path.TrimEndingDirectorySeparator(paths.RootDirectory)))
        {
            throw new InvalidOperationException("Debug smoke marker 必须直接位于隔离 data-root 下。");
        }

        var temporary = fullMarker + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true))
            {
                writer.Write("DeskButler smoke completed\n");
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullMarker, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
#endif

    /// <summary>异常退出时释放持有句柄但保留 run.lock，供下一次启动识别。</summary>
    public void Dispose()
    {
        crashSentinel?.Dispose();
        crashSentinel = null;
        singleInstance?.Dispose();
        singleInstance = null;
        smokeRootHandle?.Dispose();
        smokeRootHandle = null;
        GC.SuppressFinalize(this);
    }
}
