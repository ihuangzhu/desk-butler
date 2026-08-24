using DeskButler.Core.Scenes;

namespace DeskButler.Core.Capture;

/// <summary>表示平台捕获层提供给场景筛选器的窗口候选数据。</summary>
/// <param name="Handle">平台窗口句柄。</param>
/// <param name="ProcessId">窗口所属进程标识。</param>
/// <param name="ExecutablePath">窗口所属可执行文件路径；不可用时为 <see langword="null"/>。</param>
/// <param name="WindowClass">平台窗口类名。</param>
/// <param name="Title">窗口标题提示。</param>
/// <param name="ExplorerPath">资源管理器窗口的路径提示。</param>
/// <param name="Bounds">窗口边界。</param>
/// <param name="State">窗口显示状态。</param>
/// <param name="Monitor">窗口所在显示器。</param>
/// <param name="IsVisibleMainWindow">窗口是否为可见主窗口。</param>
/// <param name="IsSystemWindow">窗口是否属于系统界面。</param>
/// <param name="IsTemporaryWindow">窗口是否为临时窗口。</param>
/// <param name="IsDeskButlerWindow">窗口是否属于 DeskButler 自身。</param>
/// <param name="WasElevatedOrInaccessible">窗口是否以提升权限运行或在捕获时不可访问。</param>
public sealed record WindowCandidate(
    nint Handle,
    int ProcessId,
    string? ExecutablePath,
    string WindowClass,
    string? Title,
    string? ExplorerPath,
    WindowBounds Bounds,
    SceneWindowState State,
    MonitorIdentity Monitor,
    bool IsVisibleMainWindow,
    bool IsSystemWindow,
    bool IsTemporaryWindow,
    bool IsDeskButlerWindow,
    bool WasElevatedOrInaccessible);
