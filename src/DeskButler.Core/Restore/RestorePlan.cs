using System.Collections.Immutable;

namespace DeskButler.Core.Restore;

/// <summary>表示选定场景生成的不可变恢复计划。</summary>
public sealed record RestorePlan
{
    /// <summary>复制调用方项目集合并创建不可变计划。</summary>
    /// <param name="items">按原场景顺序排列的计划项目。</param>
    public RestorePlan(IEnumerable<RestorePlanItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items.ToImmutableArray();
    }

    /// <summary>获取按原场景顺序排列的不可变计划项目。</summary>
    public ImmutableArray<RestorePlanItem> Items { get; }
}
