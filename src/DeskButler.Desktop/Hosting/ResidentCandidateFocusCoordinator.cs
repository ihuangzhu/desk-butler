namespace DeskButler.Desktop.Hosting;

/// <summary>协调手动候选到达后的窗口显示和键盘焦点边界。</summary>
internal sealed class ResidentCandidateFocusCoordinator(
    Func<bool> isWindowVisible,
    Action showMainWindow,
    Action focusResidentCandidates)
{
    private readonly Func<bool> isWindowVisible = isWindowVisible ?? throw new ArgumentNullException(nameof(isWindowVisible));
    private readonly Action showMainWindow = showMainWindow ?? throw new ArgumentNullException(nameof(showMainWindow));
    private readonly Action focusResidentCandidates = focusResidentCandidates ?? throw new ArgumentNullException(nameof(focusResidentCandidates));

    /// <summary>隐藏窗口先显示一次；可见窗口仅转移焦点，避免手动发现抢占窗口激活。</summary>
    internal void Focus()
    {
        if (!isWindowVisible())
        {
            showMainWindow();
        }

        focusResidentCandidates();
    }
}
