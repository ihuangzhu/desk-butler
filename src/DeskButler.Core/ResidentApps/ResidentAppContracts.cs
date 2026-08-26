namespace DeskButler.Core.ResidentApps;

/// <summary>定义常驻应用发现的跨平台边界。</summary>
public interface IResidentAppDiscovery
{
    /// <summary>发现未出现在普通窗口集合中的可选常驻应用。</summary>
    /// <param name="ordinaryWindowPaths">已有普通窗口对应的可执行文件路径。</param>
    /// <param name="existing">已有的常驻应用条目。</param>
    /// <param name="cancellationToken">取消发现操作的令牌。</param>
    /// <returns>候选和不含敏感原文的分类诊断。</returns>
    Task<ResidentDiscoveryResult> DiscoverAsync(
        IReadOnlySet<string> ordinaryWindowPaths,
        IReadOnlyList<ResidentApplication> existing,
        CancellationToken cancellationToken);
}

/// <summary>定义可执行文件安全验证的跨平台边界。</summary>
public interface IResidentExecutablePolicy
{
    /// <summary>验证路径是否可作为常驻应用启动入口。</summary>
    /// <param name="path">待验证的路径。</param>
    /// <returns>不包含底层异常文本的验证结果。</returns>
    ResidentExecutableValidation Validate(string path);
}

/// <summary>定义当前登录会话稳定身份的提供边界。</summary>
public interface ILogonSessionIdentityProvider
{
    /// <summary>获取当前登录会话的稳定身份，不返回账号或令牌内容。</summary>
    /// <returns>当前登录会话的身份字符串。</returns>
    string GetCurrent();
}

/// <summary>定义常驻应用进程检查和启动的跨平台边界。</summary>
public interface IResidentProcessRuntime
{
    /// <summary>检查调用方指定的已知路径中是否已有应用在当前会话运行。</summary>
    /// <param name="knownProcessPaths">仅允许检查的目标进程路径。</param>
    /// <param name="cancellationToken">取消检查的令牌。</param>
    /// <returns>与目标路径相关的运行状态。</returns>
    Task<ResidentRunningCheck> CheckRunningAsync(
        IReadOnlySet<string> knownProcessPaths,
        CancellationToken cancellationToken);

    /// <summary>启动已由调用方验证的可执行文件。</summary>
    /// <param name="executablePath">待启动的绝对可执行文件路径。</param>
    /// <param name="cancellationToken">取消启动操作的令牌。</param>
    /// <returns>表示启动请求完成的任务。</returns>
    Task StartAsync(string executablePath, CancellationToken cancellationToken);
}

/// <summary>表示常驻应用启动入口的验证结果。</summary>
public sealed record ResidentExecutableValidation(
    bool IsAllowed,
    string? NormalizedPath,
    ResidentExecutableRejection Reason);

/// <summary>表示拒绝常驻应用启动入口的稳定分类。</summary>
public enum ResidentExecutableRejection
{
    /// <summary>路径已通过验证。</summary>
    None,

    /// <summary>路径格式无效。</summary>
    InvalidPath,

    /// <summary>路径不是绝对路径。</summary>
    NotAbsolutePath,

    /// <summary>路径不是可执行文件。</summary>
    NotExecutableFile,

    /// <summary>目标文件不存在。</summary>
    FileNotFound,

    /// <summary>无法安全访问目标文件。</summary>
    AccessDenied,

    /// <summary>路径位于网络位置。</summary>
    NetworkPath,

    /// <summary>路径所在卷不是固定磁盘。</summary>
    NonFixedDrive,

    /// <summary>路径位于禁止的目录。</summary>
    ProhibitedDirectory,

    /// <summary>路径通过重解析点改变了目标位置。</summary>
    ReparsePoint,

    /// <summary>目标要求提升权限。</summary>
    RequiresElevation,

    /// <summary>验证过程无法可靠完成。</summary>
    ValidationFailed
}

/// <summary>表示目标路径的运行检查状态。</summary>
public enum ResidentRunningState
{
    /// <summary>已发现与目标路径匹配的进程。</summary>
    Running,

    /// <summary>没有发现与目标路径匹配的进程。</summary>
    NotRunning,

    /// <summary>仅因可能匹配目标的进程路径无法可靠读取而无法判断。</summary>
    Unknown
}

/// <summary>保存针对调用方目标路径的运行检查结果。</summary>
/// <param name="State">运行状态。</param>
/// <param name="MatchedPath">仅在已匹配时提供、且已按诊断边界处理的路径。</param>
public sealed record ResidentRunningCheck(ResidentRunningState State, string? MatchedPath);

/// <summary>表示发现过程中不含原始异常、账号或路径的分类问题。</summary>
public enum ResidentDiscoveryIssue
{
    /// <summary>进程在读取期间退出。</summary>
    ProcessExited,

    /// <summary>读取所需公开信息时被拒绝访问。</summary>
    AccessDenied,

    /// <summary>观察到的路径格式无效。</summary>
    InvalidPath,

    /// <summary>公开产品元数据不可用。</summary>
    MetadataUnavailable,

    /// <summary>读取卸载注册表项时被拒绝访问。</summary>
    RegistryAccessDenied,

    /// <summary>发现来源发生无法进一步分类的故障。</summary>
    SourceFailure
}

/// <summary>保存不含敏感原文的发现诊断。</summary>
public sealed record ResidentDiscoveryDiagnostic(ResidentDiscoveryIssue Kind);
