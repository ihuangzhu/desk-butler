namespace DeskButler.Infrastructure.Windows.Tests;

public sealed class TestHostTests
{
    /// <summary>验证 Windows 基础设施测试程序集可由 xUnit 测试宿主发现并执行。</summary>
    [Fact]
    public void 测试宿主可执行且基础断言为真()
    {
        Assert.True(true);
    }
}
