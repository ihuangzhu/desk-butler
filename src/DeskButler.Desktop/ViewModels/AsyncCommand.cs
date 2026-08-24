using System.Windows.Input;

namespace DeskButler.Desktop.ViewModels;

/// <summary>把异步委托适配为防重入的 WPF 命令。</summary>
public sealed class AsyncCommand : ObservableObject, ICommand
{
    private readonly Func<object?, Task> executeAsync;
    private readonly Func<object?, bool>? canExecute;
    private bool isExecuting;
    private Exception? lastError;

    /// <summary>创建无参数异步命令。</summary>
    public AsyncCommand(Func<Task> executeAsync, Func<bool>? canExecute = null)
        : this(_ => executeAsync(), canExecute is null ? null : _ => canExecute())
    {
        ArgumentNullException.ThrowIfNull(executeAsync);
    }

    /// <summary>创建可接收绑定参数的异步命令。</summary>
    public AsyncCommand(Func<object?, Task> executeAsync, Func<object?, bool>? canExecute = null)
    {
        this.executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        this.canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <summary>获取命令当前是否仍在执行。</summary>
    public bool IsExecuting
    {
        get => isExecuting;
        private set
        {
            if (SetProperty(ref isExecuting, value))
            {
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>获取最近一次执行失败；新执行开始时清空。</summary>
    public Exception? LastError
    {
        get => lastError;
        private set => SetProperty(ref lastError, value);
    }

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !IsExecuting && (canExecute?.Invoke(parameter) ?? true);

    /// <inheritdoc />
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        LastError = null;
        IsExecuting = true;
        try
        {
            await executeAsync(parameter);
        }
        catch (Exception exception)
        {
            // ICommand 的 async void 边界不能向 WPF 消息泵泄漏异常；保留给绑定和诊断观察。
            LastError = exception;
        }
        finally
        {
            IsExecuting = false;
        }
    }

    /// <summary>让拥有额外状态的 ViewModel 主动要求 WPF 重新查询可执行性。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
