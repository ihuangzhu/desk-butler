namespace DeskButler.Desktop.Hosting;

/// <summary>表示一个可独立重试的异步清理步骤。</summary>
internal sealed record CleanupStep(string Name, Func<ValueTask> RunAsync);

/// <summary>逐步尽力清理；成功步骤只执行一次，失败步骤可在后续调用重试。</summary>
internal sealed class BestEffortAsyncCleanup(IEnumerable<CleanupStep> steps)
{
    private readonly CleanupState[] states = steps.Select(step => new CleanupState(step)).ToArray();

    /// <summary>获取所有步骤是否均已成功完成。</summary>
    internal bool IsComplete => states.All(state => state.Completed);

    /// <summary>执行全部未完成步骤，并在最后汇总本轮错误。</summary>
    internal async ValueTask RunAsync()
    {
        var failures = new List<Exception>();
        foreach (var state in states.Where(state => !state.Completed))
        {
            try
            {
                await state.Step.RunAsync();
                state.Completed = true;
            }
            catch (Exception exception)
            {
                failures.Add(new InvalidOperationException($"清理步骤“{state.Step.Name}”失败。", exception));
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("DeskButler 清理未完全成功。", failures);
        }
    }

    private sealed class CleanupState(CleanupStep step)
    {
        internal CleanupStep Step { get; } = step;

        internal bool Completed { get; set; }
    }
}

/// <summary>保证组合清理失败时仍按次序清理 marker 与单实例互斥量。</summary>
internal static class ExitCleanupCoordinator
{
    /// <summary>组合清理最多尝试两次，随后无条件执行两个进程身份清理动作。</summary>
    internal static async Task<Exception?> RunAsync(
        Func<ValueTask> disposeComposition,
        Action<bool> releaseMarker,
        Action releaseSingleInstance)
    {
        var failures = new List<Exception>();
        var compositionClean = false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await disposeComposition();
                compositionClean = true;
                break;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        RunSync(() => releaseMarker(compositionClean), failures);
        RunSync(releaseSingleInstance, failures);
        return failures.Count == 0 ? null : new AggregateException("退出清理存在失败。", failures);
    }

    /// <summary>隔离单个同步身份清理错误，使后续动作仍可执行。</summary>
    private static void RunSync(Action action, List<Exception> failures)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }
}
