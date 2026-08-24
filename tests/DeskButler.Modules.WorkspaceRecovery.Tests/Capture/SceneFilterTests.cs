using DeskButler.Core.Settings;
using DeskButler.Modules.WorkspaceRecovery.Capture;

namespace DeskButler.Modules.WorkspaceRecovery.Tests.Capture;

public sealed class SceneFilterTests
{
    /// <summary>验证普通可见主窗口会被纳入场景捕获。</summary>
    [Fact]
    public void ShouldCaptureIncludesVisibleNormalMainWindow()
    {
        var candidate = CandidateFactory.Normal(@"C:\Apps\notepad.exe", "Notes");

        Assert.True(new SceneFilter(ButlerSettings.Default).ShouldCapture(candidate));
    }

    /// <summary>验证系统窗口不会被纳入场景捕获。</summary>
    [Fact]
    public void ShouldCaptureRejectsSystemWindow()
    {
        var candidate = CandidateFactory.Normal() with { IsSystemWindow = true };

        Assert.False(new SceneFilter(ButlerSettings.Default).ShouldCapture(candidate));
    }

    /// <summary>验证临时窗口不会被纳入场景捕获。</summary>
    [Fact]
    public void ShouldCaptureRejectsTemporaryWindow()
    {
        var candidate = CandidateFactory.Normal() with { IsTemporaryWindow = true };

        Assert.False(new SceneFilter(ButlerSettings.Default).ShouldCapture(candidate));
    }

    /// <summary>验证 DeskButler 自身窗口不会被纳入场景捕获。</summary>
    [Fact]
    public void ShouldCaptureRejectsDeskButlerWindow()
    {
        var candidate = CandidateFactory.Normal() with { IsDeskButlerWindow = true };

        Assert.False(new SceneFilter(ButlerSettings.Default).ShouldCapture(candidate));
    }

    /// <summary>验证缺少可执行文件路径的窗口不会被纳入场景捕获。</summary>
    [Fact]
    public void ShouldCaptureRejectsWindowWithoutExecutablePath()
    {
        var candidate = CandidateFactory.Normal(null);

        Assert.False(new SceneFilter(ButlerSettings.Default).ShouldCapture(candidate));
    }

    /// <summary>验证非可见主窗口不会被纳入场景捕获。</summary>
    [Fact]
    public void ShouldCaptureRejectsWindowThatIsNotVisibleMainWindow()
    {
        var candidate = CandidateFactory.Normal() with { IsVisibleMainWindow = false };

        Assert.False(new SceneFilter(ButlerSettings.Default).ShouldCapture(candidate));
    }

    /// <summary>验证用户排除路径在大小写及冗余路径段不同的情况下仍会生效。</summary>
    [Fact]
    public void ShouldCaptureRejectsExcludedExecutable()
    {
        var settings = ButlerSettings.Default with
        {
            ExcludedExecutablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                @"C:\Tools\ignored.exe"
            }
        };
        var candidate = CandidateFactory.Normal(@"c:\tools\.\IGNORED.EXE", "Ignored");

        Assert.False(new SceneFilter(settings).ShouldCapture(candidate));
    }
}
