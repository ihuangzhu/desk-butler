using DeskButler.Core.Capture;
using DeskButler.Core.Settings;

namespace DeskButler.Modules.WorkspaceRecovery.Capture;

/// <summary>根据产品安全边界判断窗口候选项是否可被捕获到场景中。</summary>
public sealed class SceneFilter
{
    /// <summary>保存已正规化的用户排除路径，以保证比较不受输入写法和大小写影响。</summary>
    private readonly HashSet<string> excludedExecutablePaths;

    /// <summary>使用给定设置创建场景捕获筛选器。</summary>
    /// <param name="settings">包含用户排除路径的应用设置。</param>
    public SceneFilter(ButlerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        excludedExecutablePaths = new HashSet<string>(
            settings.ExcludedExecutablePaths.Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>判断候选窗口是否符合 V1 场景捕获规则。</summary>
    /// <param name="candidate">要判断的窗口候选数据。</param>
    /// <returns>候选窗口可安全纳入场景时为 <see langword="true"/>；否则为 <see langword="false"/>。</returns>
    public bool ShouldCapture(WindowCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (!candidate.IsVisibleMainWindow ||
            candidate.IsSystemWindow ||
            candidate.IsTemporaryWindow ||
            candidate.IsDeskButlerWindow ||
            candidate.ExecutablePath is null)
        {
            return false;
        }

        return !excludedExecutablePaths.Contains(Path.GetFullPath(candidate.ExecutablePath));
    }
}
