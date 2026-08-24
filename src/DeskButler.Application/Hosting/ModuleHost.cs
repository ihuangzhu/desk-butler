using DeskButler.Application.Modules;

namespace DeskButler.Application.Hosting;

/// <summary>以确定顺序管理编译期注册模块的生命周期。</summary>
public sealed class ModuleHost
{
    private readonly IModule[] modules;

    /// <summary>使用按启动顺序排列的模块初始化宿主。</summary>
    /// <param name="modules">按正序启动、逆序停止的模块集合。</param>
    public ModuleHost(IEnumerable<IModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        this.modules = modules.ToArray();
    }

    /// <summary>按注册顺序启动全部模块。</summary>
    /// <param name="cancellationToken">用于取消模块启动的令牌。</param>
    /// <returns>全部模块启动完成的任务。</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var module in modules)
        {
            await module.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>按注册顺序的相反顺序停止全部模块。</summary>
    /// <param name="cancellationToken">用于取消模块停止的令牌。</param>
    /// <returns>全部模块停止完成的任务。</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        for (var index = modules.Length - 1; index >= 0; index--)
        {
            await modules[index].StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
