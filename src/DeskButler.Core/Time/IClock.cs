namespace DeskButler.Core.Time;

/// <summary>提供可替换的时间读取和异步等待抽象。</summary>
public interface IClock
{
    /// <summary>获取当前 UTC 时刻。</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>以可取消的方式异步等待指定时长。</summary>
    /// <param name="delay">等待时长。</param>
    /// <param name="cancellationToken">取消等待的令牌。</param>
    /// <returns>等待完成后结束的任务。</returns>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}
