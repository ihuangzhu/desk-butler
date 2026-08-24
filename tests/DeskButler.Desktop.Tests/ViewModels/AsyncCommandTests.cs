using DeskButler.Desktop.ViewModels;

namespace DeskButler.Desktop.Tests.ViewModels;

public sealed class AsyncCommandTests
{
    /// <summary>异步菜单操作失败必须留在可观察命令状态中，不能逃逸并终止托盘进程。</summary>
    [Fact]
    public async Task ExecutionFailureIsCapturedWithoutEscapingAsyncVoidBoundary()
    {
        var expected = new InvalidOperationException("restore failed");
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncCommand(() => Task.FromException(expected));
        command.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(AsyncCommand.IsExecuting) && !command.IsExecuting)
            {
                completed.TrySetResult();
            }
        };

        command.Execute(null);
        await completed.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Same(expected, command.LastError);
        Assert.False(command.IsExecuting);
    }
}
