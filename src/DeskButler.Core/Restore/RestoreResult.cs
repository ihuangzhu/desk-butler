using System.Collections.Immutable;

namespace DeskButler.Core.Restore;

/// <summary>表示一个恢复项目的最终状态。</summary>
public enum RestoreItemStatus
{
    /// <summary>启动或复用窗口并完成定位。</summary>
    Succeeded,

    /// <summary>严格保留计划中的跳过决定。</summary>
    Skipped,

    /// <summary>当前项目启动、等待或定位失败。</summary>
    Failed,

    /// <summary>调用方取消时当前或尚未开始的项目。</summary>
    Cancelled
}

/// <summary>保存一个场景项目的恢复结果。</summary>
/// <param name="SceneItemId">场景项目稳定标识。</param>
/// <param name="Status">项目最终状态。</param>
/// <param name="ErrorMessage">失败时的本地错误摘要。</param>
public sealed record RestoreItemResult(
    string SceneItemId,
    RestoreItemStatus Status,
    string? ErrorMessage = null);

/// <summary>保存按计划顺序排列的不可变恢复结果。</summary>
public sealed record RestoreResult
{
    /// <summary>复制每项结果，避免调用方后续修改。</summary>
    /// <param name="items">按恢复计划顺序排列的结果。</param>
    public RestoreResult(IEnumerable<RestoreItemResult> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items.ToImmutableArray();
        var duplicateId = Items
            .GroupBy(item => item.SceneItemId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)
            ?.Key;
        if (duplicateId is not null)
        {
            throw new ArgumentException(
                $"恢复结果包含重复 SceneItemId：{duplicateId}", nameof(items));
        }
    }

    /// <summary>获取不可变项目结果。</summary>
    public ImmutableArray<RestoreItemResult> Items { get; }

    /// <summary>按稳定场景项目标识读取唯一结果。</summary>
    /// <param name="sceneItemId">要查找的场景项目标识。</param>
    public RestoreItemResult Item(string sceneItemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneItemId);
        return Items.Single(item => StringComparer.Ordinal.Equals(item.SceneItemId, sceneItemId));
    }
}
