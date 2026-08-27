using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DeskButler.Core.Settings;
using DeskButler.Desktop.Tests.ViewModels;
using DeskButler.Desktop.ViewModels;

namespace DeskButler.Desktop.Tests.Views;

public sealed class SceneManagementViewTests
{
    /// <summary>真实现场页必须呈现可勾选明细，并在空选择时禁用恢复。</summary>
    [Fact]
    public void ScenePageRendersWindowDetailsAndDisablesEmptyRestore()
    {
        RunOnStaThread(() =>
        {
            var scene = SceneFactory.Create(
                "00000000-0000-0000-0000-000000000094", DateTimeOffset.UtcNow,
                @"C:\Windows\explorer.exe", @"D:\Apps\Editor.exe");
            scene = scene with
            {
                Items =
                [
                    scene.Items[0] with
                    {
                        TitleHint = "项目文档",
                        ExplorerPath = @"C:\Users\Alice\Documents",
                        WasElevated = true
                    },
                    scene.Items[1] with { TitleHint = null }
                ]
            };
            var viewModel = new MainViewModel(
                new InMemorySceneRepository(scene),
                new RecordingCommandBus(),
                new InMemorySettingsStore(ButlerSettings.Default));
            viewModel.LoadAsync().GetAwaiter().GetResult();
            var window = new DeskButler.Desktop.Views.MainWindow(viewModel);
            try
            {
                window.Show();
                var tabs = Assert.IsType<TabControl>(window.FindName("MainTabControl"));
                tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(item => Equals(item.Header, "现场"));
                window.UpdateLayout();
                DrainDispatcher(window.Dispatcher);

                var list = Assert.IsType<ListBox>(window.FindName("SceneList"));
                var container = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
                var expander = Assert.Single(FindDescendants<Expander>(container));
                Assert.True(expander.IsExpanded);
                expander.UpdateLayout();
                DrainDispatcher(window.Dispatcher);

                var visibleText = FindDescendants<TextBlock>(container)
                    .Select(text => text.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToArray();
                Assert.Contains("项目文档", visibleText);
                Assert.Contains("explorer", visibleText);
                Assert.Contains(@"C:\Users\Alice\Documents", visibleText);
                Assert.Contains("管理员权限", visibleText);
                Assert.Contains("Editor", visibleText);

                var itemChecks = FindDescendants<CheckBox>(container).ToArray();
                Assert.Equal(2, itemChecks.Length);
                foreach (var checkBox in itemChecks)
                {
                    checkBox.IsChecked = false;
                }
                DrainDispatcher(window.Dispatcher);

                var restore = FindDescendants<Button>(container)
                    .Single(button => Equals(button.Content, "恢复所选"));
                Assert.False(viewModel.RecentScenes[0].HasSelectedItems);
                Assert.False(restore.IsEnabled);
            }
            finally
            {
                window.CloseForExit();
            }
        });
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                completed.Set();
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(
            completed.Wait(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken),
            "现场管理视图测试未在一分钟诊断时限内完成。");
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void DrainDispatcher(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
