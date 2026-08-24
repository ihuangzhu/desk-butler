namespace DeskButler.Desktop.Hosting;

/// <summary>串联“确保恢复卡存在”与“显式键盘聚焦”两个步骤。</summary>
internal sealed class RecoveryCardFocusCoordinator(Func<Task<bool>> ensureCardAsync, Action focusWindow)
{
    /// <summary>仅在确有最近现场时激活恢复卡首个控件，并返回是否成功聚焦。</summary>
    internal async Task<bool> FocusAsync()
    {
        if (!await ensureCardAsync())
        {
            return false;
        }

        focusWindow();
        return true;
    }
}
