namespace DeskButler.Infrastructure.Windows.Tests.Windows;

public sealed class TestWindowProcessTests
{
    /// <summary>验证等待超时后进程已自行退出时不再 Kill 且仍释放资源。</summary>
    [Fact]
    public async Task DisposeLifetimeAsync在Kill前已退出时安全完成()
    {
        var lifetime = new ExitDuringTimeoutProcessLifetime();

        await TestWindowProcess.DisposeLifetimeAsync(lifetime);

        Assert.False(lifetime.KillWasCalled);
        Assert.True(lifetime.WasDisposed);
    }

    private sealed class ExitDuringTimeoutProcessLifetime : ITestProcessLifetime
    {
        private int hasExitedReads;

        internal bool KillWasCalled { get; private set; }
        internal bool WasDisposed { get; private set; }

        public bool HasExited => ++hasExitedReads > 1;

        /// <summary>模拟成功发送关闭主窗口请求。</summary>
        public void CloseMainWindow()
        {
        }

        /// <summary>模拟等待超时，同时进程随后自行退出。</summary>
        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        /// <summary>记录不应发生的强制终止调用。</summary>
        public void Kill()
        {
            KillWasCalled = true;
            throw new InvalidOperationException("进程已经退出。");
        }

        /// <summary>记录进程资源已释放。</summary>
        public void Dispose()
        {
            WasDisposed = true;
        }
    }
}
