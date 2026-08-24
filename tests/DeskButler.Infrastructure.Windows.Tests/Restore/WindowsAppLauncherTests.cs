using System.Diagnostics;
using DeskButler.Core.Scenes;
using DeskButler.Infrastructure.Windows.Restore;

namespace DeskButler.Infrastructure.Windows.Tests.Restore;

public sealed class WindowsAppLauncherTests
{
    /// <summary>验证普通程序只使用可执行路径且不携带捕获参数。</summary>
    [Fact]
    public async Task LaunchAsync普通程序仅使用ExecutablePath()
    {
        var starter = new RecordingProcessStarter();
        var launcher = new WindowsAppLauncher(starter, _ => true, @"C:\Windows");

        await launcher.LaunchAsync(App("tool", @"C:\Apps\tool.exe"), CancellationToken.None);

        var startInfo = Assert.Single(starter.StartInfos);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal(@"C:\Apps\tool.exe", startInfo.FileName);
        Assert.Empty(startInfo.ArgumentList);
        Assert.Equal(string.Empty, startInfo.Arguments);
        Assert.True(starter.ReturnedProcessWasDisposed);
    }

    /// <summary>验证 Explorer 由适配器固定 exe 并仅传一个经过验证的本地目录参数。</summary>
    [Fact]
    public async Task LaunchAsync的Explorer只传一个本地目录参数()
    {
        var starter = new RecordingProcessStarter();
        var launcher = new WindowsAppLauncher(starter, path => path == @"C:\Work Files\Project", @"C:\Windows");

        await launcher.LaunchAsync(
            App("folder", @"C:\Captured\explorer.exe", explorerPath: @"C:\Work Files\Project"),
            CancellationToken.None);

        var startInfo = Assert.Single(starter.StartInfos);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal(@"C:\Windows\explorer.exe", startInfo.FileName);
        Assert.Equal([@"C:\Work Files\Project"], startInfo.ArgumentList);
        Assert.Equal(string.Empty, startInfo.Arguments);
    }

    /// <summary>验证 UNC、URI、相对及带非法引号的 Explorer 输入均在启动前拒绝。</summary>
    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData("https://example.test/folder")]
    [InlineData(@"relative\folder")]
    [InlineData("C:\\Work\\\" /select,C:\\Secret")]
    [InlineData("C:\\Work\\bad\u0001name")]
    [InlineData(@"C:\Work:alternate")]
    [InlineData(@"C:\Work\bad<name")]
    public async Task LaunchAsync拒绝非绝对本地Explorer目录(string path)
    {
        var starter = new RecordingProcessStarter();
        var launcher = new WindowsAppLauncher(starter, _ => true, @"C:\Windows");

        await Assert.ThrowsAsync<ArgumentException>(() => launcher.LaunchAsync(
            App("folder", @"C:\Windows\explorer.exe", explorerPath: path), CancellationToken.None));

        Assert.Empty(starter.StartInfos);
    }

    /// <summary>创建固定场景项目。</summary>
    private static SceneItem App(string id, string executablePath, string? explorerPath = null) => new(
        id, executablePath, "ToolClass", null, explorerPath,
        new WindowBounds(0, 0, 800, 600), SceneWindowState.Normal,
        new MonitorIdentity(@"\\.\DISPLAY1", new WindowBounds(0, 0, 1920, 1040), 96, 96), false);

    private sealed class RecordingProcessStarter : IProcessStarter
    {
        internal List<ProcessStartInfo> StartInfos { get; } = [];

        internal bool ReturnedProcessWasDisposed { get; private set; }

        /// <summary>复制启动信息，模拟 Shell 启动未返回 Process 对象。</summary>
        public IDisposable? Start(ProcessStartInfo startInfo)
        {
            var copy = new ProcessStartInfo
            {
                FileName = startInfo.FileName,
                Arguments = startInfo.Arguments,
                UseShellExecute = startInfo.UseShellExecute
            };
            foreach (var argument in startInfo.ArgumentList)
            {
                copy.ArgumentList.Add(argument);
            }

            StartInfos.Add(copy);
            return new DisposeCallback(() => ReturnedProcessWasDisposed = true);
        }

        private sealed class DisposeCallback(Action callback) : IDisposable
        {
            /// <summary>记录启动器已释放借用的进程包装。</summary>
            public void Dispose() => callback();
        }
    }
}
