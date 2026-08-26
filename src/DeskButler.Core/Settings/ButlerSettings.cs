using DeskButler.Core.ResidentApps;

namespace DeskButler.Core.Settings;

/// <summary>表示 DeskButler 的用户可配置设置。</summary>
/// <param name="CaptureEnabled">是否启用场景捕获。</param>
/// <param name="StartupEnabled">是否允许应用随系统启动。</param>
/// <param name="RecoveryCardDismissSeconds">恢复卡片自动消失前的秒数。</param>
/// <param name="ExcludedExecutablePaths">用户明确排除的可执行文件路径集合。</param>
/// <param name="ResidentApplicationsEnabled">是否允许登录后自动启动常驻应用。</param>
/// <param name="ResidentApplications">用户确认的常驻应用条目。</param>
public sealed record ButlerSettings(
    bool CaptureEnabled,
    bool StartupEnabled,
    int RecoveryCardDismissSeconds,
    IReadOnlySet<string> ExcludedExecutablePaths,
    bool ResidentApplicationsEnabled,
    IReadOnlyList<ResidentApplication> ResidentApplications)
{
    /// <summary>获取适用于首次运行的默认设置。</summary>
    public static ButlerSettings Default { get; } =
        new(
            true,
            true,
            15,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            true,
            Array.Empty<ResidentApplication>());

    /// <summary>从不含常驻应用字段的旧调用方参数创建兼容设置。</summary>
    /// <param name="captureEnabled">是否启用场景捕获。</param>
    /// <param name="startupEnabled">是否允许应用随系统启动。</param>
    /// <param name="recoveryCardDismissSeconds">恢复卡片自动消失前的秒数。</param>
    /// <param name="excludedExecutablePaths">用户明确排除的可执行文件路径集合。</param>
    /// <returns>启用常驻功能且常驻列表为空的兼容设置。</returns>
    public static ButlerSettings CreateLegacy(
        bool captureEnabled,
        bool startupEnabled,
        int recoveryCardDismissSeconds,
        IReadOnlySet<string> excludedExecutablePaths) =>
        new(
            captureEnabled,
            startupEnabled,
            recoveryCardDismissSeconds,
            excludedExecutablePaths,
            true,
            Array.Empty<ResidentApplication>());
}
