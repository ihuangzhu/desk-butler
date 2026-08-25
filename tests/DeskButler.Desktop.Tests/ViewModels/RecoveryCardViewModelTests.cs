using DeskButler.Desktop.Hosting;
using DeskButler.Desktop.ViewModels;
using DeskButler.Application.Commands;
using DeskButler.Core.Restore;
using DeskButler.Core.Diagnostics;

namespace DeskButler.Desktop.Tests.ViewModels;

public sealed class RecoveryCardViewModelTests
{
    /// <summary>卡片加载时连续失败三次的项目必须默认不选并解释保护原因。</summary>
    [Fact]
    public async Task ShowDefaultsThreeFailureItemToUnselectedWithReason()
    {
        var clock = new FakeClock();
        var scene = SceneFactory.Create("00000000-0000-0000-0000-000000000043", clock.UtcNow,
            @"C:\Apps\Editor.exe");
        var history = new FixedFailureHistoryStore(new FailureHistory(
            new Dictionary<string, int> { [scene.Items[0].Id] = 3 }));
        var vm = new RecoveryCardViewModel(new RecordingCommandBus(), clock, 15, history);

        await vm.ShowAsync(scene);

        Assert.False(vm.Items[0].IsSelected);
        Assert.Contains("连续失败 3 次", vm.Items[0].ProtectionReason, StringComparison.Ordinal);
    }

    private sealed class FixedFailureHistoryStore(FailureHistory history) : IFailureHistoryStore
    {
        /// <inheritdoc />
        public Task<FailureHistory> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(history);

