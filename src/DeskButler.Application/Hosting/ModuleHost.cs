using DeskButler.Application.Modules;
using DeskButler.Application.Events;

namespace DeskButler.Application.Hosting;

/// <summary>以确定顺序管理编译期注册模块的生命周期。</summary>
public sealed class ModuleHost
{
    private readonly IModule[] modules;
    private readonly IEventBus eventBus;

    /// <summary>使用按启动顺序排列的模块初始化宿主。</summary>
    /// <param name="modules">按正序启动、逆序停止的模块集合。</param>
    public ModuleHost(IEnumerable<IModule> modules)
        : this(modules, new InProcessEventBus())
    {
    }

    /// <summary>使用按启动顺序排列的模块和共享生产事件总线初始化宿主。</summary>
    public ModuleHost(IEnumerable<IModule> modules, IEventBus eventBus)
    {
        ArgumentNullException.ThrowIfNull(modules);
        this.modules = modules.ToArray();
        this.eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
    }

    /// <summary>按注册顺序启动全部模块。</summary>
    /// <param name="cancellationToken">用于取消模块启动的令牌。</param>
    /// <returns>全部模块启动完成的任务。</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var module in modules)
        {
            try
            {
                await module.StartAsync(cancellationToken).ConfigureAwait(false);
                await PublishAsync(new ModuleStatusChanged(module.Id, ModuleRunState.Running), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await PublishAsync(
                    new ModuleStatusChanged(module.Id, ModuleRunState.Failed, exception.Message),
                    CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>按注册顺序的相反顺序停止全部模块。</summary>
    /// <param name="cancellationToken">用于取消模块停止的令牌。</param>
    /// <returns>全部模块停止完成的任务。</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        for (var index = modules.Length - 1; index >= 0; index--)
        {
            var module = modules[index];
            try
            {
                await module.StopAsync(cancellationToken).ConfigureAwait(false);
                await PublishAsync(new ModuleStatusChanged(module.Id, ModuleRunState.Stopped), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await PublishAsync(
                    new ModuleStatusChanged(module.Id, ModuleRunState.Failed, exception.Message),
                    CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>发布状态并把订阅者故障聚合抛出，避免状态错误被静默吞掉。</summary>
    private async Task PublishAsync(ModuleStatusChanged status, CancellationToken cancellationToken)
    {
        var result = await eventBus.PublishAsync(status, cancellationToken).ConfigureAwait(false);
        if (result.Failures.Count > 0)
        {
            throw new AggregateException(
                "模块状态订阅者处理失败。", result.Failures.Select(failure => failure.Exception));
        }
    }
}
