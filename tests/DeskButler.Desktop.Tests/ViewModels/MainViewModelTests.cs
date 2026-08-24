using DeskButler.Desktop.Hosting;
using DeskButler.Desktop.ViewModels;

namespace DeskButler.Desktop.Tests.ViewModels;

public sealed class MainViewModelTests
{
    /// <summary>初始化只加载最近三份现场，并保留仓库的最新优先顺序。</summary>
    [Fact]
    public async Task LoadAsyncExposesOnlyThreeMostRecentScenes()
    {
        var now = new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);
        var scenes = Enumerable.Range(1, 4)
            .Select(index => SceneFactory.Create($"00000000-0000-0000-0000-00000000000{index}", now.AddMinutes(-index),
                @"C:\Apps\Editor.exe"))
            .ToArray();
        var vm = new MainViewModel(new InMemorySceneRepository(scenes), new RecordingCommandBus(),
            new InMemorySettingsStore(DeskButler.Core.Settings.ButlerSettings.Default));

        await vm.LoadAsync();

        Assert.Equal(3, vm.RecentScenes.Count);
        Assert.Equal(scenes.Take(3).Select(scene => scene.Id), vm.RecentScenes.Select(scene => scene.Scene.Id));
    }

    /// <summary>用户可手动选择较旧现场恢复，命令必须携带该现场而不是默认最新现场。</summary>
    [Fact]
    public async Task RestoreSceneAsyncSendsTheSelectedOlderScene()
    {
        var now = new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);
        var newest = SceneFactory.Create("00000000-0000-0000-0000-000000000011", now, @"C:\Apps\Editor.exe");
        var older = SceneFactory.Create("00000000-0000-0000-0000-000000000012", now.AddMinutes(-10), @"C:\Apps\Browser.exe");
        var commands = new RecordingCommandBus();
        var vm = new MainViewModel(new InMemorySceneRepository(newest, older), commands,
            new InMemorySettingsStore(DeskButler.Core.Settings.ButlerSettings.Default));
        await vm.LoadAsync();

        await vm.RestoreSceneAsync(vm.RecentScenes[1]);

        var command = Assert.IsType<RestoreSceneCommand>(Assert.Single(commands.SentCommands));
        Assert.Equal(older.Id, command.Scene.Id);
        Assert.Equal(older.Items.Select(item => item.Id), command.SelectedItemIds);
    }

    /// <summary>暂停捕获必须持久化并更新可观察状态，供托盘菜单切换为“继续”。</summary>
    [Fact]
    public async Task ToggleCaptureAsyncPersistsPausedState()
    {
        var store = new InMemorySettingsStore(DeskButler.Core.Settings.ButlerSettings.Default);
        var bus = new DeskButler.Application.Commands.InProcessCommandBus();
        bus.Register(new SetCaptureEnabledCommandHandler(store));
        var vm = new MainViewModel(new InMemorySceneRepository(), bus, store);
        await vm.LoadAsync();

        await vm.ToggleCaptureAsync();

        Assert.True(vm.IsCapturePaused);
        Assert.False(store.Current.CaptureEnabled);
    }

    /// <summary>设置页必须展示已经持久化的永久排除路径。</summary>
    [Fact]
    public async Task LoadAsyncExposesPersistedExclusionPaths()
    {
        var settings = DeskButler.Core.Settings.ButlerSettings.Default with
        {
            ExcludedExecutablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                @"C:\Apps\Editor.exe",
                @"C:\Apps\Browser.exe"
            }
        };
        var vm = new MainViewModel(
            new InMemorySceneRepository(), new RecordingCommandBus(), new InMemorySettingsStore(settings));

        await vm.LoadAsync();

        Assert.Equal([@"C:\Apps\Browser.exe", @"C:\Apps\Editor.exe"], vm.ExcludedExecutablePaths);
    }
}
