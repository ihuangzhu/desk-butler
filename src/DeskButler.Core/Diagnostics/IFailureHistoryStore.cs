using DeskButler.Core.Restore;

namespace DeskButler.Core.Diagnostics;

/// <summary>持久化既有恢复结果所派生的连续失败历史。</summary>
public interface IFailureHistoryStore
{
    /// <summary>读取供恢复规划器直接使用的失败历史快照。</summary>
    Task<FailureHistory> LoadAsync(CancellationToken cancellationToken);

    /// <summary>用一次既有恢复结果更新连续失败次数，失败计数最多保留三次。</summary>
    Task RecordAsync(RestoreResult result, CancellationToken cancellationToken);
}
