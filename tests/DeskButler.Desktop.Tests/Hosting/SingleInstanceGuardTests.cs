using System.Diagnostics;
using DeskButler.Desktop.Hosting;

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

    /// <summary>前一线程异常结束遗留的 abandoned mutex 必须被安全接管。</summary>
    [Fact]
    public async Task AbandonedMutexIsAcquiredSafely()
    {
        var mutexName = UniqueMutexName();
        using var observerHandle = new Mutex(initiallyOwned: false, mutexName);
        var startInfo = new ProcessStartInfo(FindMutexOwnerExecutable())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add(mutexName);
        using var ownerProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 abandoned mutex 测试进程。");

        try
        {
            Assert.Equal("acquired", await ownerProcess.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken));
            await ownerProcess.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, ownerProcess.ExitCode);

            Assert.True(SingleInstanceGuard.TryAcquire(mutexName, out var guard));
            guard!.Dispose();
        }
        finally
        {
            if (!ownerProcess.HasExited)
            {
                ownerProcess.Kill(entireProcessTree: true);
                await ownerProcess.WaitForExitAsync(CancellationToken.None);
            }
        }
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
        var configuration = output.Parent?.Name ?? "Debug";
        var repository = output;
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "DeskButler.slnx")))
        {
            repository = repository.Parent;
        }

        if (repository is null)
        {
            throw new DirectoryNotFoundException("无法从测试输出目录定位 DeskButler 仓库根目录。");
        }

        return Path.Combine(
            repository.FullName,
            "tests",
            "DeskButler.Desktop.Tests",
            "TestApps",
            "DeskButler.MutexOwner",
            "bin",
            configuration,
            "net10.0-windows10.0.17763.0",
            "DeskButler.MutexOwner.exe");
    }
}
