using System.Collections.ObjectModel;

namespace DeskButler.Core.Diagnostics;

/// <summary>表示各场景项目连续恢复失败次数的不可变快照。</summary>
public sealed record FailureHistory
{
    /// <summary>复制连续失败次数字典，避免调用方后续修改规划依据。</summary>
    /// <param name="consecutiveFailures">以稳定场景项目标识为键的连续失败次数。</param>
    public FailureHistory(IReadOnlyDictionary<string, int> consecutiveFailures)
    {
        ArgumentNullException.ThrowIfNull(consecutiveFailures);
        ConsecutiveFailures = new ReadOnlyDictionary<string, int>(
            new Dictionary<string, int>(consecutiveFailures, StringComparer.Ordinal));
    }

    /// <summary>获取没有失败记录的共享历史快照。</summary>
    public static FailureHistory Empty { get; } = new(new Dictionary<string, int>());

    /// <summary>获取只读的连续失败次数字典。</summary>
    public IReadOnlyDictionary<string, int> ConsecutiveFailures { get; }

    /// <summary>查询指定场景项目的连续失败次数。</summary>
    /// <param name="sceneItemId">稳定场景项目标识。</param>
    /// <returns>已记录的失败次数；没有记录时为零。</returns>
    public int CountFor(string sceneItemId)
    {
        ArgumentNullException.ThrowIfNull(sceneItemId);
        return ConsecutiveFailures.GetValueOrDefault(sceneItemId);
    }
}
