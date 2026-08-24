using DeskButler.Core.Capture;
using DeskButler.Core.Scenes;
using DeskButler.Core.Time;
using DeskButler.Core.Diagnostics;
using DeskButler.Modules.WorkspaceRecovery;

namespace DeskButler.Desktop.Hosting;

/// <summary>低延迟轮询窗口清单，只在真实桌面指纹变化时发出信号。</summary>
internal sealed class InventoryFingerprintChangeSource : IDesktopChangeSource, IAsyncDisposable
{
    private readonly IWindowInventory inventory;
    private readonly IClock clock;
    private readonly TimeSpan interval;
    private readonly Action<Exception>? reportFailure;
    private readonly IDiagnosticLog? diagnosticLog;
    private readonly CancellationTokenSource stopSource = new();
    private WindowFingerprint[] baseline = [];
    private Task? loop;
    private bool disposed;
    private Exception? lastFailure;

    /// <summary>创建具有固定有界检测延迟的变化源。</summary>
    internal InventoryFingerprintChangeSource(
        IWindowInventory inventory,
        IClock clock,
        TimeSpan interval,
        Action<Exception>? reportFailure = null,
        IDiagnosticLog? diagnosticLog = null)
    {
        this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        this.interval = interval;
        this.reportFailure = reportFailure;
        this.diagnosticLog = diagnosticLog;
    }

    /// <summary>获取最近一次轮询或订阅者故障，供诊断界面观察。</summary>
    internal Exception? LastFailure => Volatile.Read(ref lastFailure);

    /// <inheritdoc />
    public event EventHandler? DesktopChanged;

    /// <summary>先捕获不发信号的基线，再启动唯一检测循环。</summary>
    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        if (loop is not null)
        {
            return;
        }

        baseline = CreateFingerprint(await inventory.CaptureAsync(cancellationToken).ConfigureAwait(false));
        loop = RunLoopAsync(stopSource.Token);
    }

    /// <summary>停止循环并观察其完成，退出后不会再发事件。</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        stopSource.Cancel();
        if (loop is not null)
        {
            await loop.ConfigureAwait(false);
        }

        stopSource.Dispose();
    }

    /// <summary>每个周期比较完整指纹，静止时不重复通知。</summary>
    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await clock.DelayAsync(interval, cancellationToken).ConfigureAwait(false);
                var current = CreateFingerprint(
                    await inventory.CaptureAsync(cancellationToken).ConfigureAwait(false));
                if (!baseline.SequenceEqual(current))
                {
                    baseline = current;
                    NotifyChangedSubscribers();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // 枚举失败不改变基线；下个周期仍从最后一次成功清单继续比较。
                ReportFailure(exception);
            }
        }
    }

    /// <summary>逐个隔离变化订阅者，单个模块故障不得阻断同一信号或后续轮询。</summary>
    private void NotifyChangedSubscribers()
    {
        foreach (EventHandler subscriber in DesktopChanged?.GetInvocationList() ?? [])
        {
            try
            {
                subscriber(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
            }
        }
    }

    /// <summary>保存可观察故障并尽力通知诊断回调；回调自身故障也不会杀死循环。</summary>
    private void ReportFailure(Exception exception)
    {
        Volatile.Write(ref lastFailure, exception);
        if (reportFailure is not null)
        {
            try
            {
                reportFailure(exception);
            }
            catch (Exception reportingException)
            {
                Volatile.Write(
                    ref lastFailure,
                    new AggregateException("桌面变化故障回调失败。", exception, reportingException));
            }
        }

        if (diagnosticLog is not null)
        {
            _ = WriteDiagnosticFailureAsync(exception);
        }
    }

    /// <summary>异步记录变化源故障，并把日志自身异常合并为线程安全健康状态。</summary>
    private async Task WriteDiagnosticFailureAsync(Exception sourceFailure)
    {
        try
        {
            await diagnosticLog!.WriteAsync(
                new DiagnosticEvent(
                    DateTimeOffset.UtcNow, DiagnosticLevel.Warning, "desktop-inventory",
                    "桌面变化检测失败。",
                    new Dictionary<string, object?> { ["exceptionType"] = sourceFailure.GetType().FullName }),
                stopSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopSource.IsCancellationRequested)
        {
            // 应用退出取消诊断写入，不把正常生命周期伪装为健康故障。
        }
        catch (Exception loggingFailure)
        {
            Volatile.Write(
                ref lastFailure,
                new AggregateException("桌面变化故障的诊断日志写入失败。", sourceFailure, loggingFailure));
        }
    }

    /// <summary>投影并排序所有可观察窗口字段，避免枚举顺序伪造变化。</summary>
    private static WindowFingerprint[] CreateFingerprint(IReadOnlyList<WindowCandidate> candidates) =>
        candidates.Select(candidate => new WindowFingerprint(
                candidate.Handle,
                candidate.ExecutablePath,
                candidate.WindowClass,
                candidate.Title,
                candidate.ExplorerPath,
                candidate.Bounds,
                candidate.State,
                candidate.Monitor,
                candidate.IsVisibleMainWindow,
                candidate.IsSystemWindow,
                candidate.IsTemporaryWindow,
                candidate.IsDeskButlerWindow,
                candidate.WasElevatedOrInaccessible))
            .OrderBy(item => item.Handle)
            .ThenBy(item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private sealed record WindowFingerprint(
        nint Handle,
        string? ExecutablePath,
        string WindowClass,
        string? Title,
        string? ExplorerPath,
        WindowBounds Bounds,
        SceneWindowState State,
        MonitorIdentity Monitor,
        bool IsVisibleMainWindow,
        bool IsSystemWindow,
        bool IsTemporaryWindow,
        bool IsDeskButlerWindow,
        bool WasElevatedOrInaccessible);
}
