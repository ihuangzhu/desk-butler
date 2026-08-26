using System.Security.Cryptography;
using System.Text;
using DeskButler.Core.ResidentApps;

namespace DeskButler.Infrastructure.Windows.ResidentApps;

/// <summary>把当前会话的只读进程与安装目录快照收敛为需要用户确认的常驻候选。</summary>
public sealed class WindowsResidentAppDiscovery : IResidentAppDiscovery
{
    private static readonly string[] HelperTokens = ["helper", "updater", "update", "crash", "reporter", "renderer"];
    private static readonly string[] ExcludedKnownProcessTokens = ["updater", "update", "crash", "reporter"];
    private static readonly string[] VirtualMachineTokens = ["vmware", "virtualbox", "vbox", "qemu", "parallels", "hyper-v", "virtual machine"];

    private readonly IResidentProcessSnapshotSource processSnapshotSource;
    private readonly IInstalledApplicationCatalog installedApplicationCatalog;
    private readonly IResidentExecutablePolicy executablePolicy;
    private readonly Func<string, bool> fileExists;
    private readonly string? currentExecutablePath;

    /// <summary>创建使用真实只读 Windows 观察源、目录和安全策略的发现器。</summary>
    internal WindowsResidentAppDiscovery()
        : this(
            new WindowsResidentProcessSnapshotSource(),
            new InstalledApplicationCatalog(),
            new WindowsResidentExecutablePolicy(),
            File.Exists,
            Environment.ProcessPath)
    {
    }

    /// <summary>创建只暴露 Core 发现边界的默认 Windows 生产实现。</summary>
    /// <returns>内部组装只读进程快照、安装目录和可执行策略的发现器。</returns>
    public static IResidentAppDiscovery CreateDefault() => new WindowsResidentAppDiscovery();

    /// <summary>创建使用受控只读边界的发现器，以隔离测试并避免发现阶段写入或启动任何程序。</summary>
    internal WindowsResidentAppDiscovery(
        IResidentProcessSnapshotSource processSnapshotSource,
        IInstalledApplicationCatalog installedApplicationCatalog,
        IResidentExecutablePolicy executablePolicy,
        Func<string, bool> fileExists,
        string? currentExecutablePath)
    {
        this.processSnapshotSource = processSnapshotSource;
        this.installedApplicationCatalog = installedApplicationCatalog;
        this.executablePolicy = executablePolicy;
        this.fileExists = fileExists;
        this.currentExecutablePath = TryNormalizePath(currentExecutablePath);
    }

    /// <summary>筛选可安全解释的第三方后台观察并形成候选，绝不因此自动授权或启动应用。</summary>
    public async Task<ResidentDiscoveryResult> DiscoverAsync(
        IReadOnlySet<string> ordinaryWindowPaths,
        IReadOnlyList<ResidentApplication> existing,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var processSnapshot = await processSnapshotSource.CaptureAsync(cancellationToken);
        var catalogSnapshot = await installedApplicationCatalog.ReadAsync(cancellationToken);
        var ordinary = NormalizePaths(ordinaryWindowPaths);
        var knownExisting = CollectExistingPaths(existing);
        var discoveryDiagnostics = new List<ResidentDiscoveryDiagnostic>();
        var observations = FilterObservations(
            processSnapshot.Observations,
            catalogSnapshot.Entries,
            ordinary,
            knownExisting,
            discoveryDiagnostics,
            cancellationToken);
        var candidates = observations
            .GroupBy(observation => observation.GroupKey, StringComparer.Ordinal)
            .Select(group => BuildCandidate(group.Key, group.ToArray(), existing, cancellationToken))
            .Where(candidate => candidate is not null)
            .Cast<ResidentAppCandidate>()
            .OrderBy(candidate => candidate.DisplayName, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Kind)
            .ThenBy(candidate => candidate.LaunchPath, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.ReplacesLaunchPath, StringComparer.Ordinal)
            .ToArray();

        return new ResidentDiscoveryResult(
            candidates,
            processSnapshot.Diagnostics
                .Concat(catalogSnapshot.Diagnostics)
                .Concat(discoveryDiagnostics)
                .OrderBy(diagnostic => diagnostic.Kind)
                .ToArray());
    }

