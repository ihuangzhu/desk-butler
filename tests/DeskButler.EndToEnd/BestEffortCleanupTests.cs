namespace DeskButler.EndToEnd;

public sealed class BestEffortCleanupTests
{
    /// <summary>首个 lease 失败后仍执行其余 lease、连接池与临时目录清理，并聚合错误。</summary>
    [WindowsFact]
    public async Task 清理失败不会阻断后续精确资源释放()
    {
        var first = new FakeCleanupLease(throwAfterDispose: true);
        var second = new FakeCleanupLease(throwAfterDispose: false);
        var poolCleared = false;
        var tempDeleted = false;

        var exception = await Assert.ThrowsAsync<AggregateException>(() =>
            BestEffortCleanup.RunAsync(
                [first.DisposeAsync, second.DisposeAsync],
                [() => MarkAsync(() => poolCleared = true), () => MarkAsync(() => tempDeleted = true)]));

        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
        Assert.True(poolCleared);
        Assert.True(tempDeleted);
        Assert.Single(exception.InnerExceptions);
    }

    private static ValueTask MarkAsync(Action action)
    {
        action();
        return ValueTask.CompletedTask;
    }

    private sealed class FakeCleanupLease(bool throwAfterDispose)
    {
        internal bool Disposed { get; private set; }

        internal ValueTask DisposeAsync()
        {
            Disposed = true;
            return throwAfterDispose
                ? ValueTask.FromException(new InvalidOperationException("fixture close failed"))
                : ValueTask.CompletedTask;
        }
    }
}
