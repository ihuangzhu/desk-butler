using DeskButler.Core.Capture;
using DeskButler.Core.Settings;

namespace DeskButler.Desktop.Hosting;

/// <summary>保存一次手动窗口观察及同次读取到的捕获开关。</summary>
/// <param name="CaptureEnabled">观察时最新设置中的捕获开关。</param>
/// <param name="Candidates">应用最新永久排除后的同批窗口候选。</param>
internal sealed record ManualWindowObservation(
    bool CaptureEnabled,
    IReadOnlyList<WindowCandidate> Candidates);

/// <summary>在捕获边界读取最新设置，使暂停和永久排除立即生效。</summary>
internal sealed class SettingsAwareWindowInventory(IWindowInventory inner, ISettingsStore settingsStore)
    : IWindowInventory
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<WindowCandidate>> CaptureAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.CaptureEnabled)
        {
            return [];
        }

        var excluded = ExecutablePathExclusions.Create(settings.ExcludedExecutablePaths);
        var candidates = await inner.CaptureAsync(cancellationToken).ConfigureAwait(false);
        return ApplyExclusions(candidates, excluded);
    }

    /// <summary>为用户主动保存读取一次最新设置，并无视暂停状态观察一次底层窗口。</summary>
    /// <param name="cancellationToken">取消设置读取与底层窗口枚举的令牌。</param>
    /// <returns>同次设置开关与已经应用永久排除的窗口批次。</returns>
    internal async Task<ManualWindowObservation> CaptureForManualAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var excluded = ExecutablePathExclusions.Create(settings.ExcludedExecutablePaths);
        var candidates = await inner.CaptureAsync(cancellationToken).ConfigureAwait(false);
        return new ManualWindowObservation(
            settings.CaptureEnabled,
            ApplyExclusions(candidates, excluded));
    }

    /// <summary>用同一次设置快照过滤一批候选，隔离损坏路径且不再次读取平台清单。</summary>
    private static WindowCandidate[] ApplyExclusions(
        IReadOnlyList<WindowCandidate> candidates,
        HashSet<string> excluded)
    {
        return candidates.Where(candidate =>
        {
            if (string.IsNullOrWhiteSpace(candidate.ExecutablePath))
            {
                return true;
            }

            return !ExecutablePathExclusions.ContainsOrInvalid(candidate.ExecutablePath, excluded);
        }).ToArray();
    }
}
