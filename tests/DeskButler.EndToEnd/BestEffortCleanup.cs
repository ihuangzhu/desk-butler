namespace DeskButler.EndToEnd;

/// <summary>逐项执行精确资源清理，单项失败不阻断其余资源，并在末尾聚合全部错误。</summary>
internal static class BestEffortCleanup
{
    /// <summary>先尝试所有资源释放，再无条件尝试连接池/临时目录等最终清理。</summary>
    internal static async Task RunAsync(
        IEnumerable<Func<ValueTask>> resourceCleanup,
        IEnumerable<Func<ValueTask>> finalCleanup)
    {
        var failures = new List<Exception>();
        await RunAllAsync(resourceCleanup, failures);
        await RunAllAsync(finalCleanup, failures);
        if (failures.Count > 0)
        {
            throw new AggregateException("一个或多个受控 fixture 清理步骤失败。", failures);
        }
    }

    private static async Task RunAllAsync(IEnumerable<Func<ValueTask>> operations, List<Exception> failures)
    {
        foreach (var operation in operations)
        {
            try
            {
                await operation();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
    }
}
