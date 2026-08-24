namespace DeskButler.Application.Commands;

/// <summary>定义将命令发送给其显式注册处理器的总线。</summary>
public interface ICommandBus
{
    /// <summary>发送命令并返回已注册处理器的响应。</summary>
    /// <typeparam name="TResponse">命令的响应类型。</typeparam>
    /// <param name="command">待发送的命令。</param>
    /// <param name="cancellationToken">用于取消命令处理的令牌。</param>
    /// <returns>命令处理器返回的响应。</returns>
    /// <exception cref="CommandHandlerNotFoundException">命令类型未注册处理器时抛出。</exception>
    Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken);
}

/// <summary>表示命令总线未找到指定命令处理器的错误。</summary>
public sealed class CommandHandlerNotFoundException : InvalidOperationException
{
    /// <summary>使用未注册命令的运行时类型初始化异常。</summary>
    /// <param name="commandType">未注册命令的运行时类型。</param>
    public CommandHandlerNotFoundException(Type commandType)
        : base($"未找到命令类型“{commandType.FullName}”的处理器。")
    {
        CommandType = commandType;
    }

    /// <summary>获取未注册命令的运行时类型。</summary>
    public Type CommandType { get; }
}
