namespace DeskButler.Core.Scenes;

/// <summary>表示场景快照中一个可恢复的窗口条目。</summary>
/// <param name="Id">窗口条目的稳定标识。</param>
/// <param name="ExecutablePath">启动窗口所需的可执行文件路径。</param>
/// <param name="WindowClass">窗口类名，用于后续匹配窗口。</param>
/// <param name="TitleHint">窗口标题提示，用于后续匹配窗口。</param>
/// <param name="ExplorerPath">资源管理器窗口的路径提示。</param>
/// <param name="Bounds">窗口边界。</param>
/// <param name="State">窗口显示状态。</param>
/// <param name="Monitor">窗口所在显示器。</param>
/// <param name="WasElevated">窗口捕获时是否以提升权限运行。</param>
public sealed record SceneItem(
    string Id,
    string ExecutablePath,
    string WindowClass,
    string? TitleHint,
    string? ExplorerPath,
    WindowBounds Bounds,
    SceneWindowState State,
    MonitorIdentity Monitor,
    bool WasElevated);
