using DeskButler.Application.Commands;
using DeskButler.Core.ResidentApps;
using DeskButler.Core.Settings;
using DeskButler.Desktop.Hosting;
using DeskButler.Desktop.Tests.ViewModels;
using DeskButler.Infrastructure.Windows.Startup;

namespace DeskButler.Desktop.Tests.Hosting;

public sealed class ResidentAppCommandTests
{
    /// <summary>总开关不能重写列表中的用户选择。</summary>
    [Fact]
    public async Task TotalSwitchChangesOnlyItsOwnField()
    {
        var app = App("Chat", @"C:\Apps\chat.exe", true, 0);
        var store = new InMemorySettingsStore(ButlerSettings.Default with
        {
            CaptureEnabled = false,
            ResidentApplications = [app]
        });
        using var settings = new SettingsCoordinator(store);
        var handler = new SetResidentApplicationsEnabledCommandHandler(settings);

        var result = await handler.HandleAsync(
            new SetResidentApplicationsEnabledCommand(false), CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(ResidentSettingsError.None, result.Error);
        Assert.False(result.ResidentApplicationsEnabled);
        Assert.False(store.Current.ResidentApplicationsEnabled);
        Assert.False(store.Current.CaptureEnabled);
        Assert.Equal(app, Assert.Single(result.Applications));
    }

    /// <summary>总开关已是目标状态时保持幂等，并返回当前设置快照。</summary>
    [Fact]
    public async Task TotalSwitchSameValueIsNoOp()
    {
        var store = new InMemorySettingsStore(ButlerSettings.Default);
        using var settings = new SettingsCoordinator(store);
        var handler = new SetResidentApplicationsEnabledCommandHandler(settings);

        var result = await handler.HandleAsync(
            new SetResidentApplicationsEnabledCommand(true), CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Equal(ResidentSettingsError.None, result.Error);
        Assert.True(result.ResidentApplicationsEnabled);
    }

    /// <summary>移动只接受相邻偏移并把实际持久化顺序连续编号。</summary>
    [Fact]
    public async Task MoveAcceptsOnlyAdjacentOffsetAndRenumbersStableOrder()
    {
        var store = new InMemorySettingsStore(ButlerSettings.Default with
        {
            ResidentApplications =
            [
                App("First", @"C:\Apps\first.exe", true, 0),
                App("Second", @"C:\Apps\second.exe", true, 1),
                App("Third", @"C:\Apps\third.exe", true, 2)
            ]
        });
        using var settings = new SettingsCoordinator(store);
        var handler = new MoveResidentApplicationCommandHandler(settings);

        var invalid = await handler.HandleAsync(
            new MoveResidentApplicationCommand(@"C:\Apps\first.exe", 2), CancellationToken.None);
        var moved = await handler.HandleAsync(
            new MoveResidentApplicationCommand(@"C:\Apps\first.exe", 1), CancellationToken.None);

        Assert.False(invalid.Changed);
        Assert.Equal(ResidentSettingsError.InvalidMoveOffset, invalid.Error);
        Assert.Equal(["Second", "First", "Third"], moved.Applications.Select(app => app.DisplayName));
        Assert.Equal([0, 1, 2], moved.Applications.Select(app => app.LaunchOrder));
    }

    /// <summary>合法相邻偏移在首尾边界没有目标位置时是幂等 no-op。</summary>
    [Fact]
    public async Task MoveAtFirstAndLastBoundaryIsNoOp()
    {
        var store = new InMemorySettingsStore(ButlerSettings.Default with
        {
            ResidentApplications =
            [
                App("First", @"C:\Apps\first.exe", true, 0),
                App("Last", @"C:\Apps\last.exe", true, 1)
            ]
        });
        using var settings = new SettingsCoordinator(store);
        var handler = new MoveResidentApplicationCommandHandler(settings);

        var first = await handler.HandleAsync(
            new MoveResidentApplicationCommand(@"C:\Apps\first.exe", -1), CancellationToken.None);
        var last = await handler.HandleAsync(
            new MoveResidentApplicationCommand(@"C:\Apps\last.exe", 1), CancellationToken.None);

        Assert.False(first.Changed);
        Assert.Equal(ResidentSettingsError.None, first.Error);
        Assert.False(last.Changed);
        Assert.Equal(ResidentSettingsError.None, last.Error);
        Assert.Equal(["First", "Last"], store.Current.ResidentApplications.Select(app => app.DisplayName));
    }

    /// <summary>重复的删除请求是无副作用且不把幂等结果当作错误。</summary>
    [Fact]
    public async Task RemoveMissingEntryIsIdempotentNoOp()
    {
        var store = new InMemorySettingsStore(ButlerSettings.Default with
        {
            ResidentApplications = [App("Chat", @"C:\Apps\chat.exe", true, 0)]
        });
        using var settings = new SettingsCoordinator(store);
        var handler = new RemoveResidentApplicationCommandHandler(settings);

        var result = await handler.HandleAsync(
            new RemoveResidentApplicationCommand(@"C:\Apps\missing.exe"), CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Equal(ResidentSettingsError.None, result.Error);
        Assert.Equal("Chat", Assert.Single(result.Applications).DisplayName);
    }

    /// <summary>启用会重新执行入口验证，并拒绝与已启用条目共享识别路径。</summary>
    [Fact]
    public async Task EnableRevalidatesEntryAndRejectsEnabledKnownPathConflict()
    {
        const string shared = @"C:\Apps\shared.exe";
        var store = new InMemorySettingsStore(ButlerSettings.Default with
        {
            ResidentApplications =
            [
                App("Running", @"C:\Apps\running.exe", true, 0, shared),
                App("Candidate", @"C:\Apps\candidate.exe", false, 1, shared)
            ]
        });
        using var settings = new SettingsCoordinator(store);
        var policy = new AllowingPolicy(new HashSet<string>([@"C:\Apps\blocked.exe"], StringComparer.OrdinalIgnoreCase));
        var handler = new SetResidentApplicationEnabledCommandHandler(settings, policy);

        var conflict = await handler.HandleAsync(
            new SetResidentApplicationEnabledCommand(@"C:\Apps\candidate.exe", true), CancellationToken.None);
        var rejected = await handler.HandleAsync(
            new SetResidentApplicationEnabledCommand(@"C:\Apps\blocked.exe", true), CancellationToken.None);

        Assert.Equal(ResidentSettingsError.KnownProcessPathConflict, conflict.Error);
        Assert.Equal(ResidentSettingsError.ExecutablePathRejected, rejected.Error);
        Assert.False(store.Current.ResidentApplications.Single(app => app.DisplayName == "Candidate").Enabled);
    }

    /// <summary>已启用不等于绕过安全检查；维持可启动状态也必须重新验证当前入口。</summary>
    [Fact]
    public async Task AlreadyEnabledEntryStillRevalidatesExecutablePolicy()
    {
        const string guardedPath = @"C:\Apps\guarded.exe";
        var store = new InMemorySettingsStore(ButlerSettings.Default with
        {
            ResidentApplications = [App("Guarded", guardedPath, true, 0)]
        });
        using var settings = new SettingsCoordinator(store);
        var handler = new SetResidentApplicationEnabledCommandHandler(
            settings,
            new AllowingPolicy(new HashSet<string>([guardedPath], StringComparer.OrdinalIgnoreCase)));

        var result = await handler.HandleAsync(
            new SetResidentApplicationEnabledCommand(guardedPath, true), CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Equal(ResidentSettingsError.ExecutablePathRejected, result.Error);
        Assert.True(Assert.Single(store.Current.ResidentApplications).Enabled);
    }

    /// <summary>已启用条目重新确认时也不能保留与另一已启用条目冲突的识别路径。</summary>
    [Fact]
    public async Task AlreadyEnabledEntryStillRejectsKnownPathConflict()
    {
        const string shared = @"C:\Apps\shared.exe";
        var store = new InMemorySettingsStore(ButlerSettings.Default with
        {
            ResidentApplications =
            [
                App("First", @"C:\Apps\first.exe", true, 0, shared),
                App("Second", @"C:\Apps\second.exe", true, 1, shared)
            ]
        });
        using var settings = new SettingsCoordinator(store);
        var handler = new SetResidentApplicationEnabledCommandHandler(settings, new AllowingPolicy());

        var result = await handler.HandleAsync(
            new SetResidentApplicationEnabledCommand(@"C:\Apps\second.exe", true), CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Equal(ResidentSettingsError.KnownProcessPathConflict, result.Error);
    }

    /// <summary>已启用的同值请求仍重新调用 policy；安全通过后才是 no-op。</summary>
    [Fact]
    public async Task AlreadyEnabledSameValueRevalidatesPolicyBeforeNoOp()
    {
        const string path = @"C:\Apps\chat.exe";
        var store = new InMemorySettingsStore(ButlerSettings.Default with
        {
            ResidentApplications = [App("Chat", path, true, 0)]
        });
        using var settings = new SettingsCoordinator(store);
        var policy = new TrackingPolicy();
        var handler = new SetResidentApplicationEnabledCommandHandler(settings, policy);

        var result = await handler.HandleAsync(
            new SetResidentApplicationEnabledCommand(path, true), CancellationToken.None);

        Assert.Equal(1, policy.ValidationCount);
        Assert.False(result.Changed);
        Assert.Equal(ResidentSettingsError.None, result.Error);
    }

    /// <summary>已停用的同值请求可短路，不需要重新检查不会启动的入口。</summary>
    [Fact]
    public async Task AlreadyDisabledSameValueSkipsExecutablePolicy()
    {
        const string path = @"C:\Apps\chat.exe";
        var store = new InMemorySettingsStore(ButlerSettings.Default with
        {
            ResidentApplications = [App("Chat", path, false, 0)]
        });
        using var settings = new SettingsCoordinator(store);
        var policy = new TrackingPolicy(new(false, null, ResidentExecutableRejection.ProhibitedDirectory));
        var handler = new SetResidentApplicationEnabledCommandHandler(settings, policy);

        var result = await handler.HandleAsync(
            new SetResidentApplicationEnabledCommand(path, false), CancellationToken.None);

        Assert.Equal(0, policy.ValidationCount);
        Assert.False(result.Changed);
        Assert.Equal(ResidentSettingsError.None, result.Error);
    }

    /// <summary>遗留的停用条目路径冲突不应阻止目标启用或之后再次停用。</summary>
    [Fact]
    public async Task DisabledPeerKnownPathConflictDoesNotBlockEnableOrDisable()
    {
        const string shared = @"C:\Apps\shared.exe";
        var store = new InMemorySettingsStore(ButlerSettings.Default with
        {
            ResidentApplications =
            [
                App("Target", @"C:\Apps\target.exe", false, 0, shared),
                App("Disabled peer", @"C:\Apps\peer.exe", false, 1, shared)
            ]
        });
        using var settings = new SettingsCoordinator(store);
        var handler = new SetResidentApplicationEnabledCommandHandler(settings, new TrackingPolicy());

        var enabled = await handler.HandleAsync(
            new SetResidentApplicationEnabledCommand(@"C:\Apps\target.exe", true), CancellationToken.None);
        var disabled = await handler.HandleAsync(
            new SetResidentApplicationEnabledCommand(@"C:\Apps\target.exe", false), CancellationToken.None);

        Assert.True(enabled.Changed);
        Assert.Equal(ResidentSettingsError.None, enabled.Error);
        Assert.True(disabled.Changed);
        Assert.Equal(ResidentSettingsError.None, disabled.Error);
        Assert.False(store.Current.ResidentApplications.Single(app => app.DisplayName == "Target").Enabled);
    }

    /// <summary>添加不能绕过统一的可执行入口安全策略。</summary>
    [Fact]
    public async Task AddRejectsPathDeniedByExecutablePolicy()
    {
        var store = new InMemorySettingsStore(ButlerSettings.Default);
        using var settings = new SettingsCoordinator(store);
        var handler = new AddResidentApplicationCommandHandler(
            settings, new AllowingPolicy(new HashSet<string>([@"C:\Apps\blocked.exe"], StringComparer.OrdinalIgnoreCase)));

        var result = await handler.HandleAsync(
            new AddResidentApplicationCommand(@"C:\Apps\blocked.exe", "Blocked"), CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Equal(ResidentSettingsError.ExecutablePathRejected, result.Error);
        Assert.Empty(result.Applications);
    }

    /// <summary>添加已有启动入口必须返回重复条目的类型化错误。</summary>
    [Fact]
    public async Task AddDuplicateLaunchPathReturnsTypedError()
    {
        const string path = @"C:\Apps\chat.exe";
        var store = new InMemorySettingsStore(ButlerSettings.Default with
        {
            ResidentApplications = [App("Chat", path, true, 0)]
        });
        using var settings = new SettingsCoordinator(store);
        var handler = new AddResidentApplicationCommandHandler(settings, new TrackingPolicy());

        var result = await handler.HandleAsync(new AddResidentApplicationCommand(path, "Again"), CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Equal(ResidentSettingsError.DuplicateLaunchPath, result.Error);
        Assert.Equal("Chat", Assert.Single(result.Applications).DisplayName);
    }

    /// <summary>替换入口保留用户管理字段，但必须清除旧发现得出的识别路径。</summary>
    [Fact]
    public async Task ReplacePreservesUserFieldsAndResetsKnownPathsToValidatedEntry()
    {
        var store = new InMemorySettingsStore(ButlerSettings.Default with
        {
            ResidentApplications =
            [
                App("Chat", @"C:\Apps\old.exe", false, 0, @"C:\Apps\helper.exe"),
                App("Other", @"C:\Apps\other.exe", true, 1)
            ]
        });
        using var settings = new SettingsCoordinator(store);
        var handler = new ReplaceResidentApplicationPathCommandHandler(settings, new AllowingPolicy());

        var result = await handler.HandleAsync(
            new ReplaceResidentApplicationPathCommand(@"C:\Apps\old.exe", @"C:\Apps\new.exe"),
            CancellationToken.None);

        var replaced = result.Applications.Single(app => app.DisplayName == "Chat");
        Assert.True(result.Changed);
        Assert.Equal(@"C:\Apps\new.exe", replaced.LaunchPath);
        Assert.False(replaced.Enabled);
        Assert.Equal(0, replaced.LaunchOrder);
        Assert.Equal([@"C:\Apps\new.exe"], replaced.KnownProcessPaths);
    }

    /// <summary>替换为同一正规化身份时仍先验证策略，成功后不重写用户条目。</summary>
    [Fact]
    public async Task ReplaceSameNormalizedIdentityRevalidatesPolicyThenNoOps()
    {
        const string path = @"C:\Apps\chat.exe";
        var store = new InMemorySettingsStore(ButlerSettings.Default with
        {
            ResidentApplications = [App("Chat", path, true, 0, @"C:\Apps\helper.exe")]
        });
        using var settings = new SettingsCoordinator(store);
        var policy = new TrackingPolicy();
        var handler = new ReplaceResidentApplicationPathCommandHandler(settings, policy);

        var result = await handler.HandleAsync(
            new ReplaceResidentApplicationPathCommand(path, @"C:\Apps\.\chat.exe"), CancellationToken.None);

        Assert.Equal(1, policy.ValidationCount);
        Assert.False(result.Changed);
        Assert.Equal(ResidentSettingsError.None, result.Error);
        Assert.Contains(@"C:\Apps\helper.exe", Assert.Single(result.Applications).KnownProcessPaths);
    }

    /// <summary>同一正规化身份的替换也不能绕过策略拒绝。</summary>
    [Fact]
    public async Task ReplaceSameNormalizedIdentityReturnsPolicyErrorWhenRejected()
    {
        const string path = @"C:\Apps\chat.exe";
        var store = new InMemorySettingsStore(ButlerSettings.Default with
        {
            ResidentApplications = [App("Chat", path, true, 0)]
        });
        using var settings = new SettingsCoordinator(store);
        var policy = new TrackingPolicy(new(false, null, ResidentExecutableRejection.ProhibitedDirectory));
        var handler = new ReplaceResidentApplicationPathCommandHandler(settings, policy);

        var result = await handler.HandleAsync(
            new ReplaceResidentApplicationPathCommand(path, @"C:\Apps\.\chat.exe"), CancellationToken.None);

        Assert.Equal(1, policy.ValidationCount);
        Assert.False(result.Changed);
        Assert.Equal(ResidentSettingsError.ExecutablePathRejected, result.Error);
    }

    /// <summary>共享设置门必须使四种无关字段的并发修改全部保留。</summary>
    [Fact]
    public async Task ConcurrentSettingsFieldsAndResidentMutationPreserveEveryChange()
    {
        var store = new FirstLoadBarrierSettingsStore(ButlerSettings.Default with { StartupEnabled = false });
        using var settings = new SettingsCoordinator(store);
        var capture = new SetCaptureEnabledCommandHandler(settings);
        var startup = new SetStartupEnabledCommandHandler(settings, new RecordingStartupRegistration(false));
        var exclusion = new PersistExclusionCommandHandler(settings);
        var add = new AddResidentApplicationCommandHandler(settings, new AllowingPolicy());

        var captureTask = capture.HandleAsync(new SetCaptureEnabledCommand(false), CancellationToken.None);
        await store.FirstLoadStarted.Task;
        var startupTask = startup.HandleAsync(new SetStartupEnabledCommand(true), CancellationToken.None);
        var exclusionTask = exclusion.HandleAsync(new PersistExclusionCommand(@"C:\Apps\excluded.exe"), CancellationToken.None);
        var addTask = add.HandleAsync(new AddResidentApplicationCommand(@"C:\Apps\resident.exe", "Resident"), CancellationToken.None);
        store.ReleaseFirstLoad.TrySetResult();
        await Task.WhenAll(captureTask, startupTask, exclusionTask, addTask);

        Assert.False(store.Current.CaptureEnabled);
        Assert.True(store.Current.StartupEnabled);
        Assert.Contains(@"C:\Apps\excluded.exe", store.Current.ExcludedExecutablePaths);
        Assert.Equal(@"C:\Apps\resident.exe", Assert.Single(store.Current.ResidentApplications).LaunchPath);
    }

    /// <summary>候选确认与列表移动同时提交时，二者都基于同一设置事务门而不丢失条目。</summary>
    [Fact]
    public async Task ConcurrentCandidateConfirmationAndMovePreserveEntriesAndContinuousOrder()
    {
        var store = new FirstLoadBarrierSettingsStore(ButlerSettings.Default with
        {
            ResidentApplications =
            [
                App("First", @"C:\Apps\first.exe", true, 0),
                App("Second", @"C:\Apps\second.exe", true, 1)
            ]
        }, blockFirstLoad: false);
        using var settings = new SettingsCoordinator(store);
        var candidate = new ResidentAppCandidate(
            "candidate", "Candidate", @"C:\Apps\candidate.exe",
            new HashSet<string>([@"C:\Apps\candidate.exe"], StringComparer.OrdinalIgnoreCase),
            ResidentCandidateConfidence.High, ResidentCandidateKind.NewApplication, null);
        var candidates = new ResidentCandidateCoordinator(new StaticDiscovery(candidate), store, settings);
        var batch = await candidates.DiscoverAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);
        var move = new MoveResidentApplicationCommandHandler(settings);
        store.BlockNextLoad();

        var confirmationTask = candidates.ConfirmAsync(
            batch.Generation,
            [new ResidentCandidateSelection("candidate", @"C:\Apps\candidate.exe", true)],
            CancellationToken.None);
        await store.FirstLoadStarted.Task;
        var moveTask = move.HandleAsync(new MoveResidentApplicationCommand(@"C:\Apps\first.exe", 1), CancellationToken.None);
        store.ReleaseFirstLoad.TrySetResult();
        await Task.WhenAll(confirmationTask, moveTask);

        Assert.Equal(["Second", "First", "Candidate"], store.Current.ResidentApplications.Select(app => app.DisplayName));
        Assert.Equal([0, 1, 2], store.Current.ResidentApplications.Select(app => app.LaunchOrder));
    }

    private static ResidentApplication App(string name, string path, bool enabled, int order, params string[] additionalKnownPaths) =>
        new(path, new HashSet<string>([path, .. additionalKnownPaths], StringComparer.OrdinalIgnoreCase), name, enabled, order);

    private sealed class AllowingPolicy(IReadOnlySet<string>? denied = null) : IResidentExecutablePolicy
    {
        public ResidentExecutableValidation Validate(string path) =>
            denied?.Contains(path) == true
                ? new(false, null, ResidentExecutableRejection.ProhibitedDirectory)
                : new(true, Path.GetFullPath(path), ResidentExecutableRejection.None);
    }

    private sealed class TrackingPolicy(ResidentExecutableValidation? validation = null) : IResidentExecutablePolicy
    {
        internal int ValidationCount { get; private set; }

        public ResidentExecutableValidation Validate(string path)
        {
            ValidationCount++;
            return validation ?? new(true, Path.GetFullPath(path), ResidentExecutableRejection.None);
        }
    }

    private sealed class StaticDiscovery(ResidentAppCandidate candidate) : IResidentAppDiscovery
    {
        public Task<ResidentDiscoveryResult> DiscoverAsync(
            IReadOnlySet<string> ordinaryWindowPaths,
            IReadOnlyList<ResidentApplication> existing,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ResidentDiscoveryResult([candidate], []));
    }

    private sealed class RecordingStartupRegistration(bool enabled) : IStartupRegistration
    {
        public bool IsEnabled { get; private set; } = enabled;
        public void Enable() => IsEnabled = true;
        public void Disable() => IsEnabled = false;
    }

    private sealed class FirstLoadBarrierSettingsStore(ButlerSettings initial, bool blockFirstLoad = true) : ISettingsStore
    {
        private int shouldBlock = blockFirstLoad ? 1 : 0;
        internal TaskCompletionSource FirstLoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFirstLoad { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ButlerSettings Current { get; private set; } = initial;

        internal void BlockNextLoad() => Interlocked.Exchange(ref shouldBlock, 1);

        public async Task<ButlerSettings> LoadAsync(CancellationToken cancellationToken)
        {
            var snapshot = Current;
            if (Interlocked.Exchange(ref shouldBlock, 0) == 1)
            {
                FirstLoadStarted.TrySetResult();
                await ReleaseFirstLoad.Task.WaitAsync(cancellationToken);
            }

            return snapshot;
        }

        public Task SaveAsync(ButlerSettings settings, CancellationToken cancellationToken)
        {
            Current = settings;
            return Task.CompletedTask;
        }
    }
}
