using DeskButler.Application.Commands;
using DeskButler.Core.Capture;
using DeskButler.Core.Diagnostics;
using DeskButler.Core.Persistence;
using DeskButler.Core.Restore;
using DeskButler.Core.Scenes;
using DeskButler.Core.Settings;
using DeskButler.Modules.WorkspaceRecovery.Capture;
using DeskButler.Modules.WorkspaceRecovery.Restore;
using DeskButler.Modules.WorkspaceRecovery;
using DeskButler.Infrastructure.Windows.Startup;
using System.IO;
using System.Runtime.ExceptionServices;

namespace DeskButler.Desktop.Hosting;

/// <summary>请求立即保存当前工作现场。</summary>
public sealed record SaveSceneNowCommand : ICommand<bool>;

/// <summary>请求恢复用户明确选中的场景项目。</summary>
public sealed record RestoreSceneCommand(
    SceneSnapshot Scene,
    IReadOnlyList<string> SelectedItemIds,
    bool SafeMode) : ICommand<RestoreResult>
{
    /// <summary>获取用户在卡片中明确重新勾选、仅允许覆盖连续失败保护的项目。</summary>
    public IReadOnlySet<string> ExplicitFailureRetryItemIds { get; init; } = new HashSet<string>();
}

/// <summary>请求持久化捕获启用状态。</summary>
public sealed record SetCaptureEnabledCommand(bool IsEnabled) : ICommand<bool>;

