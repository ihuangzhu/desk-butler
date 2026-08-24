using DeskButler.Core.Capture;
using DeskButler.Core.Settings;

namespace DeskButler.Desktop.Hosting;

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
