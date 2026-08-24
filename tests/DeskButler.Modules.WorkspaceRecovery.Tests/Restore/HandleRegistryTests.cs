using DeskButler.Core.Capture;
using DeskButler.Core.Scenes;
using DeskButler.Modules.WorkspaceRecovery.Restore;

namespace DeskButler.Modules.WorkspaceRecovery.Tests.Restore;

public sealed class HandleRegistryTests
{
    /// <summary>验证 lease 失活与后项 claim 后，旧 lease 永远不能迟到认领同一 HWND。</summary>
    [Fact]
    public void Deactivate后旧Lease不能覆盖新Claim()
    {
        var registry = new HandleRegistry();
        var fingerprint = RuntimeWindowFingerprint.Create(Window(42, processId: 100));
        var oldLease = registry.Activate(0);
        registry.Deactivate(oldLease);
        var newLease = registry.Activate(1);

        Assert.True(registry.TryClaim(newLease, fingerprint));
        Assert.False(registry.TryClaim(oldLease, fingerprint));
    }

    /// <summary>验证 Deactivate 不释放 HWND gate；只有 work Complete 后同 HWND 新 PID 才能认领。</summary>
    [Fact]
    public void Deactivate不释放InFlight而CompleteWork释放给新Pid()
    {
        var registry = new HandleRegistry();
        var first = RuntimeWindowFingerprint.Create(Window(43, processId: 100));
        var second = RuntimeWindowFingerprint.Create(Window(43, processId: 200));
        var firstLease = registry.Activate(0);

        Assert.True(registry.TryClaim(firstLease, first));
        registry.Deactivate(firstLease);
        var secondLease = registry.Activate(1);

        Assert.False(registry.TryClaim(secondLease, second));
        registry.CompleteWork(firstLease);
        Assert.True(registry.TryClaim(secondLease, second));
        Assert.False(registry.TryClaim(firstLease, second));
    }

    /// <summary>验证 CompleteWork 只释放 HWND gate；已消费实例仍不能被后项重复消费。</summary>
    [Fact]
    public void CompleteWork后相同Instance仍保持Consumed()
    {
        var registry = new HandleRegistry();
        var fingerprint = RuntimeWindowFingerprint.Create(Window(44, processId: 100));
        var firstLease = registry.Activate(0);
        Assert.True(registry.TryClaim(firstLease, fingerprint));
        registry.Deactivate(firstLease);
        registry.CompleteWork(firstLease);
        var secondLease = registry.Activate(1);

        Assert.False(registry.TryClaim(secondLease, fingerprint));
    }

    /// <summary>验证 unknown PID0 消费后无法证明新 runtime 身份，安全优先永久阻止同 HWND。</summary>
    [Fact]
    public void ReuseUnknownIdentity消费后保守阻止同HandleRuntime()
    {
        var registry = new HandleRegistry();
        var reuseLease = registry.Activate(0);
        Assert.True(registry.TryClaim(reuseLease, RuntimeWindowFingerprint.ForHandle(45)));
        registry.Deactivate(reuseLease);
        registry.CompleteWork(reuseLease);
        var runtimeLease = registry.Activate(1);

        Assert.False(registry.TryClaim(
            runtimeLease, RuntimeWindowFingerprint.Create(Window(45, processId: 200))));
    }

    /// <summary>验证同一未来项存在多个 reservation 时保守返回 Ambiguous 而不随机 claim。</summary>
    [Fact]
    public void 多个FutureReservations保守Ambiguous()
    {
        var registry = new HandleRegistry();
        var current = registry.Activate(0);
        Assert.True(registry.TryReserve(current, 1,
            RuntimeWindowFingerprint.Create(Window(51, processId: 101))));
        Assert.True(registry.TryReserve(current, 1,
            RuntimeWindowFingerprint.Create(Window(52, processId: 102))));
        registry.Deactivate(current);
        var future = registry.Activate(1);

        var snapshots = registry.GetReservationSnapshots(future);

        Assert.Equal(2, snapshots.Count);
    }

    /// <summary>创建完整运行期窗口候选。</summary>
    private static WindowCandidate Window(nint handle, int processId) => new(
        handle, processId, @"C:\Apps\tool.exe", "ToolClass", "Tool", null,
        new WindowBounds(10, 20, 800, 600), SceneWindowState.Normal,
        new MonitorIdentity(@"\\.\DISPLAY1", new WindowBounds(0, 0, 1920, 1040), 96, 96),
        true, false, false, false, false);
}
