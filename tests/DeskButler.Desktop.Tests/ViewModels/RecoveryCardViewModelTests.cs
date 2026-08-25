using DeskButler.Desktop.Hosting;
using DeskButler.Desktop.ViewModels;
using DeskButler.Application.Commands;
using DeskButler.Core.Restore;
using DeskButler.Core.Diagnostics;

namespace DeskButler.Desktop.Tests.ViewModels;

public sealed class RecoveryCardViewModelTests
{
    /// <summary>较慢的旧历史加载不得在较新的显示请求完成后覆盖恢复项目。</summary>
    [Fact]
    public async Task LatestShowRequestWinsWhenOlderHistoryLoadCompletesLast()
    {
        var history = new SequencedFailureHistoryStore();
        var vm = new RecoveryCardViewModel(new RecordingCommandBus(), new FakeClock(), 15, history);
        var older = SceneFactory.Create("00000000-0000-0000-0000-000000000051", DateTimeOffset.UtcNow,
            @"C:\Apps\Old.exe");
        var newer = SceneFactory.Create("00000000-0000-0000-0000-000000000052", DateTimeOffset.UtcNow,
            @"C:\Apps\New.exe");

        var oldShow = vm.ShowAsync(older);
        await history.FirstLoadStarted.Task;
        await vm.ShowAsync(newer);
        history.ReleaseFirstLoad.TrySetResult();
        await oldShow;

        Assert.Equal([newer.Items[0].Id], vm.Items.Select(item => item.Item.Id));
    }

    /// <summary>恢复命令完成前新的显示请求必须等待，命令与最终界面各自使用完整单一现场。</summary>
    [Fact]
    public async Task RestoreUsesOnePublishedSceneWhileNewShowWaits()
    {
        var restoreStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new ControlledCommandBus(async command =>
        {
            if (command is RestoreSceneCommand)
            {
                restoreStarted.TrySetResult();
                await releaseRestore.Task;
            }
        });
        var clock = new FakeClock();
        var vm = new RecoveryCardViewModel(commands, clock, 15);
        var older = SceneFactory.Create("00000000-0000-0000-0000-000000000053", clock.UtcNow,
            @"C:\Apps\Old.exe");
        var newer = SceneFactory.Create("00000000-0000-0000-0000-000000000054", clock.UtcNow,
            @"C:\Apps\New.exe");
        await vm.ShowAsync(older);

        var restore = vm.RestoreImmediatelyAsync();
        await restoreStarted.Task;
        var newShow = vm.ShowAsync(newer);
        try
        {
            Assert.False(newShow.IsCompleted);
            Assert.Equal([older.Items[0].Id], vm.Items.Select(item => item.Item.Id));
        }
        finally
        {
            releaseRestore.TrySetResult();
            await Task.WhenAll(restore, newShow);
        }

        var sent = Assert.IsType<RestoreSceneCommand>(Assert.Single(commands.SentCommands));
        Assert.Equal(older.Id, sent.Scene.Id);
        Assert.All(sent.SelectedItemIds, id => Assert.Contains(id, older.Items.Select(item => item.Id)));
        Assert.Equal([newer.Items[0].Id], vm.Items.Select(item => item.Item.Id));
        Assert.True(vm.IsVisible);
    }

    /// <summary>释放资源后才完成的历史读取不得再发布现场、项目或显示计时器。</summary>
    [Fact]
    public async Task DisposePreventsBlockedShowFromPublishing()
    {
        var history = new SequencedFailureHistoryStore();
        var clock = new FakeClock();
        var vm = new RecoveryCardViewModel(new RecordingCommandBus(), clock, 15, history);
        var scene = SceneFactory.Create("00000000-0000-0000-0000-000000000055", clock.UtcNow,
            @"C:\Apps\DisposedShow.exe");

        var show = vm.ShowAsync(scene);
        await history.FirstLoadStarted.Task;
        vm.Dispose();
        history.ReleaseFirstLoad.TrySetResult();
        await show;

        Assert.False(vm.IsVisible);
        Assert.Empty(vm.Items);
        Assert.Equal(0, clock.DelayCallCount);
    }

