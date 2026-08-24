namespace DeskButler.Core.Capture;

/// <summary>定义平台窗口清单捕获边界。</summary>
public interface IWindowInventory
{
    /// <summary>异步捕获当前会话中可恢复的普通可见主窗口。</summary>
    /// <param name="cancellationToken">用于取消本次捕获的令牌。</param>
    /// <returns>不包含命令行、文档内容或屏幕内容的窗口候选集合。</returns>
    Task<IReadOnlyList<WindowCandidate>> CaptureAsync(CancellationToken cancellationToken);
}
