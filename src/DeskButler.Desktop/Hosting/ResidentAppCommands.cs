using DeskButler.Application.Commands;
using DeskButler.Core.ResidentApps;
using DeskButler.Core.Settings;
using DeskButler.Modules.WorkspaceRecovery.Capture;
using System.IO;

namespace DeskButler.Desktop.Hosting;

/// <summary>返回用户主动保存的窗口捕获结果和常驻候选发现批次。</summary>
/// <param name="Capture">窗口现场保存结果。</param>
/// <param name="Discovery">随后独立完成的常驻候选发现结果。</param>
public sealed record ManualSaveResult(
    CaptureOutcome Capture,
    ResidentDiscoveryBatch Discovery);

/// <summary>请求不保存现场、直接查找常驻应用候选。</summary>
public sealed record FindResidentCandidatesCommand : ICommand<ResidentDiscoveryBatch>;

/// <summary>请求确认指定候选代次中的 UI 选择。</summary>
/// <param name="Generation">UI 捕获的候选代次。</param>
/// <param name="Selections">仅包含允许 UI 回传字段的选择快照。</param>
public sealed record ConfirmResidentCandidatesCommand(
    long Generation,
    IReadOnlyList<ResidentCandidateSelection> Selections) : ICommand<bool>;

/// <summary>请求本次忽略指定候选代次。</summary>
/// <param name="Generation">UI 捕获的候选代次。</param>
public sealed record DismissResidentCandidatesCommand(long Generation) : ICommand<bool>;

/// <summary>表示常驻设置编辑可安全显示给 UI 的稳定错误分类。</summary>
public enum ResidentSettingsError
{
    /// <summary>操作成功或无需变更。</summary>
    None,

    /// <summary>启动入口未通过统一的可执行文件安全策略。</summary>
    ExecutablePathRejected,

    /// <summary>启动入口与已有条目重复。</summary>
    DuplicateLaunchPath,

    /// <summary>识别路径与另一条目冲突。</summary>
    KnownProcessPathConflict,

    /// <summary>请求的条目不存在。</summary>
    EntryNotFound,

    /// <summary>移动偏移不是相邻的 -1 或 +1。</summary>
    InvalidMoveOffset
}

/// <summary>请求设置常驻应用总开关。</summary>
public sealed record SetResidentApplicationsEnabledCommand(bool IsEnabled) : ICommand<ResidentSettingsMutationResult>;

/// <summary>请求启用或停用一个常驻应用条目。</summary>
public sealed record SetResidentApplicationEnabledCommand(string LaunchPath, bool IsEnabled)
    : ICommand<ResidentSettingsMutationResult>;

/// <summary>请求删除一个常驻应用条目。</summary>
public sealed record RemoveResidentApplicationCommand(string LaunchPath) : ICommand<ResidentSettingsMutationResult>;

/// <summary>请求把一个条目向前或向后移动一个位置。</summary>
public sealed record MoveResidentApplicationCommand(string LaunchPath, int Offset)
    : ICommand<ResidentSettingsMutationResult>;

/// <summary>请求通过浏览器选择新增一个常驻应用。</summary>
public sealed record AddResidentApplicationCommand(string LaunchPath, string? DisplayName)
    : ICommand<ResidentSettingsMutationResult>;

/// <summary>请求将已有条目的启动入口替换为新的受验证入口。</summary>
public sealed record ReplaceResidentApplicationPathCommand(string OldLaunchPath, string NewLaunchPath)
    : ICommand<ResidentSettingsMutationResult>;

/// <summary>保存一次常驻设置编辑后的完整 UI 快照，UI 不需要解析异常文字。</summary>
public sealed record ResidentSettingsMutationResult(
    bool Changed,
    ResidentSettingsError Error,
    IReadOnlyList<ResidentApplication> Applications,
    bool ResidentApplicationsEnabled);

/// <summary>为所有常驻列表写命令提供唯一的 SettingsCoordinator 原子提交边界。</summary>
internal abstract class ResidentSettingsCommandHandlerBase
{
    private readonly SettingsCoordinator settings;

    /// <summary>使用生产对象图中的共享设置协调器构造处理器。</summary>
    protected ResidentSettingsCommandHandlerBase(SettingsCoordinator settings)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>供组合根测试确认所有列表命令确实复用同一协调器。</summary>
    internal SettingsCoordinator SettingsCoordinator => settings;

