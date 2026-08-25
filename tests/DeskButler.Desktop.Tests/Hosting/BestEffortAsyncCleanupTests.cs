using DeskButler.Desktop.Hosting;

namespace DeskButler.Desktop.Tests.Hosting;

public sealed class BestEffortAsyncCleanupTests
{
    /// <summary>两个并发调用必须共享同一清理 pass，成功步骤只执行一次。</summary>
    [Fact]
    public async Task ConcurrentRunsShareOneSuccessfulPass()
    {
        var stepStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cleanup = new BestEffortAsyncCleanup(
        [
            new("shared", async () =>
            {
                Interlocked.Increment(ref calls);
                stepStarted.TrySetResult();
                await releaseStep.Task.WaitAsync(TestContext.Current.CancellationToken);
            })
        ]);

        var first = cleanup.RunAsync().AsTask();
        await stepStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = cleanup.RunAsync().AsTask();
        try
        {
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            releaseStep.TrySetResult();
        }

        await Task.WhenAll(first, second);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.True(cleanup.IsComplete);
    }

    /// <summary>并发调用必须共享失败结果，失败步骤只能在整个 pass 结束后的新调用中重试。</summary>
    [Fact]
    public async Task ConcurrentRunsShareFailureBeforeRetryStartsNewPass()
    {
        var firstAttemptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("retryable cleanup failed");
        var attempts = 0;
        var cleanup = new BestEffortAsyncCleanup(
        [
            new("retryable", async () =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt == 1)
                {
                    firstAttemptStarted.TrySetResult();
                    await releaseFailure.Task.WaitAsync(TestContext.Current.CancellationToken);
                    throw failure;
                }
            })
        ]);

        var first = cleanup.RunAsync().AsTask();
        await firstAttemptStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = cleanup.RunAsync().AsTask();
        releaseFailure.TrySetResult();

        var firstError = await Assert.ThrowsAsync<AggregateException>(() => first);
        var secondError = await Assert.ThrowsAsync<AggregateException>(() => second);

        Assert.Same(firstError, secondError);
        Assert.Equal(1, Volatile.Read(ref attempts));
        Assert.False(cleanup.IsComplete);

        await cleanup.RunAsync();

        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.True(cleanup.IsComplete);
    }

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