    /// <summary>只留下已通过启动策略、非自身、非普通窗口且尚未由用户配置的观察，并计算严格分组信息。</summary>
    private List<EligibleObservation> FilterObservations(
        IReadOnlyList<ResidentProcessObservation> observations,
        IReadOnlyList<InstalledApplicationEntry> catalog,
        HashSet<string> ordinaryWindowPaths,
        HashSet<string> existingPaths,
        List<ResidentDiscoveryDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var result = new List<EligibleObservation>();
        foreach (var observation in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResidentExecutableValidation validation;
            try
            {
                validation = executablePolicy.Validate(observation.ExecutablePath);
            }
            catch (OperationCanceledException)
            {
                // 取消是调用方控制流，绝不能被单项观察隔离逻辑吞掉。
                throw;
            }
            catch
            {
                // 策略不应泄露路径或异常；单项失败降级后仍帮助用户缩小其它候选范围。
                diagnostics.Add(new ResidentDiscoveryDiagnostic(ResidentDiscoveryIssue.SourceFailure));
                continue;
            }
            if (!validation.IsAllowed || validation.NormalizedPath is null)
            {
                continue;
            }

            var path = validation.NormalizedPath;
            if ((currentExecutablePath is not null && PathEquals(path, currentExecutablePath)) ||
                ordinaryWindowPaths.Contains(path) ||
                existingPaths.Contains(path))
            {
                continue;
            }

            var productName = NormalizeText(observation.ProductName);
            var companyName = NormalizeText(observation.CompanyName);
            var installed = FindInstalledEntry(path, productName, companyName, catalog);
            var groupKey = installed is not null && productName is not null && companyName is not null
                ? $"product|{installed.InstallRoot}|{productName}|{companyName}"
                : $"path|{path}";
            result.Add(new EligibleObservation(path, observation, installed, groupKey));
        }

        return result
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(MergeDuplicatePathObservations)
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>合并同一 exe 的多实例窗口分类，避免 PID 重复使同一路径入口虚假并列。</summary>
    private static EligibleObservation MergeDuplicatePathObservations(IGrouping<string, EligibleObservation> group)
    {
        var ordered = group
            .OrderBy(item => item.Observation.ProductName, StringComparer.Ordinal)
            .ThenBy(item => item.Observation.CompanyName, StringComparer.Ordinal)
            .ThenBy(item => item.Observation.FileDescription, StringComparer.Ordinal)
            .ThenBy(item => item.Observation.ProcessId)
            .ToArray();
        var first = ordered[0];
        var traits = ordered.Aggregate(
            ResidentWindowTraits.None,
            (current, item) => new ResidentWindowTraits(
                current.HasVisibleTopLevelWindow || item.Observation.WindowTraits.HasVisibleTopLevelWindow,
                current.HasHiddenTopLevelWindow || item.Observation.WindowTraits.HasHiddenTopLevelWindow,
                current.HasOwnedTopLevelWindow || item.Observation.WindowTraits.HasOwnedTopLevelWindow,
                current.HasToolWindow || item.Observation.WindowTraits.HasToolWindow,
                current.HasCloakedWindow || item.Observation.WindowTraits.HasCloakedWindow));
        return first with { Observation = first.Observation with { WindowTraits = traits } };
    }

    /// <summary>为一组观察选择唯一入口、收集可识别进程路径，并在严格同产品条件下改为路径替换建议。</summary>
    private ResidentAppCandidate? BuildCandidate(
        string groupKey,
        IReadOnlyList<EligibleObservation> observations,
        IReadOnlyList<ResidentApplication> existing,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var installed = observations[0].Installed;
        var displayName = installed?.DisplayName ??
            NormalizeText(observations[0].Observation.ProductName) ??
            Path.GetFileNameWithoutExtension(observations[0].Path);
        var knownPaths = observations
            .Where(item => !HasAnyToken(Path.GetFileNameWithoutExtension(item.Path), ExcludedKnownProcessTokens))
            .Select(item => item.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var selection = SelectLaunchPath(observations, installed);
        var replacement = FindReplacement(existing, displayName, installed, cancellationToken);
        if (replacement is not null)
        {
            var candidateId = CreateCandidateId(
                ResidentCandidateKind.PathReplacement,
                groupKey,
                selection.SortingPath,
                replacement.LaunchPath);
            return new ResidentAppCandidate(
                candidateId,
                displayName,
                selection.LaunchPath,
                new HashSet<string>(knownPaths, StringComparer.OrdinalIgnoreCase),
                ResidentCandidateConfidence.Low,
                ResidentCandidateKind.PathReplacement,
                replacement.LaunchPath);
        }

        var confidence = IsHighConfidence(selection, installed, observations)
            ? ResidentCandidateConfidence.High
            : ResidentCandidateConfidence.Low;
        var id = CreateCandidateId(ResidentCandidateKind.NewApplication, groupKey, selection.SortingPath, null);
        return new ResidentAppCandidate(
            id,
            displayName,
            selection.LaunchPath,
            new HashSet<string>(knownPaths, StringComparer.OrdinalIgnoreCase),
            confidence,
            ResidentCandidateKind.NewApplication,
            null);
    }

    /// <summary>按固定评分选择唯一入口；并列或低于安全门槛时明确不给出启动路径。</summary>
    private static LaunchSelection SelectLaunchPath(
        IReadOnlyList<EligibleObservation> observations,
        InstalledApplicationEntry? installed)
    {
        var ranked = observations
            .Select(observation => new RankedObservation(
                observation,
                Score(observation, installed)))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Observation.Path, StringComparer.Ordinal)
            .ToArray();
        var sortingPath = ranked[0].Observation.Path;
        if (ranked[0].Score < 100 || ranked.Count(item => item.Score == ranked[0].Score) != 1)
        {
            return new LaunchSelection(null, sortingPath, null);
        }

        return new LaunchSelection(ranked[0].Observation.Path, sortingPath, ranked[0].Observation);
    }

    /// <summary>严格按窗口、目录图标和非辅助程序产品根的固定分值累计，避免隐式偏好。</summary>
    private static int Score(EligibleObservation observation, InstalledApplicationEntry? installed)
    {
        var windowTraits = observation.Observation.WindowTraits;
        var score = windowTraits.HasHiddenTopLevelWindow || windowTraits.HasToolWindow || windowTraits.HasCloakedWindow
            ? 300
            : 0;
        if (installed?.DisplayIconPath is not null && PathEquals(installed.DisplayIconPath, observation.Path))
        {
            score += 200;
        }

        if (installed?.InstallRoot is not null &&
            IsWithinDirectory(observation.Path, installed.InstallRoot) &&
            !HasAnyToken(Path.GetFileNameWithoutExtension(observation.Path), HelperTokens))
        {
            score += 100;
        }

        return score;
    }

    /// <summary>只有隐藏、工具或 cloaked 的唯一非辅助入口，且拥有完整稳定安装信息时才可高可信默认选中。</summary>
    private static bool IsHighConfidence(
        LaunchSelection selection,
        InstalledApplicationEntry? installed,
        IReadOnlyList<EligibleObservation> observations)
    {
        if (selection.Observation is null || installed?.InstallRoot is null ||
            string.IsNullOrWhiteSpace(installed.Publisher) ||
            HasAnyToken(Path.GetFileNameWithoutExtension(selection.Observation.Path), HelperTokens))
        {
            return false;
        }

        var traits = selection.Observation.Observation.WindowTraits;
        var isBackground = traits.HasHiddenTopLevelWindow || traits.HasToolWindow || traits.HasCloakedWindow;
        var hasCompleteMetadata = observations.All(item =>
            NormalizeText(item.Observation.ProductName) is not null && NormalizeText(item.Observation.CompanyName) is not null);
        return isBackground && hasCompleteMetadata && !IsVirtualMachineTool(selection.Observation, installed);
    }

    /// <summary>只在旧启动文件确实不存在、显示名相同且旧新路径都位于同一已安装根时建议替换。</summary>
    private ResidentApplication? FindReplacement(
        IReadOnlyList<ResidentApplication> existing,
        string displayName,
        InstalledApplicationEntry? installed,
        CancellationToken cancellationToken)
    {
        if (installed?.InstallRoot is null)
        {
            return null;
        }

        foreach (var application in existing.OrderBy(item => item.LaunchPath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var oldPath = TryNormalizePath(application.LaunchPath);
            if (oldPath is null || fileExists(oldPath) ||
                !TextEquals(application.DisplayName, displayName) ||
                !IsWithinDirectory(oldPath, installed.InstallRoot))
            {
                continue;
            }

            return application with { LaunchPath = oldPath };
        }

        return null;
    }

    /// <summary>从目录中选择同产品、同厂商且包含该 exe 的最近安装根，防止宽泛厂商目录误归组。</summary>
    private static InstalledApplicationEntry? FindInstalledEntry(
        string executablePath,
        string? productName,
        string? companyName,
        IReadOnlyList<InstalledApplicationEntry> catalog)
    {
        if (productName is null || companyName is null)
        {
            return null;
        }

        return catalog
            .Where(entry => entry.InstallRoot is not null &&
                TextEquals(entry.DisplayName, productName) &&
                TextEquals(entry.Publisher, companyName) &&
                IsWithinDirectory(executablePath, entry.InstallRoot))
            .OrderByDescending(entry => entry.InstallRoot!.Length)
            .ThenBy(entry => entry.InstallRoot, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>汇总用户现有条目的启动和已知进程路径，使发现不会重新推荐已授权产品。</summary>
    private static HashSet<string> CollectExistingPaths(IReadOnlyList<ResidentApplication> existing)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var application in existing)
        {
            AddNormalizedPath(paths, application.LaunchPath);
            foreach (var path in application.KnownProcessPaths)
            {
                AddNormalizedPath(paths, path);
            }
        }

        return paths;
    }

    /// <summary>将调用方普通窗口路径正规化到 Windows 忽略大小写集合，畸形值不会扩大候选范围。</summary>
    private static HashSet<string> NormalizePaths(IReadOnlySet<string> paths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            AddNormalizedPath(result, path);
        }

        return result;
    }

    /// <summary>只把可安全正规化的绝对路径写入比较集合，避免格式异常中断其他候选。</summary>
    private static void AddNormalizedPath(HashSet<string> paths, string? value)
    {
        var path = TryNormalizePath(value);
        if (path is not null)
        {
            paths.Add(path);
        }
    }

    /// <summary>生成不含 PID 或明文旧路径的稳定 SHA-256 身份，并以候选种类隔离新增与替换。</summary>
    private static string CreateCandidateId(
        ResidentCandidateKind kind,
        string groupKey,
        string sortingPath,
        string? replacesLaunchPath)
    {
        var replacement = TryNormalizePath(replacesLaunchPath) ?? string.Empty;
        var payload = string.Join("\n", (int)kind, groupKey, sortingPath, replacement);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>识别虚拟机辅助工具关键词；即使其他信号充分也必须降为低可信。</summary>
    private static bool IsVirtualMachineTool(EligibleObservation observation, InstalledApplicationEntry installed) =>
        HasAnyToken(
            string.Join(" ", observation.Observation.ProductName, observation.Observation.CompanyName, installed.DisplayName, installed.Publisher, Path.GetFileNameWithoutExtension(observation.Path)),
            VirtualMachineTokens);

    /// <summary>以不区分大小写的文件名 token 判断辅助程序，避免它成为高可信主入口。</summary>
    private static bool HasAnyToken(string value, IReadOnlyList<string> tokens) =>
        tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    /// <summary>安全比较路径或产品文本，保持 Windows 路径和目录目录项的忽略大小写语义。</summary>
    private static bool PathEquals(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);

    /// <summary>比较已去除首尾空白的公开产品文本，不向候选透传原始注册表或版本资源值。</summary>
    private static bool TextEquals(string? left, string? right) =>
        NormalizeText(left)?.Equals(NormalizeText(right), StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>只保留非空、无控制字符的公开产品文本，缺失时触发路径级保守分组。</summary>
    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Any(char.IsControl) ? null : normalized;
    }

    /// <summary>把绝对路径转为完整路径；畸形或相对路径只作为不可用处理而不会抛出敏感错误。</summary>
    private static string? TryNormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>以分隔符边界判断路径是否位于安装根中，避免 C:\Apps 与 C:\AppsOld 前缀混淆。</summary>
    private static bool IsWithinDirectory(string path, string directory)
    {
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(directory);
        return PathEquals(path, normalizedDirectory) ||
               path.StartsWith(normalizedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>保存经筛选观察及其稳定分组依据，不保留 PID 作为候选身份输入。</summary>
    private sealed record EligibleObservation(
        string Path,
        ResidentProcessObservation Observation,
        InstalledApplicationEntry? Installed,
        string GroupKey);

    /// <summary>保存固定入口评分及原始观察，以确保并列时不猜测启动路径。</summary>
    private sealed record RankedObservation(EligibleObservation Observation, int Score);

    /// <summary>保存入口选择与确定性排序路径，供可信度和候选身份分别使用。</summary>
    private sealed record LaunchSelection(string? LaunchPath, string SortingPath, EligibleObservation? Observation);
}
