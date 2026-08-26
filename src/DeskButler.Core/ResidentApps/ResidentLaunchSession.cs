namespace DeskButler.Core.ResidentApps;

/// <summary>表示一个登录会话内固定不变的常驻应用启动计划。</summary>
/// <param name="FormatVersion">持久化格式版本，当前固定为 1。</param>
/// <param name="LogonSessionId">不包含账号或令牌内容的稳定登录会话身份。</param>
/// <param name="Completed">计划是否已经完成或被明确跳过。</param>
/// <param name="Plan">按固定顺序记录的启动尝试计划。</param>
public sealed record ResidentLaunchSession(
    int FormatVersion,
    string LogonSessionId,
    bool Completed,
    IReadOnlyList<ResidentLaunchPlanItem> Plan);

/// <summary>表示固定登录计划中的一项启动身份及其尝试状态。</summary>
/// <param name="LaunchIdentity">不包含原始路径负载的稳定启动身份。</param>
/// <param name="Attempted">该项是否已在本登录会话尝试处理。</param>
public sealed record ResidentLaunchPlanItem(string LaunchIdentity, bool Attempted);

/// <summary>表示损坏登录会话恢复后的安全处置结果。</summary>
public enum ResidentLaunchRecoveryResult
{
    /// <summary>已保全损坏证据，并写入当前登录的已完成空计划。</summary>
    RecoveredWithEmptyPlan,

    /// <summary>无法保全损坏证据，因此严格不覆盖原文件且本次登录停止启动。</summary>
    PreservationFailedFailClosed
}
