using DeskButler.Application.Commands;
using DeskButler.Core.Capture;
using DeskButler.Core.Diagnostics;
using DeskButler.Core.Persistence;
using DeskButler.Core.Restore;
using DeskButler.Core.Scenes;
using DeskButler.Core.Settings;
using DeskButler.Modules.WorkspaceRecovery.Capture;
using DeskButler.Modules.WorkspaceRecovery.Restore;
using System.IO;

namespace DeskButler.Desktop.Hosting;

/// <summary>请求立即保存当前工作现场。</summary>
public sealed record SaveSceneNowCommand : ICommand<bool>;

/// <summary>请求恢复用户明确选中的场景项目。</summary>
public sealed record RestoreSceneCommand(
    SceneSnapshot Scene,
    IReadOnlyList<string> SelectedItemIds,
    bool SafeMode) : ICommand<RestoreResult>;

/// <summary>请求持久化捕获启用状态。</summary>
public sealed record SetCaptureEnabledCommand(bool IsEnabled) : ICommand<bool>;

/// <summary>请求永久排除一个可执行文件路径。</summary>
public sealed record PersistExclusionCommand(string ExecutablePath) : ICommand<bool>;

/// <summary>串行修改不可变设置，避免两个 UI 操作互相覆盖。</summary>
public sealed class SettingsCoordinator : IDisposable
{
    private readonly ISettingsStore store;
    private readonly SemaphoreSlim mutationGate = new(1, 1);

    /// <summary>使用实际设置存储创建串行协调器。</summary>
    public SettingsCoordinator(ISettingsStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>以最新持久值为基础执行一次原子 UI 设置变更。</summary>
    public async Task<ButlerSettings> UpdateAsync(
        Func<ButlerSettings, ButlerSettings> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var updated = update(current);
            await store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            mutationGate.Release();
        }
    }

    /// <summary>释放设置变更串行门。</summary>
    public void Dispose()
    {
        mutationGate.Dispose();
    }
}

/// <summary>把“立即保存”命令连接到既有捕获协调器。</summary>
public sealed class SaveSceneNowCommandHandler(CaptureCoordinator coordinator)
    : ICommandHandler<SaveSceneNowCommand, bool>
{
    /// <inheritdoc />
    public async Task<bool> HandleAsync(SaveSceneNowCommand command, CancellationToken cancellationToken)
    {
        await coordinator.SaveNowAsync("manual", cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>把显式场景恢复命令连接到既有规划器和执行器。</summary>
public sealed class RestoreSceneCommandHandler(
    IWindowInventory inventory,
    IRestorePlanner planner,
    RestoreExecutor executor,
    ISettingsStore settingsStore) : ICommandHandler<RestoreSceneCommand, RestoreResult>
{
    /// <inheritdoc />
    public async Task<RestoreResult> HandleAsync(RestoreSceneCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var selectedIds = command.SelectedItemIds.ToHashSet(StringComparer.Ordinal);
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var excludedPaths = ExecutablePathExclusions.Create(settings.ExcludedExecutablePaths);
        var selectedScene = command.Scene with
        {
            Items = command.Scene.Items
                .Where(item => selectedIds.Contains(item.Id) && !ExecutablePathExclusions.ContainsOrInvalid(
                    item.ExecutablePath, excludedPaths))
                .ToArray()
        };
        var currentWindows = await inventory.CaptureAsync(cancellationToken).ConfigureAwait(false);
        var plan = planner.Build(selectedScene, currentWindows, FailureHistory.Empty, command.SafeMode);
        return await executor.ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);
    }

}

/// <summary>集中正规化捕获与恢复共用的永久排除路径语义。</summary>
internal static class ExecutablePathExclusions
{
    /// <summary>忽略损坏的设置路径并生成 Windows 大小写不敏感排除集合。</summary>
    internal static HashSet<string> Create(IEnumerable<string> paths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            try
            {
                result.Add(Path.GetFullPath(path));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // 单条人为损坏设置不应阻止其余恢复项目。
            }
        }

        return result;
    }

    /// <summary>将无效场景路径视为不可恢复，并判断有效路径是否已永久排除。</summary>
    internal static bool ContainsOrInvalid(string executablePath, HashSet<string> excludedPaths)
    {
        try
        {
            return excludedPaths.Contains(Path.GetFullPath(executablePath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }
}

/// <summary>持久化托盘的暂停或继续选择。</summary>
public sealed class SetCaptureEnabledCommandHandler : ICommandHandler<SetCaptureEnabledCommand, bool>
{
    private readonly SettingsCoordinator settings;

    /// <summary>使用独立设置存储创建处理器，适合小型宿主或测试。</summary>
    public SetCaptureEnabledCommandHandler(ISettingsStore store)
        : this(new SettingsCoordinator(store))
    {
    }

    /// <summary>使用共享串行设置协调器创建处理器。</summary>
    public SetCaptureEnabledCommandHandler(SettingsCoordinator settings)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <inheritdoc />
    public async Task<bool> HandleAsync(SetCaptureEnabledCommand command, CancellationToken cancellationToken)
    {
        await settings.UpdateAsync(
            current => current with { CaptureEnabled = command.IsEnabled }, cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>把用户选择的可执行路径并入永久排除集合。</summary>
public sealed class PersistExclusionCommandHandler : ICommandHandler<PersistExclusionCommand, bool>
{
    private readonly SettingsCoordinator settings;

    /// <summary>使用独立设置存储创建处理器，适合小型宿主或测试。</summary>
    public PersistExclusionCommandHandler(ISettingsStore store)
        : this(new SettingsCoordinator(store))
    {
    }

    /// <summary>使用共享串行设置协调器创建处理器。</summary>
    public PersistExclusionCommandHandler(SettingsCoordinator settings)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <inheritdoc />
    public async Task<bool> HandleAsync(PersistExclusionCommand command, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ExecutablePath);
        var fullPath = Path.GetFullPath(command.ExecutablePath);
        await settings.UpdateAsync(
            current => current with
            {
                ExcludedExecutablePaths = new HashSet<string>(
                    current.ExcludedExecutablePaths.Append(fullPath), StringComparer.OrdinalIgnoreCase)
            },
            cancellationToken).ConfigureAwait(false);
        return true;
    }
}
