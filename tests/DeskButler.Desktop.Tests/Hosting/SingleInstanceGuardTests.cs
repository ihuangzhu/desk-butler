using System.Diagnostics;
using DeskButler.Desktop.Hosting;
using DeskButler.Desktop.Tests;

namespace DeskButler.Desktop.Tests.Hosting;

public sealed class SingleInstanceGuardTests
{
    /// <summary>公开默认入口必须使用产品约定的稳定 V1 mutex 名称。</summary>
    [Fact]
    public void DefaultMutexNameMatchesStableV1Contract()
    {
        Assert.Equal(@"Local\DeskButler.SingleInstance.v1", SingleInstanceGuard.MutexName);
    }

    /// <summary>首实例必须成功，第二线程模拟的另一实例必须立即失败。</summary>
    [Fact]
    public void FirstInstanceSucceedsAndSecondInstanceFails()
    {
        var mutexName = UniqueMutexName();
        Assert.True(SingleInstanceGuard.TryAcquire(mutexName, out var first));
        Assert.NotNull(first);

        var secondAcquired = TryAcquireOnAnotherThread(mutexName);

        Assert.False(secondAcquired);
        first.Dispose();
    }

    /// <summary>拥有者释放互斥量后，后续实例必须能够取得所有权。</summary>
    [Fact]
    public void ReleasedMutexCanBeAcquiredAgain()
    {
        var mutexName = UniqueMutexName();
        Assert.True(SingleInstanceGuard.TryAcquire(mutexName, out var first));
        first!.Dispose();

        Assert.True(SingleInstanceGuard.TryAcquire(mutexName, out var second));
        second!.Dispose();
    }

    /// <summary>owner 线程明确终止后，仍存活 helper 中的 abandoned mutex 必须在压力循环中立即接管。</summary>
    [Fact]
    public async Task SynchronizedOwnerThreadExitIsAcquiredSafelyUnderStress()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            await VerifySynchronizedAbandonAsync(iteration, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>执行一轮父子握手，分别证明 owner 存活和 owner 已终止两个互斥量状态。</summary>
    private static async Task VerifySynchronizedAbandonAsync(int iteration, CancellationToken cancellationToken)
    {
        var mutexName = $@"Local\DeskButler.Tests.Abandon.{iteration}.{Guid.NewGuid():N}";
        using var observerHandle = new Mutex(initiallyOwned: false, mutexName);
        using var ownerProcess = StartSynchronizedMutexOwner(mutexName);

        try
        {
            Assert.Equal("acquired", await ownerProcess.StandardOutput.ReadLineAsync(cancellationToken));
            Assert.False(ownerProcess.HasExited);
            Assert.False(SingleInstanceGuard.TryAcquire(mutexName, out var prematureGuard));
            Assert.Null(prematureGuard);

            await ownerProcess.StandardInput.WriteLineAsync("abandon");
            Assert.Equal("owner-exited", await ownerProcess.StandardOutput.ReadLineAsync(cancellationToken));
            Assert.False(ownerProcess.HasExited);

            Assert.True(SingleInstanceGuard.TryAcquire(mutexName, out var guard));
            guard!.Dispose();

            await ownerProcess.StandardInput.WriteLineAsync("exit");
            await ownerProcess.WaitForExitAsync(cancellationToken);
            Assert.Equal(0, ownerProcess.ExitCode);
        }
        finally
        {
            if (!ownerProcess.HasExited)
            {
                await ownerProcess.StandardInput.WriteLineAsync("abandon");
                await ownerProcess.StandardInput.WriteLineAsync("exit");
                if (!ownerProcess.WaitForExit(1000))
                {
                    ownerProcess.Kill(entireProcessTree: true);
                    await ownerProcess.WaitForExitAsync(CancellationToken.None);
                }
            }
        }
    }

    /// <summary>启动支持 owner 线程终止握手的真实 MutexOwner helper。</summary>
    private static Process StartSynchronizedMutexOwner(string mutexName)
    {
        var startInfo = new ProcessStartInfo(FindMutexOwnerExecutable())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("--synchronized-abandon");
        startInfo.ArgumentList.Add(mutexName);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 synchronized abandoned mutex 测试进程。");
    }

    /// <summary>跨线程 Dispose 必须明确失败，且不破坏拥有者随后正确释放。</summary>
    [Fact]
    public void DisposeOnNonOwnerThreadThrowsAndOwnerCanStillDispose()
    {
        var mutexName = UniqueMutexName();
        Assert.True(SingleInstanceGuard.TryAcquire(mutexName, out var guard));
        Exception? failure = null;
        var wrongThread = new Thread(() =>
        {
            try
            {
                guard!.Dispose();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        wrongThread.Start();
        wrongThread.Join();

        Assert.IsType<InvalidOperationException>(failure);
        guard!.Dispose();
        guard.Dispose();
    }

    private static bool TryAcquireOnAnotherThread(string mutexName)
    {
        var acquired = false;
        var thread = new Thread(() =>
        {
            acquired = SingleInstanceGuard.TryAcquire(mutexName, out var guard);
            guard?.Dispose();
        });
        thread.Start();
        thread.Join();
        return acquired;
    }

    private static string UniqueMutexName() => $@"Local\DeskButler.Tests.{Guid.NewGuid():N}";

    private static string FindMutexOwnerExecutable()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = FindTestConfiguration(output);

        return Path.Combine(
            TestRepository.Root,
            "tests",
            "DeskButler.Desktop.Tests",
            "TestApps",
            "DeskButler.MutexOwner",
            "bin",
            configuration,
            "net10.0-windows10.0.17763.0",
            "DeskButler.MutexOwner.exe");
    }

    /// <summary>从 bin 下的首级目录读取当前测试配置，兼容其后的 TFM 与 RID 层级。</summary>
    private static string FindTestConfiguration(DirectoryInfo output)
    {
        var current = output;
        while (current.Parent is not null &&
               !StringComparer.OrdinalIgnoreCase.Equals(current.Parent.Name, "bin"))
        {
            current = current.Parent;
        }

        return current.Parent is not null
            ? current.Name
            : throw new DirectoryNotFoundException("无法从测试输出目录定位构建配置。");
    }
}
