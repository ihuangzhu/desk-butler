namespace DeskButler.Modules.WorkspaceRecovery.Capture;

/// <summary>表示捕获没有写入新快照的稳定原因。</summary>
public enum CaptureSkipReason
{
    /// <summary>本次已经写入快照。</summary>
    None,

    /// <summary>捕获设置处于暂停状态。</summary>
    Disabled,

    /// <summary>平台没有返回任何窗口候选。</summary>
    NoCandidates,

    /// <summary>候选经安全过滤后没有可保存条目。</summary>
    NoItems,

    /// <summary>正规化现场与最新快照相同。</summary>
    Unchanged,

    /// <summary>手动捕获工作流发生可恢复故障。</summary>
    Failed
}

/// <summary>返回一次手动窗口保存的结果以及同批普通窗口可执行路径。</summary>
/// <param name="SnapshotSaved">是否实际写入了新快照。</param>
/// <param name="SkipReason">未写入时的稳定原因。</param>
/// <param name="WindowExecutablePaths">本批安全普通窗口的正规化可执行路径。</param>
public sealed record CaptureOutcome(
    bool SnapshotSaved,
    CaptureSkipReason SkipReason,
    IReadOnlySet<string> WindowExecutablePaths);
