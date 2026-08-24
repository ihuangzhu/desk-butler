using DeskButler.Core.Capture;
using DeskButler.Core.Diagnostics;
using DeskButler.Core.Restore;
using DeskButler.Core.Scenes;

namespace DeskButler.Modules.WorkspaceRecovery.Restore;

/// <summary>按照稳定属性优先级生成宁可少恢复也不重复启动的恢复计划。</summary>
public sealed class RestorePlanner : IRestorePlanner
{
    private const int UnsafeFailureThreshold = 3;
    private readonly Func<string, bool> pathExists;

    /// <summary>创建使用真实本地文件系统检查启动路径的恢复规划器。</summary>
    public RestorePlanner()
        : this(DefaultPathExists)
    {
    }

    /// <summary>创建使用指定路径存在谓词的恢复规划器。</summary>
    /// <param name="pathExists">接收已规范化绝对路径的存在性谓词。</param>
    public RestorePlanner(Func<string, bool> pathExists)
    {
        this.pathExists = pathExists ?? throw new ArgumentNullException(nameof(pathExists));
    }

    /// <inheritdoc />
    public RestorePlan Build(
        SceneSnapshot scene,
        IReadOnlyCollection<WindowCandidate> currentWindows,
        FailureHistory failureHistory,
        bool safeMode)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(currentWindows);
        ArgumentNullException.ThrowIfNull(failureHistory);

        var sceneItems = scene.Items
            .Select((item, index) => new SceneProjection(index, item, NormalizePath(item.ExecutablePath),
                NormalizeOptionalPath(item.ExplorerPath), NormalizeTitle(item.TitleHint)))
            .ToArray();
        var candidates = ProjectCandidates(currentWindows);
        var decisions = new RestorePlanItem?[sceneItems.Length];
        var consumedHandles = new HashSet<nint>();

        MarkPersistedUnsafeItems(sceneItems, failureHistory, decisions);
        MatchExplorerPaths(sceneItems, candidates, decisions, consumedHandles);
        MatchExecutableClassAndTitle(sceneItems, candidates, decisions, consumedHandles);
        MatchUniqueExecutables(sceneItems, candidates, decisions, consumedHandles);
        PlanMissingWindows(sceneItems, decisions, safeMode);

