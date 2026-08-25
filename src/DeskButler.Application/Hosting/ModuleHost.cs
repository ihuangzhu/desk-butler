using DeskButler.Application.Modules;
using DeskButler.Application.Events;

namespace DeskButler.Application.Hosting;

/// <summary>以确定顺序管理编译期注册模块的生命周期。</summary>
public sealed class ModuleHost
{
    private readonly IModule[] modules;
    private readonly IEventBus eventBus;
    private readonly Action<Exception> reportEventFailure;

    /// <summary>使用按启动顺序排列的模块初始化宿主。</summary>
    /// <param name="modules">按正序启动、逆序停止的模块集合。</param>
    public ModuleHost(IEnumerable<IModule> modules)
        : this(modules, new InProcessEventBus())
    {
    }

    /// <summary>使用按启动顺序排列的模块和共享生产事件总线初始化宿主。</summary>
    public ModuleHost(IEnumerable<IModule> modules, IEventBus eventBus)
        : this(modules, eventBus, null)
    {
    }

    /// <summary>使用共享事件总线和最终诊断接收器初始化宿主。</summary>
    public ModuleHost(
        IEnumerable<IModule> modules,
        IEventBus eventBus,
        Action<Exception>? reportEventFailure)
    {
        ArgumentNullException.ThrowIfNull(modules);
        this.modules = modules.ToArray();
        this.eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        this.reportEventFailure = reportEventFailure ?? (_ => { });
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
            }
            catch (Exception exception)
            {
                await PublishBestEffortAsync(
                    new ModuleStatusChanged(module.Id, ModuleRunState.Failed, exception.Message),
                    CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            await PublishBestEffortAsync(
                new ModuleStatusChanged(module.Id, ModuleRunState.Running), cancellationToken)
                .ConfigureAwait(false);
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
            }
            catch (Exception exception)
            {
                await PublishBestEffortAsync(
                    new ModuleStatusChanged(module.Id, ModuleRunState.Failed, exception.Message),
                    CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            await PublishBestEffortAsync(
                new ModuleStatusChanged(module.Id, ModuleRunState.Stopped), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>尽力发布真实生命周期状态，并把所有观察失败交给最终诊断边界。</summary>
    private async Task PublishBestEffortAsync(
        ModuleStatusChanged status,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await eventBus.PublishAsync(status, cancellationToken).ConfigureAwait(false);
            foreach (var failure in result.Failures)
            {
                ReportBestEffort(failure.Exception);
            }
        }
        catch (Exception exception)
        {
            ReportBestEffort(exception);
        }
    }

    /// <summary>调用最终诊断接收器，且永不让其自身故障改变模块生命周期结果。</summary>
    private void ReportBestEffort(Exception exception)
    {
        try
        {
            reportEventFailure(exception);
        }
        catch
        {
            // 最终观察边界不得污染模块真实结果。
        }
    }
}
