using DeskButler.Core.Scenes;

namespace DeskButler.Core.Persistence;

/// <summary>定义场景快照的持久化边界。</summary>
public interface ISceneRepository
{
    /// <summary>原子保存快照，并仅保留最近三份有效快照。</summary>
    /// <param name="snapshot">要保存的完整场景快照。</param>
    /// <param name="cancellationToken">用于取消持久化操作的令牌。</param>
    /// <returns>保存完成的任务。</returns>
    Task SaveAsync(SceneSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>按捕获时间倒序读取最多指定数量的有效快照。</summary>
    /// <param name="maximumCount">最多返回的快照数量。</param>
    /// <param name="cancellationToken">用于取消读取操作的令牌。</param>
    /// <returns>按最新优先排列的有效场景快照。</returns>
    Task<IReadOnlyList<SceneSnapshot>> GetRecentAsync(int maximumCount, CancellationToken cancellationToken);

    /// <summary>保留指定快照的原始数据并将其标记为不可恢复。</summary>
    /// <param name="snapshotId">要标记的快照标识。</param>
    /// <param name="reason">快照不可恢复的原因。</param>
    /// <param name="cancellationToken">用于取消更新操作的令牌。</param>
    /// <returns>标记完成的任务。</returns>
    Task MarkInvalidAsync(Guid snapshotId, string reason, CancellationToken cancellationToken);
}