    /// <summary>在单个设置事务中决定变更和错误，并从 UpdateAsync 的最新结果投影 UI 快照。</summary>
    protected async Task<ResidentSettingsMutationResult> MutateAsync(
        Func<ButlerSettings, ResidentSettingsMutation> mutate,
        CancellationToken cancellationToken)
    {
        ResidentSettingsMutation? decision = null;
        var persisted = await settings.UpdateAsync(
            current =>
            {
                decision = mutate(current);
                return decision.Settings;
            },
            cancellationToken).ConfigureAwait(false);

        return new ResidentSettingsMutationResult(
            decision!.Changed,
            decision.Error,
            persisted.ResidentApplications,
            persisted.ResidentApplicationsEnabled);
    }

    /// <summary>把集合重新交给现有正规化器，避免移动、删除和添加留下非连续顺序或路径冲突。</summary>
    protected static ResidentSettingsMutation Normalize(
        ButlerSettings current,
        IReadOnlyList<ResidentApplication> applications)
    {
        var normalized = ResidentApplicationNormalizer.Normalize(applications);
        if (normalized.Diagnostics.Count != 0 || normalized.Applications.Count != applications.Count)
        {
            return new(current, false, MapNormalizationError(normalized));
        }

        return new(current with { ResidentApplications = normalized.Applications }, true, ResidentSettingsError.None);
    }

    /// <summary>将正规化器的内部诊断映射为 UI 可消费的稳定错误枚举。</summary>
    private static ResidentSettingsError MapNormalizationError(ResidentNormalizationResult normalized) =>
        normalized.Diagnostics.Any(diagnostic => diagnostic.Kind == ResidentNormalizationIssue.DuplicateLaunchPath)
            ? ResidentSettingsError.DuplicateLaunchPath
            : normalized.Diagnostics.Any(diagnostic => diagnostic.Kind is ResidentNormalizationIssue.KnownPathConflict or ResidentNormalizationIssue.LaunchPathConflict)
                ? ResidentSettingsError.KnownProcessPathConflict
                : ResidentSettingsError.ExecutablePathRejected;