        return new RestorePlan(decisions.Select(item => item
            ?? throw new InvalidOperationException("恢复规划器未能为场景项目生成决策。")));
    }

    /// <summary>先标记场景自身已知不安全或连续失败达到阈值的项目。</summary>
    private static void MarkPersistedUnsafeItems(
        IReadOnlyList<SceneProjection> sceneItems,
        FailureHistory failureHistory,
        RestorePlanItem?[] decisions)
    {
        foreach (var scene in sceneItems)
        {
            if (scene.Item.WasElevated || failureHistory.CountFor(scene.Item.Id) >= UnsafeFailureThreshold)
            {
                decisions[scene.Index] = Decision(scene.Item, RestoreDisposition.SkipUnsafe);
            }
        }
    }

    /// <summary>以最高优先级按规范化资源管理器目录建立严格一对一匹配。</summary>
    private static void MatchExplorerPaths(
        IReadOnlyList<SceneProjection> sceneItems,
        IReadOnlyList<CandidateProjection> candidates,
        RestorePlanItem?[] decisions,
        HashSet<nint> consumedHandles)
    {
        foreach (var path in sceneItems
                     .Where(scene => decisions[scene.Index] is null && scene.ExplorerPath is not null)
                     .Select(scene => scene.ExplorerPath!)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // 计数使用原始全集而非剩余项，避免先消费后把多实例伪装成唯一实例。
            var allScenes = sceneItems.Where(scene => PathEquals(scene.ExplorerPath, path)).ToArray();
            var allCandidates = candidates.Where(candidate => PathEquals(candidate.ExplorerPath, path)).ToArray();
            var pendingScenes = allScenes.Where(scene => decisions[scene.Index] is null).ToArray();

            if (allScenes.Length == 1 && allCandidates.Length == 1)
            {
                AssignMatchedCandidate(pendingScenes[0], allCandidates[0], decisions, consumedHandles);
            }
            else if (allCandidates.Length > 0)
            {
                MarkAmbiguous(pendingScenes, decisions);
            }
        }
    }

    /// <summary>按规范化 exe、精确类名和受限标题提示进行第二优先级匹配。</summary>
    private static void MatchExecutableClassAndTitle(
        IReadOnlyList<SceneProjection> sceneItems,
        IReadOnlyList<CandidateProjection> candidates,
        RestorePlanItem?[] decisions,
        HashSet<nint> consumedHandles)
    {
        var keys = sceneItems
            .Where(scene => decisions[scene.Index] is null && scene.ExecutablePath is not null && scene.Title is not null)
            .Select(scene => new MatchKey(scene.ExecutablePath!, scene.Item.WindowClass, scene.Title!))
            .Distinct(MatchKeyComparer.Instance)
            .ToArray();

        foreach (var key in keys)
        {
            var allScenes = sceneItems.Where(scene => Matches(scene, key)).ToArray();
            var allCandidates = candidates.Where(candidate => Matches(candidate, key)).ToArray();
            var pendingScenes = allScenes.Where(scene => decisions[scene.Index] is null).ToArray();

            if (allScenes.Length == 1 && allCandidates.Length == 1)
            {
                AssignMatchedCandidate(pendingScenes[0], allCandidates[0], decisions, consumedHandles);
            }
            else if (allCandidates.Length > 0)
            {
                MarkAmbiguous(pendingScenes, decisions);
            }
        }
    }

    /// <summary>仅当同一规范化 exe 在完整场景与当前窗口中均唯一时执行最终回退匹配。</summary>
    private static void MatchUniqueExecutables(
        IReadOnlyList<SceneProjection> sceneItems,
        IReadOnlyList<CandidateProjection> candidates,
        RestorePlanItem?[] decisions,
        HashSet<nint> consumedHandles)
    {
        foreach (var path in sceneItems
                     .Where(scene => decisions[scene.Index] is null && scene.ExecutablePath is not null)
                     .Select(scene => scene.ExecutablePath!)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var allScenes = sceneItems.Where(scene => PathEquals(scene.ExecutablePath, path)).ToArray();
            var allCandidates = candidates.Where(candidate => PathEquals(candidate.ExecutablePath, path)).ToArray();
            var pendingScenes = allScenes.Where(scene => decisions[scene.Index] is null).ToArray();

            if (allScenes.Length == 1 && allCandidates.Length == 1)
            {
                AssignMatchedCandidate(pendingScenes[0], allCandidates[0], decisions, consumedHandles);
            }
            else if (allCandidates.Length > 0)
            {
                MarkAmbiguous(pendingScenes, decisions);
            }
        }
    }

    /// <summary>为完全没有可靠当前窗口的剩余项目决定启动、缺失路径或安全模式跳过。</summary>
    private void PlanMissingWindows(
        IReadOnlyList<SceneProjection> sceneItems,
        RestorePlanItem?[] decisions,
        bool safeMode)
    {
        foreach (var scene in sceneItems.Where(scene => decisions[scene.Index] is null))
        {
            var isExplorer = !string.IsNullOrWhiteSpace(scene.Item.ExplorerPath);
            var launchPath = isExplorer ? scene.ExplorerPath : scene.ExecutablePath;

            if (launchPath is null || !PathExistsSafely(launchPath))
            {
                decisions[scene.Index] = Decision(scene.Item, RestoreDisposition.MissingPath);
            }
            else if (safeMode && !isExplorer)
            {
                decisions[scene.Index] = Decision(scene.Item, RestoreDisposition.SkipUnsafe);
            }
            else
            {
                decisions[scene.Index] = Decision(scene.Item, RestoreDisposition.Launch);
            }
        }
    }

    /// <summary>将可靠匹配转换为复用或权限不足跳过，并消费本次窗口句柄。</summary>
    private static void AssignMatchedCandidate(
        SceneProjection scene,
        CandidateProjection candidate,
        RestorePlanItem?[] decisions,
        HashSet<nint> consumedHandles)
    {
        if (candidate.IsUnsafe)
        {
            consumedHandles.Add(candidate.Handle);
            decisions[scene.Index] = Decision(scene.Item, RestoreDisposition.SkipUnsafe);
        }
        else if (consumedHandles.Add(candidate.Handle))
        {
            decisions[scene.Index] = Decision(scene.Item, RestoreDisposition.Reuse, candidate.Handle);
        }
        else
        {
            decisions[scene.Index] = Decision(scene.Item, RestoreDisposition.SkipAmbiguous);
        }
    }

    /// <summary>将无法形成严格一对一关系的待处理场景项目全部标记为歧义。</summary>
    private static void MarkAmbiguous(IEnumerable<SceneProjection> scenes, RestorePlanItem?[] decisions)
    {
        foreach (var scene in scenes)
        {
            decisions[scene.Index] = Decision(scene.Item, RestoreDisposition.SkipAmbiguous);
        }
    }

    /// <summary>创建不携带持久进程身份的计划决策。</summary>
    private static RestorePlanItem Decision(
        SceneItem sceneItem,
        RestoreDisposition disposition,
        nint? targetWindowHandle = null)
    {
        return new RestorePlanItem(sceneItem, disposition, targetWindowHandle);
    }

    /// <summary>在路径谓词异常时把单项视为路径缺失，不影响其余计划。</summary>
    private bool PathExistsSafely(string normalizedPath)
    {
        try
        {
            return pathExists(normalizedPath);
        }
        catch (Exception exception) when (IsRecoverablePathExistenceException(exception))
        {
            return false;
        }
    }

    /// <summary>判断真实本地路径是现存文件或目录，以同时支持普通程序和 Explorer。</summary>
    private static bool DefaultPathExists(string normalizedPath)
    {
        return File.Exists(normalizedPath) || Directory.Exists(normalizedPath);
    }

    /// <summary>规范化必填绝对 Windows 路径；相对或畸形路径返回空。</summary>
    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (IsPathException(exception))
        {
            return null;
        }
    }

    /// <summary>规范化可选绝对 Windows 路径；缺失、相对或畸形路径返回空。</summary>
    private static string? NormalizeOptionalPath(string? path)
    {
        return NormalizePath(path);
    }

    /// <summary>将空标题视为无约束，其他标题仅去除首尾空白并做精确比较。</summary>
    private static string? NormalizeTitle(string? title)
    {
        return string.IsNullOrWhiteSpace(title) ? null : title.Trim();
    }

    /// <summary>判断异常是否属于可以隔离到单个路径项目的预期异常。</summary>
    private static bool IsPathException(Exception exception)
    {
        return exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException
            or UnauthorizedAccessException
            or IOException;
    }

    /// <summary>仅隔离路径提供器的普通异常；取消、内存耗尽及进程级致命异常必须继续传播。</summary>
    private static bool IsRecoverablePathExistenceException(Exception exception)
    {
        return exception is not OperationCanceledException
            and not OutOfMemoryException
            and not AccessViolationException
            and not StackOverflowException
            and not ThreadAbortException
            and not System.Runtime.InteropServices.SEHException;
    }

    /// <summary>按 Windows 路径语义比较两个已规范化路径。</summary>
    private static bool PathEquals(string? left, string? right)
    {
        return left is not null && right is not null &&
               StringComparer.OrdinalIgnoreCase.Equals(left, right);
    }

    /// <summary>判断场景投影是否满足 exe、类名和受限标题组合。</summary>
    private static bool Matches(SceneProjection scene, MatchKey key)
    {
        return PathEquals(scene.ExecutablePath, key.ExecutablePath) &&
               StringComparer.Ordinal.Equals(scene.Item.WindowClass, key.WindowClass) &&
               scene.Title is not null && StringComparer.OrdinalIgnoreCase.Equals(scene.Title, key.Title);
    }

    /// <summary>判断候选投影是否满足 exe、类名和受限标题组合。</summary>
    private static bool Matches(CandidateProjection candidate, MatchKey key)
    {
        return PathEquals(candidate.ExecutablePath, key.ExecutablePath) &&
               StringComparer.Ordinal.Equals(candidate.WindowClass, key.WindowClass) &&
               candidate.Title is not null && StringComparer.OrdinalIgnoreCase.Equals(candidate.Title, key.Title);
    }

    /// <summary>按 HWND 聚合当前候选，并保留冲突身份别名及其保守风险。</summary>
    private static CandidateProjection[] ProjectCandidates(IReadOnlyCollection<WindowCandidate> currentWindows)
    {
        var projections = currentWindows.Select(candidate => new CandidateProjection(
            candidate.Handle,
            candidate.WasElevatedOrInaccessible,
            NormalizeOptionalPath(candidate.ExecutablePath),
            NormalizeOptionalPath(candidate.ExplorerPath),
            candidate.WindowClass,
            NormalizeTitle(candidate.Title)));

        return projections
            .GroupBy(candidate => candidate.Handle)
            .SelectMany(ProjectHandleAliases)
            .ToArray();
    }

    /// <summary>折叠同一 HWND 的等价身份，并把身份冲突安全传播到该句柄的全部别名。</summary>
    private static IEnumerable<CandidateProjection> ProjectHandleAliases(
        IGrouping<nint, CandidateProjection> handleGroup)
    {
        var aliases = handleGroup
            .GroupBy(candidate => new CandidateIdentity(candidate.ExecutablePath, candidate.ExplorerPath,
                candidate.WindowClass, candidate.Title), CandidateIdentityComparer.Instance)
            .Select(group => group.First() with { IsUnsafe = group.Any(candidate => candidate.IsUnsafe) })
            .ToArray();
        var hasIdentityConflict = aliases.Length > 1;

        return aliases.Select(alias => alias with { IsUnsafe = alias.IsUnsafe || hasIdentityConflict });
    }

    private sealed record SceneProjection(
        int Index,
        SceneItem Item,
        string? ExecutablePath,
        string? ExplorerPath,
        string? Title);

    private sealed record CandidateProjection(
        nint Handle,
        bool IsUnsafe,
        string? ExecutablePath,
        string? ExplorerPath,
        string WindowClass,
        string? Title);

    private sealed record MatchKey(string ExecutablePath, string WindowClass, string Title);

    private sealed record CandidateIdentity(
        string? ExecutablePath,
        string? ExplorerPath,
        string WindowClass,
        string? Title);

    /// <summary>为 exe 路径和标题提供不区分大小写、类名精确的组合键比较。</summary>
    private sealed class MatchKeyComparer : IEqualityComparer<MatchKey>
    {
        public static MatchKeyComparer Instance { get; } = new();

        /// <summary>比较两个稳定组合键。</summary>
        public bool Equals(MatchKey? x, MatchKey? y)
        {
            return ReferenceEquals(x, y) || x is not null && y is not null &&
                StringComparer.OrdinalIgnoreCase.Equals(x.ExecutablePath, y.ExecutablePath) &&
                StringComparer.Ordinal.Equals(x.WindowClass, y.WindowClass) &&
                StringComparer.OrdinalIgnoreCase.Equals(x.Title, y.Title);
        }

        /// <summary>计算与组合键比较语义一致的哈希值。</summary>
        public int GetHashCode(MatchKey obj)
        {
            var hash = new HashCode();
            hash.Add(obj.ExecutablePath, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.WindowClass, StringComparer.Ordinal);
            hash.Add(obj.Title, StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }
    }

    /// <summary>按完整规范身份比较同一 HWND 的重复观测，风险标志不属于身份而单独取 OR。</summary>
    private sealed class CandidateIdentityComparer : IEqualityComparer<CandidateIdentity>
    {
        public static CandidateIdentityComparer Instance { get; } = new();

        /// <summary>比较两个候选的完整规范身份。</summary>
        public bool Equals(CandidateIdentity? x, CandidateIdentity? y)
        {
            return ReferenceEquals(x, y) || x is not null && y is not null &&
                StringComparer.OrdinalIgnoreCase.Equals(x.ExecutablePath, y.ExecutablePath) &&
                StringComparer.OrdinalIgnoreCase.Equals(x.ExplorerPath, y.ExplorerPath) &&
                StringComparer.Ordinal.Equals(x.WindowClass, y.WindowClass) &&
                StringComparer.OrdinalIgnoreCase.Equals(x.Title, y.Title);
        }

        /// <summary>计算与完整规范身份比较语义一致的哈希值。</summary>
        public int GetHashCode(CandidateIdentity obj)
        {
            var hash = new HashCode();
            hash.Add(obj.ExecutablePath, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.ExplorerPath, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.WindowClass, StringComparer.Ordinal);
            hash.Add(obj.Title, StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }
    }
}
