using DeskButler.Core.ResidentApps;
using DeskButler.Core.Settings;

namespace DeskButler.Core.Tests.ResidentApps;

public sealed class ResidentApplicationNormalizerTests
{
    /// <summary>验证首次运行时启用常驻功能且没有预置条目。</summary>
    [Fact]
    public void 默认设置启用常驻应用且列表为空()
    {
        Assert.True(ButlerSettings.Default.ResidentApplicationsEnabled);
        Assert.Empty(ButlerSettings.Default.ResidentApplications);
    }

    /// <summary>验证旧调用方不会因新增字段而误把常驻功能关闭。</summary>
    [Fact]
    public void CreateLegacy保留旧字段并使用常驻默认值()
    {
        var excludedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Apps\Excluded.exe"
        };

        var settings = ButlerSettings.CreateLegacy(false, false, 45, excludedPaths);

        Assert.False(settings.CaptureEnabled);
        Assert.False(settings.StartupEnabled);
        Assert.Equal(45, settings.RecoveryCardDismissSeconds);
        Assert.Same(excludedPaths, settings.ExcludedExecutablePaths);
        Assert.True(settings.ResidentApplicationsEnabled);
        Assert.Empty(settings.ResidentApplications);
    }

    /// <summary>验证相对路径和重复已知路径会得到稳定的绝对路径集合。</summary>
    [Fact]
    public void Normalize将路径转为绝对路径并确保启动路径被识别()
    {
        var result = ResidentApplicationNormalizer.Normalize(
        [
            App("apps\\QQ.exe", ["apps\\QQ.exe", "apps\\QQ.exe"], 0, displayName: "")
        ]);

        var application = Assert.Single(result.Applications);
        var expectedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath("apps\\QQ.exe"));

        Assert.Equal(expectedPath, application.LaunchPath);
        Assert.Equal("QQ", application.DisplayName);
        Assert.Single(application.KnownProcessPaths);
        Assert.Contains(expectedPath, application.KnownProcessPaths);
    }

    /// <summary>验证重复启动入口只保留输入中的第一项。</summary>
    [Fact]
    public void Normalize保留第一个重复启动入口()
    {
        var first = App(@"C:\Apps\QQ\QQ.exe", [@"C:\Apps\QQ\QQ.exe"], 0, displayName: "first");
        var duplicate = App(@"C:\Apps\QQ\QQ.exe", [@"C:\Apps\QQ\QQ.exe"], 1, displayName: "second");

        var result = ResidentApplicationNormalizer.Normalize([first, duplicate]);

        var application = Assert.Single(result.Applications);
        Assert.Equal("first", application.DisplayName);
        Assert.Contains(result.Diagnostics, item => item.Kind == ResidentNormalizationIssue.DuplicateLaunchPath);
    }

    /// <summary>验证已占用的启动入口会使后续条目整体无效。</summary>
    [Fact]
    public void Normalize丢弃启动入口与前项识别路径冲突的后项()
    {
        var first = App(@"C:\Apps\QQ\QQ.exe", [@"C:\Apps\QQ\QQ.exe", @"C:\Apps\QQ\helper.exe"], 0);
        var conflicting = App(@"C:\Apps\QQ\helper.exe", [@"C:\Apps\QQ\helper.exe"], 1);

        var result = ResidentApplicationNormalizer.Normalize([first, conflicting]);

        Assert.Single(result.Applications);
        Assert.Contains(result.Diagnostics, item => item.Kind == ResidentNormalizationIssue.LaunchPathConflict);
    }

    /// <summary>验证仅识别路径冲突时保留后续条目并移除冲突路径。</summary>
    [Fact]
    public void NormalizeKeepsFirstLaunchIdentityAndRemovesLaterKnownPathConflict()
    {
        var first = App(@"C:\Apps\QQ\QQ.exe", [@"C:\Apps\QQ\QQ.exe", @"C:\Apps\QQ\helper.exe"], 0);
        var second = App(@"C:\Apps\Futu\Futu.exe", [@"C:\Apps\Futu\Futu.exe", @"C:\Apps\QQ\helper.exe"], 1);

        var result = ResidentApplicationNormalizer.Normalize([first, second]);

        Assert.Equal([@"C:\Apps\QQ\QQ.exe", @"C:\Apps\Futu\Futu.exe"],
            result.Applications.Select(item => item.LaunchPath));
        Assert.DoesNotContain(@"C:\Apps\QQ\helper.exe", result.Applications[1].KnownProcessPaths);
        Assert.Equal([0, 1], result.Applications.Select(item => item.LaunchOrder));
        Assert.Contains(result.Diagnostics, item => item.Kind == ResidentNormalizationIssue.KnownPathConflict);
    }

    /// <summary>验证损坏路径只隔离当前条目且诊断不携带异常文本。</summary>
    [Fact]
    public void Normalize隔离损坏路径且不泄露原始异常消息()
    {
        var invalid = App("bad\0path", ["bad\0path"], 0);
        var valid = App(@"C:\Apps\Futu\Futu.exe", [@"C:\Apps\Futu\Futu.exe"], 1);

        var result = ResidentApplicationNormalizer.Normalize([invalid, valid]);

        var application = Assert.Single(result.Applications);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(@"C:\Apps\Futu\Futu.exe", application.LaunchPath);
        Assert.Equal(ResidentNormalizationIssue.InvalidPath, diagnostic.Kind);
        Assert.DoesNotContain("bad", diagnostic.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>验证按原顺序和路径排序后始终重新编号为连续顺序。</summary>
    [Fact]
    public void Normalize按启动顺序和路径稳定排序并重新编号()
    {
        var result = ResidentApplicationNormalizer.Normalize(
        [
            App(@"C:\Apps\Zeta\Zeta.exe", [@"C:\Apps\Zeta\Zeta.exe"], 5),
            App(@"C:\Apps\Alpha\Alpha.exe", [@"C:\Apps\Alpha\Alpha.exe"], 5),
            App(@"C:\Apps\Middle\Middle.exe", [@"C:\Apps\Middle\Middle.exe"], -1)
        ]);

        Assert.Equal(
        [
            @"C:\Apps\Middle\Middle.exe",
            @"C:\Apps\Alpha\Alpha.exe",
            @"C:\Apps\Zeta\Zeta.exe"
        ], result.Applications.Select(item => item.LaunchPath));
        Assert.Equal([0, 1, 2], result.Applications.Select(item => item.LaunchOrder));
    }

    /// <summary>创建用于正规化测试的常驻应用条目。</summary>
    private static ResidentApplication App(
        string launchPath,
        string[] knownProcessPaths,
        int launchOrder,
        string displayName = "Application") =>
        new(
            launchPath,
            new HashSet<string>(knownProcessPaths, StringComparer.OrdinalIgnoreCase),
            displayName,
            true,
            launchOrder);
}
