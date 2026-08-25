using DeskButler.Application.Hosting;
using DeskButler.Application.Modules;
using DeskButler.Application.Events;

namespace DeskButler.Application.Tests.Hosting;

public sealed class ModuleHostTests
{
    /// <summary>Running 观察者失败不得把已成功启动的模块误报为 Failed。</summary>
    [Fact]
    public async Task RunningSubscriberFailureDoesNotChangeSuccessfulStartResult()
    {
        var calls = new List<string>();
        var statuses = new List<ModuleStatusChanged>();
        var observedFailures = new List<Exception>();
        var subscriberFailure = new InvalidOperationException("running observer failure");
        var bus = new InProcessEventBus();
        using var subscription = bus.Subscribe<ModuleStatusChanged>("failing-running", (status, _) =>
        {
            statuses.Add(status);
            return status.State == ModuleRunState.Running
                ? Task.FromException(subscriberFailure)
                : Task.CompletedTask;
        });
        var host = new ModuleHost(
            [new FakeModule("ok", calls)], bus, observedFailures.Add);

        await host.StartAsync(CancellationToken.None);

        Assert.Equal(["start:ok"], calls);
        Assert.DoesNotContain(statuses, status => status.State == ModuleRunState.Failed);
        Assert.Same(subscriberFailure, Assert.Single(observedFailures));
    }

    /// <summary>启动与 Failed 观察同时失败时必须保留原始启动异常身份。</summary>
    [Fact]
    public async Task StartFailureHasPriorityOverFailedSubscriberFailure()
    {
        var calls = new List<string>();
        var observedFailures = new List<Exception>();
        var startFailure = new InvalidOperationException("start failure");
        var subscriberFailure = new IOException("failed observer failure");
        var bus = new InProcessEventBus();
        using var subscription = bus.Subscribe<ModuleStatusChanged>("failing-failed", (status, _) =>
            status.State == ModuleRunState.Failed
                ? Task.FromException(subscriberFailure)
                : Task.CompletedTask);
        var host = new ModuleHost(
            [new FakeModule("bad", calls, startFailure: startFailure)], bus, observedFailures.Add);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync(CancellationToken.None));

        Assert.Same(startFailure, thrown);
        Assert.Same(subscriberFailure, Assert.Single(observedFailures));
    }

    /// <summary>Stopped 观察者失败不得把已成功停止的模块误报为 Failed。</summary>
    [Fact]
    public async Task StoppedSubscriberFailureDoesNotChangeSuccessfulStopResult()
    {
        var calls = new List<string>();
        var statuses = new List<ModuleStatusChanged>();
        var observedFailures = new List<Exception>();
        var subscriberFailure = new InvalidOperationException("stopped observer failure");
        var bus = new InProcessEventBus();
        using var subscription = bus.Subscribe<ModuleStatusChanged>("failing-stopped", (status, _) =>
        {
            statuses.Add(status);
            return status.State == ModuleRunState.Stopped
                ? Task.FromException(subscriberFailure)
                : Task.CompletedTask;
        });
        var host = new ModuleHost(
            [new FakeModule("ok", calls)], bus, observedFailures.Add);

        await host.StopAsync(CancellationToken.None);

        Assert.Equal(["stop:ok"], calls);
        Assert.DoesNotContain(statuses, status => status.State == ModuleRunState.Failed);
        Assert.Same(subscriberFailure, Assert.Single(observedFailures));
    }

    /// <summary>停止与 Failed 观察同时失败时必须保留原始停止异常身份。</summary>
    [Fact]
    public async Task StopFailureHasPriorityOverFailedSubscriberFailure()
    {
        var calls = new List<string>();
        var observedFailures = new List<Exception>();
        var stopFailure = new InvalidOperationException("stop failure");
        var subscriberFailure = new IOException("failed observer failure");
        var bus = new InProcessEventBus();
        using var subscription = bus.Subscribe<ModuleStatusChanged>("failing-failed", (status, _) =>
            status.State == ModuleRunState.Failed
                ? Task.FromException(subscriberFailure)
                : Task.CompletedTask);
        var host = new ModuleHost(
            [new FakeModule("bad", calls, stopFailure: stopFailure)], bus, observedFailures.Add);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StopAsync(CancellationToken.None));

        Assert.Same(stopFailure, thrown);
        Assert.Same(subscriberFailure, Assert.Single(observedFailures));
    }

    /// <summary>最终诊断接收器自身失败不得污染已成功的生命周期结果。</summary>
    [Fact]
    public async Task DiagnosticSinkFailureDoesNotChangeLifecycleResult()
    {
        var calls = new List<string>();
        var bus = new InProcessEventBus();
        using var subscription = bus.Subscribe<ModuleStatusChanged>(
            "failing-running", (_, _) => Task.FromException(new IOException("observer failure")));
        var host = new ModuleHost(
            [new FakeModule("ok", calls)], bus, _ => throw new InvalidOperationException("sink failure"));

        await host.StartAsync(CancellationToken.None);

        Assert.Equal(["start:ok"], calls);
    }

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
        private readonly Exception? startFailure;
        private readonly Exception? stopFailure;

        /// <summary>创建可记录调用并按需抛出指定生命周期异常的测试模块。</summary>
        public FakeModule(
            string id,
            List<string> calls,
            bool failStart = false,
            Exception? startFailure = null,
            Exception? stopFailure = null)
        {
            Id = id;
            this.calls = calls;
            this.failStart = failStart;
            this.startFailure = startFailure;
            this.stopFailure = stopFailure;
        }

        /// <summary>获取测试模块的唯一标识。</summary>
        public string Id { get; }

        /// <inheritdoc />
        public ModuleDescriptor Descriptor => new(Id, Id, new Version(1, 0), true, [], [], []);

        /// <summary>记录测试模块的启动操作。</summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (startFailure is not null)
            {
                throw startFailure;
            }

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
            if (stopFailure is not null)
            {
                throw stopFailure;
            }

            calls.Add($"stop:{Id}");
            return Task.CompletedTask;
        }
    }
}
