namespace DeskButler.Application.Commands;

/// <summary>通过显式泛型注册在当前进程内路由命令。</summary>
public sealed class InProcessCommandBus : ICommandBus
{
    private readonly Dictionary<Type, ICommandHandlerRegistration> handlers = [];

    /// <summary>初始化空的进程内命令处理器注册表。</summary>
    public InProcessCommandBus()
    {
    }

    /// <summary>为指定命令类型注册处理器。</summary>
    /// <typeparam name="TCommand">处理器支持的命令类型。</typeparam>
    /// <typeparam name="TResponse">命令的响应类型。</typeparam>
    /// <param name="handler">要注册的处理器。</param>
    public void Register<TCommand, TResponse>(ICommandHandler<TCommand, TResponse> handler)
        where TCommand : ICommand<TResponse>
    {
        ArgumentNullException.ThrowIfNull(handler);
        handlers[typeof(TCommand)] = new CommandHandlerRegistration<TCommand, TResponse>(handler);
    }

    /// <inheritdoc />
    public async Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var commandType = command.GetType();
        if (!handlers.TryGetValue(commandType, out var registration))
        {
            throw new CommandHandlerNotFoundException(commandType);
        }

        var response = await registration.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        return (TResponse)response!;
    }

    /// <summary>定义命令处理器注册项的非泛型调用边界。</summary>
    private interface ICommandHandlerRegistration
    {
        /// <summary>使用运行时命令实例调用已注册处理器。</summary>
        /// <param name="command">待处理的命令实例。</param>
        /// <param name="cancellationToken">用于取消命令处理的令牌。</param>
        /// <returns>装箱后的命令响应。</returns>
        Task<object?> HandleAsync(object command, CancellationToken cancellationToken);
    }

    /// <summary>保存某一强类型命令处理器，并将其适配为内部调用边界。</summary>
    /// <typeparam name="TCommand">处理器支持的命令类型。</typeparam>
    /// <typeparam name="TResponse">命令的响应类型。</typeparam>
    private sealed class CommandHandlerRegistration<TCommand, TResponse> : ICommandHandlerRegistration
        where TCommand : ICommand<TResponse>
    {
        private readonly ICommandHandler<TCommand, TResponse> handler;

        /// <summary>使用强类型命令处理器初始化注册项。</summary>
        /// <param name="handler">已显式注册的命令处理器。</param>
        public CommandHandlerRegistration(ICommandHandler<TCommand, TResponse> handler)
        {
            this.handler = handler;
        }

        /// <inheritdoc />
        public async Task<object?> HandleAsync(object command, CancellationToken cancellationToken)
        {
            return await handler.HandleAsync((TCommand)command, cancellationToken).ConfigureAwait(false);
        }
    }
}