    /// <summary>释放资源后才失败的恢复不得产生任何错误重显或新计时器副作用。</summary>
    [Fact]
    public async Task DisposedRestoreFailureHasNoUiSideEffects()
    {
        var restoreStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new ControlledCommandBus(async command =>
        {
            if (command is RestoreSceneCommand)
            {
                restoreStarted.TrySetResult();
                await releaseFailure.Task;
                throw new InvalidOperationException("释放后的恢复失败");
            }
        });
        var clock = new FakeClock();
        var vm = new RecoveryCardViewModel(commands, clock, 15);
        await vm.ShowAsync(SceneFactory.Create("00000000-0000-0000-0000-000000000056", clock.UtcNow,
            @"C:\Apps\DisposedRestore.exe"));
        var timerStartsBeforeFailure = clock.DelayCallCount;

        var restore = vm.RestoreImmediatelyAsync();
        await restoreStarted.Task;
        vm.Dispose();
        releaseFailure.TrySetResult();
        await restore;

        Assert.Equal(timerStartsBeforeFailure, clock.DelayCallCount);
        Assert.False(vm.IsVisible);
        Assert.Null(vm.ErrorMessage);
    }

    /// <summary>Show 发布通知中重入 Dispose 后不得继续重显或创建计时器。</summary>
    [Fact]
    public async Task DisposeDuringShowPublicationPreventsLaterVisibilityAndTimer()
    {
        var clock = new FakeClock();
        var vm = new RecoveryCardViewModel(new RecordingCommandBus(), clock, 15);
        var disposedDuringPublication = false;
        vm.Items.CollectionChanged += (_, _) =>
        {
            if (disposedDuringPublication)
            {
                return;
            }

            disposedDuringPublication = true;
            vm.Dispose();
        };

        await vm.ShowAsync(SceneFactory.Create("00000000-0000-0000-0000-000000000057", clock.UtcNow,
            @"C:\Apps\ReentrantShow.exe"));

        Assert.True(disposedDuringPublication);
        Assert.False(vm.IsVisible);
        Assert.Equal(0, clock.DelayCallCount);
    }

