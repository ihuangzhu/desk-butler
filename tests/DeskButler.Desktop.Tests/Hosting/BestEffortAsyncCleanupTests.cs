using DeskButler.Desktop.Hosting;

namespace DeskButler.Desktop.Tests.Hosting;

public sealed class BestEffortAsyncCleanupTests
{
    /// <summary>已经向调用方发布失败的旧 pass 不得阻止未完成步骤开启重试。</summary>
    [Fact]
    public async Task ExternallyCompletedFailedPassCannotBeReturnedForRetry()
    {
        var coordinator = new CleanupPassCoordinator(
        [
            new("retryable", () => ValueTask.CompletedTask)
        ]);
        var failedPass = coordinator.Enter();
        var failure = new IOException("cleanup pass failed");

        failedPass.Completion!.TrySetException(failure);
        var observed = await Assert.ThrowsAsync<IOException>(() => failedPass.Task);
        var retry = coordinator.Enter();

        Assert.Same(failure, observed);
        Assert.True(retry.StartsPass);
        Assert.NotSame(failedPass.Task, retry.Task);
    }

    /// <summary>两个调用方遇到已完成旧 pass 时必须只创建并共享一个新重试 pass。</summary>
    [Fact]
    public async Task CallersAtExternallyCompletedBoundaryShareOneRetryPass()
    {
        var coordinator = new CleanupPassCoordinator(
        [
            new("retryable", () => ValueTask.CompletedTask)
        ]);
        var failedPass = coordinator.Enter();
        failedPass.Completion!.TrySetException(new IOException("cleanup pass failed"));
        _ = failedPass.Task.Exception;
        var releaseCallers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable.Range(0, 2)
            .Select(_ => Task.Run(async () =>
            {
                await releaseCallers.Task.WaitAsync(TestContext.Current.CancellationToken);
                return coordinator.Enter();
            }))
            .ToArray();

        releaseCallers.TrySetResult();
        var retries = await Task.WhenAll(callers);

        Assert.Single(retries, retry => retry.StartsPass);
        Assert.Same(retries[0].Task, retries[1].Task);
        Assert.NotSame(failedPass.Task, retries[0].Task);
    }

    /// <summary>全部步骤成功且结果已向调用方发布后，不得因旧 pass 登记而报告未完成。</summary>
    [Fact]
    public async Task ExternallyCompletedSuccessfulPassReportsComplete()
    {
        var coordinator = new CleanupPassCoordinator(
        [
            new("completed", () => ValueTask.CompletedTask)
        ]);
        var pass = coordinator.Enter();
        var state = Assert.Single(coordinator.GetIncompleteStates());
        coordinator.MarkCompleted(state);

        pass.Completion!.TrySetResult();
        await pass.Task;

        Assert.True(coordinator.IsComplete);
    }

    /// <summary>资源回调同步重入时必须观察已经发布的同一个 live pass。</summary>
    [Fact]
    public async Task SynchronousReentrantRunSharesPublishedPass()
    {
        BestEffortAsyncCleanup cleanup = null!;
        Task? reentrantPass = null;
        cleanup = new BestEffortAsyncCleanup(
        [
            new("reentrant", () =>
            {
                reentrantPass = cleanup.RunAsync().AsTask();
                return ValueTask.CompletedTask;
            })
        ]);

        var firstPass = cleanup.RunAsync().AsTask();
        await firstPass;

        Assert.Same(firstPass, reentrantPass);
        Assert.True(cleanup.IsComplete);
    }

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
