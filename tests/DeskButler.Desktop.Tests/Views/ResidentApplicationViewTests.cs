using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml.Linq;
using DeskButler.Application.Commands;
using DeskButler.Core.Persistence;
using DeskButler.Core.Settings;
using DeskButler.Desktop.Hosting;
using DeskButler.Desktop.Tests.ViewModels;
using DeskButler.Desktop.Tray;
using DeskButler.Desktop.ViewModels;

namespace DeskButler.Desktop.Tests.Views;

/// <summary>验证常驻应用的可访问视图、图标所有权、候选聚焦和托盘入口。</summary>
public sealed class ResidentApplicationViewTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>删除确认区将导致候选无法由键盘确认，此测试保护完整绑定与精确提示文案。</summary>
    [Fact]
    public void HomeConfirmationAreaBindsCandidateDetailsAndCommandsAccessibly()
    {
        var document = LoadMainWindow();
        var confirmation = document.Descendants(Presentation + "Border")
            .Single(element => (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "ResidentCandidateConfirmationPanel");
        Assert.NotEqual("Cycle", (string?)confirmation.Attribute("KeyboardNavigation.TabNavigation"));
        var candidates = confirmation.Descendants(Presentation + "ItemsControl")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding ResidentCandidates}");

        var selection = Assert.Single(candidates.Descendants(Presentation + "CheckBox"));
        Assert.Equal("{Binding IsSelected, Mode=TwoWay}", (string?)selection.Attribute("IsChecked"));
        Assert.Equal("{Binding DisplayName}", (string?)selection.Attribute("AutomationProperties.Name"));
        Assert.Contains(candidates.Descendants(Presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "{Binding DisplayName}");
        Assert.Contains(candidates.Descendants(Presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "{Binding Confidence}");

        var path = Assert.Single(candidates.Descendants(Presentation + "TextBox"));
        Assert.Equal("{Binding FinalLaunchPath, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}", (string?)path.Attribute("Text"));
        Assert.Equal("True", (string?)path.Attribute("IsTabStop"));
        Assert.Equal("Auto", (string?)path.Attribute("HorizontalScrollBarVisibility"));
        Assert.Contains(candidates.Descendants(Presentation + "Button"), element =>
            (string?)element.Attribute("Command") == "{Binding BrowsePathCommand}");
        Assert.Contains(confirmation.Descendants(Presentation + "Button"), element =>
            (string?)element.Attribute("Command") == "{Binding ConfirmResidentCandidatesCommand}");
        Assert.Contains(confirmation.Descendants(Presentation + "Button"), element =>
            (string?)element.Attribute("Command") == "{Binding DismissResidentCandidatesCommand}");
        Assert.Contains(document.Descendants(Presentation + "TextBlock"), element => (string?)element.Attribute("Text") == "低可信，默认不添加");
        Assert.Contains(document.Descendants(Presentation + "TextBlock"), element => (string?)element.Attribute("Text") == "请选择主程序 .exe");
        Assert.Contains(document.Descendants(Presentation + "TextBlock"), element => (string?)element.Attribute("Text") == "发现可能的新路径，需要你确认");
        var missingPathTrigger = document.Descendants(Presentation + "DataTrigger")
            .Single(element => (string?)element.Attribute("Value") == "True" &&
                (string?)element.Attribute("Binding") == "{Binding NeedsLaunchPath}");
        Assert.NotNull(missingPathTrigger);
    }

    /// <summary>移除设置滚动或项目命令会使大量条目无法管理，此测试保护所有受支持操作且不暴露命令行。</summary>
    [Fact]
    public void SettingsAreaScrollsAndExposesResidentManagementWithoutCommandLines()
    {
        var document = LoadMainWindow();
        var settings = document.Descendants(Presentation + "TabItem")
            .Single(element => (string?)element.Attribute("Header") == "设置");
        Assert.NotEmpty(settings.Descendants(Presentation + "ScrollViewer"));
        var residentToggle = settings.Descendants(Presentation + "CheckBox")
            .Single(element => (string?)element.Attribute("Command") == "{Binding ToggleResidentApplicationsCommand}");
        Assert.Equal("{Binding ResidentApplicationsEnabled, Mode=OneWay}", (string?)residentToggle.Attribute("IsChecked"));
        Assert.Contains("常驻", (string?)residentToggle.Attribute("AutomationProperties.Name"), StringComparison.Ordinal);
        Assert.Contains(settings.Descendants(Presentation + "Button"), element =>
            (string?)element.Attribute("Command") == "{Binding FindResidentCandidatesCommand}");
        Assert.Contains(settings.Descendants(Presentation + "Button"), element =>
            (string?)element.Attribute("Command") == "{Binding AddResidentApplicationCommand}");
        Assert.Contains(settings.Descendants(Presentation + "Button"), element =>
            (string?)element.Attribute("Command") == "{Binding LaunchResidentsNowCommand}");

        var applications = settings.Descendants(Presentation + "ItemsControl")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding ResidentApplications}");
        foreach (var command in new[] { "EnableCommand", "MoveUpCommand", "MoveDownCommand", "ReplacePathCommand", "RemoveCommand" })
        {
            Assert.Contains(applications.Descendants(Presentation + "Button"), element =>
                (string?)element.Attribute("Command") == $"{{Binding {command}}}");
        }

        var savedPath = Assert.Single(applications.Descendants(Presentation + "TextBox"));
        Assert.Equal("{Binding LaunchPath, Mode=OneWay}", (string?)savedPath.Attribute("Text"));
        Assert.Equal("True", (string?)savedPath.Attribute("IsTabStop"));
        Assert.Equal("Auto", (string?)savedPath.Attribute("HorizontalScrollBarVisibility"));
        Assert.DoesNotContain(applications.Descendants(Presentation + "TextBlock"), element =>
            ((string?)element.Attribute("Text"))?.Contains("命令行", StringComparison.Ordinal) == true);
    }

    /// <summary>若图标提取遗留文件句柄，移动测试副本会失败；缺失或损坏入口必须安全回退。</summary>
    [Fact]
    public void WindowsIconProviderFreezesIconsReleasesFixtureAndFallsBackForInvalidPaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"DeskButler-icon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("测试进程缺少可执行文件路径。");
        var fixture = Path.Combine(directory, "fixture.exe");
        var moved = Path.Combine(directory, "moved.exe");
        File.Copy(source, fixture);

        try
        {
            var provider = new WindowsExecutableIconProvider();
            for (var index = 0; index < 24; index++)
            {
                var icon = Assert.IsAssignableFrom<BitmapSource>(provider.GetIcon(fixture));
                Assert.True(icon.IsFrozen);
            }

            File.Move(fixture, moved);
            Assert.True(File.Exists(moved));
            Assert.IsType<DrawingImage>(provider.GetIcon(Path.Combine(directory, "missing.exe")));
            File.WriteAllText(fixture, "not an executable");
            Assert.IsType<DrawingImage>(provider.GetIcon(fixture));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>手动候选可见时不得重复打开窗口，只能把键盘焦点移到确认区。</summary>
    [Fact]
    public void CandidateFocusCoordinatorShowsHiddenWindowButOnlyFocusesVisibleWindow()
    {
        var calls = new List<string>();
        var hidden = new ResidentCandidateFocusCoordinator(
            () => false,
            () => calls.Add("show"),
            () => calls.Add("focus"));

        hidden.Focus();

        Assert.Equal(["show", "focus"], calls);
        calls.Clear();
        var visible = new ResidentCandidateFocusCoordinator(
            () => true,
            () => calls.Add("show"),
            () => calls.Add("focus"));

        visible.Focus();

        Assert.Equal(["focus"], calls);
    }

    /// <summary>若候选到达时仍停在其他页，用户会看不到确认区；此测试使用真实 WPF 窗口验证显示、首页选择和键盘焦点。</summary>
    [Fact]
    public void CandidateFocusSelectsHomeAndPlacesKeyboardFocusForHiddenAndVisibleWindows()
    {
        foreach (var (initiallyVisible, previousTab) in new[]
                 {
                     (false, "设置"),
                     (true, "现场"),
                     (true, "诊断")
                 })
        {
            RunOnStaThread(() =>
            {
                var window = new DeskButler.Desktop.Views.MainWindow(CreateViewModel(_ => Task.CompletedTask));
                try
                {
                    var tabs = Assert.IsType<TabControl>(window.FindName("MainTabControl"));
                    var home = Assert.IsType<TabItem>(window.FindName("HomeTabItem"));
                    var before = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, previousTab));
                    tabs.SelectedItem = before;
                    if (initiallyVisible)
                    {
                        window.Show();
                        window.Activate();
                    }

                    var coordinator = new ResidentCandidateFocusCoordinator(
                        () => window.IsVisible,
                        () =>
                        {
                            window.Show();
                            window.Activate();
                        },
                        window.FocusResidentCandidateConfirmation);
                    coordinator.Focus();
                    DrainDispatcher(window.Dispatcher);

                    var confirmation = Assert.IsType<Border>(window.FindName("ResidentCandidateConfirmationPanel"));
                    Assert.True(window.IsVisible);
                    Assert.Same(home, tabs.SelectedItem);
                    Assert.Same(confirmation, Keyboard.FocusedElement);
                }
                finally
                {
                    window.CloseForExit();
                }
            });
        }
    }

    /// <summary>托盘“立即启动”只能复用 ViewModel 的手动命令，不能创建额外持续守护。</summary>
    [Fact]
    public async Task TrayLaunchResidentItemExecutesViewModelManualLaunchCommand()
    {
        var launches = 0;
        var viewModel = CreateViewModel(_ =>
        {
            launches++;
            return Task.CompletedTask;
        });
        using var item = TrayIconService.CreateLaunchResidentsNowItem(viewModel);

        item.PerformClick();
        await Task.Yield();

        Assert.Equal(1, launches);
    }

    private static XDocument LoadMainWindow() => XDocument.Load(Path.Combine(
        TestRepository.Root, "src", "DeskButler.Desktop", "Views", "MainWindow.xaml"));

    private static MainViewModel CreateViewModel(Func<CancellationToken, Task> launchEnabledNowAsync) => new(
        new InMemorySceneRepository(),
        new RecordingCommandBus(),
        new InMemorySettingsStore(ButlerSettings.Default),
        new InlineUiDispatcher(),
        residentDependencies: new ResidentViewModelDependencies(
            new NullExecutablePicker(),
            new FallbackExecutableIconProvider(),
            _ => throw new InvalidOperationException("本测试不验证路径。"),
            launchEnabledNowAsync));

    private sealed class NullExecutablePicker : IExecutablePicker
    {
        public Task<string?> PickAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
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
        Assert.True(completed.Wait(TimeSpan.FromSeconds(15)), "WPF 焦点测试未在时限内完成。");
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
