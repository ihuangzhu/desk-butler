using System.Collections.Immutable;
using DeskButler.Core.ResidentApps;
using DeskButler.Infrastructure.Windows.ResidentApps;

namespace DeskButler.Infrastructure.Windows.Tests.ResidentApps;

public sealed class WindowsResidentAppDiscoveryTests
{
    /// <summary>验证发现层防御性排除自身、不可启动路径、普通窗口、已有常驻项与重复实例。</summary>
    [Fact]
    public async Task DiscoverAsync仅让允许的第三方后台观察进入分组()
    {
        var discovery = CreateDiscovery(
            observations:
            [
                Observation(1, @"C:\DeskButler\DeskButler.exe", "DeskButler", "DeskButler", hidden: true),
                Observation(2, @"C:\Windows\System32\service.exe", "System", "Microsoft", hidden: true),
                Observation(3, @"C:\Users\test\AppData\Local\Temp\temp.exe", "Temp", "Vendor", hidden: true),
                Observation(4, @"C:\Apps\Editor\Editor.exe", "Editor", "Vendor", hidden: true),
                Observation(5, @"C:\Apps\Existing\Existing.exe", "Existing", "Vendor", hidden: true),
                Observation(6, @"C:\Apps\Chat\Chat.exe", "Chat", "Vendor", hidden: true),
                Observation(7, @"C:\Apps\Chat\Chat.exe", "Chat", "Vendor", hidden: true),
                Observation(8, @"C:\Apps\Broken\Broken.exe", "Broken", "Vendor", hidden: true)
            ],
            catalog:
            [
                Catalog("Editor", "Vendor", @"C:\Apps\Editor", @"C:\Apps\Editor\Editor.exe"),
                Catalog("Existing", "Vendor", @"C:\Apps\Existing", @"C:\Apps\Existing\Existing.exe"),
                Catalog("Chat", "Vendor", @"C:\Apps\Chat", @"C:\Apps\Chat\Chat.exe")
            ],
            rejectedPaths:
            [@"C:\DeskButler\DeskButler.exe", @"C:\Windows\System32\service.exe", @"C:\Users\test\AppData\Local\Temp\temp.exe", @"C:\Apps\Broken\Broken.exe"]);
        ResidentApplication[] existing =
        {
            new ResidentApplication(@"C:\Apps\Existing\Existing.exe", Paths(@"C:\Apps\Existing\Existing.exe"), "Existing", true, 0)
        };

        var result = await discovery.DiscoverAsync(
            Paths(@"C:\Apps\Editor\Editor.exe"),
            existing,
            CancellationToken.None);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Chat", candidate.DisplayName);
        Assert.Equal(@"C:\Apps\Chat\Chat.exe", candidate.LaunchPath);
        Assert.Equal(Paths(@"C:\Apps\Chat\Chat.exe"), candidate.KnownProcessPaths);
        Assert.Empty(result.Diagnostics);
    }

    /// <summary>验证同一稳定产品只产生一个候选，主进程优先且通用辅助程序不会被登记为运行路径。</summary>
    [Fact]
    public async Task DiscoverAsync按完整稳定产品键分组并排除通用辅助程序()
    {
        var discovery = CreateDiscovery(
            observations:
            [
                Observation(1, @"C:\Apps\Tencent\QQ\QQ.exe", "QQ", "Tencent", hidden: true),
                Observation(2, @"C:\Apps\Tencent\QQ\renderer.exe", "QQ", "Tencent", hidden: true),
                Observation(3, @"C:\Apps\Tencent\QQ\updater.exe", "QQ", "Tencent", hidden: true),
                Observation(4, @"C:\Apps\Tencent\QQ\crash-reporter.exe", "QQ", "Tencent", hidden: true)
            ],
            catalog: [Catalog("QQ", "Tencent", @"C:\Apps\Tencent\QQ", @"C:\Apps\Tencent\QQ\QQ.exe")]);

        var candidate = Assert.Single((await discovery.DiscoverAsync(Paths(), [], CancellationToken.None)).Candidates);

        Assert.Equal("QQ", candidate.DisplayName);
        Assert.Equal(@"C:\Apps\Tencent\QQ\QQ.exe", candidate.LaunchPath);
        Assert.Equal(Paths(@"C:\Apps\Tencent\QQ\QQ.exe", @"C:\Apps\Tencent\QQ\renderer.exe"), candidate.KnownProcessPaths);
        Assert.Equal(ResidentCandidateConfidence.High, candidate.Confidence);
    }

