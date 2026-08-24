using System.Windows.Threading;
using DeskButler.Core.Persistence;
using DeskButler.Core.Scenes;

namespace DeskButler.Desktop.Hosting;

/// <summary>提供后台工作切回 WPF UI 线程的最小边界。</summary>
internal interface IUiDispatcher
{
    /// <summary>排队执行异步 UI 工作，并由实现观察其异常。</summary>
    void Post(Func<Task> action);
}

/// <summary>使用 WPF Dispatcher 串行调度 UI 刷新。</summary>
internal sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    /// <inheritdoc />
    public void Post(Func<Task> action)
    {
        _ = dispatcher.InvokeAsync(action).Task.Unwrap().ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

/// <summary>保存成功后发布不含敏感数据的现场变更通知。</summary>
internal sealed class NotifyingSceneRepository(ISceneRepository inner) : ISceneRepository
{
    /// <summary>在底层保存成功后触发。</summary>
    internal event EventHandler? SceneSaved;

    /// <inheritdoc />
    public async Task SaveAsync(SceneSnapshot snapshot, CancellationToken cancellationToken)
    {
        await inner.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        SceneSaved?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SceneSnapshot>> GetRecentAsync(int maximumCount, CancellationToken cancellationToken) =>
        inner.GetRecentAsync(maximumCount, cancellationToken);

    /// <inheritdoc />
    public Task MarkInvalidAsync(Guid snapshotId, string reason, CancellationToken cancellationToken) =>
        inner.MarkInvalidAsync(snapshotId, reason, cancellationToken);
}

/// <summary>在当前线程立即运行，供非 WPF 宿主与单元测试使用。</summary>
internal sealed class InlineUiDispatcher : IUiDispatcher
{
    /// <inheritdoc />
    public void Post(Func<Task> action) => _ = action();
}
