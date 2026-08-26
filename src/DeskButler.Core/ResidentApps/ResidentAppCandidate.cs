namespace DeskButler.Core.ResidentApps;

/// <summary>表示发现候选的可信度。</summary>
public enum ResidentCandidateConfidence
{
    /// <summary>需要用户确认后才会选中。</summary>
    Low,

    /// <summary>可作为默认选中项。</summary>
    High
}

/// <summary>表示发现候选将执行的设置操作类型。</summary>
public enum ResidentCandidateKind
{
    /// <summary>新增一个常驻应用条目。</summary>
    NewApplication,

    /// <summary>替换已有条目的启动路径。</summary>
    PathReplacement
}

/// <summary>表示供用户确认的常驻应用候选。</summary>
public sealed record ResidentAppCandidate(
    string CandidateId,
    string DisplayName,
    string? LaunchPath,
    IReadOnlySet<string> KnownProcessPaths,
    ResidentCandidateConfidence Confidence,
    ResidentCandidateKind Kind,
    string? ReplacesLaunchPath)
{
    /// <summary>获取候选是否应在确认界面中默认选中。</summary>
    public bool IsSelectedByDefault => Confidence == ResidentCandidateConfidence.High && LaunchPath is not null;
}

/// <summary>保存一次常驻应用发现的候选与分类诊断。</summary>
public sealed record ResidentDiscoveryResult(
    IReadOnlyList<ResidentAppCandidate> Candidates,
    IReadOnlyList<ResidentDiscoveryDiagnostic> Diagnostics);
