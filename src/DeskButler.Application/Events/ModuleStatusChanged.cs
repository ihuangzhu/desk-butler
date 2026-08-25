namespace DeskButler.Application.Events;

/// <summary>表示模块宿主发布的真实运行状态。</summary>
public enum ModuleRunState
{
    /// <summary>模块已成功启动。</summary>
    Running,
    /// <summary>模块已成功停止。</summary>
    Stopped,
    /// <summary>模块生命周期操作失败。</summary>
    Failed
}

/// <summary>携带模块稳定标识、运行状态和可诊断错误。</summary>
public sealed record ModuleStatusChanged(
    string ModuleId,
    ModuleRunState State,
    string? ErrorMessage = null);
