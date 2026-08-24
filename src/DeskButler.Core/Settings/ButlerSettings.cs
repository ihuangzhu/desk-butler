namespace DeskButler.Core.Settings;

/// <summary>表示 DeskButler 的用户可配置设置。</summary>
/// <param name="CaptureEnabled">是否启用场景捕获。</param>
/// <param name="StartupEnabled">是否允许应用随系统启动。</param>
/// <param name="RecoveryCardDismissSeconds">恢复卡片自动消失前的秒数。</param>
/// <param name="ExcludedExecutablePaths">用户明确排除的可执行文件路径集合。</param>
public sealed record ButlerSettings(
    bool CaptureEnabled,
    bool StartupEnabled,
    int RecoveryCardDismissSeconds,
    IReadOnlySet<string> ExcludedExecutablePaths)
{
    /// <summary>获取适用于首次运行的默认设置。</summary>
    public static ButlerSettings Default { get; } =
        new(true, true, 15, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}