    /// <summary>成功结果摘要通知中重入 Dispose 后不得继续发布错误或重显。</summary>
    [Fact]
    public async Task DisposeDuringRestoreResultPublicationPreventsLaterUiWrites()
    {
        var clock = new FakeClock();
        var scene = SceneFactory.Create("00000000-0000-0000-0000-000000000058", clock.UtcNow,
            @"C:\Apps\ReentrantResult.exe");
        var vm = new RecoveryCardViewModel(
            new ResultBus(new RestoreResult([
                new(scene.Items[0].Id, RestoreItemStatus.Failed, "结果发布失败")
            ])), clock, 15);
        await vm.ShowAsync(scene);
        var disposedDuringPublication = false;
        vm.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(RecoveryCardViewModel.LastRestoreSummary))
            {
                disposedDuringPublication = true;
                vm.Dispose();
            }
        };

        await vm.RestoreImmediatelyAsync();

        Assert.True(disposedDuringPublication);
        Assert.False(vm.IsVisible);
        Assert.Null(vm.ErrorMessage);
    }

    /// <summary>恢复异常通知中重入 Dispose 后不得继续重显或创建新计时器。</summary>
    [Fact]
    public async Task DisposeDuringRestoreFailurePublicationPreventsLaterVisibilityAndTimer()
    {
        var clock = new FakeClock();
        var commands = new ControlledCommandBus(command =>
            command is RestoreSceneCommand
                ? Task.FromException(new InvalidOperationException("重入释放失败"))
                : Task.CompletedTask);
        var vm = new RecoveryCardViewModel(commands, clock, 15);
        await vm.ShowAsync(SceneFactory.Create("00000000-0000-0000-0000-000000000059", clock.UtcNow,
            @"C:\Apps\ReentrantFailure.exe"));
        var timerStartsBeforeFailure = clock.DelayCallCount;
        var disposedDuringPublication = false;
        vm.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(RecoveryCardViewModel.ErrorMessage))
            {
                disposedDuringPublication = true;
                vm.Dispose();
            }
        };

        await vm.RestoreImmediatelyAsync();

        Assert.True(disposedDuringPublication);
        Assert.False(vm.IsVisible);
        Assert.Equal(timerStartsBeforeFailure, clock.DelayCallCount);
    }

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

    /// <summary>让首次历史读取精确晚于第二次读取完成，暴露异步显示的乱序完成。</summary>
    private sealed class SequencedFailureHistoryStore : IFailureHistoryStore
    {
        private int loadCount;

        internal TaskCompletionSource FirstLoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseFirstLoad { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public async Task<FailureHistory> LoadAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref loadCount) == 1)
            {
                FirstLoadStarted.TrySetResult();
                await ReleaseFirstLoad.Task.WaitAsync(cancellationToken);
            }

            return FailureHistory.Empty;
        }

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

    /// <summary>旧卡片项目排队期间若新卡片已发布，不得取消新选择或持久化旧路径。</summary>
    [Fact]
    public async Task StaleExclusionQueuedBehindNewShowDoesNotPersistOldItem()
    {
        var firstExclusionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstExclusion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new ControlledCommandBus(async command =>
        {
            if (command is PersistExclusionCommand exclusion &&
                exclusion.ExecutablePath == @"C:\Apps\GateHolder.exe")
            {
                firstExclusionStarted.TrySetResult();
                await releaseFirstExclusion.Task.WaitAsync(TestContext.Current.CancellationToken);
            }
        });
        var clock = new FakeClock();
        var vm = new RecoveryCardViewModel(commands, clock, 15);
        var older = SceneFactory.Create("00000000-0000-0000-0000-000000000060", clock.UtcNow,
            @"C:\Apps\GateHolder.exe", @"C:\Apps\Stale.exe");
        var newer = SceneFactory.Create("00000000-0000-0000-0000-000000000061", clock.UtcNow,
            @"C:\Apps\Current.exe");
        await vm.ShowAsync(older);
        var staleItem = vm.Items[1];

        var gateHolder = vm.ExcludePermanentlyAsync(vm.Items[0]);
        await firstExclusionStarted.Task;
        var newShow = vm.ShowAsync(newer);
        var staleExclusion = vm.ExcludePermanentlyAsync(staleItem);

        releaseFirstExclusion.TrySetResult();
        await Task.WhenAll(gateHolder, newShow, staleExclusion);

        var exclusions = commands.SentCommands.OfType<PersistExclusionCommand>().ToArray();
        Assert.Equal([@"C:\Apps\GateHolder.exe"], exclusions.Select(item => item.ExecutablePath));
        Assert.Equal([newer.Items[0].Id], vm.Items.Select(item => item.Item.Id));
        Assert.True(vm.Items[0].IsSelected);
        Assert.Null(vm.ErrorMessage);
    }

    /// <summary>同代次但不属于当前集合的伪造项目不得改变选择或持久化路径。</summary>
    [Fact]
    public async Task ExclusionRequiresSamePublishedItemInstance()
    {
        var commands = new RecordingCommandBus();
        var clock = new FakeClock();
        var vm = new RecoveryCardViewModel(commands, clock, 15);
        await vm.ShowAsync(SceneFactory.Create("00000000-0000-0000-0000-000000000062", clock.UtcNow,
            @"C:\Apps\Current.exe"));
        var publishedItem = vm.Items[0];
        var mismatchedItem = new RecoveryItemViewModel(
            publishedItem.Item, failureProtected: false,
            publicationIdentity: publishedItem.PublicationIdentity);

        await vm.ExcludePermanentlyAsync(mismatchedItem);

        Assert.True(publishedItem.IsSelected);
        Assert.True(mismatchedItem.IsSelected);
        Assert.Empty(commands.SentCommands);
        Assert.Null(vm.ErrorMessage);
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
