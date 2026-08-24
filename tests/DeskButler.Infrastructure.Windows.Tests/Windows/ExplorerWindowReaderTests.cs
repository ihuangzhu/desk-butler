using DeskButler.Infrastructure.Windows.Windows;

namespace DeskButler.Infrastructure.Windows.Tests.Windows;

public sealed class ExplorerWindowReaderTests
{
    /// <summary>验证读取器只把匹配 HWND 的现有本地 file 位置关联为资源管理器目录。</summary>
    [Fact]
    public void TryGetFolderPath仅返回匹配句柄的现有本地目录()
    {
        var source = new FakeExplorerWindowSource(
            new ExplorerWindowLocation(10, "https://example.test/folder"),
            new ExplorerWindowLocation(11, "file:///C:/Other"),
            new ExplorerWindowLocation(10, "file:///C:/Work/Project"));
        var reader = new ExplorerWindowReader(source, path => path == @"C:\Work\Project");

        var path = reader.TryGetFolderPath(10);

        Assert.Equal(@"C:\Work\Project", path);
    }

    /// <summary>验证非 file 协议和所有 UNC authority 即使目录检查通过也会被拒绝。</summary>
    [Theory]
    [InlineData("https://example.test/folder")]
    [InlineData("file://server/share")]
    [InlineData("file://localhost/C:/Private")]
    [InlineData("file://127.0.0.1/C:/Private")]
    public void TryGetFolderPath拒绝远程位置即使目录检查通过(string location)
    {
        var reader = new ExplorerWindowReader(
            new FakeExplorerWindowSource(new ExplorerWindowLocation(10, location)),
            _ => true);

        Assert.Null(reader.TryGetFolderPath(10));
    }

    /// <summary>验证不存在或空的本地 file 位置不会持久化为恢复目录。</summary>
    [Theory]
    [InlineData("file:///C:/Missing")]
    [InlineData(null)]
    public void TryGetFolderPath拒绝不存在或空的本地位置(string? location)
    {
        var reader = new ExplorerWindowReader(
            new FakeExplorerWindowSource(new ExplorerWindowLocation(10, location)),
            _ => false);

        Assert.Null(reader.TryGetFolderPath(10));
    }

    /// <summary>创建返回指定 Shell 窗口位置的测试来源。</summary>
    /// <param name="windows">受控 Shell 窗口位置。</param>
    private sealed class FakeExplorerWindowSource(params ExplorerWindowLocation[] windows) : IExplorerWindowSource
    {
        /// <summary>返回受控 Shell 窗口位置，避免测试启动或读取真实 Explorer。</summary>
        public IReadOnlyList<ExplorerWindowLocation> ReadLocations() => windows;
    }
}
