using DeskButler.Core.Scenes;

namespace DeskButler.Core.Restore;

/// <summary>表示一个场景条目在本次恢复计划中的不可变决策。</summary>
/// <param name="SceneItem">要恢复的场景条目。</param>
/// <param name="Disposition">保守规划得到的处理方式。</param>
/// <param name="TargetWindowHandle">仅供本次执行复用的当前窗口句柄；它不是持久身份。</param>
public sealed record RestorePlanItem(
    SceneItem SceneItem,
    RestoreDisposition Disposition,
    nint? TargetWindowHandle);
