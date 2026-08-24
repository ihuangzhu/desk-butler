namespace DeskButler.Desktop.Hosting;

/// <summary>
/// 持有 DeskButler 当前用户会话的单实例互斥量。成功取得后才能创建崩溃标记；
/// 正常退出时应先刷新状态并删除崩溃标记，最后由取得所有权的同一线程释放本 guard。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex mutex;
    private readonly int ownerManagedThreadId;
    private int disposed;

    /// <summary>V1 使用的稳定命名互斥量名称。</summary>
    public const string MutexName = @"Local\DeskButler.SingleInstance.v1";

    private SingleInstanceGuard(Mutex mutex, int ownerManagedThreadId)
    {
        this.mutex = mutex;
        this.ownerManagedThreadId = ownerManagedThreadId;
    }

    /// <summary>尝试取得正式单实例互斥量。</summary>
    public static bool TryAcquire(out SingleInstanceGuard? guard) => TryAcquire(MutexName, out guard);

    /// <summary>使用显式名称尝试取得互斥量，供隔离测试使用。</summary>
    internal static bool TryAcquire(string mutexName, out SingleInstanceGuard? guard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        var candidate = new Mutex(initiallyOwned: false, mutexName);
        bool acquired;
        try
        {
            acquired = candidate.WaitOne(millisecondsTimeout: 0);
        }
        catch (AbandonedMutexException)
        {
            // AbandonedMutexException 表示当前线程已经取得所有权，可安全继续启动。
            acquired = true;
        }
        catch
        {
            candidate.Dispose();
            throw;
        }

        if (!acquired)
        {
            // 未取得所有权的候选句柄必须立即释放，避免二实例探测累积内核资源。
            candidate.Dispose();
            guard = null;
            return false;
        }

        guard = new SingleInstanceGuard(candidate, Environment.CurrentManagedThreadId);
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        if (Environment.CurrentManagedThreadId != ownerManagedThreadId)
        {
            throw new InvalidOperationException("SingleInstanceGuard 必须由取得互斥量的同一托管线程释放。");
        }

        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        finally
        {
            mutex.Dispose();
        }
    }
}