    /// <summary>验证不完整产品元数据退回 exe 路径，不会按宽泛厂商目录错误合并不同产品。</summary>
    [Fact]
    public async Task DiscoverAsync信息不足时退回路径分组()
    {
        var discovery = CreateDiscovery(
            observations:
            [
                Observation(1, @"C:\Apps\Vendor\One\same.exe", null, "Vendor", hidden: true),
                Observation(2, @"C:\Apps\Vendor\Two\same.exe", null, "Vendor", hidden: true)
            ],
            catalog: Array.Empty<InstalledApplicationEntry>());

        var candidates = (await discovery.DiscoverAsync(Paths(), [], CancellationToken.None)).Candidates;

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate => Assert.Equal(ResidentCandidateConfidence.Low, candidate.Confidence));
        Assert.NotEqual(candidates[0].CandidateId, candidates[1].CandidateId);
    }

    /// <summary>验证并列入口和不足门槛时绝不猜测启动路径，保留低可信候选供用户确认。</summary>
    [Fact]
    public async Task DiscoverAsync入口不唯一或无可靠评分时不提供启动路径()
    {
        var discovery = CreateDiscovery(
            observations:
            [
                Observation(1, @"C:\Apps\Tie\one.exe", "Tie", "Vendor", hidden: true),
                Observation(2, @"C:\Apps\Tie\two.exe", "Tie", "Vendor", hidden: true),
                Observation(3, @"C:\Loose\unknown.exe", null, null, hidden: false)
            ],
            catalog: [Catalog("Tie", "Vendor", @"C:\Apps\Tie", null)]);

        var candidates = (await discovery.DiscoverAsync(Paths(), [], CancellationToken.None)).Candidates;

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate => Assert.Null(candidate.LaunchPath));
        Assert.All(candidates, candidate => Assert.Equal(ResidentCandidateConfidence.Low, candidate.Confidence));
    }

    /// <summary>验证虚拟机辅助程序即使具备隐藏窗口和安装信息也永远只能低可信。</summary>
    [Fact]
    public async Task DiscoverAsync虚拟机辅助工具永远低可信()
    {
        var discovery = CreateDiscovery(
            observations: [Observation(1, @"C:\Apps\VMware\vmtoolsd.exe", "VMware Tools", "VMware", hidden: true)],
            catalog: [Catalog("VMware Tools", "VMware", @"C:\Apps\VMware", @"C:\Apps\VMware\vmtoolsd.exe")]);

        var candidate = Assert.Single((await discovery.DiscoverAsync(Paths(), [], CancellationToken.None)).Candidates);

        Assert.Equal(@"C:\Apps\VMware\vmtoolsd.exe", candidate.LaunchPath);
        Assert.Equal(ResidentCandidateConfidence.Low, candidate.Confidence);
    }

    /// <summary>验证仅在显示名一致且同一目录根内的缺失旧路径产生低可信替换建议。</summary>
    [Fact]
    public async Task DiscoverAsync只为同目录同显示名的缺失路径生成替换候选()
    {
        var discovery = CreateDiscovery(
            observations: [Observation(1, @"C:\Apps\Chat\Chat2.exe", "Chat", "Vendor", hidden: true)],
            catalog: [Catalog("Chat", "Vendor", @"C:\Apps\Chat", @"C:\Apps\Chat\Chat2.exe")],
            missingPaths: [@"C:\Apps\Chat\Chat.exe", @"C:\Other\Chat.exe", @"C:\Apps\Other\Other.exe"]);
        ResidentApplication[] existing =
        [
            new ResidentApplication(@"C:\Apps\Chat\Chat.exe", Paths(@"C:\Apps\Chat\Chat.exe"), "Chat", true, 0),
            new ResidentApplication(@"C:\Other\Chat.exe", Paths(@"C:\Other\Chat.exe"), "Chat", true, 1),
            new ResidentApplication(@"C:\Apps\Other\Other.exe", Paths(@"C:\Apps\Other\Other.exe"), "Other", true, 2)
        ];

        var candidate = Assert.Single((await discovery.DiscoverAsync(Paths(), existing, CancellationToken.None)).Candidates);

        Assert.Equal(ResidentCandidateKind.PathReplacement, candidate.Kind);
        Assert.Equal(@"C:\Apps\Chat\Chat.exe", candidate.ReplacesLaunchPath);
        Assert.Equal(@"C:\Apps\Chat\Chat2.exe", candidate.LaunchPath);
        Assert.Equal(ResidentCandidateConfidence.Low, candidate.Confidence);
    }

    /// <summary>验证候选身份不含 PID、按种类隔离，且替换路径会稳定参与散列。</summary>
    [Fact]
    public async Task DiscoverAsync候选身份稳定区分新增与替换()
    {
        var discovery = CreateDiscovery(
            observations: [Observation(99, @"C:\Apps\Chat\Chat.exe", "Chat", "Vendor", hidden: true)],
            catalog: [Catalog("Chat", "Vendor", @"C:\Apps\Chat", @"C:\Apps\Chat\Chat.exe")],
            missingPaths: [@"C:\Apps\Chat\Old.exe"]);
        var replacementExisting = new[]
        {
            new ResidentApplication(@"C:\Apps\Chat\Old.exe", Paths(@"C:\Apps\Chat\Old.exe"), "Chat", true, 0)
        };

        var newCandidate = Assert.Single((await discovery.DiscoverAsync(Paths(), [], CancellationToken.None)).Candidates);
        var replacement = Assert.Single((await discovery.DiscoverAsync(Paths(), replacementExisting, CancellationToken.None)).Candidates);

        var differentPid = CreateDiscovery(
            observations: [Observation(100, @"c:\apps\chat\chat.EXE", "chat", "vendor", hidden: true)],
            catalog: [Catalog("Chat", "Vendor", @"C:\Apps\Chat", @"C:\Apps\Chat\Chat.exe")]);
        var sameProductDifferentPid = Assert.Single(
            (await differentPid.DiscoverAsync(Paths(), [], CancellationToken.None)).Candidates);
        var otherReplacement = new[]
        {
            new ResidentApplication(@"C:\Apps\Chat\OtherOld.exe", Paths(@"C:\Apps\Chat\OtherOld.exe"), "Chat", true, 0)
        };
        var replacementOtherPath = Assert.Single(
            (await discovery.DiscoverAsync(Paths(), otherReplacement, CancellationToken.None)).Candidates);

        Assert.Equal(newCandidate.CandidateId, sameProductDifferentPid.CandidateId);
        Assert.NotEqual(newCandidate.CandidateId, replacement.CandidateId);
        Assert.NotEqual(replacement.CandidateId, replacementOtherPath.CandidateId);
    }

    [Fact]
    public async Task DiscoverAsync普通可见成员抑制整个产品组而隐藏工具窗口仍可候选()
    {
        var ordinaryTraits = new ResidentWindowTraits(true, false, false, false, false)
        {
            HasOrdinaryVisibleTopLevelWindow = true
        };
        var discovery = CreateDiscovery(
            observations:
            [
                new ResidentProcessObservation(1, @"C:\Apps\Chat\Chat.exe", "Chat", "Vendor", null, ordinaryTraits),
                Observation(2, @"C:\Apps\Chat\helper.exe", "Chat", "Vendor", hidden: true),
                Observation(3, @"C:\Apps\Tray\Tray.exe", "Tray", "Vendor", hidden: true)
            ],
            catalog:
            [
                Catalog("Chat", "Vendor", @"C:\Apps\Chat", @"C:\Apps\Chat\Chat.exe"),
                Catalog("Tray", "Vendor", @"C:\Apps\Tray", @"C:\Apps\Tray\Tray.exe")
            ]);

        var candidates = (await discovery.DiscoverAsync(Paths(), [], CancellationToken.None)).Candidates;

        var candidate = Assert.Single(candidates);
        Assert.Equal("Tray", candidate.DisplayName);
    }

    [Fact]
    public async Task DiscoverAsync产品键忽略大小写且分隔符文本不碰撞()
    {
        var discovery = CreateDiscovery(
            observations:
            [
                Observation(1, @"C:\Apps\One\a.exe", "A|B", "Vendor", hidden: true),
                Observation(2, @"c:\apps\one\b.exe", "a|b", "vendor", hidden: true),
                Observation(3, @"C:\Apps\Two\c.exe", "A", "B|Vendor", hidden: true)
            ],
            catalog:
            [
                Catalog("A|B", "Vendor", @"C:\Apps\One", @"C:\Apps\One\a.exe"),
                Catalog("A", "B|Vendor", @"C:\Apps\Two", @"C:\Apps\Two\c.exe")
            ]);

        var candidates = (await discovery.DiscoverAsync(Paths(), [], CancellationToken.None)).Candidates;

        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, candidate => candidate.KnownProcessPaths.Count == 2);
    }

    [Fact]
    public async Task DiscoverAsync无法访问旧路径不建议替换()
    {
        var observations = new[] { Observation(1, @"C:\Apps\Chat\Chat2.exe", "Chat", "Vendor", hidden: true) };
        var discovery = new WindowsResidentAppDiscovery(
            new FakeSnapshotSource(observations),
            new FakeCatalog([Catalog("Chat", "Vendor", @"C:\Apps\Chat", @"C:\Apps\Chat\Chat2.exe")]),
            new FakePolicy(Paths(), null),
            _ => WindowsResidentAppDiscovery.PathAvailability.Inaccessible,
            @"C:\DeskButler\DeskButler.exe");
        var existing = new[]
        {
            new ResidentApplication(@"C:\Apps\Chat\Chat.exe", Paths(@"C:\Apps\Chat\Chat.exe"), "Chat", true, 0)
        };

        var candidate = Assert.Single((await discovery.DiscoverAsync(Paths(), existing, CancellationToken.None)).Candidates);

        Assert.Equal(ResidentCandidateKind.NewApplication, candidate.Kind);
    }

    /// <summary>验证单一路径的策略失败只追加无敏感分类诊断，不能丢弃其它可安全发现的候选。</summary>
    [Fact]
    public async Task DiscoverAsync策略拒绝以外的单项异常被隔离为SourceFailure()
    {
        var discovery = CreateDiscovery(
            observations:
            [
                Observation(1, @"C:\Apps\Broken\Broken.exe", "Broken", "Vendor", hidden: true),
                Observation(2, @"C:\Apps\Chat\Chat.exe", "Chat", "Vendor", hidden: true)
            ],
            catalog: [Catalog("Chat", "Vendor", @"C:\Apps\Chat", @"C:\Apps\Chat\Chat.exe")],
            policyValidation: path => path.Equals(@"C:\Apps\Broken\Broken.exe", StringComparison.OrdinalIgnoreCase)
                ? throw new InvalidOperationException("测试策略失败")
                : new ResidentExecutableValidation(true, Path.GetFullPath(path), ResidentExecutableRejection.None));

        var result = await discovery.DiscoverAsync(Paths(), [], CancellationToken.None);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(@"C:\Apps\Chat\Chat.exe", candidate.LaunchPath);
        Assert.Equal([ResidentDiscoveryIssue.SourceFailure], result.Diagnostics.Select(item => item.Kind));
    }

    /// <summary>验证策略边界报告调用方取消时必须传播原令牌，不能被单项故障隔离逻辑吞掉。</summary>
    [Fact]
    public async Task DiscoverAsync策略取消必须传播原取消令牌()
    {
        using var cancellation = new CancellationTokenSource();
        var discovery = CreateDiscovery(
            observations: [Observation(1, @"C:\Apps\Chat\Chat.exe", "Chat", "Vendor", hidden: true)],
            catalog: [Catalog("Chat", "Vendor", @"C:\Apps\Chat", @"C:\Apps\Chat\Chat.exe")],
            policyValidation: _ => throw new OperationCanceledException(cancellation.Token));

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => discovery.DiscoverAsync(Paths(), [], cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    /// <summary>构造只使用 fake snapshot、目录、策略和文件状态的发现器，不访问真实系统资源。</summary>
    private static WindowsResidentAppDiscovery CreateDiscovery(
        IReadOnlyList<ResidentProcessObservation> observations,
        IReadOnlyList<InstalledApplicationEntry> catalog,
        HashSet<string>? rejectedPaths = null,
        HashSet<string>? missingPaths = null,
        Func<string, ResidentExecutableValidation>? policyValidation = null) =>
        new(
            new FakeSnapshotSource(observations),
            new FakeCatalog(catalog),
            new FakePolicy(rejectedPaths ?? Paths(), policyValidation),
            path => !(missingPaths ?? Paths()).Contains(path),
            @"C:\DeskButler\DeskButler.exe");

    /// <summary>创建忽略大小写的路径集合，以模拟 Windows 路径比较语义。</summary>
    private static HashSet<string> Paths(params string[] paths) => new(paths, StringComparer.OrdinalIgnoreCase);

    /// <summary>构造具有最小窗口分类的受控进程观察。</summary>
    private static ResidentProcessObservation Observation(
        int processId,
        string path,
        string? product,
        string? company,
        bool hidden) =>
        new(
            processId,
            path,
            product,
            company,
            null,
            new ResidentWindowTraits(false, hidden, false, false, false));

    /// <summary>构造受控已安装产品目录项。</summary>
    private static InstalledApplicationEntry Catalog(string name, string? publisher, string? root, string? icon) =>
        new(name, publisher, root, icon);

    private sealed class FakeSnapshotSource(IReadOnlyList<ResidentProcessObservation> observations) : IResidentProcessSnapshotSource
    {
        /// <summary>返回测试指定的不可变观察快照。</summary>
        public Task<ResidentProcessSnapshot> CaptureAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ResidentProcessSnapshot(observations.ToImmutableArray(), []));
        }
    }

    private sealed class FakeCatalog(IReadOnlyList<InstalledApplicationEntry> entries) : IInstalledApplicationCatalog
    {
        /// <summary>返回测试指定的不可变产品目录快照。</summary>
        public Task<InstalledApplicationSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new InstalledApplicationSnapshot(entries.ToImmutableArray(), []));
        }
    }

    private sealed class FakePolicy(
        IReadOnlySet<string> rejectedPaths,
        Func<string, ResidentExecutableValidation>? policyValidation) : IResidentExecutablePolicy
    {
        /// <summary>只按测试提供的拒绝路径决定候选是否可启动。</summary>
        public ResidentExecutableValidation Validate(string path) => policyValidation?.Invoke(path) ??
            (rejectedPaths.Contains(path)
                ? new ResidentExecutableValidation(false, null, ResidentExecutableRejection.ProhibitedDirectory)
                : new ResidentExecutableValidation(true, Path.GetFullPath(path), ResidentExecutableRejection.None));
    }
}
