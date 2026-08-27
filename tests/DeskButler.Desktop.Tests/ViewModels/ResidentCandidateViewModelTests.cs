using System.Windows.Media;
using DeskButler.Core.ResidentApps;
using DeskButler.Desktop.Hosting;
using DeskButler.Desktop.ViewModels;

namespace DeskButler.Desktop.Tests.ViewModels;

/// <summary>覆盖常驻候选的默认选择、路径编辑与确认快照边界。</summary>
public sealed class ResidentCandidateViewModelTests
{
    /// <summary>高可信且有明确入口的新增候选默认勾选并允许确认。</summary>
    [Fact]
    public void HighConfidenceNewCandidateWithLaunchPathIsSelectedByDefault()
    {
        var candidate = CreateCandidate(
            confidence: ResidentCandidateConfidence.High,
            kind: ResidentCandidateKind.NewApplication,
            launchPath: @"C:\Apps\Agent.exe");

        var vm = new ResidentCandidateViewModel(candidate, 17, new FakeExecutablePicker(), new FakeExecutableIconProvider());

        Assert.True(vm.IsSelected);
        Assert.True(vm.CanConfirm);
        Assert.Equal(17, vm.Generation);
        Assert.Equal(@"C:\Apps\Agent.exe", vm.ToSelection().FinalLaunchPath);
    }

    /// <summary>低可信、路径替换和空入口候选均不得默认选择或让确认命令可用。</summary>
    [Theory]
    [InlineData(ResidentCandidateConfidence.Low, ResidentCandidateKind.NewApplication, @"C:\Apps\Low.exe")]
    [InlineData(ResidentCandidateConfidence.High, ResidentCandidateKind.PathReplacement, @"C:\Apps\Replacement.exe")]
    [InlineData(ResidentCandidateConfidence.High, ResidentCandidateKind.NewApplication, null)]
    public void UnsafeDefaultCandidatesAreNotSelectedAndCannotConfirm(
        ResidentCandidateConfidence confidence,
        ResidentCandidateKind kind,
        string? launchPath)
    {
        var candidate = CreateCandidate(confidence, kind, launchPath);

        var vm = new ResidentCandidateViewModel(candidate, 18, new FakeExecutablePicker(), new FakeExecutableIconProvider());

        Assert.False(vm.IsSelected);
        Assert.False(vm.CanConfirm);
    }

    /// <summary>路径替换必须同时显示旧入口和新入口，且回传同一发现代次。</summary>
    [Fact]
    public void PathReplacementShowsBothPathsAndKeepsGeneration()
    {
        var candidate = CreateCandidate(
            ResidentCandidateConfidence.High,
            ResidentCandidateKind.PathReplacement,
            @"C:\New\Agent.exe") with { ReplacesLaunchPath = @"C:\Old\Agent.exe" };

        var vm = new ResidentCandidateViewModel(candidate, 19, new FakeExecutablePicker(), new FakeExecutableIconProvider());

        Assert.Contains(@"C:\Old\Agent.exe", vm.PathReplacementText, StringComparison.Ordinal);
        Assert.Contains(@"C:\New\Agent.exe", vm.PathReplacementText, StringComparison.Ordinal);
        Assert.Equal(19, vm.Generation);
    }

    /// <summary>路径替换浏览后必须通知绑定层刷新旧、新入口说明。</summary>
    [Fact]
    public async Task BrowsingPathReplacementRaisesPathReplacementTextChanged()
    {
        var candidate = CreateCandidate(
            ResidentCandidateConfidence.High,
            ResidentCandidateKind.PathReplacement,
            @"C:\New\Agent.exe") with { ReplacesLaunchPath = @"C:\Old\Agent.exe" };
        var vm = new ResidentCandidateViewModel(
            candidate,
            21,
            new FakeExecutablePicker(@"C:\Chosen\Agent.exe"),
            new FakeExecutableIconProvider());
        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

        await vm.BrowsePathAsync();

        Assert.Contains(nameof(ResidentCandidateViewModel.PathReplacementText), changedProperties);
        Assert.Contains(@"C:\Old\Agent.exe", vm.PathReplacementText, StringComparison.Ordinal);
        Assert.Contains(@"C:\Chosen\Agent.exe", vm.PathReplacementText, StringComparison.Ordinal);
    }

