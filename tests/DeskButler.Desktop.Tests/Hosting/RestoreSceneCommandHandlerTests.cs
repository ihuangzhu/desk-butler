using DeskButler.Core.Capture;
using DeskButler.Core.Restore;
using DeskButler.Core.Scenes;
using DeskButler.Desktop.Hosting;
using DeskButler.Modules.WorkspaceRecovery.Restore;
using DeskButler.Desktop.Tests.ViewModels;

namespace DeskButler.Desktop.Tests.Hosting;

public sealed class RestoreSceneCommandHandlerTests
{
    /// <summary>永久排除必须在统一恢复边界生效，旧快照也不能重新启动该程序。</summary>
    [Fact]
    public async Task PermanentlyExcludedExecutableIsRemovedBeforePlanning()
    {
        var scene = SceneFactory.Create(
            "00000000-0000-0000-0000-000000000021",
            new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero),
            @"C:\Apps\Editor.exe");
        var settings = DeskButler.Core.Settings.ButlerSettings.Default with
        {
            ExcludedExecutablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                @"C:\Apps\Editor.exe"
            }
        };
        var inventory = new EmptyInventory();
        var executor = new RestoreExecutor(
            new FailingLauncher(), inventory, new FailingPositioner(), new FakeClock());
        var handler = new RestoreSceneCommandHandler(
            inventory, new RestorePlanner(_ => true), executor, new InMemorySettingsStore(settings));

        var result = await handler.HandleAsync(
            new RestoreSceneCommand(scene, [scene.Items[0].Id], SafeMode: false),
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Items);
    }

    private sealed class EmptyInventory : IWindowInventory
    {
        /// <inheritdoc />
        public Task<IReadOnlyList<WindowCandidate>> CaptureAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WindowCandidate>>([]);
    }

    private sealed class FailingLauncher : IAppLauncher
    {
        /// <inheritdoc />
        public Task LaunchAsync(SceneItem sceneItem, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("永久排除项不应到达启动器。");
    }

    private sealed class FailingPositioner : IWindowPositioner
    {
        /// <inheritdoc />
        public Task PositionAsync(nint windowHandle, SceneItem sceneItem, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("永久排除项不应到达定位器。");
    }
}
