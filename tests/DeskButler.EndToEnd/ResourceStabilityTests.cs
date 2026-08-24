using System.Globalization;
using System.Text.Json;

namespace DeskButler.EndToEnd;

public sealed class ResourceStabilityTests
{
    private static readonly string[] CsvHeader =
        ["minute,handleCount,privateBytes,databaseBytes,notificationCount,captureCount,workloadCompleted,schedulerStopped,stopListenerStarted"];
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };

    /// <summary>快速验证最后五个句柄样本持续上升时会被核心断言拒绝。</summary>
    [WindowsFact]
    public void 快速分析拒绝最后五个样本单调增长()
    {
        var samples = new[]
        {
            new ResourceSample(0, 20, 1_000, 10),
            new ResourceSample(1, 21, 1_100, 11),
            new ResourceSample(2, 22, 1_200, 12),
            new ResourceSample(3, 23, 1_300, 13),
            new ResourceSample(4, 24, 1_400, 14)
        };

        var result = ResourceStabilityAnalyzer.Analyze(samples, validSnapshots: 3);

        Assert.False(result.IsStable);
        Assert.Contains("句柄", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>快速验证夹有平台期但总体不下降的句柄序列仍属于单调增长。</summary>
    [WindowsFact]
    public void 快速分析拒绝带平台期的单调句柄增长()
    {
        var samples = new[]
        {
            new ResourceSample(0, 20, 1_000, 10),
            new ResourceSample(1, 21, 1_100, 11),
            new ResourceSample(2, 21, 1_200, 12),
            new ResourceSample(3, 22, 1_300, 13),
            new ResourceSample(4, 23, 1_400, 14)
        };

        var result = ResourceStabilityAnalyzer.Analyze(samples, validSnapshots: 3);

        Assert.False(result.IsStable);
        Assert.Contains("句柄", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>快速验证句柄出现回落且数据库只含三份有效快照时通过。</summary>
    [WindowsFact]
    public void 快速分析接受非单调句柄与三份快照()
    {
        var samples = new[]
        {
            new ResourceSample(0, 20, 1_000, 10),
            new ResourceSample(1, 21, 1_100, 11),
            new ResourceSample(2, 20, 1_200, 12),
            new ResourceSample(3, 22, 1_300, 13),
            new ResourceSample(4, 21, 1_400, 14)
        };

        var result = ResourceStabilityAnalyzer.Analyze(samples, validSnapshots: 3);

        Assert.True(result.IsStable, result.Reason);
    }

    /// <summary>快速验证数据库有效快照不是三份时会被拒绝。</summary>
    [WindowsFact]
    public void 快速分析拒绝超过三份有效快照()
    {
        var samples = Enumerable.Range(0, 5)
            .Select(minute => new ResourceSample(minute, 20, 1_000, 10))
            .ToArray();

        var result = ResourceStabilityAnalyzer.Analyze(samples, validSnapshots: 4);

        Assert.False(result.IsStable);
        Assert.Contains("恰为 3", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>短时密集采样专用 workload 子进程，证明测试宿主句柄不再混入目标。</summary>
    [WindowsFact]
    public async Task 专用资源进程短时密集采样保持稳定()
    {
        await using var harness = await ResourceHarnessProcess.StartAsync(
            TestContext.Current.CancellationToken,
            TimeSpan.FromSeconds(10));
        var samples = new List<ResourceSample>();
        for (var index = 0; index < 200; index++)
        {
            samples.Add(harness.Sample(index));
            if (samples[^1].WorkloadCompleted)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        await harness.WaitForCompletionAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var handleCountBeforeCompletion = samples.Last(sample => !sample.WorkloadCompleted).HandleCount;
        var completionSamples = new List<ResourceSample>();
        for (var index = 0; index < 10; index++)
        {
            completionSamples.Add(harness.Sample(100 + index));
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        samples.AddRange(completionSamples);
        var validSnapshots = await harness.CountValidSnapshotsAsync(TestContext.Current.CancellationToken);
        var result = ResourceStabilityAnalyzer.Analyze(samples, validSnapshots);
        Assert.True(result.IsStable,
            $"{result.Reason} tail={string.Join(',', samples.TakeLast(5).Select(sample => sample.HandleCount))}");
        Assert.Equal(10_000, samples[^1].NotificationCount);
        Assert.Equal(100, samples[^1].CaptureCount);
        Assert.True(samples[^1].WorkloadCompleted);
        Assert.False(samples[^1].SchedulerStopped);
        Assert.True(harness.IsRunning);
        Assert.All(samples, sample => Assert.True(sample.StopListenerStarted));
        Assert.InRange(completionSamples.Max(sample => sample.HandleCount) - handleCountBeforeCompletion, int.MinValue, 2);
    }

    /// <summary>真实运行三十分钟，每分钟采集进程与数据库资源并保存仓库外证据。</summary>
    [LongRunningWindowsFact]
    [Trait("Category", "LongRunning")]
    public async Task 三十分钟真实采样不出现持续句柄增长且仅保留三份快照()
    {
        var evidenceDirectory = Path.GetFullPath(Environment.GetEnvironmentVariable("DESKBUTLER_EVIDENCE_DIRECTORY")!);
        Directory.CreateDirectory(evidenceDirectory);
        var runId = $"resource-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var startedAtUtc = DateTimeOffset.UtcNow;
        await using var harness = await ResourceHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var samples = new List<ResourceSample>(31);
        var validSnapshots = 0;
        for (var minute = 0; minute <= 30; minute++)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            samples.Add(harness.Sample(minute));
            validSnapshots = await harness.CountValidSnapshotsAsync(TestContext.Current.CancellationToken);
            await WriteEvidenceAsync(
                evidenceDirectory,
                runId,
                startedAtUtc,
                samples,
                validSnapshots,
                samples.Count >= 5 ? ResourceStabilityAnalyzer.Analyze(samples, validSnapshots) : null,
                CancellationToken.None);
            if (minute < 30)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
            }
        }

        await harness.WaitForCompletionAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        samples[^1] = harness.Sample(30);
        Assert.True(harness.IsRunning);
        Assert.True(samples[^1].WorkloadCompleted);
        Assert.False(samples[^1].SchedulerStopped);
        Assert.Equal(10_000, samples[^1].NotificationCount);
        Assert.Equal(100, samples[^1].CaptureCount);
        validSnapshots = await harness.CountValidSnapshotsAsync(TestContext.Current.CancellationToken);
        var result = ResourceStabilityAnalyzer.Analyze(samples, validSnapshots);
        await WriteEvidenceAsync(evidenceDirectory, runId, startedAtUtc, samples, validSnapshots, result, CancellationToken.None);
        Assert.True(result.IsStable, result.Reason);
    }

    /// <summary>把完整分钟样本同时写为 CSV 与 JSON，便于发布审查。</summary>
    private static async Task WriteEvidenceAsync(
        string directory,
        string runId,
        DateTimeOffset startedAtUtc,
        List<ResourceSample> samples,
        int validSnapshots,
        ResourceStabilityResult? result,
        CancellationToken cancellationToken)
    {
        var csvLines = CsvHeader
            .Concat(samples.Select(sample => string.Join(',',
                sample.Minute.ToString(CultureInfo.InvariantCulture),
                sample.HandleCount.ToString(CultureInfo.InvariantCulture),
                sample.PrivateBytes.ToString(CultureInfo.InvariantCulture),
                sample.DatabaseBytes.ToString(CultureInfo.InvariantCulture),
                sample.NotificationCount.ToString(CultureInfo.InvariantCulture),
                sample.CaptureCount.ToString(CultureInfo.InvariantCulture),
                sample.WorkloadCompleted.ToString(CultureInfo.InvariantCulture),
                sample.SchedulerStopped.ToString(CultureInfo.InvariantCulture),
                sample.StopListenerStarted.ToString(CultureInfo.InvariantCulture))));
        await File.WriteAllLinesAsync(Path.Combine(directory, runId + ".csv"), csvLines, cancellationToken);
        var payload = new
        {
            startedAtUtc,
            updatedAtUtc = DateTimeOffset.UtcNow,
            completed = samples.Count == 31 && result is not null,
            validSnapshots,
            result,
            samples
        };
        await File.WriteAllTextAsync(
            Path.Combine(directory, runId + ".json"),
            JsonSerializer.Serialize(payload, EvidenceJsonOptions),
            cancellationToken);
    }

}
