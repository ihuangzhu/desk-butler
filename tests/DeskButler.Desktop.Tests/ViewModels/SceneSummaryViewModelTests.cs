using DeskButler.Core.Scenes;
using DeskButler.Desktop.ViewModels;

namespace DeskButler.Desktop.Tests.ViewModels;

public sealed class SceneSummaryViewModelTests
{
    /// <summary>现场摘要必须把已保存窗口投影为用户可识别且默认选中的明细。</summary>
    [Fact]
    public void SceneSummaryExposesSelectableWindowDetails()
    {
        var scene = CreateScene();

        var summary = new SceneSummaryViewModel(scene, isExpanded: true);

        Assert.True(summary.IsExpanded);
        Assert.True(summary.HasSelectedItems);
        Assert.Equal("已选择 2/2", summary.SelectedItemCountText);
        Assert.Collection(
            summary.Items,
            item =>
            {
                Assert.Equal("项目文档", item.DisplayName);
                Assert.Equal("explorer", item.ApplicationName);
                Assert.Equal(@"C:\Users\Alice\Documents", item.ExplorerPath);
                Assert.Equal(@"C:\Windows\explorer.exe", item.ExecutablePath);
                Assert.True(item.IsElevated);
                Assert.True(item.IsSelected);
            },
            item =>
            {
                Assert.Equal("Editor", item.DisplayName);
                Assert.Equal("Editor", item.ApplicationName);
                Assert.Null(item.ExplorerPath);
                Assert.False(item.IsElevated);
                Assert.True(item.IsSelected);
            });
    }

    /// <summary>全选控制必须同步更新每个项目、计数和恢复可用状态。</summary>
    [Fact]
    public void SceneSummarySelectAllCommandsUpdateRestoreSelection()
    {
        var summary = new SceneSummaryViewModel(CreateScene());

        summary.SelectNoneCommand.Execute(null);

        Assert.All(summary.Items, item => Assert.False(item.IsSelected));
        Assert.False(summary.HasSelectedItems);
        Assert.Equal("已选择 0/2", summary.SelectedItemCountText);
        Assert.Empty(summary.SelectedItemIds);

        summary.SelectAllCommand.Execute(null);

        Assert.All(summary.Items, item => Assert.True(item.IsSelected));
        Assert.True(summary.HasSelectedItems);
        Assert.Equal(["explorer-item", "editor-item"], summary.SelectedItemIds);
    }

    private static SceneSnapshot CreateScene()
    {
        var monitor = new MonitorIdentity("DISPLAY1", new WindowBounds(0, 0, 1920, 1080), 96, 96);
        return new SceneSnapshot(
            Guid.Parse("00000000-0000-0000-0000-000000000091"),
            1,
            new DateTimeOffset(2026, 8, 27, 10, 30, 0, TimeSpan.FromHours(8)),
            "test",
            [
                new SceneItem(
                    "explorer-item", @"C:\Windows\explorer.exe", "CabinetWClass", "项目文档",
                    @"C:\Users\Alice\Documents", new WindowBounds(10, 20, 900, 700),
                    SceneWindowState.Normal, monitor, true),
                new SceneItem(
                    "editor-item", @"D:\Apps\Editor.exe", "EditorWindow", null, null,
                    new WindowBounds(30, 40, 1000, 800), SceneWindowState.Maximized, monitor, false)
            ]);
    }
}
