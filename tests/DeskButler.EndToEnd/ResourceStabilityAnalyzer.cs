namespace DeskButler.EndToEnd;

/// <summary>保存一次真实资源采样。</summary>
/// <param name="Minute">从长测开始起经过的整分钟数。</param>
/// <param name="HandleCount">当前进程句柄数。</param>
/// <param name="PrivateBytes">当前进程私有字节数。</param>
/// <param name="DatabaseBytes">SQLite 主文件及 WAL/SHM 的总字节数。</param>
internal sealed record ResourceSample(
    int Minute,
    int HandleCount,
    long PrivateBytes,
    long DatabaseBytes,
    int NotificationCount = 0,
    int CaptureCount = 0,
    bool WorkloadCompleted = false,
    bool SchedulerStopped = false,
    bool StopListenerStarted = false);

/// <summary>保存可快速单测的稳定性判断结果。</summary>
/// <param name="IsStable">是否满足发布门槛。</param>
/// <param name="Reason">失败原因或稳定说明。</param>
internal sealed record ResourceStabilityResult(bool IsStable, string Reason);

/// <summary>集中实现资源趋势和三份有效快照门槛。</summary>
internal static class ResourceStabilityAnalyzer
{
    /// <summary>检查至少五个样本、最后五次句柄非严格单调增长且有效快照恰为三份。</summary>
    internal static ResourceStabilityResult Analyze(IReadOnlyList<ResourceSample> samples, int validSnapshots)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < 5)
        {
            return new ResourceStabilityResult(false, "至少需要五个资源样本。");
        }

        if (validSnapshots != 3)
        {
            return new ResourceStabilityResult(false, $"有效快照应恰为 3，实际为 {validSnapshots}。");
        }

        var tail = samples.TakeLast(5).ToArray();
        var handleSteps = tail.Zip(tail.Skip(1), (left, right) => right.HandleCount - left.HandleCount).ToArray();
        var handlesMonotonicallyGrow = handleSteps.All(change => change >= 0) && handleSteps.Any(change => change > 0);
        return handlesMonotonicallyGrow
            ? new ResourceStabilityResult(false, "最后五个样本的句柄数单调增长且没有回落。")
            : new ResourceStabilityResult(true, "句柄趋势与三份快照保留均满足门槛。");
    }
}