    /// <summary>用不抛出路径异常的比较查找已持久化条目。</summary>
    protected static int FindIndex(IReadOnlyList<ResidentApplication> applications, string launchPath)
    {
        if (!TryNormalizePath(launchPath, out var normalizedPath))
        {
            return -1;
        }

        for (var index = 0; index < applications.Count; index++)
        {
            if (TryNormalizePath(applications[index].LaunchPath, out var existingPath) &&
                StringComparer.OrdinalIgnoreCase.Equals(existingPath, normalizedPath))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>正规化人工输入路径；无效输入仅作为找不到条目处理，不能把异常泄漏给 UI。</summary>
    private static bool TryNormalizePath(string path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>保存一次同步的设置变更决定，供原子回调返回新的不可变设置。</summary>
    protected sealed record ResidentSettingsMutation(
        ButlerSettings Settings,
        bool Changed,
        ResidentSettingsError Error);
}

/// <summary>处理总开关；它刻意只改变总开关字段。</summary>
internal sealed class SetResidentApplicationsEnabledCommandHandler(SettingsCoordinator settings)
    : ResidentSettingsCommandHandlerBase(settings),
        ICommandHandler<SetResidentApplicationsEnabledCommand, ResidentSettingsMutationResult>
{
    /// <inheritdoc />
    public Task<ResidentSettingsMutationResult> HandleAsync(
        SetResidentApplicationsEnabledCommand command,
        CancellationToken cancellationToken) =>
        MutateAsync(
            current => current.ResidentApplicationsEnabled == command.IsEnabled
                ? new(current, false, ResidentSettingsError.None)
                : new(current with { ResidentApplicationsEnabled = command.IsEnabled }, true, ResidentSettingsError.None),
            cancellationToken);
}

/// <summary>处理单条启停，并在启用时再次执行入口安全验证和已启用识别路径冲突检查。</summary>
internal sealed class SetResidentApplicationEnabledCommandHandler(
    SettingsCoordinator settings,
    IResidentExecutablePolicy executablePolicy)
    : ResidentSettingsCommandHandlerBase(settings),
        ICommandHandler<SetResidentApplicationEnabledCommand, ResidentSettingsMutationResult>
{
    private readonly IResidentExecutablePolicy executablePolicy = executablePolicy ??
        throw new ArgumentNullException(nameof(executablePolicy));

    /// <inheritdoc />
    public Task<ResidentSettingsMutationResult> HandleAsync(
        SetResidentApplicationEnabledCommand command,
        CancellationToken cancellationToken) =>
        MutateAsync(current => Mutate(current, command), cancellationToken);

    /// <summary>启用先验证调用方提供的入口，再依据策略正规化路径定位条目，避免绕过最终路径安全检查。</summary>
    private ResidentSettingsMutation Mutate(ButlerSettings current, SetResidentApplicationEnabledCommand command)
    {
        var path = command.LaunchPath;
        if (command.IsEnabled)
        {
            var validation = executablePolicy.Validate(command.LaunchPath);
            if (!validation.IsAllowed || string.IsNullOrWhiteSpace(validation.NormalizedPath))
            {
                return new(current, false, ResidentSettingsError.ExecutablePathRejected);
            }

            path = validation.NormalizedPath;
        }

        var index = FindIndex(current.ResidentApplications, path);
        if (index < 0)
        {
            return new(current, false, ResidentSettingsError.EntryNotFound);
        }

        var target = current.ResidentApplications[index];
        if (command.IsEnabled && current.ResidentApplications
            .Where((_, candidateIndex) => candidateIndex != index && current.ResidentApplications[candidateIndex].Enabled)
            .Any(other => target.KnownProcessPaths.Overlaps(other.KnownProcessPaths)))
        {
            // 只比较已启用条目：停用项可保留历史识别信息，直到用户显式再次启用。
            return new(current, false, ResidentSettingsError.KnownProcessPathConflict);
        }

        if (target.Enabled == command.IsEnabled)
        {
            return new(current, false, ResidentSettingsError.None);
        }

        var applications = current.ResidentApplications.ToArray();
        applications[index] = target with { Enabled = command.IsEnabled };
        // 启停只改变目标字段；不得用全表正规化器把停用项的历史冲突重新解释为本次错误。
        return new(current with { ResidentApplications = applications }, true, ResidentSettingsError.None);
    }
}

/// <summary>处理删除；找不到目标按幂等 no-op 返回当前快照。</summary>
internal sealed class RemoveResidentApplicationCommandHandler(SettingsCoordinator settings)
    : ResidentSettingsCommandHandlerBase(settings),
        ICommandHandler<RemoveResidentApplicationCommand, ResidentSettingsMutationResult>
{
    /// <inheritdoc />
    public Task<ResidentSettingsMutationResult> HandleAsync(
        RemoveResidentApplicationCommand command,
        CancellationToken cancellationToken) =>
        MutateAsync(current =>
        {
            var index = FindIndex(current.ResidentApplications, command.LaunchPath);
            if (index < 0)
            {
                return new(current, false, ResidentSettingsError.None);
            }

            var applications = current.ResidentApplications.Where((_, candidateIndex) => candidateIndex != index).ToArray();
            return Normalize(current, applications);
        }, cancellationToken);
}

/// <summary>处理相邻移动，并保持其余项相对顺序稳定。</summary>
internal sealed class MoveResidentApplicationCommandHandler(SettingsCoordinator settings)
    : ResidentSettingsCommandHandlerBase(settings),
        ICommandHandler<MoveResidentApplicationCommand, ResidentSettingsMutationResult>
{
    /// <inheritdoc />
    public Task<ResidentSettingsMutationResult> HandleAsync(
        MoveResidentApplicationCommand command,
        CancellationToken cancellationToken) =>
        MutateAsync(current =>
        {
            if (command.Offset is not -1 and not 1)
            {
                return new(current, false, ResidentSettingsError.InvalidMoveOffset);
            }

            var index = FindIndex(current.ResidentApplications, command.LaunchPath);
            if (index < 0)
            {
                return new(current, false, ResidentSettingsError.EntryNotFound);
            }

            var destination = index + command.Offset;
            if (destination < 0 || destination >= current.ResidentApplications.Count)
            {
                return new(current, false, ResidentSettingsError.None);
            }

            var applications = current.ResidentApplications.ToList();
            (applications[index], applications[destination]) = (applications[destination], applications[index]);
            // 交换后先按用户可见的新顺序重编号，随后正规化器只负责验证并守住连续编号不变量。
            var renumbered = applications
                .Select((application, launchOrder) => application with { LaunchOrder = launchOrder })
                .ToArray();
            return Normalize(current, renumbered);
        }, cancellationToken);
}

/// <summary>通过统一安全策略验证后新增一个只含启动入口的常驻条目。</summary>
internal sealed class AddResidentApplicationCommandHandler(
    SettingsCoordinator settings,
    IResidentExecutablePolicy executablePolicy)
    : ResidentSettingsCommandHandlerBase(settings),
        ICommandHandler<AddResidentApplicationCommand, ResidentSettingsMutationResult>
{
    private readonly IResidentExecutablePolicy executablePolicy = executablePolicy ??
        throw new ArgumentNullException(nameof(executablePolicy));

    /// <inheritdoc />
    public Task<ResidentSettingsMutationResult> HandleAsync(
        AddResidentApplicationCommand command,
        CancellationToken cancellationToken) =>
        MutateAsync(current =>
        {
            var validation = executablePolicy.Validate(command.LaunchPath);
            if (!validation.IsAllowed || string.IsNullOrWhiteSpace(validation.NormalizedPath))
            {
                return new(current, false, ResidentSettingsError.ExecutablePathRejected);
            }

            var applications = current.ResidentApplications.Append(new ResidentApplication(
                validation.NormalizedPath,
                new HashSet<string>([validation.NormalizedPath], StringComparer.OrdinalIgnoreCase),
                command.DisplayName ?? string.Empty,
                true,
                current.ResidentApplications.Count)).ToArray();
            return Normalize(current, applications);
        }, cancellationToken);
}

/// <summary>替换入口时保留用户管理字段，并清除只能由后续发现重新确认的旧识别路径。</summary>
internal sealed class ReplaceResidentApplicationPathCommandHandler(
    SettingsCoordinator settings,
    IResidentExecutablePolicy executablePolicy)
    : ResidentSettingsCommandHandlerBase(settings),
        ICommandHandler<ReplaceResidentApplicationPathCommand, ResidentSettingsMutationResult>
{
    private readonly IResidentExecutablePolicy executablePolicy = executablePolicy ??
        throw new ArgumentNullException(nameof(executablePolicy));

    /// <inheritdoc />
    public Task<ResidentSettingsMutationResult> HandleAsync(
        ReplaceResidentApplicationPathCommand command,
        CancellationToken cancellationToken) =>
        MutateAsync(current =>
        {
            var validation = executablePolicy.Validate(command.NewLaunchPath);
            if (!validation.IsAllowed || string.IsNullOrWhiteSpace(validation.NormalizedPath))
            {
                return new(current, false, ResidentSettingsError.ExecutablePathRejected);
            }

            var index = FindIndex(current.ResidentApplications, command.OldLaunchPath);
            if (index < 0)
            {
                return new(current, false, ResidentSettingsError.EntryNotFound);
            }

            var target = current.ResidentApplications[index];
            if (StringComparer.OrdinalIgnoreCase.Equals(target.LaunchPath, validation.NormalizedPath))
            {
                return new(current, false, ResidentSettingsError.None);
            }

            var applications = current.ResidentApplications.ToArray();
            applications[index] = target with
            {
                LaunchPath = validation.NormalizedPath,
                // 路径替换不能继承旧发现结论；只信任刚刚通过策略验证的新入口。
                KnownProcessPaths = new HashSet<string>([validation.NormalizedPath], StringComparer.OrdinalIgnoreCase)
            };
            return Normalize(current, applications);
        }, cancellationToken);
}

/// <summary>把独立查找命令路由到共享候选协调器。</summary>
internal sealed class FindResidentCandidatesCommandHandler(ResidentCandidateCoordinator coordinator)
    : ICommandHandler<FindResidentCandidatesCommand, ResidentDiscoveryBatch>
{
    /// <inheritdoc />
    public Task<ResidentDiscoveryBatch> HandleAsync(
        FindResidentCandidatesCommand command,
        CancellationToken cancellationToken) =>
        coordinator.DiscoverAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            cancellationToken);

    /// <summary>供组合根验证查找命令与其它常驻命令共享同一设置门。</summary>
    internal SettingsCoordinator SettingsCoordinator => coordinator.SettingsCoordinator;
}

/// <summary>把候选确认命令路由到共享候选协调器。</summary>
internal sealed class ConfirmResidentCandidatesCommandHandler(ResidentCandidateCoordinator coordinator)
    : ICommandHandler<ConfirmResidentCandidatesCommand, bool>
{
    /// <inheritdoc />
    public Task<bool> HandleAsync(
        ConfirmResidentCandidatesCommand command,
        CancellationToken cancellationToken) =>
        coordinator.ConfirmAsync(command.Generation, command.Selections, cancellationToken);

    /// <summary>供组合根测试验证候选确认仍使用共享设置协调器。</summary>
    internal SettingsCoordinator SettingsCoordinator => coordinator.SettingsCoordinator;
}

/// <summary>把本次忽略命令路由到共享候选协调器。</summary>
internal sealed class DismissResidentCandidatesCommandHandler(ResidentCandidateCoordinator coordinator)
    : ICommandHandler<DismissResidentCandidatesCommand, bool>
{
    /// <inheritdoc />
    public Task<bool> HandleAsync(
        DismissResidentCandidatesCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(coordinator.Dismiss(command.Generation));

    /// <summary>供组合根测试验证候选命令仍使用共享设置协调器。</summary>
    internal SettingsCoordinator SettingsCoordinator => coordinator.SettingsCoordinator;
}

/// <summary>集中创建并注册候选与列表命令，避免生产对象图意外分裂出第二个设置协调器。</summary>
internal sealed class ResidentAppCommandHandlerSet
{
    private ResidentAppCommandHandlerSet(
        FindResidentCandidatesCommandHandler find,
        ConfirmResidentCandidatesCommandHandler confirm,
        DismissResidentCandidatesCommandHandler dismiss,
        SetResidentApplicationsEnabledCommandHandler setApplicationsEnabled,
        SetResidentApplicationEnabledCommandHandler setApplicationEnabled,
        RemoveResidentApplicationCommandHandler remove,
        MoveResidentApplicationCommandHandler move,
        AddResidentApplicationCommandHandler add,
        ReplaceResidentApplicationPathCommandHandler replace)
    {
        Find = find;
        Confirm = confirm;
        Dismiss = dismiss;
        SetApplicationsEnabled = setApplicationsEnabled;
        SetApplicationEnabled = setApplicationEnabled;
        Remove = remove;
        Move = move;
        Add = add;
        Replace = replace;
    }

    internal FindResidentCandidatesCommandHandler Find { get; }
    internal ConfirmResidentCandidatesCommandHandler Confirm { get; }
    internal DismissResidentCandidatesCommandHandler Dismiss { get; }
    internal SetResidentApplicationsEnabledCommandHandler SetApplicationsEnabled { get; }
    internal SetResidentApplicationEnabledCommandHandler SetApplicationEnabled { get; }
    internal RemoveResidentApplicationCommandHandler Remove { get; }
    internal MoveResidentApplicationCommandHandler Move { get; }
    internal AddResidentApplicationCommandHandler Add { get; }
    internal ReplaceResidentApplicationPathCommandHandler Replace { get; }

    /// <summary>从同一生产 SettingsCoordinator 创建所有常驻命令处理器，并在构造时阻止错误对象图。</summary>
    internal static ResidentAppCommandHandlerSet Create(
        ResidentCandidateCoordinator candidates,
        SettingsCoordinator settings,
        IResidentExecutablePolicy executablePolicy)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(executablePolicy);
        if (!ReferenceEquals(candidates.SettingsCoordinator, settings))
        {
            throw new InvalidOperationException("常驻候选确认与列表命令必须共享同一设置协调器。");
        }

        return new(
            new FindResidentCandidatesCommandHandler(candidates),
            new ConfirmResidentCandidatesCommandHandler(candidates),
            new DismissResidentCandidatesCommandHandler(candidates),
            new SetResidentApplicationsEnabledCommandHandler(settings),
            new SetResidentApplicationEnabledCommandHandler(settings, executablePolicy),
            new RemoveResidentApplicationCommandHandler(settings),
            new MoveResidentApplicationCommandHandler(settings),
            new AddResidentApplicationCommandHandler(settings, executablePolicy),
            new ReplaceResidentApplicationPathCommandHandler(settings, executablePolicy));
    }

    /// <summary>将完整命令集注册到唯一生产 InProcessCommandBus，供 UI 只通过类型化命令访问。</summary>
    internal void Register(InProcessCommandBus commandBus)
    {
        ArgumentNullException.ThrowIfNull(commandBus);
        commandBus.Register(Find);
        commandBus.Register(Confirm);
        commandBus.Register(Dismiss);
        commandBus.Register(SetApplicationsEnabled);
        commandBus.Register(SetApplicationEnabled);
        commandBus.Register(Remove);
        commandBus.Register(Move);
        commandBus.Register(Add);
        commandBus.Register(Replace);
    }
}
