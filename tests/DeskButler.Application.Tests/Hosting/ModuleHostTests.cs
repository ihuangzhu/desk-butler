using DeskButler.Application.Hosting;
using DeskButler.Application.Modules;

namespace DeskButler.Application.Tests.Hosting;

public sealed class ModuleHostTests
{
    /// <summary>验证模块宿主以启动顺序的相反顺序停止模块。</summary>
    [Fact]
    public async Task HostStopsModulesInReverseStartOrder()
    {
        var calls = new List<string>();
        var host = new ModuleHost([new FakeModule("a", calls), new FakeModule("b", calls)]);

        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);

        Assert.Equal(["start:a", "start:b", "stop:b", "stop:a"], calls);
    }

    private sealed class FakeModule : IModule
    {
        private readonly List<string> calls;

        /// <summary>初始化测试模块及其调用记录集合。</summary>
        public FakeModule(string id, List<string> calls)
        {
            Id = id;
            this.calls = calls;
        }

        /// <summary>获取测试模块的唯一标识。</summary>
        public string Id { get; }

        /// <summary>记录测试模块的启动操作。</summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            calls.Add($"start:{Id}");
            return Task.CompletedTask;
        }

        /// <summary>记录测试模块的停止操作。</summary>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            calls.Add($"stop:{Id}");
            return Task.CompletedTask;
        }
    }
}
