namespace DeskButler.EndToEnd;

public sealed class DpiAwarenessContextScopeTests
{
    /// <summary>首次进入 PMv2 返回零时立即失败，且不得执行主体。</summary>
    [WindowsFact]
    public void 进入Dpi上下文失败不执行主体()
    {
        var native = new FakeDpiContextNative([0]);
        var bodyCalled = false;

        Assert.Throws<System.ComponentModel.Win32Exception>(() =>
            DpiAwarenessContextScope.Run(native, () => bodyCalled = true));

        Assert.False(bodyCalled);
        Assert.Single(native.Calls);
    }

    /// <summary>恢复原上下文失败必须显式失败，不能让后续测试线程被无声污染。</summary>
    [WindowsFact]
    public void 恢复Dpi上下文失败可观察()
    {
        var native = new FakeDpiContextNative([new nint(7), 0]);

        var exception = Assert.Throws<System.ComponentModel.Win32Exception>(() =>
            DpiAwarenessContextScope.Run(native, () => 42));

        Assert.Contains("恢复", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, native.Calls.Count);
        Assert.Equal(new nint(7), native.Calls[1]);
    }

    /// <summary>主体与恢复同时失败时两项异常都必须保留。</summary>
    [WindowsFact]
    public void 主体及恢复同时失败形成聚合异常()
    {
        var native = new FakeDpiContextNative([new nint(7), 0]);

        var exception = Assert.Throws<AggregateException>(() =>
            DpiAwarenessContextScope.Run<int>(native, () => throw new InvalidOperationException("body")));

        Assert.Contains(exception.InnerExceptions, item => item is InvalidOperationException);
        Assert.Contains(exception.InnerExceptions, item => item is System.ComponentModel.Win32Exception);
    }

    private sealed class FakeDpiContextNative(IReadOnlyList<nint> results) : IDpiAwarenessContextNative
    {
        private int index;

        internal List<nint> Calls { get; } = [];

        public nint SetThreadContext(nint context)
        {
            Calls.Add(context);
            return results[index++];
        }

        public int GetLastError() => 5;
    }
}
