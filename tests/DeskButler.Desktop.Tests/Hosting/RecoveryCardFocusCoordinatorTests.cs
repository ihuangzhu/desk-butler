using DeskButler.Desktop.Hosting;

namespace DeskButler.Desktop.Tests.Hosting;

public sealed class RecoveryCardFocusCoordinatorTests
{
    /// <summary>键盘入口必须先确保卡片存在，再显式激活并聚焦其首个控件。</summary>
    [Fact]
    public async Task FocusEntryEnsuresCardBeforeFocusingWindow()
    {
        var calls = new List<string>();
        var coordinator = new RecoveryCardFocusCoordinator(
            () => { calls.Add("show"); return Task.FromResult(true); },
            () => calls.Add("focus"));

        var focused = await coordinator.FocusAsync();

        Assert.True(focused);
        Assert.Equal(["show", "focus"], calls);
    }

    /// <summary>全新仓库没有任何快照时，入口不得显示或聚焦不可操作的空卡。</summary>
    [Fact]
    public async Task EmptyRepositoryDoesNotFocusRecoveryCard()
    {
        var focusCalls = 0;
        var coordinator = new RecoveryCardFocusCoordinator(
            () => Task.FromResult(false),
            () => focusCalls++);

        var focused = await coordinator.FocusAsync();

        Assert.False(focused);
        Assert.Equal(0, focusCalls);
    }
}
