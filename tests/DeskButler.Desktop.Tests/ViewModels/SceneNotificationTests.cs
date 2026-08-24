using DeskButler.Desktop.Hosting;
using DeskButler.Desktop.ViewModels;

namespace DeskButler.Desktop.Tests.ViewModels;

public sealed class SceneNotificationTests
{
    /// <summary>后台自动保存成功后必须经 UI 调度刷新历史，且仍只显示最近三份。</summary>
    [Fact]
    public async Task AutomaticSaveRefreshesRecentScenesThroughDispatcher()
    {
        var now = new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
        var inner = new InMemorySceneRepository(
            SceneFactory.Create("00000000-0000-0000-0000-000000000031", now, @"C:\Apps\1.exe"),
            SceneFactory.Create("00000000-0000-0000-0000-000000000032", now.AddMinutes(-1), @"C:\Apps\2.exe"),
            SceneFactory.Create("00000000-0000-0000-0000-000000000033", now.AddMinutes(-2), @"C:\Apps\3.exe"));
        var repository = new NotifyingSceneRepository(inner);
        var dispatcher = new RecordingUiDispatcher();
        using var vm = new MainViewModel(repository, new RecordingCommandBus(),
            new InMemorySettingsStore(DeskButler.Core.Settings.ButlerSettings.Default), dispatcher);
        await vm.LoadAsync();
        var newest = SceneFactory.Create("00000000-0000-0000-0000-000000000034", now.AddMinutes(1), @"C:\Apps\4.exe");

        await repository.SaveAsync(newest, TestContext.Current.CancellationToken);
        await dispatcher.LastDispatch;

        Assert.Equal(1, dispatcher.DispatchCount);
        Assert.Equal(3, vm.RecentScenes.Count);
        Assert.Equal(newest.Id, vm.RecentScenes[0].Scene.Id);
    }

    private sealed class RecordingUiDispatcher : IUiDispatcher
    {
        internal int DispatchCount { get; private set; }

        internal Task LastDispatch { get; private set; } = Task.CompletedTask;

        /// <inheritdoc />
        public void Post(Func<Task> action)
        {
            DispatchCount++;
            LastDispatch = action();
        }
    }
}
