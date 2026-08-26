using System.Collections.Immutable;
using DeskButler.Core.ResidentApps;

namespace DeskButler.Infrastructure.Windows.ResidentApps;

/// <summary>保存进程拥有的顶层窗口分类，不包含标题、类名或窗口内容。</summary>
internal sealed record ResidentWindowTraits(
    bool HasVisibleTopLevelWindow,
    bool HasHiddenTopLevelWindow,
    bool HasOwnedTopLevelWindow,
    bool HasToolWindow,
    bool HasCloakedWindow)
{
    /// <summary>创建未观察到顶层窗口的分类。</summary>
    internal static ResidentWindowTraits None { get; } = new(false, false, false, false, false);

    /// <summary>把单个顶层窗口的公开分类合并到进程聚合分类。</summary>
    internal ResidentWindowTraits Include(ResidentTopLevelWindow window) => new(
        HasVisibleTopLevelWindow || window.IsVisible,
        HasHiddenTopLevelWindow || !window.IsVisible,
        HasOwnedTopLevelWindow || window.IsOwned,
        HasToolWindow || window.IsToolWindow,
        HasCloakedWindow || window.IsCloaked);
}

/// <summary>保存可安全供后续候选发现使用的单进程公开观察信息。</summary>
internal sealed record ResidentProcessObservation(
    int ProcessId,
    string ExecutablePath,
    string? ProductName,
    string? CompanyName,
    string? FileDescription,
    ResidentWindowTraits WindowTraits);

/// <summary>保存一次进程观察的不可变、稳定排序结果。</summary>
internal sealed record ResidentProcessSnapshot(
    ImmutableArray<ResidentProcessObservation> Observations,
    ImmutableArray<ResidentDiscoveryDiagnostic> Diagnostics);

/// <summary>保存注册表卸载项中允许读取的公开显示字段。</summary>
internal sealed record InstalledApplicationEntry(
    string DisplayName,
    string? Publisher,
    string? InstallRoot,
    string? DisplayIconPath);

/// <summary>保存一次已安装应用目录读取的不可变、稳定排序结果。</summary>
internal sealed record InstalledApplicationSnapshot(
    ImmutableArray<InstalledApplicationEntry> Entries,
    ImmutableArray<ResidentDiscoveryDiagnostic> Diagnostics);