/// <summary>请求以可补偿事务切换当前用户登录启动。</summary>
public sealed record SetStartupEnabledCommand(bool IsEnabled) : ICommand<bool>;

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

    /// <summary>在设置串行门内同步提交登录启动注册与设置，并独立尝试全部补偿。</summary>
    public async Task<bool> SetStartupEnabledAsync(
        IStartupRegistration registration,
        bool enabled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var originalSettings = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var originalRegistration = registration.IsEnabled;
            try
            {
                SetRegistration(registration, enabled);
                await store.SaveAsync(
                    originalSettings with { StartupEnabled = enabled }, cancellationToken).ConfigureAwait(false);
                return registration.IsEnabled;
            }
            catch (Exception originalFailure)
            {
                var failures = new List<Exception> { originalFailure };
                try
                {
                    SetRegistration(registration, originalRegistration);
                }
                catch (Exception rollbackFailure)
                {
                    failures.Add(rollbackFailure);
                }

                try
                {
                    await store.SaveAsync(originalSettings, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackFailure)
                {
                    failures.Add(rollbackFailure);
                }

                if (failures.Count > 1)
                {
                    throw new AggregateException("登录启动设置失败且补偿未全部完成。", failures);
                }

                ExceptionDispatchInfo.Capture(originalFailure).Throw();
                throw new InvalidOperationException("无法重新抛出登录启动设置异常。");
            }
        }
        finally
        {
            mutationGate.Release();
        }
    }

    /// <summary>只调用指定的登录启动注册边界，不接触其他 Run 值。</summary>
    private static void SetRegistration(IStartupRegistration registration, bool enabled)
    {
        if (enabled)
        {
            registration.Enable();
        }
        else
        {
            registration.Disable();
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
public sealed class RestoreSceneCommandHandler : ICommandHandler<RestoreSceneCommand, RestoreResult>
{
    private readonly IWindowInventory inventory;
    private readonly IRestorePlanner planner;
    private readonly RestoreExecutor executor;
    private readonly ISettingsStore settingsStore;
    private readonly IFailureHistoryStore failureHistoryStore;
    private readonly IDiagnosticLog? diagnosticLog;
    private readonly TimeSpan persistenceTimeout;

    /// <summary>为兼容隔离宿主创建不持久化失败历史的恢复处理器。</summary>
    public RestoreSceneCommandHandler(
        IWindowInventory inventory,
        IRestorePlanner planner,
        RestoreExecutor executor,
        ISettingsStore settingsStore)
        : this(inventory, planner, executor, settingsStore, TransientFailureHistoryStore.Instance)
    {
    }

    /// <summary>使用真实失败历史读写边界创建生产恢复处理器。</summary>
    public RestoreSceneCommandHandler(
        IWindowInventory inventory,
        IRestorePlanner planner,
        RestoreExecutor executor,
        ISettingsStore settingsStore,
        IFailureHistoryStore failureHistoryStore)
        : this(inventory, planner, executor, settingsStore, failureHistoryStore, null)
    {
    }

    /// <summary>使用失败历史与诊断日志创建生产恢复处理器。</summary>
    public RestoreSceneCommandHandler(
        IWindowInventory inventory,
        IRestorePlanner planner,
        RestoreExecutor executor,
        ISettingsStore settingsStore,
        IFailureHistoryStore failureHistoryStore,
        IDiagnosticLog? diagnosticLog,
        TimeSpan? persistenceTimeout = null)
    {
        this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        this.failureHistoryStore = failureHistoryStore ?? throw new ArgumentNullException(nameof(failureHistoryStore));
        this.diagnosticLog = diagnosticLog;
        this.persistenceTimeout = persistenceTimeout ?? TimeSpan.FromSeconds(2);
    }

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
        var failureHistory = await failureHistoryStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var plan = planner.Build(
            selectedScene, currentWindows, failureHistory, command.SafeMode,
            command.ExplicitFailureRetryItemIds);
        var result = await executor.ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);
        try
        {
            // 恢复结果已经产生后不再受用户取消影响，否则成功/失败历史会永久丢失。
            using var persistenceCancellation = new CancellationTokenSource(persistenceTimeout);
            // 调用接口本身也可能在返回 Task 前阻塞，因此把调用置于线程池调度边界。
            var recordTask = Task.Run(
                () => failureHistoryStore.RecordAsync(result, persistenceCancellation.Token),
                CancellationToken.None);
            ObserveLateFailure(recordTask);
            await recordTask.WaitAsync(persistenceTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatalPersistenceFailure(exception))
        {
            await ReportHistoryFailureAsync(exception).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>尽力记录历史落库失败但不覆盖已经完成的恢复结果。</summary>
    private async Task ReportHistoryFailureAsync(Exception failure)
    {
        if (diagnosticLog is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(persistenceTimeout);
            var diagnosticEvent = new DiagnosticEvent(
                    DateTimeOffset.UtcNow, DiagnosticLevel.Warning, "failure-history",
                    "恢复已经完成，但失败历史未能持久化。",
                    new Dictionary<string, object?> { ["exceptionType"] = failure.GetType().FullName });
            var logTask = Task.Run(
                () => diagnosticLog.WriteAsync(diagnosticEvent, timeout.Token),
                CancellationToken.None);
            ObserveLateFailure(logTask);
            await logTask.WaitAsync(persistenceTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatalPersistenceFailure(exception))
        {
            // 诊断日志自身失败不能把已完成恢复改写成命令失败。
        }
    }

    /// <summary>观察超时后迟到任务的异常，避免后台持久化故障成为未观察异常。</summary>
    private static void ObserveLateFailure(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>识别不得由持久化降级边界吞掉的进程级致命故障。</summary>
    private static bool IsFatalPersistenceFailure(Exception exception) =>
        exception is OutOfMemoryException
            or AccessViolationException
            or StackOverflowException
            or ThreadAbortException
            or System.Runtime.InteropServices.SEHException;

    /// <summary>为旧构造入口提供仅进程内空历史，不影响生产组合的兼容实现。</summary>
    private sealed class TransientFailureHistoryStore : IFailureHistoryStore
    {
        internal static TransientFailureHistoryStore Instance { get; } = new();

        /// <inheritdoc />
        public Task<FailureHistory> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(FailureHistory.Empty);

        /// <inheritdoc />
        public Task RecordAsync(RestoreResult result, CancellationToken cancellationToken) => Task.CompletedTask;
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
    private readonly AutomaticCaptureGate? automaticCaptureGate;

    /// <summary>使用独立设置存储创建处理器，适合小型宿主或测试。</summary>
    public SetCaptureEnabledCommandHandler(ISettingsStore store)
        : this(new SettingsCoordinator(store))
    {
    }

    /// <summary>使用共享串行设置协调器创建处理器。</summary>
    public SetCaptureEnabledCommandHandler(SettingsCoordinator settings)
        : this(settings, null)
    {
    }

    /// <summary>使用共享设置协调器，并在用户明确继续时解除运行期安全门禁。</summary>
    public SetCaptureEnabledCommandHandler(
        SettingsCoordinator settings,
        AutomaticCaptureGate? automaticCaptureGate)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.automaticCaptureGate = automaticCaptureGate;
    }

    /// <inheritdoc />
    public async Task<bool> HandleAsync(SetCaptureEnabledCommand command, CancellationToken cancellationToken)
    {
        await settings.UpdateAsync(
            current => current with { CaptureEnabled = command.IsEnabled }, cancellationToken).ConfigureAwait(false);
        if (command.IsEnabled)
        {
            automaticCaptureGate?.Resume();
        }

        return true;
    }
}

/// <summary>同时维护 JSON 设置与唯一 HKCU Run 值，失败时恢复原状态。</summary>
public sealed class SetStartupEnabledCommandHandler : ICommandHandler<SetStartupEnabledCommand, bool>
{
    private readonly SettingsCoordinator settings;
    private readonly IStartupRegistration startupRegistration;

    /// <summary>使用共享设置协调器和当前用户启动注册边界创建处理器。</summary>
    public SetStartupEnabledCommandHandler(
        SettingsCoordinator settings,
        IStartupRegistration startupRegistration)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.startupRegistration = startupRegistration ?? throw new ArgumentNullException(nameof(startupRegistration));
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(SetStartupEnabledCommand command, CancellationToken cancellationToken) =>
        settings.SetStartupEnabledAsync(startupRegistration, command.IsEnabled, cancellationToken);
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
