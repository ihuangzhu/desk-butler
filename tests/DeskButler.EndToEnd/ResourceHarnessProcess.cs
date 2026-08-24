using System.Diagnostics;
using DeskButler.Persistence.Paths;
using DeskButler.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace DeskButler.EndToEnd;

/// <summary>只拥有本轮专用资源 workload 子进程和唯一临时数据库。</summary>
internal sealed class ResourceHarnessProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly string rootDirectory;
    private readonly AppDataPaths paths;
    private readonly string progressFile;

    /// <summary>指示专用 workload 进程仍可供父测试采样。</summary>
    internal bool IsRunning => !process.HasExited;

    /// <summary>保存已就绪的精确子进程和本轮路径。</summary>
    private ResourceHarnessProcess(Process process, string rootDirectory, string progressFile)
    {
        this.process = process;
        this.rootDirectory = rootDirectory;
        this.progressFile = progressFile;
        paths = new AppDataPaths(rootDirectory);
    }

    /// <summary>启动独立无窗口 workload，并等待其完成 10,000 通知与 100 捕获。</summary>
    internal static async Task<ResourceHarnessProcess> StartAsync(
        CancellationToken cancellationToken,
        TimeSpan? duration = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"DeskButler-ResourceHarness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var readyFile = Path.Combine(root, "ready");
        var progressFile = Path.Combine(root, "progress.json");
        var startInfo = new ProcessStartInfo(FindExecutablePath())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--data-root");
        startInfo.ArgumentList.Add(root);
        startInfo.ArgumentList.Add("--ready-file");
        startInfo.ArgumentList.Add(readyFile);
        startInfo.ArgumentList.Add("--progress-file");
        startInfo.ArgumentList.Add(progressFile);
        startInfo.ArgumentList.Add("--duration-seconds");
        startInfo.ArgumentList.Add((duration ?? TimeSpan.FromMinutes(30)).TotalSeconds.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动专用资源 workload。");
        var harness = new ResourceHarnessProcess(process, root, progressFile);
        try
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    throw new InvalidOperationException($"资源 workload 过早退出，代码 {process.ExitCode}。");
                }

                if (File.Exists(readyFile))
                {
                    var recordedPid = await TryReadReadyPidAsync(readyFile, cancellationToken);
                    if (recordedPid is null)
                    {
                        await Task.Delay(10, cancellationToken);
                        continue;
                    }

                    if (recordedPid.Value != process.Id)
                    {
                        throw new InvalidOperationException("资源 workload ready PID 与精确子进程不一致。");
                    }

                    return harness;
                }

                await Task.Delay(50, cancellationToken);
            }

            throw new TimeoutException("等待专用资源 workload 就绪超时。");
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    /// <summary>以共享读取方式容忍 ready 文件刚创建但异步写入尚未关闭的短暂窗口。</summary>
    private static async Task<int?> TryReadReadyPidAsync(string readyFile, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(readyFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync(cancellationToken);
            return int.TryParse(text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var processId)
                ? processId
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>只采样专用 workload PID 的句柄和私有字节，以及本轮数据库大小。</summary>
    internal ResourceSample Sample(int minute)
    {
        if (process.HasExited)
        {
            throw new InvalidOperationException($"资源 workload 已退出，代码 {process.ExitCode}。");
        }

        process.Refresh();
        var progress = ReadProgress();
        return new ResourceSample(
            minute,
            process.HandleCount,
            process.PrivateMemorySize64,
            DatabaseSize(paths.DatabasePath),
            progress.NotificationCount,
            progress.CaptureCount,
            progress.Completed,
            progress.Stopped,
            progress.StopListenerStarted);
    }

    /// <summary>等待专用 workload 报告精确完成 10,000 通知和 100 捕获。</summary>
    internal async Task WaitForCompletionAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progress = ReadProgress();
            if (progress.Completed && !progress.Stopped &&
                progress.NotificationCount == 10_000 && progress.CaptureCount == 100)
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException("专用资源 workload 未在目标时长后完成精确计数。");
    }

    /// <summary>通过真实仓库读取本轮数据库的有效快照数。</summary>
    internal async Task<int> CountValidSnapshotsAsync(CancellationToken cancellationToken)
    {
        using var repository = new SqliteSceneRepository(paths);
        var count = (await repository.GetRecentAsync(10, cancellationToken)).Count;
        return count;
    }

    /// <summary>发送 stdin 停止信号；超时只终止已记录精确 PID，并清理唯一临时根。</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("stop");
                await process.StandardInput.FlushAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None);
                    }
                }
            }
        }
        finally
        {
            process.Dispose();
            ClearFixtureDatabasePool();
            await DeleteRootWithRetryAsync(rootDirectory);
        }
    }

    /// <summary>定位与当前配置相同的专用 workload apphost。</summary>
    private static string FindExecutablePath()
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
        return Path.Combine(repository.FullName, "tests", "DeskButler.EndToEnd", "TestApps",
            "DeskButler.ResourceHarness", "bin", configuration, "net10.0-windows10.0.17763.0",
            "DeskButler.ResourceHarness.exe");
    }

    /// <summary>计算本轮 SQLite 主文件、WAL 与 SHM 总大小。</summary>
    private static long DatabaseSize(string databasePath)
    {
        return new[] { databasePath, databasePath + "-wal", databasePath + "-shm" }
            .Where(File.Exists)
            .Sum(path => new FileInfo(path).Length);
    }

    /// <summary>仅清理本轮唯一数据库的连接池，避免干扰并行测试或用户数据库。</summary>
    private void ClearFixtureDatabasePool()
    {
        using var connection = new SqliteConnection($"Data Source={paths.DatabasePath}");
        SqliteConnection.ClearPool(connection);
    }

    /// <summary>在跨进程覆盖竞态下重试读取单一长期句柄写出的 JSON 进度。</summary>
    private ResourceHarnessProgress ReadProgress()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var stream = new FileStream(progressFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return JsonSerializer.Deserialize<ResourceHarnessProgress>(stream)
                    ?? throw new JsonException("资源进度为空。");
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                Thread.Sleep(5);
            }
        }

        throw new IOException("无法读取专用资源 workload 进度。");
    }

    /// <summary>短暂重试删除带固定前缀的本轮唯一临时根。</summary>
    private static async Task DeleteRootWithRetryAsync(string root)
    {
        if (!Path.GetFileName(root).StartsWith("DeskButler-ResourceHarness-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("拒绝删除非资源 harness 临时根。");
        }

        Exception? lastFailure = null;
        for (var attempt = 0; attempt < 50; attempt++)
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

        throw new IOException("无法清理专用资源 harness 临时根。", lastFailure);
    }
}

/// <summary>表示专用子进程的持续工作负载进度。</summary>
/// <param name="NotificationCount">已发送通知数。</param>
/// <param name="CaptureCount">已完成捕获数。</param>
/// <param name="Completed">是否完成精确总量。</param>
internal sealed record ResourceHarnessProgress(
    [property: System.Text.Json.Serialization.JsonPropertyName("notificationCount")] int NotificationCount,
    [property: System.Text.Json.Serialization.JsonPropertyName("captureCount")] int CaptureCount,
    [property: System.Text.Json.Serialization.JsonPropertyName("completed")] bool Completed,
    [property: System.Text.Json.Serialization.JsonPropertyName("stopped")] bool Stopped,
    [property: System.Text.Json.Serialization.JsonPropertyName("stopListenerStarted")] bool StopListenerStarted);
