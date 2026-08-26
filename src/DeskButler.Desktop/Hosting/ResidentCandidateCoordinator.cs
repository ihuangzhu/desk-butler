using DeskButler.Core.ResidentApps;
using DeskButler.Core.Settings;
using System.Collections.Frozen;
using System.IO;

namespace DeskButler.Desktop.Hosting;

/// <summary>保存一次候选发现的单调代次、候选和系统级失败状态。</summary>
/// <param name="Generation">由协调器分配的单调发现代次。</param>
/// <param name="Candidates">本代次等待用户确认的候选快照。</param>
/// <param name="DiscoveryFailed">平台发现是否发生系统级故障。</param>
public sealed record ResidentDiscoveryBatch(
    long Generation,
    IReadOnlyList<ResidentAppCandidate> Candidates,
    bool DiscoveryFailed);

/// <summary>表示 UI 对单个候选唯一允许回传的选择字段。</summary>
/// <param name="CandidateId">发现器产生的候选稳定标识。</param>
/// <param name="FinalLaunchPath">用户最终确认或修正的启动路径。</param>
/// <param name="IsSelected">用户是否选择本次提交该候选。</param>
public sealed record ResidentCandidateSelection(
    string CandidateId,
    string? FinalLaunchPath,
    bool IsSelected);

/// <summary>协调 latest-wins 候选发现以及经共享设置门串行化的确认。</summary>
public sealed class ResidentCandidateCoordinator
{
    private readonly IResidentAppDiscovery discovery;
    private readonly ISettingsStore settingsStore;
    private readonly SettingsCoordinator settings;
    private readonly object stateSync = new();
    private long latestRequestedGeneration;
    private ResidentDiscoveryBatch current = new(0, [], false);

    /// <summary>使用只读设置存储和共享设置事务协调器创建候选协调器。</summary>
    public ResidentCandidateCoordinator(
        IResidentAppDiscovery discovery,
        ISettingsStore settingsStore,
        SettingsCoordinator settings)
    {
        this.discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>供组合根验证候选确认和列表编辑共用同一个设置事务协调器。</summary>
    internal SettingsCoordinator SettingsCoordinator => settings;

    /// <summary>获取当前已发布候选批次的不可变快照。</summary>
    public ResidentDiscoveryBatch Current
    {
        get
        {
            lock (stateSync)
            {
                return current;
            }
        }
    }

    /// <summary>在锁外完成设置读取与平台发现，并让调用方只观察实际发布的最新批次。</summary>
    public async Task<ResidentDiscoveryBatch> DiscoverAsync(
        IReadOnlySet<string> ordinaryWindowPaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ordinaryWindowPaths);
        var paths = new HashSet<string>(ordinaryWindowPaths, StringComparer.OrdinalIgnoreCase);
        long generation;
        lock (stateSync)
        {
            generation = checked(++latestRequestedGeneration);
        }

        ResidentDiscoveryBatch batch;
        try
        {
            var existing = (await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false))
                .ResidentApplications
                .ToArray();
            var discovered = await discovery.DiscoverAsync(paths, existing, cancellationToken).ConfigureAwait(false);
            var candidates = Array.AsReadOnly(discovered.Candidates.Select(CloneCandidate).ToArray());
            batch = new ResidentDiscoveryBatch(generation, candidates, false);
        }
        catch (Exception exception) when (IsRecoverableDiscoveryFailure(exception))
        {
            // 系统级发现故障只发布脱敏失败状态；不得回滚此前已独立完成的现场保存。
            batch = new ResidentDiscoveryBatch(generation, [], true);
        }

