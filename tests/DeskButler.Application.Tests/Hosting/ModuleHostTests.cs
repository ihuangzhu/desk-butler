using DeskButler.Application.Hosting;
using DeskButler.Application.Modules;
using DeskButler.Application.Events;

namespace DeskButler.Application.Tests.Hosting;

public sealed class ModuleHostTests
{
    /// <summary>真实事件总线必须收到启动成功和启动失败状态，且失败仍向调用者抛出。</summary>
    [Fact]
    public async Task HostPublishesLifecycleStatusAndRethrowsStartFailure()
    {
        var calls = new List<string>();
        var bus = new InProcessEventBus();
        var statuses = new List<ModuleStatusChanged>();
        using var subscription = bus.Subscribe<ModuleStatusChanged>("test", (status, _) =>
        {
            statuses.Add(status);
            return Task.CompletedTask;
        });
        var host = new ModuleHost([
            new FakeModule("ok", calls),
            new FakeModule("bad", calls, failStart: true)], bus);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ModuleRunState.Running, statuses[0].State);
        Assert.Equal(ModuleRunState.Failed, statuses[1].State);
        Assert.Equal("bad", statuses[1].ModuleId);
        Assert.Contains("controlled start failure", statuses[1].ErrorMessage, StringComparison.Ordinal);
    }

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
        private readonly bool failStart;

        public FakeModule(string id, List<string> calls, bool failStart = false)
        {
            Id = id;
            this.calls = calls;
            this.failStart = failStart;
        }

        /// <summary>获取测试模块的唯一标识。</summary>
        public string Id { get; }

        /// <inheritdoc />
        public ModuleDescriptor Descriptor => new(Id, Id, new Version(1, 0), true, [], [], []);

        /// <summary>记录测试模块的启动操作。</summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (failStart)
            {
                throw new InvalidOperationException("controlled start failure");
            }
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