    /// <summary>空白启动入口必须与空值同样提示用户选择主程序，避免只靠 XAML 空值触发器漏掉空字符串。</summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData(@"C:\Apps\Agent.exe", false)]
    public void NeedsLaunchPathTreatsNullEmptyAndWhitespaceAsMissing(string? launchPath, bool expected)
    {
        var vm = new ResidentCandidateViewModel(
            CreateCandidate(ResidentCandidateConfidence.Low, ResidentCandidateKind.NewApplication, launchPath),
            24,
            new FakeExecutablePicker(),
            new FakeExecutableIconProvider());

        Assert.Equal(expected, vm.NeedsLaunchPath);
    }

    /// <summary>路径草稿从有效变为空白时必须通知绑定层重新评估提示可见性。</summary>
    [Fact]
    public void ChangingLaunchPathRaisesNeedsLaunchPathNotification()
    {
        var vm = new ResidentCandidateViewModel(
            CreateCandidate(ResidentCandidateConfidence.Low, ResidentCandidateKind.NewApplication, @"C:\Apps\Agent.exe"),
            25,
            new FakeExecutablePicker(),
            new FakeExecutableIconProvider());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, eventArgs) => changed.Add(eventArgs.PropertyName);

        vm.FinalLaunchPath = " ";

