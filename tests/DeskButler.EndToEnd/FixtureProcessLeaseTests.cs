namespace DeskButler.EndToEnd;

public sealed class FixtureProcessLeaseTests
{
    /// <summary>原始进程句柄已退出后，即使 PID 可被系统复用也绝不调用终止。</summary>
    [WindowsFact]
    public async Task 原始进程已退出时不按复用PID重新解析或终止()
    {
        var lifetime = new FakeFixtureProcessLifetime(hasExited: true);
        await using var lease = new FixtureProcessLease(lifetime);

        await lease.CloseAsync(CancellationToken.None);

        Assert.Equal(0, lifetime.CloseCalls);
        Assert.Equal(0, lifetime.TerminateCalls);
        Assert.Equal(0, lifetime.WaitCalls);
        Assert.False(lease.RepresentsRunningIdentity(
            4242, @"C:\fixture\DeskButler.TestWindow.exe", lifetime.StartTimeUtc.AddSeconds(1)));
    }

    private sealed class FakeFixtureProcessLifetime(bool hasExited) : IFixtureProcessLifetime
    {
        internal int CloseCalls { get; private set; }

        internal int TerminateCalls { get; private set; }

        internal int WaitCalls { get; private set; }

        public bool HasExited => hasExited;

        public int ProcessId => 4242;

        public string ExecutablePath => @"C:\fixture\DeskButler.TestWindow.exe";

        public DateTime StartTimeUtc { get; } = new(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc);

        public nint MainWindowHandle => 0;

        /// <summary>fake 无缓存需要刷新。</summary>
        public void Refresh()
        {
        }

        /// <summary>记录正常关闭请求。</summary>
        public void CloseMainWindow() => CloseCalls++;

        /// <summary>记录对原始进程句柄的等待。</summary>
        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitCalls++;
            return Task.CompletedTask;
        }

        /// <summary>记录对原始进程句柄的精确终止。</summary>
        public void TerminateHeldProcess() => TerminateCalls++;

        /// <summary>fake 不持有真实资源。</summary>
        public void Dispose()
        {
        }
    }
}
