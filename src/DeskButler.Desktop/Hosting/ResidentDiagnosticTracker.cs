using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DeskButler.Core.Diagnostics;
using DeskButler.Core.ResidentApps;
using DeskButler.Core.Time;

namespace DeskButler.Desktop.Hosting;

/// <summary>登记常驻正规化与发现摘要的异步日志任务，并在日志释放前尽力排空。</summary>
/// <remarks>事件只含稳定分类与计数；指纹输入不含原始路径、账号、异常消息或命令行。</remarks>
internal sealed class ResidentDiagnosticTracker
{
    private static readonly ConcurrentDictionary<string, byte> ReportedDiscoveryFingerprints = new();
    private readonly object syncRoot = new();
    private readonly List<Task> pending = [];
    private readonly IDiagnosticLog diagnosticLog;
    private readonly IClock clock;
    private bool sealedForCleanup;

    /// <summary>创建复用既有日志和时钟的常驻诊断所有者。</summary>
    internal ResidentDiagnosticTracker(IDiagnosticLog diagnosticLog, IClock clock)
    {
        this.diagnosticLog = diagnosticLog ?? throw new ArgumentNullException(nameof(diagnosticLog));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>尝试接收已由 JsonSettingsStore 预留指纹的单项正规化分类；封存后明确拒绝。</summary>
    internal bool TryReportNormalization(ResidentNormalizationDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return TryStartWrite(new DiagnosticEvent(
            clock.UtcNow,
            DiagnosticLevel.Warning,
            "resident-normalization",
            "常驻应用设置正规化隔离了无效或冲突条目。",
            new Dictionary<string, object?>
            {
                ["issue"] = diagnostic.Kind.ToString(),
                ["count"] = 1
            }));
    }

    /// <summary>按候选稳定身份和分类计数去重后写入一条不含路径的发现摘要。</summary>
    internal void ReportDiscovery(ResidentDiscoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var issueCounts = result.Diagnostics
            .GroupBy(diagnostic => diagnostic.Kind)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key}={group.Count()}")
            .ToArray();
        var stableCandidateIds = result.Candidates
            .Select(candidate => candidate.CandidateId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var fingerprintInput = string.Join('|', stableCandidateIds) + "\n" + string.Join(';', issueCounts);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput)));
        StartDiscoveryWrite(fingerprint, new DiagnosticEvent(
            clock.UtcNow,
            result.Diagnostics.Count == 0 ? DiagnosticLevel.Information : DiagnosticLevel.Warning,
            "resident-discovery-summary",
            "常驻应用候选发现已完成。",
            new Dictionary<string, object?>
            {
                ["candidateCount"] = result.Candidates.Count,
                ["diagnosticCount"] = result.Diagnostics.Count,
                ["issueCounts"] = string.Join(';', issueCounts)
            }));
    }

    /// <summary>封存后拒绝新任务，并尽力观察此前登记任务的全部异常。</summary>
    internal async ValueTask DrainAsync()
    {
        Task[] snapshot;
        lock (syncRoot)
        {
            sealedForCleanup = true;
            snapshot = [.. pending];
        }

        try
        {
            await Task.WhenAll(snapshot).ConfigureAwait(false);
        }
        catch
        {
            // 日志是最终旁路；故障已被观察，但不得反向阻断资源清理。
        }
    }

    /// <summary>锁内原子接受并登记 completion，锁外才启动日志，避免封存遗漏已接受事件。</summary>
    private bool TryStartWrite(DiagnosticEvent diagnosticEvent)
    {
        TaskCompletionSource completion;
        lock (syncRoot)
        {
            if (sealedForCleanup)
            {
                return false;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add(completion.Task);
        }

        StartWriteOutsideLock(diagnosticEvent, completion);
        return true;
    }

    /// <summary>线性化封存检查、进程级指纹占用与任务登记，避免迟到报告吞掉未来事件。</summary>
    private void StartDiscoveryWrite(string fingerprint, DiagnosticEvent diagnosticEvent)
    {
        TaskCompletionSource completion;
        lock (syncRoot)
        {
            if (sealedForCleanup || !ReportedDiscoveryFingerprints.TryAdd(fingerprint, 0))
            {
                return;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add(completion.Task);
        }

        StartWriteOutsideLock(diagnosticEvent, completion);
    }

    /// <summary>在 tracker 状态锁外调度尽力日志，并确保所有终态都完成已登记的 cleanup completion。</summary>
    private void StartWriteOutsideLock(DiagnosticEvent diagnosticEvent, TaskCompletionSource completion)
    {
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await diagnosticLog.WriteAsync(diagnosticEvent, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // 日志是最终旁路；故障必须被观察，但不得反向影响设置或发现结果。
                }
                finally
                {
                    completion.TrySetResult();
                }
            }, CancellationToken.None);
        }
        catch
        {
            // 调度自身失败也必须释放 cleanup 等待，且不得改变调用方产品结果。
            completion.TrySetResult();
        }
    }
}
