using DeskButler.Core.Capture;
using DeskButler.Core.Diagnostics;
using DeskButler.Core.Scenes;

namespace DeskButler.Core.Restore;

/// <summary>为选定场景生成不关闭当前窗口的保守恢复计划。</summary>
public interface IRestorePlanner
{
    /// <summary>根据场景、当前窗口和安全状态构建不可变恢复计划。</summary>
    /// <param name="scene">用户选定的场景快照。</param>
    /// <param name="currentWindows">本次规划时枚举到的当前窗口。</param>
    /// <param name="failureHistory">各场景项目的连续失败次数。</param>
    /// <param name="safeMode">是否采用安全恢复默认过滤。</param>
    /// <returns>仅包含场景项目的保守恢复计划。</returns>
    RestorePlan Build(
        SceneSnapshot scene,
        IReadOnlyCollection<WindowCandidate> currentWindows,
        FailureHistory failureHistory,
        bool safeMode);
}
