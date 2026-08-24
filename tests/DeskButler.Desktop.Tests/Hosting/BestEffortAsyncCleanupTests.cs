using DeskButler.Desktop.Hosting;

namespace DeskButler.Desktop.Tests.Hosting;

public sealed class BestEffortAsyncCleanupTests
{
    /// <summary>单步失败不得阻止后续资源释放，再次调用只重试尚未成功的步骤。</summary>
    [Fact]
    public async Task FailureContinuesRemainingStepsAndRetryOnlyRunsFailedStep()
    {
        var failingAttempts = 0;
        var laterCalls = 0;
        var cleanup = new BestEffortAsyncCleanup(
        [
            new("session", () =>
            {
                if (++failingAttempts == 1)
                {
                    throw new InvalidOperationException("detach failed");
                }

                return ValueTask.CompletedTask;
            }),
            new("tray", () =>
            {
                laterCalls++;
                return ValueTask.CompletedTask;
            })
        ]);

        await Assert.ThrowsAsync<AggregateException>(async () => await cleanup.RunAsync());
        Assert.Equal(1, laterCalls);
        Assert.False(cleanup.IsComplete);

        await cleanup.RunAsync();

        Assert.Equal(2, failingAttempts);
        Assert.Equal(1, laterCalls);
        Assert.True(cleanup.IsComplete);
    }

    /// <summary>宿主清理故障时 marker 与互斥量仍必须释放，组合清理还会重试一次。</summary>
    [Fact]
    public async Task ExitCleanupAlwaysReleasesMarkerAndMutexWhenCompositionFails()
    {
        var compositionAttempts = 0;
        var markerReleased = false;
        var markerWasClean = true;
        var mutexReleased = false;

        var error = await ExitCleanupCoordinator.RunAsync(
            () =>
            {
                compositionAttempts++;
                return ValueTask.FromException(new InvalidOperationException("cleanup failed"));
            },
            clean => { markerReleased = true; markerWasClean = clean; },
            () => mutexReleased = true);

        Assert.NotNull(error);
        Assert.Equal(2, compositionAttempts);
        Assert.True(markerReleased);
        Assert.False(markerWasClean);
        Assert.True(mutexReleased);
    }
}