        Assert.True(vm.NeedsLaunchPath);
        Assert.Contains(nameof(ResidentCandidateViewModel.NeedsLaunchPath), changed);
    }

    /// <summary>浏览取消不能改写候选入口、选择状态或确认能力。</summary>
    [Fact]
    public async Task BrowseCancellationLeavesCandidateUntouched()
    {
        var candidate = CreateCandidate(
            ResidentCandidateConfidence.Low,
            ResidentCandidateKind.NewApplication,
            @"C:\Apps\Agent.exe");
        var vm = new ResidentCandidateViewModel(candidate, 20, new FakeExecutablePicker(), new FakeExecutableIconProvider());

        await vm.BrowsePathAsync();

        Assert.Equal(@"C:\Apps\Agent.exe", vm.FinalLaunchPath);
        Assert.False(vm.IsSelected);
        Assert.False(vm.CanConfirm);
    }

    [Theory]
    [InlineData(@"\\server\share\app.exe")]
    [InlineData(@"relative\app.exe")]
    [InlineData(@"C:\Windows\System32\app.exe")]
    public void RejectedCandidatePathNeverReachesIconProvider(string rejectedPath)
    {
        var icons = new RecordingIconProvider();

        _ = new ResidentCandidateViewModel(
            CreateCandidate(ResidentCandidateConfidence.Low, ResidentCandidateKind.NewApplication, rejectedPath),
            30,
            new FakeExecutablePicker(),
            icons,
            validateExecutable: _ => new(false, null, ResidentExecutableRejection.ProhibitedDirectory));

        Assert.Empty(icons.Paths);
    }

    [Fact]
    public void AcceptedCandidateIconReceivesOnlyNormalizedPath()
    {
        var icons = new RecordingIconProvider();

        _ = new ResidentCandidateViewModel(
            CreateCandidate(ResidentCandidateConfidence.Low, ResidentCandidateKind.NewApplication, @"C:\Apps\.\Agent.exe"),
            31,
            new FakeExecutablePicker(),
            icons,
            validateExecutable: _ => new(true, @"C:\Apps\Agent.exe", ResidentExecutableRejection.None));

        Assert.Equal([@"C:\Apps\Agent.exe"], icons.Paths);
    }

    /// <summary>条目状态仅投影路径验证结果；拒绝或无法访问时仍允许替换和删除。</summary>
    [Fact]
    public void RejectedApplicationCannotEnableButCanStillBeReplacedOrRemoved()
    {
        var app = new ResidentApplication(@"C:\Blocked\Agent.exe", new HashSet<string>(), "Agent", false, 0);
        var vm = new ResidentApplicationViewModel(
            app,
            new FakeExecutablePicker(),
            new FakeExecutableIconProvider(),
            _ => new ResidentExecutableValidation(false, null, ResidentExecutableRejection.ProhibitedDirectory),
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask);

        Assert.False(vm.CanEnable);
        Assert.Contains("拒绝", vm.PathStatusText, StringComparison.Ordinal);
        Assert.True(vm.ReplacePathCommand.CanExecute(null));
        Assert.True(vm.RemoveCommand.CanExecute(null));
        Assert.False(vm.EnableCommand.CanExecute(true));
    }

    /// <summary>已保存条目的 UNC、禁止、相对和无效路径被策略拒绝后，绝不能触发图标 I/O。</summary>
    [Theory]
    [InlineData(@"\\server\share\agent.exe", ResidentExecutableRejection.NetworkPath)]
    [InlineData(@"C:\Windows\System32\agent.exe", ResidentExecutableRejection.ProhibitedDirectory)]
    [InlineData(@"relative\agent.exe", ResidentExecutableRejection.NotAbsolutePath)]
    [InlineData("invalid\0agent.exe", ResidentExecutableRejection.InvalidPath)]
    public void RejectedSavedApplicationPathNeverReachesIconProvider(
        string rejectedPath,
        ResidentExecutableRejection rejection)
    {
        var icons = new RecordingIconProvider();

        _ = new ResidentApplicationViewModel(
            new ResidentApplication(rejectedPath, new HashSet<string>(), "Agent", false, 0),
            new FakeExecutablePicker(),
            icons,
            _ => new ResidentExecutableValidation(false, null, rejection),
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask);

        Assert.Empty(icons.Paths);
    }

    /// <summary>已保存条目的允许路径只提取一次图标，且副作用边界只接收策略正规化路径。</summary>
    [Fact]
    public void AcceptedSavedApplicationIconReceivesNormalizedPathExactlyOnce()
    {
        var icons = new RecordingIconProvider();

        _ = new ResidentApplicationViewModel(
            new ResidentApplication(@"C:\Apps\.\Agent.exe", new HashSet<string>(), "Agent", true, 0),
            new FakeExecutablePicker(),
            icons,
            _ => new ResidentExecutableValidation(
                true,
                @"C:\Apps\Agent.exe",
                ResidentExecutableRejection.None),
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask);

        Assert.Equal([@"C:\Apps\Agent.exe"], icons.Paths);
    }

    /// <summary>WPF XAML 字符串命令参数必须被解析为布尔值，否则“启用”按钮无法执行。</summary>
    [Fact]
    public void ApplicationEnableCommandAcceptsWpfBooleanCommandParameter()
    {
        var app = new ResidentApplication(@"C:\Apps\Agent.exe", new HashSet<string>(), "Agent", false, 0);
        var vm = new ResidentApplicationViewModel(
            app,
            new FakeExecutablePicker(),
            new FakeExecutableIconProvider(),
            path => new ResidentExecutableValidation(true, path, ResidentExecutableRejection.None),
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask);

        Assert.True(vm.EnableCommand.CanExecute("True"));
    }

    private static ResidentAppCandidate CreateCandidate(
        ResidentCandidateConfidence confidence,
        ResidentCandidateKind kind,
        string? launchPath) =>
        new(
            "candidate-1",
            "Agent",
            launchPath,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            confidence,
            kind,
            kind == ResidentCandidateKind.PathReplacement ? @"C:\Old\Agent.exe" : null);

    private sealed class FakeExecutablePicker(string? selectedPath = null) : IExecutablePicker
    {
        public Task<string?> PickAsync(CancellationToken cancellationToken) => Task.FromResult(selectedPath);
    }

    private sealed class FakeExecutableIconProvider : IExecutableIconProvider
    {
        public ImageSource? GetIcon(string? executablePath) => null;
    }

    private sealed class RecordingIconProvider : IExecutableIconProvider
    {
        internal List<string?> Paths { get; } = [];

        public ImageSource? GetIcon(string? executablePath)
        {
            Paths.Add(executablePath);
            return null;
        }
    }
}
