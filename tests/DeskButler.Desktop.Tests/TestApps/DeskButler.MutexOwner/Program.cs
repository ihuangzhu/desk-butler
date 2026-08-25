if (args.Length == 2 &&
    StringComparer.Ordinal.Equals(args[0], "--synchronized-abandon"))
{
    return RunSynchronizedAbandon(args[1]);
}

if (args.Length != 1)
{
    return 2;
}

return RunExitBasedOwner(args[0]);

/// <summary>保留旧协议：当前进程取得 mutex 后立即退出。</summary>
static int RunExitBasedOwner(string mutexName)
{
    var mutex = new Mutex(initiallyOwned: false, mutexName);
    mutex.WaitOne();
    Console.WriteLine("acquired");
    Console.Out.Flush();
    GC.KeepAlive(mutex);
    return 0;
}

/// <summary>让专用 owner 线程按父进程指令终止，同时保持 helper 进程与内核句柄存活。</summary>
static int RunSynchronizedAbandon(string mutexName)
{
    using var abandonOwner = new ManualResetEventSlim();
    Mutex? ownedMutex = null;
    var ownerThread = new Thread(() =>
    {
        ownedMutex = new Mutex(initiallyOwned: false, mutexName);
        ownedMutex.WaitOne();
        Console.WriteLine("acquired");
        Console.Out.Flush();
        abandonOwner.Wait();
        // 不调用 ReleaseMutex：线程终止必须把仍有打开句柄的内核 mutex 标记为 abandoned。
    });
    ownerThread.Start();

    if (!StringComparer.Ordinal.Equals(Console.ReadLine(), "abandon"))
    {
        return 3;
    }

    abandonOwner.Set();
    ownerThread.Join();
    Console.WriteLine("owner-exited");
    Console.Out.Flush();
    if (!StringComparer.Ordinal.Equals(Console.ReadLine(), "exit"))
    {
        return 4;
    }

    GC.KeepAlive(ownedMutex);
    return 0;
}
