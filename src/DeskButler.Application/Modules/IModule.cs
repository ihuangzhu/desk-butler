namespace DeskButler.Application.Modules;

/// <summary>定义由应用宿主负责启停的编译期注册模块。</summary>
public interface IModule
{
    /// <summary>获取模块的唯一稳定标识。</summary>
    string Id { get; }

    /// <summary>启动模块。</summary>
    /// <param name="cancellationToken">用于取消模块启动的令牌。</param>
    /// <returns>模块启动完成的任务。</returns>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>停止模块。</summary>
    /// <param name="cancellationToken">用于取消模块停止的令牌。</param>
    /// <returns>模块停止完成的任务。</returns>
    Task StopAsync(CancellationToken cancellationToken);
}
