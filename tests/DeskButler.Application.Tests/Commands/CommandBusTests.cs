using DeskButler.Application.Commands;

namespace DeskButler.Application.Tests.Commands;

public sealed class CommandBusTests
{
    /// <summary>验证未注册命令会显式失败，避免调用方误认为命令已执行。</summary>
    [Fact]
    public async Task SendAsyncUnregisteredCommandThrowsCommandHandlerNotFoundException()
    {
        var bus = new InProcessCommandBus();

        await Assert.ThrowsAsync<CommandHandlerNotFoundException>(
            () => bus.SendAsync(new UnregisteredCommand(), CancellationToken.None));
    }

    /// <summary>验证命令总线将命令转交给其显式注册的同类型处理器。</summary>
    [Fact]
    public async Task SendAsyncRegisteredCommandReturnsHandlerResponse()
    {
        var bus = new InProcessCommandBus();
        bus.Register(new GreetingCommandHandler());

        var response = await bus.SendAsync(new GreetingCommand("DeskButler"), CancellationToken.None);

        Assert.Equal("你好，DeskButler", response);
    }

    private sealed class UnregisteredCommand : ICommand<string>
    {
        /// <summary>初始化未注册的测试命令。</summary>
        public UnregisteredCommand()
        {
        }
    }

    private sealed class GreetingCommand : ICommand<string>
    {
        /// <summary>初始化包含指定名称的测试命令。</summary>
        public GreetingCommand(string name)
        {
            Name = name;
        }

        /// <summary>获取要用于问候的名称。</summary>
        public string Name { get; }
    }

    private sealed class GreetingCommandHandler : ICommandHandler<GreetingCommand, string>
    {
        /// <summary>返回与输入名称对应的问候文本。</summary>
        public Task<string> HandleAsync(GreetingCommand command, CancellationToken cancellationToken)
        {
            return Task.FromResult($"你好，{command.Name}");
        }
    }
}
