namespace DeskButler.Core.ResidentApps;

/// <summary>定义登录批次常驻应用固定启动计划的持久化边界。</summary>
public interface IResidentLaunchSessionStore
{
    /// <summary>加载上次持久化的登录批次计划；文件不存在时返回空值。</summary>
    /// <param name="cancellationToken">取消加载操作的令牌。</param>
    /// <returns>已验证的固定计划，或不存在时的空值。</returns>
    Task<ResidentLaunchSession?> LoadAsync(CancellationToken cancellationToken);

    /// <summary>原子保存一个完整固定计划。</summary>
    /// <param name="session">要保存的登录批次计划。</param>
    /// <param name="cancellationToken">取消保存操作的令牌。</param>
    /// <returns>保存完成的任务。</returns>
    Task SaveAsync(ResidentLaunchSession session, CancellationToken cancellationToken);

    /// <summary>保全损坏会话证据后，写入当前登录的已完成空计划。</summary>
    /// <param name="currentLogonSessionId">当前登录会话的稳定身份。</param>
    /// <param name="cancellationToken">取消恢复操作的令牌。</param>
    /// <returns>表明已恢复或严格拒绝覆盖故障证据的结果。</returns>
    Task<ResidentLaunchRecoveryResult> RecoverCorruptAsync(
        string currentLogonSessionId,
        CancellationToken cancellationToken);
}