        /// <inheritdoc />
        public Task RecordAsync(RestoreResult result, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>全失败结果必须保留卡片、数量摘要和诊断原因，维持唯一重试入口。</summary>
    [Fact]
    public async Task AllFailedResultKeepsRetryCardAndShowsReason()
    {
        var clock = new FakeClock();
        var scene = SceneFactory.Create("00000000-0000-0000-0000-000000000041", clock.UtcNow,
            @"C:\Apps\Editor.exe");
        var vm = new RecoveryCardViewModel(
            new ResultBus(new RestoreResult([new(scene.Items[0].Id, RestoreItemStatus.Failed, "启动超时")])),
            clock, 15);
        await vm.ShowAsync(scene);

        await vm.RestoreImmediatelyAsync();

        Assert.True(vm.IsVisible);
        Assert.Contains("失败 1", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("启动超时", vm.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>取消结果必须保留卡片，全成功结果才可隐藏。</summary>
    [Theory]
    [InlineData(RestoreItemStatus.Cancelled, true, "取消 1")]
    [InlineData(RestoreItemStatus.Succeeded, false, "成功 1")]
    public async Task ResultVisibilityFollowsRetryability(
        RestoreItemStatus status, bool expectedVisible, string expectedSummary)
    {
        var clock = new FakeClock();
        var scene = SceneFactory.Create("00000000-0000-0000-0000-000000000042", clock.UtcNow,
            @"C:\Apps\Editor.exe");
        var vm = new RecoveryCardViewModel(
            new ResultBus(new RestoreResult([new(scene.Items[0].Id, status, "用户取消")])), clock, 15);
        await vm.ShowAsync(scene);

        await vm.RestoreImmediatelyAsync();

        Assert.Equal(expectedVisible, vm.IsVisible);
        Assert.Contains(expectedSummary, vm.LastRestoreSummary, StringComparison.Ordinal);
    }

    private sealed class ResultBus(RestoreResult result) : ICommandBus
    {
        /// <inheritdoc />
        public Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken) =>
            Task.FromResult((TResponse)(object)result);
    }

    /// <summary>未加载任何现场时误触恢复入口不得显示空卡或发送无效命令。</summary>
    [Fact]
    public async Task RestoreWithoutSceneRemainsHiddenAndDoesNotSendCommand()
    {
        var commands = new RecordingCommandBus();
        var vm = new RecoveryCardViewModel(commands, new FakeClock(), 15);

        await vm.RestoreImmediatelyAsync();

        Assert.False(vm.IsVisible);
        Assert.Empty(commands.SentCommands);
    }

    /// <summary>倒计时到期必须只隐藏卡片，绝不能替用户发送恢复命令。</summary>
    [Fact]
    public async Task AutoDismissHidesCardWithoutSendingRestoreCommand()
    {
        var commands = new RecordingCommandBus();
        var clock = new FakeClock();
        var vm = new RecoveryCardViewModel(commands, clock, dismissSeconds: 15);

        await vm.ShowAsync(SceneFactory.Create("00000000-0000-0000-0000-000000000001", clock.UtcNow, @"C:\Apps\Editor.exe"));
        await clock.AdvanceAsync(TimeSpan.FromSeconds(15));

        Assert.False(vm.IsVisible);
        Assert.Empty(commands.SentCommands);
    }

    /// <summary>立即恢复必须只提交用户当前勾选的项目并关闭卡片。</summary>
    [Fact]
    public async Task RestoreImmediatelySendsSelectedItemsInNormalMode()
    {
        var commands = new RecordingCommandBus();
        var clock = new FakeClock();
        var scene = SceneFactory.Create("00000000-0000-0000-0000-000000000002", clock.UtcNow,
            @"C:\Apps\Editor.exe", @"C:\Apps\Browser.exe");
        var vm = new RecoveryCardViewModel(commands, clock, 15);
        await vm.ShowAsync(scene);
        vm.Items[1].IsSelected = false;

        await vm.RestoreImmediatelyAsync();

        var command = Assert.IsType<RestoreSceneCommand>(Assert.Single(commands.SentCommands));
        Assert.False(command.SafeMode);
        Assert.Equal(scene.Id, command.Scene.Id);
        Assert.Equal([scene.Items[0].Id], command.SelectedItemIds);
        Assert.False(vm.IsVisible);
    }

    /// <summary>安全恢复必须显式携带安全模式，不得与普通恢复混淆。</summary>
    [Fact]
    public async Task RestoreSafelySendsSafeRestoreCommand()
    {
        var commands = new RecordingCommandBus();
        var clock = new FakeClock();
        var vm = new RecoveryCardViewModel(commands, clock, 15);
        await vm.ShowAsync(SceneFactory.Create("00000000-0000-0000-0000-000000000003", clock.UtcNow,
            @"C:\Apps\Editor.exe"));

        await vm.RestoreSafelyAsync();

        var command = Assert.IsType<RestoreSceneCommand>(Assert.Single(commands.SentCommands));
        Assert.True(command.SafeMode);
        Assert.False(vm.IsVisible);
    }

    /// <summary>跳过只隐藏卡片并取消倒计时，不产生任何恢复副作用。</summary>
    [Fact]
    public async Task SkipHidesWithoutSendingCommand()
    {
        var commands = new RecordingCommandBus();
        var clock = new FakeClock();
        var vm = new RecoveryCardViewModel(commands, clock, 15);
        await vm.ShowAsync(SceneFactory.Create("00000000-0000-0000-0000-000000000004", clock.UtcNow,
            @"C:\Apps\Editor.exe"));

        await vm.SkipAsync();
        await clock.AdvanceAsync(TimeSpan.FromSeconds(15));

        Assert.False(vm.IsVisible);
        Assert.Empty(commands.SentCommands);
    }

    /// <summary>永久排除必须经真实处理器写入设置，并立即取消该项选择。</summary>
    [Fact]
    public async Task ExcludePermanentlyPersistsExecutablePathAndUnchecksItem()
    {
        var store = new InMemorySettingsStore(DeskButler.Core.Settings.ButlerSettings.Default);
        var bus = new DeskButler.Application.Commands.InProcessCommandBus();
        bus.Register(new PersistExclusionCommandHandler(store));
        var clock = new FakeClock();
        var vm = new RecoveryCardViewModel(bus, clock, 15);
        await vm.ShowAsync(SceneFactory.Create("00000000-0000-0000-0000-000000000005", clock.UtcNow,
            @"C:\Apps\Editor.exe"));

        await vm.ExcludePermanentlyAsync(vm.Items[0]);

        Assert.False(vm.Items[0].IsSelected);
        Assert.Contains(@"C:\Apps\Editor.exe", store.Current.ExcludedExecutablePaths);
    }

    /// <summary>排除写入尚未完成时立刻恢复，恢复必须等待且绝不包含已排除项。</summary>
    [Fact]
    public async Task RestoreWaitsForInFlightExclusionAndOmitsExcludedItem()
    {
        var exclusionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExclusion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new ControlledCommandBus(async command =>
        {
            if (command is PersistExclusionCommand)
            {
                exclusionStarted.TrySetResult();
                await releaseExclusion.Task;
            }
        });
        var clock = new FakeClock();
        var vm = new RecoveryCardViewModel(commands, clock, 15);
        await vm.ShowAsync(SceneFactory.Create("00000000-0000-0000-0000-000000000006", clock.UtcNow,
            @"C:\Apps\Editor.exe"));

        var exclusion = vm.ExcludePermanentlyAsync(vm.Items[0]);
        await exclusionStarted.Task;
        var restore = vm.RestoreImmediatelyAsync();

        Assert.False(vm.Items[0].IsSelected);
        Assert.DoesNotContain(commands.SentCommands, command => command is RestoreSceneCommand);
        releaseExclusion.TrySetResult();
        await Task.WhenAll(exclusion, restore);
        var restoreCommand = Assert.IsType<RestoreSceneCommand>(commands.SentCommands.Last());
        Assert.Empty(restoreCommand.SelectedItemIds);
    }

    /// <summary>恢复失败时卡片与选择必须保留并显示错误，随后可原地重试成功。</summary>
    [Fact]
    public async Task FailedRestoreKeepsCardVisibleAndAllowsRetry()
    {
        var attempts = 0;
        var commands = new ControlledCommandBus(command =>
        {
            if (command is RestoreSceneCommand && ++attempts == 1)
            {
                throw new InvalidOperationException("恢复失败");
            }

            return Task.CompletedTask;
        });
        var clock = new FakeClock();
        var vm = new RecoveryCardViewModel(commands, clock, 15);
        await vm.ShowAsync(SceneFactory.Create("00000000-0000-0000-0000-000000000007", clock.UtcNow,
            @"C:\Apps\Editor.exe"));

        await vm.RestoreImmediatelyAsync();

        Assert.True(vm.IsVisible);
        Assert.Contains("恢复失败", vm.ErrorMessage, StringComparison.Ordinal);
        await vm.RestoreImmediatelyAsync();
        Assert.False(vm.IsVisible);
        Assert.Null(vm.ErrorMessage);
    }

    /// <summary>临近到期的慢失败恢复必须取消旧倒计时，并从失败时重新给予完整可见期。</summary>
    [Fact]
    public async Task SlowFailureNearDeadlineRestartsFullDismissPeriod()
    {
        var attempt = 0;
        var restoreStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new ControlledCommandBus(async command =>
        {
            if (command is RestoreSceneCommand && ++attempt == 1)
            {
                restoreStarted.TrySetResult();
                await releaseFailure.Task;
                throw new InvalidOperationException("慢恢复失败");
            }
        });
        var clock = new FakeClock();
        var scene = SceneFactory.Create("00000000-0000-0000-0000-000000000008", clock.UtcNow,
            @"C:\Apps\Editor.exe");
        var vm = new RecoveryCardViewModel(commands, clock, 15);
        await vm.ShowAsync(scene);
        await clock.AdvanceAsync(TimeSpan.FromSeconds(14));

        var restore = vm.RestoreImmediatelyAsync();
        await restoreStarted.Task;
        await clock.AdvanceAsync(TimeSpan.FromSeconds(2));
        Assert.True(vm.IsVisible);
        releaseFailure.TrySetResult();
        await restore;

        Assert.True(vm.IsVisible);
        Assert.Contains("慢恢复失败", vm.ErrorMessage, StringComparison.Ordinal);
        await clock.AdvanceAsync(TimeSpan.FromSeconds(14));
        Assert.True(vm.IsVisible);
        await clock.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.False(vm.IsVisible);

        await vm.ShowAsync(scene);
        await vm.RestoreImmediatelyAsync();
        Assert.False(vm.IsVisible);
        Assert.Null(vm.ErrorMessage);
    }
}
