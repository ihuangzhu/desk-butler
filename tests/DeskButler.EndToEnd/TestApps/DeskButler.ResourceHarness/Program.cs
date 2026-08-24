using DeskButler.Core.Capture;
using DeskButler.Core.Scenes;
using DeskButler.Core.Settings;
using DeskButler.Core.Time;
using DeskButler.Modules.WorkspaceRecovery.Capture;
using DeskButler.Persistence.Paths;
using DeskButler.Persistence.Sqlite;
using System.Text;
using System.Text.Json;

namespace DeskButler.ResourceHarness;

internal static class Program
{
    /// <summary>生成真实调度与 SQLite 工作负载，然后在独立进程中等待父测试的精确停止信号。</summary>
    internal static async Task<int> Main(string[] arguments)
    {
        var dataRoot = ReadPathArgument(arguments, "--data-root");
        var readyFile = ReadPathArgument(arguments, "--ready-file");
        var progressFile = ReadPathArgument(arguments, "--progress-file");
        var duration = TimeSpan.FromSeconds(double.Parse(
            ReadArgumentValue(arguments, "--duration-seconds"),
            System.Globalization.CultureInfo.InvariantCulture));
        var paths = new AppDataPaths(dataRoot);
        var inventory = new ChangingInventory();
        using var repository = new SqliteSceneRepository(paths);
        using var coordinator = new CaptureCoordinator(
            ButlerSettings.Default,
            inventory,
            new SceneFilter(ButlerSettings.Default),
            repository,
            new SystemClock());
        await using var scheduler = new SnapshotScheduler(new SystemClock(), (_, _) => Task.CompletedTask);

        // Console TextReader 的 ReadLineAsync 会在此平台同步阻塞到首个 await；改用专用长期线程并在
        // ready/sample0 前握手，令 stdin 基础句柄贯穿整轮而非在 sample30 突增。
        var stopListenerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopSignal = Task.Factory.StartNew(
            () =>
            {
                stopListenerStarted.TrySetResult();
                return Console.In.ReadLine();
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        await stopListenerStarted.Task;
        await using var progressStream = new FileStream(
            progressFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 4096, useAsync: true);
        var progressGate = new SemaphoreSlim(1, 1);
        var startedAt = DateTimeOffset.UtcNow;
        await WriteProgressAsync(progressStream, progressGate, 0, 0, completed: false, stopped: false);
        await File.WriteAllTextAsync(readyFile, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var notifications = RunNotificationsAsync(scheduler, progressStream, progressGate, startedAt, duration);
        var captures = RunCapturesAsync(coordinator, inventory, progressStream, progressGate, startedAt, duration);
        await Task.WhenAll(notifications, captures);
        await WriteProgressAsync(progressStream, progressGate, 10_000, 100, completed: true, stopped: false);
        _ = await stopSignal;
        await scheduler.StopAsync(CancellationToken.None);
        await WriteProgressAsync(progressStream, progressGate, 10_000, 100, completed: true, stopped: true);
        progressGate.Dispose();
        return 0;
    }

    /// <summary>读取唯一必填命令行参数，拒绝缺失或空值。</summary>
    private static string ReadPathArgument(string[] arguments, string name) =>
        Path.GetFullPath(ReadArgumentValue(arguments, name));

    /// <summary>读取唯一必填命令行参数的原始值。</summary>
    private static string ReadArgumentValue(string[] arguments, string name)
    {
        for (var index = 0; index + 1 < arguments.Length; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }

        throw new ArgumentException($"缺少必填参数 {name}。", nameof(arguments));
    }

    /// <summary>在完整持续时间内按绝对目标时刻均匀发送精确 10,000 次桌面通知。</summary>
    private static async Task RunNotificationsAsync(
        SnapshotScheduler scheduler,
        FileStream progressStream,
        SemaphoreSlim progressGate,
        DateTimeOffset startedAt,
        TimeSpan duration)
    {
        for (var index = 1; index <= 10_000; index++)
        {
            await DelayUntilAsync(startedAt + duration * (index / 10_000d));
            scheduler.NotifyDesktopChanged();
            if (index % 100 == 0)
            {
                await WriteProgressAsync(progressStream, progressGate, index, null, completed: false, stopped: false);
            }
        }
    }

    /// <summary>在完整持续时间内按绝对目标时刻执行精确 100 次真实捕获。</summary>
    private static async Task RunCapturesAsync(
        CaptureCoordinator coordinator,
        ChangingInventory inventory,
        FileStream progressStream,
        SemaphoreSlim progressGate,
        DateTimeOffset startedAt,
        TimeSpan duration)
    {
        for (var index = 1; index <= 100; index++)
        {
            await DelayUntilAsync(startedAt + duration * (index / 100d));
            inventory.Sequence = index;
            await coordinator.SaveNowAsync($"stability-{index:D3}", CancellationToken.None);
            await WriteProgressAsync(progressStream, progressGate, null, index, completed: false, stopped: false);
        }
    }

    /// <summary>按绝对时刻等待，避免逐间隔调度误差累计导致工作负载超过目标时长。</summary>
    private static async Task DelayUntilAsync(DateTimeOffset target)
    {
        var remaining = target - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining);
        }
    }

    // 两个并发工作循环只经 progressGate 更新此进度，文件始终由单一长期句柄覆盖。
    private static int lastNotificationCount;
    private static int lastCaptureCount;

    /// <summary>复用单一长期文件句柄更新跨进程进度，不让逐次证据 I/O 制造目标句柄增长。</summary>
    private static async Task WriteProgressAsync(
        FileStream stream,
        SemaphoreSlim gate,
        int? notificationCount,
        int? captureCount,
        bool completed,
        bool stopped)
    {
        await gate.WaitAsync();
        try
        {
            if (notificationCount is not null)
            {
                lastNotificationCount = notificationCount.Value;
            }

            if (captureCount is not null)
            {
                lastCaptureCount = captureCount.Value;
            }

            var payload = JsonSerializer.Serialize(new
            {
                notificationCount = lastNotificationCount,
                captureCount = lastCaptureCount,
                completed,
                stopped,
                stopListenerStarted = true
            });
            var bytes = Encoding.UTF8.GetBytes(payload);
            stream.Position = 0;
            await stream.WriteAsync(bytes);
            stream.SetLength(bytes.Length);
            await stream.FlushAsync();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>为一百次捕获返回边界持续变化的单一安全候选。</summary>
    private sealed class ChangingInventory : IWindowInventory
    {
        internal int Sequence { get; set; }

        /// <summary>返回只包含固定 fixture 身份的当前候选。</summary>
        public Task<IReadOnlyList<WindowCandidate>> CaptureAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var monitor = new MonitorIdentity(@"\\.\DISPLAY1", new WindowBounds(0, 0, 1920, 1040), 96, 96);
            IReadOnlyList<WindowCandidate> candidates =
            [
                new WindowCandidate(42, 42, @"C:\DeskButlerFixture\TestWindow.exe", "FixtureWindow", "fixture", null,
                    new WindowBounds(100 + Sequence, 100, 640, 480), SceneWindowState.Normal, monitor,
                    true, false, false, false, false)
            ];
            return Task.FromResult(candidates);
        }
    }

    /// <summary>提供真实 UTC 时间与可取消墙钟等待。</summary>
    private sealed class SystemClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        /// <summary>使用系统计时器等待。</summary>
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
    }
}