        lock (stateSync)
        {
            // 代次是否仍为最新、发布与返回必须共享同一线性化点；迟到调用返回当时 Current，锁内没有 await 或 I/O。
            if (generation == latestRequestedGeneration)
            {
                current = batch;
            }

            return current;
        }
    }

    /// <summary>深拷贝发现候选的集合字段，防止发布后外部引用改变确认时的可信元数据。</summary>
    private static ResidentAppCandidate CloneCandidate(ResidentAppCandidate candidate) =>
        candidate with
        {
            KnownProcessPaths = candidate.KnownProcessPaths.ToFrozenSet(StringComparer.OrdinalIgnoreCase)
        };

    /// <summary>识别可降级为发现失败状态、但不包括取消和进程级致命故障的异常。</summary>
    private static bool IsRecoverableDiscoveryFailure(Exception exception) =>
        exception is not OperationCanceledException
            and not OutOfMemoryException
            and not AccessViolationException
            and not StackOverflowException
            and not ThreadAbortException
            and not System.Runtime.InteropServices.SEHException;

    /// <summary>在共享设置门内线性化确认，并只在保存成功后清空同代候选。</summary>
    public async Task<bool> ConfirmAsync(
        long generation,
        IReadOnlyList<ResidentCandidateSelection> selections,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selections);
        var selectionSnapshot = selections
            .Select(selection => new ResidentCandidateSelection(
                selection.CandidateId,
                selection.FinalLaunchPath,
                selection.IsSelected))
            .ToArray();

        try
        {
            await settings.UpdateAsync(
                persisted => BuildConfirmedSettings(
                    persisted,
                    generation,
                    selectionSnapshot),
                cancellationToken).ConfigureAwait(false);
        }
        catch (CandidateConfirmationRejectedException)
        {
            return false;
        }
        catch (Exception exception) when (IsRecoverableConfirmationFailure(exception))
        {
            // 设置读取或保存失败时不清理同代候选，允许用户在事务门释放后安全重试。
            return false;
        }

        lock (stateSync)
        {
            // 新发现可在设置保存期间发布；成功保存只能清理其自身线性化的代次。
            if (current.Generation == generation)
            {
                current = current with { Candidates = [] };
            }
        }

        return true;
    }

    /// <summary>识别可转为确认失败结果、但不得吞掉取消和进程级致命故障的异常。</summary>
    private static bool IsRecoverableConfirmationFailure(Exception exception) =>
        exception is not OperationCanceledException
            and not OutOfMemoryException
            and not AccessViolationException
            and not StackOverflowException
            and not ThreadAbortException
            and not System.Runtime.InteropServices.SEHException;

    /// <summary>在“设置门 → stateSync”固定锁序下验证当前批次并构造新的设置值。</summary>
    private ButlerSettings BuildConfirmedSettings(
        ButlerSettings persisted,
        long generation,
        IReadOnlyList<ResidentCandidateSelection> selections)
    {
        lock (stateSync)
        {
            // 同步 update 回调是确认线性化点；锁内不等待、不访问存储，也不信任 UI 提供候选元数据。
            if (current.Generation != generation)
            {
                throw new CandidateConfirmationRejectedException();
            }

            var candidatesById = new Dictionary<string, ResidentAppCandidate>(StringComparer.Ordinal);
            foreach (var candidate in current.Candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate.CandidateId) ||
                    !candidatesById.TryAdd(candidate.CandidateId, candidate))
                {
                    throw new CandidateConfirmationRejectedException();
                }
            }

            var selectedIds = new HashSet<string>(StringComparer.Ordinal);
            var applications = persisted.ResidentApplications.ToList();
            foreach (var selection in selections)
            {
                if (string.IsNullOrWhiteSpace(selection.CandidateId) ||
                    !selectedIds.Add(selection.CandidateId) ||
                    !candidatesById.TryGetValue(selection.CandidateId, out var candidate))
                {
                    throw new CandidateConfirmationRejectedException();
                }

                if (!selection.IsSelected)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(selection.FinalLaunchPath))
                {
                    throw new CandidateConfirmationRejectedException();
                }

                var knownPaths = new HashSet<string>(candidate.KnownProcessPaths, StringComparer.OrdinalIgnoreCase)
                {
                    selection.FinalLaunchPath
                };
                if (candidate.Kind == ResidentCandidateKind.NewApplication)
                {
                    applications.Add(new ResidentApplication(
                        selection.FinalLaunchPath,
                        knownPaths,
                        candidate.DisplayName,
                        true,
                        applications.Count));
                    continue;
                }

                if (candidate.Kind != ResidentCandidateKind.PathReplacement ||
                    string.IsNullOrWhiteSpace(candidate.ReplacesLaunchPath))
                {
                    throw new CandidateConfirmationRejectedException();
                }

                var replacementIndex = applications.FindIndex(
                    application => PathsEqual(application.LaunchPath, candidate.ReplacesLaunchPath));
                if (replacementIndex < 0)
                {
                    throw new CandidateConfirmationRejectedException();
                }

                var replaced = applications[replacementIndex];
                applications[replacementIndex] = replaced with
                {
                    LaunchPath = selection.FinalLaunchPath,
                    KnownProcessPaths = knownPaths,
                    DisplayName = candidate.DisplayName
                };
            }

            var normalized = ResidentApplicationNormalizer.Normalize(applications);
            if (normalized.Diagnostics.Count != 0 || normalized.Applications.Count != applications.Count)
            {
                throw new CandidateConfirmationRejectedException();
            }

            return persisted with { ResidentApplications = normalized.Applications };
        }
    }

    /// <summary>按正规化 Windows 路径语义比较替换目标，并把畸形路径视为不匹配。</summary>
    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return StringComparer.OrdinalIgnoreCase.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>仅在代次仍为当前值时清空待确认候选，不写设置或永久黑名单。</summary>
    public bool Dismiss(long generation)
    {
        lock (stateSync)
        {
            if (current.Generation != generation)
            {
                return false;
            }

            current = current with { Candidates = [] };
            return true;
        }
    }

    private sealed class CandidateConfirmationRejectedException : Exception
    {
        internal CandidateConfirmationRejectedException()
        {
        }
    }
}
