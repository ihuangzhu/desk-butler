namespace DeskButler.Application.Modules;

/// <summary>描述 V1 编译期模块向界面公开的稳定能力和声明。</summary>
public sealed record ModuleDescriptor(
    string Id,
    string DisplayName,
    Version Version,
    bool EnabledByDefault,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Settings,
    IReadOnlyList<string> Diagnostics);

/// <summary>定义由应用宿主负责启停的编译期注册模块。</summary>
public interface IModule
{
    /// <summary>获取模块的唯一稳定标识。</summary>
    string Id { get; }

    /// <summary>获取模块的用户可见编译期描述。</summary>
    ModuleDescriptor Descriptor { get; }

    /// <summary>启动模块。</summary>
    /// <param name="cancellationToken">用于取消模块启动的令牌。</param>
    /// <returns>模块启动完成的任务。</returns>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>停止模块。</summary>
    /// <param name="cancellationToken">用于取消模块停止的令牌。</param>
    /// <returns>模块停止完成的任务。</returns>
    Task StopAsync(CancellationToken cancellationToken);
}
