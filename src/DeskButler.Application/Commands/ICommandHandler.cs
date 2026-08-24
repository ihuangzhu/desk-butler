namespace DeskButler.Application.Commands;

/// <summary>定义某一命令类型的同步进程内处理器。</summary>
/// <typeparam name="TCommand">处理器可处理的命令类型。</typeparam>
/// <typeparam name="TResponse">命令的响应类型。</typeparam>
public interface ICommandHandler<TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    /// <summary>处理命令并返回其响应。</summary>
    /// <param name="command">待处理的命令。</param>
    /// <param name="cancellationToken">用于取消处理操作的令牌。</param>
    /// <returns>命令的处理响应。</returns>
    Task<TResponse> HandleAsync(TCommand command, CancellationToken cancellationToken);
}
