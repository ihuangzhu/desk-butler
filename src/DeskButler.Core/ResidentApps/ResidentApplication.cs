namespace DeskButler.Core.ResidentApps;

/// <summary>表示一个由用户确认的常驻应用设置条目。</summary>
/// <param name="LaunchPath">实际启动入口的绝对可执行文件路径。</param>
/// <param name="KnownProcessPaths">用于判断该产品是否已运行的已知进程路径。</param>
/// <param name="DisplayName">面向用户显示的应用名称。</param>
/// <param name="Enabled">是否参与登录后的自动启动。</param>
/// <param name="LaunchOrder">自动启动时的相对顺序。</param>
public sealed record ResidentApplication(
    string LaunchPath,
    IReadOnlySet<string> KnownProcessPaths,
    string DisplayName,
    bool Enabled,
    int LaunchOrder);
