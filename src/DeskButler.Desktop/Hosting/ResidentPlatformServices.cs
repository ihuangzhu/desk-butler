using DeskButler.Core.ResidentApps;

namespace DeskButler.Desktop.Hosting;

/// <summary>收口常驻功能的只读平台边界；组合根创建后不得替换任一生产依赖。</summary>
/// <remarks>本包只表达对象所有权输入，不提供按类型查询，也不允许可空平台依赖。</remarks>
internal sealed record ResidentPlatformServices
{
    /// <summary>创建完整且不可变的平台依赖包。</summary>
    internal ResidentPlatformServices(
        IResidentExecutablePolicy executablePolicy,
        ILogonSessionIdentityProvider logonIdentity,
        IResidentProcessRuntime processRuntime,
        IResidentAppDiscovery discovery,
        IResidentLaunchSessionStore launchSessionStore)
    {
        ExecutablePolicy = executablePolicy ?? throw new ArgumentNullException(nameof(executablePolicy));
        LogonIdentity = logonIdentity ?? throw new ArgumentNullException(nameof(logonIdentity));
        ProcessRuntime = processRuntime ?? throw new ArgumentNullException(nameof(processRuntime));
        Discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        LaunchSessionStore = launchSessionStore ?? throw new ArgumentNullException(nameof(launchSessionStore));
    }

    internal IResidentExecutablePolicy ExecutablePolicy { get; }

    internal ILogonSessionIdentityProvider LogonIdentity { get; }

    internal IResidentProcessRuntime ProcessRuntime { get; }

    internal IResidentAppDiscovery Discovery { get; }

    internal IResidentLaunchSessionStore LaunchSessionStore { get; }
}

/// <summary>Debug smoke 专用禁用 runtime；不枚举进程、不创建进程，也不拥有第三方生命周期。</summary>
internal sealed class DisabledResidentProcessRuntime : IResidentProcessRuntime
{
    private int checkCallCount;
    private int startCallCount;

    /// <summary>获取进入禁用运行检查边界的次数。</summary>
    internal int CheckCallCount => Volatile.Read(ref checkCallCount);

    /// <summary>获取错误越过 Unknown 防线并请求启动的次数。</summary>
    internal int StartCallCount => Volatile.Read(ref startCallCount);

    /// <summary>不读取进程清单，固定返回无法确认，令上层 fail-closed。</summary>
    public Task<ResidentRunningCheck> CheckRunningAsync(
        IReadOnlySet<string> knownProcessPaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(knownProcessPaths);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref checkCallCount);
        return Task.FromResult(new ResidentRunningCheck(ResidentRunningState.Unknown, null));
    }

    /// <summary>只记录错误调用并在进程内拒绝，绝不调用 Process.Start 或 Stop/Kill。</summary>
    public Task StartAsync(string executablePath, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref startCallCount);
        return Task.FromException(new InvalidOperationException("Debug smoke 已禁用常驻应用进程启动。"));
    }
}
