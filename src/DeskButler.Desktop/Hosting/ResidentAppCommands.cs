using DeskButler.Application.Commands;
using DeskButler.Modules.WorkspaceRecovery.Capture;

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
}
