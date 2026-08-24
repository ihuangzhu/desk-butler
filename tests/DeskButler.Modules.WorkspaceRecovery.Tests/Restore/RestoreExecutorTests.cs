using DeskButler.Core.Capture;
using DeskButler.Core.Restore;
using DeskButler.Core.Scenes;
using DeskButler.Core.Time;
using DeskButler.Modules.WorkspaceRecovery.Restore;
using DeskButler.Modules.WorkspaceRecovery.Tests.Capture;

namespace DeskButler.Modules.WorkspaceRecovery.Tests.Restore;

public sealed class RestoreExecutorTests
{
    /// <summary>验证一个启动失败只失败当前项，后续启动与定位仍会执行。</summary>
    [Fact]
    public async Task ExecuteAsync在一项启动失败后继续后续项()
    {
        var clock = new FakeClock();
        var launcher = new FakeLauncher(scene =>
        {
            if (scene.Id == "bad")
            {
                throw new InvalidOperationException("模拟启动失败");
            }
        });
        var inventory = new MutableInventory();
        launcher.AfterLaunch = scene => inventory.Windows = [Window(201, scene)];
        var positioner = new RecordingPositioner();
        var executor = CreateExecutor(launcher, inventory, positioner, clock);

        var result = await executor.ExecuteAsync(Plan(
            Launch(App("bad", @"C:\Apps\bad.exe")),
            Launch(App("good", @"C:\Apps\good.exe"))), CancellationToken.None);

        Assert.Equal(RestoreItemStatus.Failed, result.Item("bad").Status);
        Assert.Equal(RestoreItemStatus.Succeeded, result.Item("good").Status);
        Assert.Equal(["bad", "good"], launcher.LaunchedIds);
        Assert.Equal([(nint)201], positioner.Handles);
    }

    /// <summary>验证调用方取消会取消当前及全部未开始项，且启动器契约不向执行器暴露可终止进程。</summary>
    [Fact]
    public async Task ExecuteAsync取消后不启动后续且不终止已启动进程()
    {
        var clock = new FakeClock();
        using var cancellation = new CancellationTokenSource();
        var launcher = new FakeLauncher(_ => cancellation.Cancel());
        var executor = CreateExecutor(launcher, new MutableInventory(), new RecordingPositioner(), clock);

        var result = await executor.ExecuteAsync(Plan(
            Launch(App("started", @"C:\Apps\started.exe")),
            Launch(App("later", @"C:\Apps\later.exe"))), cancellation.Token);

        Assert.Equal(RestoreItemStatus.Cancelled, result.Item("started").Status);
        Assert.Equal(RestoreItemStatus.Cancelled, result.Item("later").Status);
        Assert.Equal(["started"], launcher.LaunchedIds);
    }

    /// <summary>验证 Reuse 只定位计划中固定 HWND，不重新枚举或启动。</summary>
    [Fact]
    public async Task ExecuteAsync的Reuse直接定位计划窗口()
    {
        var launcher = new FakeLauncher();
        var inventory = new MutableInventory();
        var positioner = new RecordingPositioner();
        var executor = CreateExecutor(launcher, inventory, positioner, new FakeClock());

        var result = await executor.ExecuteAsync(
            Plan(new RestorePlanItem(App("reuse", @"C:\Apps\reuse.exe"), RestoreDisposition.Reuse, 77)),
            CancellationToken.None);

        Assert.Equal(RestoreItemStatus.Succeeded, result.Item("reuse").Status);
        Assert.Equal([(nint)77], positioner.Handles);
        Assert.Empty(launcher.LaunchedIds);
        Assert.Equal(0, inventory.CaptureCount);
    }

    /// <summary>验证规划器的三种保守跳过决策不会被执行器升级或调用任何平台依赖。</summary>
    [Fact]
    public async Task ExecuteAsync保持SkipMissingUnsafe为Skipped且不执行()
    {
        var launcher = new FakeLauncher();
        var inventory = new MutableInventory();
        var positioner = new RecordingPositioner();
        var executor = CreateExecutor(launcher, inventory, positioner, new FakeClock());

        var result = await executor.ExecuteAsync(Plan(
            new RestorePlanItem(App("ambiguous", @"C:\Apps\a.exe"), RestoreDisposition.SkipAmbiguous, null),
            new RestorePlanItem(App("unsafe", @"C:\Apps\u.exe"), RestoreDisposition.SkipUnsafe, null),
            new RestorePlanItem(App("missing", @"C:\Apps\m.exe"), RestoreDisposition.MissingPath, null)),
            CancellationToken.None);

        Assert.All(result.Items, item => Assert.Equal(RestoreItemStatus.Skipped, item.Status));
        Assert.Empty(launcher.LaunchedIds);
        Assert.Empty(positioner.Handles);
        Assert.Equal(0, inventory.CaptureCount);
    }

    /// <summary>验证单项从 baseline 到超时恰好使用 30 秒预算、500ms tick，且启动不重试。</summary>
    [Fact]
    public async Task ExecuteAsync单项30秒每500毫秒轮询且同次不重试()
    {
        var clock = new FakeClock();
        var timers = new AdvancingPollingTimerFactory(clock);
        var launcher = new FakeLauncher();
        var inventory = new MutableInventory();
        var executor = new RestoreExecutor(launcher, inventory, new RecordingPositioner(), clock, timers);

        var result = await executor.ExecuteAsync(
            Plan(Launch(App("slow", @"C:\Apps\slow.exe"))), CancellationToken.None);

        Assert.Equal(RestoreItemStatus.Failed, result.Item("slow").Status);
        Assert.Equal(["slow"], launcher.LaunchedIds);
        Assert.Equal(60, timers.TickCount);
        Assert.All(timers.RequestedIntervals, interval => Assert.Equal(TimeSpan.FromMilliseconds(500), interval));
        Assert.Equal(TimeSpan.FromSeconds(30), clock.UtcNow - clock.Start);
    }

    /// <summary>验证启动前已存在的相似 HWND 不会被当作本次启动产生的新窗口。</summary>
    [Fact]
    public async Task ExecuteAsync只接受启动后新增窗口()
    {
        var scene = App("target", @"C:\Apps\target.exe");
        var clock = new FakeClock();
        var inventory = new MutableInventory { Windows = [Window(301, scene)] };
        var positioner = new RecordingPositioner();
        var executor = CreateExecutor(new FakeLauncher(), inventory, positioner, clock);

        var result = await executor.ExecuteAsync(Plan(Launch(scene)), CancellationToken.None);

        Assert.Equal(RestoreItemStatus.Failed, result.Item("target").Status);
        Assert.Empty(positioner.Handles);
    }

    /// <summary>验证当前项为未来唯一归属者保留窗口，未来项直接消费且不会重复启动。</summary>
    [Fact]
    public async Task ExecuteAsync不抢占更匹配未来计划项的新窗口()
    {
        var current = App("generic", @"C:\Apps\editor.exe", "GenericClass", null);
        var future = App("specific", @"C:\Apps\editor.exe", "EditorClass", "Specific");
        var clock = new FakeClock();
        var inventory = new MutableInventory();
        var launcher = new FakeLauncher();
        launcher.AfterLaunch = scene =>
        {
            if (scene.Id == "generic")
            {
                inventory.Windows = [Window(401, future)];
            }
        };
        var positioner = new RecordingPositioner();
        var executor = CreateExecutor(launcher, inventory, positioner, clock);

        var result = await executor.ExecuteAsync(Plan(Launch(current), Launch(future)), CancellationToken.None);

        Assert.Equal(RestoreItemStatus.Failed, result.Item("generic").Status);
        Assert.Equal(RestoreItemStatus.Succeeded, result.Item("specific").Status);
        Assert.Equal(["generic"], launcher.LaunchedIds);
        Assert.Equal([(nint)401], positioner.Handles);
    }

    /// <summary>验证目标窗口出现后定位失败只失败该项，不重新等待且不阻塞后续恢复。</summary>
    [Fact]
    public async Task ExecuteAsync窗口出现后定位失败仅当前项Failed()
    {
        var first = App("first", @"C:\Apps\first.exe");
        var second = App("second", @"C:\Apps\second.exe");
        var clock = new FakeClock();
        var inventory = new MutableInventory();
        var nextHandle = 500;
        var launcher = new FakeLauncher { AfterLaunch = scene => inventory.Windows = [Window(++nextHandle, scene)] };
        var positioner = new RecordingPositioner(failingHandles: [(nint)501]);
        var executor = CreateExecutor(launcher, inventory, positioner, clock);

        var result = await executor.ExecuteAsync(Plan(Launch(first), Launch(second)), CancellationToken.None);

        Assert.Equal(RestoreItemStatus.Failed, result.Item("first").Status);
        Assert.Equal(RestoreItemStatus.Succeeded, result.Item("second").Status);
        Assert.Equal([(nint)501, (nint)502], positioner.Handles);
        Assert.Equal(["first", "second"], launcher.LaunchedIds);
    }

    /// <summary>验证 Launch 操作本身也受单项 30 秒预算约束，超时后继续下一项。</summary>
    [Fact]
    public async Task ExecuteAsync启动阶段超时只失败当前项并继续()
    {
        var clock = new FakeClock();
        var launcher = new BlockingFirstLauncher();
        var inventory = new MutableInventory();
        launcher.AfterSuccessfulLaunch = scene => inventory.Windows = [Window(601, scene)];
        var executor = CreateExecutor(launcher, inventory, new RecordingPositioner(), clock);

        var execution = executor.ExecuteAsync(Plan(
            Launch(App("blocked", @"C:\Apps\blocked.exe")),
            Launch(App("next", @"C:\Apps\next.exe"))), CancellationToken.None);
        await launcher.Entered.Task;
        await clock.AdvanceAsync(TimeSpan.FromSeconds(30));
        var result = await execution;
        await launcher.Finished.Task;

        Assert.Equal(RestoreItemStatus.Failed, result.Item("blocked").Status);
        Assert.Equal(RestoreItemStatus.Succeeded, result.Item("next").Status);
        Assert.Equal(["blocked", "next"], launcher.LaunchedIds);
    }

    /// <summary>验证启动器在返回 Task 前同步阻塞也不会阻止 30 秒预算完成并继续后项。</summary>
    [Fact]
    public async Task ExecuteAsync同步阻塞启动器仍受30秒预算约束()
    {
        var clock = new FakeClock();
        var launcher = new SynchronouslyBlockingLauncher();
        var executor = CreateExecutor(
            launcher, new MutableInventory(), new RecordingPositioner(), clock);
        var plan = Plan(
            Launch(App("blocked", @"C:\Apps\blocked.exe")),
            new RestorePlanItem(App("skipped", @"C:\Apps\skipped.exe"), RestoreDisposition.SkipUnsafe, null));

        var execution = Task.Run(() => executor.ExecuteAsync(plan, CancellationToken.None));
        await launcher.Started.Task;
        try
        {
            await clock.AdvanceAsync(TimeSpan.FromSeconds(30));
            await FakeClock.DrainAsync();

            Assert.True(execution.IsCompleted, "30 秒预算完成后不应继续等待同步阻塞的启动器。始终会在 finally 释放测试线程。");
        }
        finally
        {
            launcher.Release.Set();
        }

        var result = await execution;
        Assert.Equal(RestoreItemStatus.Failed, result.Item("blocked").Status);
        Assert.Equal(RestoreItemStatus.Skipped, result.Item("skipped").Status);
    }

    /// <summary>验证依赖响应 caller 取消时抛普通异常也不能把当前项误报为 Failed。</summary>
    [Fact]
    public async Task ExecuteAsync调用方取消优先于依赖取消清理异常()
    {
        var clock = new FakeClock();
        var launcher = new CancellationCleanupFailingLauncher();
        using var cancellation = new CancellationTokenSource();
        var executor = CreateExecutor(
            launcher, new MutableInventory(), new RecordingPositioner(), clock);
        var execution = executor.ExecuteAsync(
            Plan(
                Launch(App("current", @"C:\Apps\current.exe")),
                Launch(App("later", @"C:\Apps\later.exe"))),
            cancellation.Token);
        await launcher.Started.Task;

        cancellation.Cancel();
        var result = await execution;

        Assert.Equal(RestoreItemStatus.Cancelled, result.Item("current").Status);
        Assert.Equal(RestoreItemStatus.Cancelled, result.Item("later").Status);
    }

    /// <summary>验证 Reuse 的定位操作也共享单项 30 秒硬预算。</summary>
    [Fact]
    public async Task ExecuteAsync的Reuse定位也受30秒预算约束()
    {
        var clock = new FakeClock();
        var positioner = new BlockingPositioner();
        var execution = CreateExecutor(
            new FakeLauncher(), new MutableInventory(), positioner, clock).ExecuteAsync(
            Plan(new RestorePlanItem(
                App("reuse", @"C:\Apps\reuse.exe"), RestoreDisposition.Reuse, 88)),
            CancellationToken.None);
        await positioner.Started.Task;

        await clock.AdvanceAsync(TimeSpan.FromSeconds(30));
        var result = await execution;

        Assert.Equal(RestoreItemStatus.Failed, result.Item("reuse").Status);
    }

    /// <summary>验证依赖自行抛出的 OCE 在 caller 未取消时只失败当前项并继续。</summary>
    [Fact]
    public async Task ExecuteAsync非Caller的OperationCanceledException仅失败当前项()
    {
        var clock = new FakeClock();
        var inventory = new MutableInventory();
        var launcher = new FakeLauncher(scene =>
        {
            if (scene.Id == "bad")
            {
                throw new OperationCanceledException("依赖内部取消");
            }
        });
        launcher.AfterLaunch = scene => inventory.Windows = [Window(901, scene)];
        var executor = CreateExecutor(launcher, inventory, new RecordingPositioner(), clock);

        var result = await executor.ExecuteAsync(Plan(
            Launch(App("bad", @"C:\Apps\bad.exe")),
            Launch(App("good", @"C:\Apps\good.exe"))), CancellationToken.None);

        Assert.Equal(RestoreItemStatus.Failed, result.Item("bad").Status);
        Assert.Equal(RestoreItemStatus.Succeeded, result.Item("good").Status);
    }

    /// <summary>验证旧 item 在候选与 claim 间超时后，不能越过失活 lease 定位已由下一项认领的 HWND。</summary>
    [Fact]
    public async Task ExecuteAsync超时旧Worker不能在下一项Claim后迟到Position()
    {
        var first = App("first", @"C:\Apps\first.exe");
        var second = App("second", @"C:\Apps\second.exe");
        var clock = new FakeClock();
        var inventory = new MutableInventory();
        var launcher = new FakeLauncher { AfterLaunch = _ => inventory.Windows = [Window(777, first)] };
        var positioner = new RecordingPositioner();
        var registryFactory = new BarrierHandleRegistryFactory(itemIndex: 0, handle: 777);
        var executor = new RestoreExecutor(
            launcher,
            inventory,
            positioner,
            clock,
            new AdvancingPollingTimerFactory(clock),
            registryFactory);
        var execution = executor.ExecuteAsync(Plan(
            Launch(first),
            new RestorePlanItem(second, RestoreDisposition.Reuse, 777)), CancellationToken.None);
        await registryFactory.Entered.Task;

        await clock.AdvanceAsync(TimeSpan.FromSeconds(30));
        var result = await execution;
        registryFactory.Release.Set();
        await registryFactory.Finished.Task;

        Assert.Equal(RestoreItemStatus.Failed, result.Item("first").Status);
        Assert.Equal(RestoreItemStatus.Succeeded, result.Item("second").Status);
        Assert.Equal([(nint)777], positioner.Handles);
    }

    /// <summary>验证重复 Reuse HWND 只能由第一项原子认领并定位一次。</summary>
    [Fact]
    public async Task ExecuteAsync重复ReuseHandle不会双定位()
    {
        var positioner = new RecordingPositioner();
        var executor = CreateExecutor(
            new FakeLauncher(), new MutableInventory(), positioner, new FakeClock());

        var result = await executor.ExecuteAsync(Plan(
            new RestorePlanItem(App("one", @"C:\Apps\one.exe"), RestoreDisposition.Reuse, 42),
            new RestorePlanItem(App("two", @"C:\Apps\two.exe"), RestoreDisposition.Reuse, 42)),
            CancellationToken.None);

        Assert.Equal(RestoreItemStatus.Succeeded, result.Item("one").Status);
        Assert.Equal(RestoreItemStatus.Failed, result.Item("two").Status);
        Assert.Equal([(nint)42], positioner.Handles);
    }

    /// <summary>验证当前启动意外产生未来项窗口时保存 reservation，未来项不二次 Launch。</summary>
    [Fact]
    public async Task ExecuteAsync消费未来窗口Reservation而不二次Launch()
    {
        var current = App("current", @"C:\Apps\current.exe");
        var future = App("future", @"C:\Apps\future.exe", "FutureClass", "Future");
        var clock = new FakeClock();
        var inventory = new MutableInventory();
        var launcher = new FakeLauncher
        {
            AfterLaunch = scene =>
            {
                if (scene.Id == "current")
                {
                    inventory.Windows = [Window(801, future)];
                }
            }
        };
        var positioner = new RecordingPositioner();

        var result = await CreateExecutor(launcher, inventory, positioner, clock).ExecuteAsync(
            Plan(Launch(current), Launch(future)), CancellationToken.None);

        Assert.Equal(RestoreItemStatus.Failed, result.Item("current").Status);
        Assert.Equal(RestoreItemStatus.Succeeded, result.Item("future").Status);
        Assert.Equal(["current"], launcher.LaunchedIds);
        Assert.Equal([(nint)801], positioner.Handles);
    }

    /// <summary>验证旧 reservation 对应实例消失或变为不安全窗口时不得定位，未来项会正常启动新安全窗口。</summary>
    [Theory]
    [InlineData(200, false, false)]
    [InlineData(100, true, false)]
    [InlineData(100, false, true)]
    public async Task ExecuteAsync重新验证Reservation并拒绝复用或不安全窗口(
        int replacementProcessId,
        bool isSystemWindow,
        bool isDeskButlerWindow)
    {
        var current = App("current", @"C:\Apps\current.exe", "CurrentClass");
        var future = App("future", @"C:\Apps\future.exe", "FutureClass", "Future");
        var reserved = Window(811, future) with { ProcessId = 100 };
        var replacement = reserved with
        {
            ProcessId = replacementProcessId,
            IsSystemWindow = isSystemWindow,
            IsDeskButlerWindow = isDeskButlerWindow
        };
        var launched = Window(812, future) with { ProcessId = 300 };
        var inventory = new ReservationRevalidationInventory(reserved, replacement, launched);
        var launcher = new FakeLauncher
        {
            AfterLaunch = scene => inventory.FutureWasLaunched = scene.Id == "future"
        };
        var positioner = new RecordingPositioner();

        var result = await CreateExecutor(
            launcher, inventory, positioner, new FakeClock()).ExecuteAsync(
            Plan(Launch(current), Launch(future)), CancellationToken.None);

        Assert.Equal(RestoreItemStatus.Failed, result.Item("current").Status);
        Assert.Equal(RestoreItemStatus.Succeeded, result.Item("future").Status);
        Assert.Equal(["current", "future"], launcher.LaunchedIds);
        Assert.Equal([(nint)812], positioner.Handles);
    }

    /// <summary>验证 reservation 重验证使用当前字段；同实例标题/路径变化但仍唯一归属时仍可安全消费。</summary>
    [Fact]
    public async Task ExecuteAsyncReservation同实例标题变化后按当前字段重新判定()
    {
        var current = App("current", @"C:\Apps\current.exe", "CurrentClass");
        var future = App("future", @"C:\Apps\future.exe", "FutureClass");
        var reserved = Window(821, future) with
        {
            ProcessId = 100,
            Title = "Old title",
            ExplorerPath = @"C:\Work\Old"
        };
        var updated = reserved with
        {
            Title = "New title",
            ExplorerPath = @"C:\Work\New"
        };
        var inventory = new ReservationRevalidationInventory(reserved, updated, updated);
        var launcher = new FakeLauncher();
        var positioner = new RecordingPositioner();

        var result = await CreateExecutor(
            launcher, inventory, positioner, new FakeClock()).ExecuteAsync(
            Plan(Launch(current), Launch(future)), CancellationToken.None);

        Assert.Equal(RestoreItemStatus.Failed, result.Item("current").Status);
        Assert.Equal(RestoreItemStatus.Succeeded, result.Item("future").Status);
        Assert.Equal(["current"], launcher.LaunchedIds);
        Assert.Equal([(nint)821], positioner.Handles);
        Assert.True(inventory.FutureValidationWasCaptured);
    }

    /// <summary>验证相同数值 HWND 的 PID/运行期身份变化会被视为启动后的新窗口。</summary>
    [Fact]
    public async Task ExecuteAsync相同Handle不同Pid视为新窗口()
    {
        var scene = App("reused", @"C:\Apps\reused.exe", "ToolClass", "Reused");
        var before = Window(901, scene) with { ProcessId = 100 };
        var after = Window(901, scene) with { ProcessId = 200 };
        var clock = new FakeClock();
        var inventory = new MutableInventory { Windows = [before] };
        var launcher = new FakeLauncher { AfterLaunch = _ => inventory.Windows = [after] };
        var positioner = new RecordingPositioner();

        var result = await CreateExecutor(launcher, inventory, positioner, clock).ExecuteAsync(
            Plan(Launch(scene)), CancellationToken.None);

        Assert.Equal(RestoreItemStatus.Succeeded, result.Item("reused").Status);
        Assert.Equal([(nint)901], positioner.Handles);
    }

    /// <summary>验证前项完成 claim 后，同数值 HWND 的新 PID 实例仍可由后项 claim 并定位。</summary>
    [Fact]
    public async Task ExecuteAsync前项完成后同Handle新Pid仍可定位()
    {
        var first = App("first", @"C:\Apps\first.exe", "FirstClass");
        var second = App("second", @"C:\Apps\second.exe", "SecondClass");
        var inventory = new MutableInventory();
        var launcher = new FakeLauncher
        {
            AfterLaunch = scene => inventory.Windows =
            [
                Window(904, scene) with { ProcessId = scene.Id == "first" ? 100 : 200 }
            ]
        };
        var positioner = new RecordingPositioner();

        var result = await CreateExecutor(
            launcher, inventory, positioner, new FakeClock()).ExecuteAsync(
            Plan(Launch(first), Launch(second)), CancellationToken.None);

        Assert.All(result.Items, item => Assert.Equal(RestoreItemStatus.Succeeded, item.Status));
        Assert.Equal([(nint)904, (nint)904], positioner.Handles);
    }

    /// <summary>验证 timeout 仅 Deactivate 时，迟到 Position work 仍独占数值 HWND，后项 Reuse 不得并发定位。</summary>
    [Fact]
    public async Task ExecuteAsync迟到Work完成前阻止同HandleReuse并发Position()
    {
        var first = App("first", @"C:\Apps\first.exe", "FirstClass");
        var second = App("reuse", @"C:\Apps\reuse.exe", "ReuseClass");
        var clock = new FakeClock();
        var inventory = new MutableInventory();
        var launcher = new FakeLauncher
        {
            AfterLaunch = scene => inventory.Windows =
            [
                Window(905, scene) with { ProcessId = 100 }
            ]
        };
        var positioner = new LateFirstPositioner();
        var execution = CreateExecutor(launcher, inventory, positioner, clock).ExecuteAsync(
            Plan(
                Launch(first),
                new RestorePlanItem(second, RestoreDisposition.Reuse, 905)),
            CancellationToken.None);
        await positioner.FirstStarted.Task;

        await clock.AdvanceAsync(TimeSpan.FromSeconds(30));
        var result = await execution;

        Assert.Equal(RestoreItemStatus.Failed, result.Item("first").Status);
        Assert.Equal(RestoreItemStatus.Failed, result.Item("reuse").Status);
        Assert.Equal([(nint)905], positioner.Handles);

        positioner.ReleaseFirst.TrySetResult();
        await positioner.FirstFinished.Task;
        await FakeClock.DrainAsync();
    }

    /// <summary>验证 Launch 后项轮询在旧 work 完成前不能 claim 同 HWND，新 PID 在 CompleteWork 后下一 tick 可定位。</summary>
    [Fact]
    public async Task ExecuteAsync旧Work完成后下一Tick允许同Handle新Pid定位()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var first = App("first", @"C:\Apps\first.exe", "FirstClass");
        var second = App("second", @"C:\Apps\second.exe", "SecondClass");
        var clock = new FakeClock();
        var timers = new ControlledPollingTimerFactory();
        var registries = new CompletionTrackingRegistryFactory();
        var inventory = new MutableInventory();
        var launcher = new FakeLauncher
        {
            AfterLaunch = scene => inventory.Windows =
            [
                Window(906, scene) with { ProcessId = scene.Id == "first" ? 100 : 200 }
            ]
        };
        var positioner = new LateFirstPositioner();
        var executor = new RestoreExecutor(
            launcher, inventory, positioner, clock, timers, registries);
        var execution = executor.ExecuteAsync(
            Plan(Launch(first), Launch(second)), CancellationToken.None);
        var firstTimer = await timers.FirstCreated.Task.WaitAsync(
            TimeSpan.FromSeconds(2), testCancellation);
        firstTimer.Pulse();
        await positioner.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);

        await clock.AdvanceAsync(TimeSpan.FromSeconds(30));
        var secondTimer = await timers.SecondCreated.Task.WaitAsync(
            TimeSpan.FromSeconds(2), testCancellation);
        secondTimer.Pulse();
        await secondTimer.SecondWaitStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(2), testCancellation);
        Assert.Equal([(nint)906], positioner.Handles);

        positioner.ReleaseFirst.TrySetResult();
        await positioner.FirstFinished.Task.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);
        await registries.FirstWorkCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(2), testCancellation);
        secondTimer.Pulse();
        var completedAfterRelease = await Task.WhenAny(
            execution, Task.Delay(TimeSpan.FromMilliseconds(250), testCancellation));
        if (completedAfterRelease != execution)
        {
            await clock.AdvanceAsync(TimeSpan.FromSeconds(30));
        }

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);
        Assert.Same(execution, completedAfterRelease);
        Assert.Equal(RestoreItemStatus.Failed, result.Item("first").Status);
        Assert.Equal(RestoreItemStatus.Succeeded, result.Item("second").Status);
        Assert.Equal([(nint)906, (nint)906], positioner.Handles);
    }

    /// <summary>验证 work fault 的 finally 释放 HWND gate，但相同实例 consumed 状态不被清除。</summary>
    [Fact]
    public async Task ExecuteAsyncWorkFault释放HandleGate供新Pid接管()
    {
        var first = App("first", @"C:\Apps\first.exe", "FirstClass");
        var second = App("second", @"C:\Apps\second.exe", "SecondClass");
        var inventory = new MutableInventory();
        var launcher = new FakeLauncher
        {
            AfterLaunch = scene => inventory.Windows =
            [
                Window(907, scene) with { ProcessId = scene.Id == "first" ? 100 : 200 }
            ]
        };
        var positioner = new FirstFaultPositioner();

        var result = await CreateExecutor(
            launcher, inventory, positioner, new FakeClock()).ExecuteAsync(
            Plan(Launch(first), Launch(second)), CancellationToken.None);

        Assert.Equal(RestoreItemStatus.Failed, result.Item("first").Status);
        Assert.Equal(RestoreItemStatus.Succeeded, result.Item("second").Status);
        Assert.Equal([(nint)907, (nint)907], positioner.Handles);
    }

    /// <summary>验证 timeout cancel 后 work finally 释放 HWND gate，新 PID 可在后项首个 tick 接管。</summary>
    [Fact]
    public async Task ExecuteAsyncWorkCancel的Finally释放HandleGate()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var first = App("first", @"C:\Apps\first.exe", "FirstClass");
        var second = App("second", @"C:\Apps\second.exe", "SecondClass");
        var clock = new FakeClock();
        var timers = new ControlledPollingTimerFactory();
        var registries = new CompletionTrackingRegistryFactory();
        var inventory = new MutableInventory();
        var launcher = new FakeLauncher
        {
            AfterLaunch = scene => inventory.Windows =
            [
                Window(908, scene) with { ProcessId = scene.Id == "first" ? 100 : 200 }
            ]
        };
        var positioner = new FirstCancellationPositioner();
        var executor = new RestoreExecutor(
            launcher, inventory, positioner, clock, timers, registries);
        var execution = executor.ExecuteAsync(
            Plan(Launch(first), Launch(second)), CancellationToken.None);
        var firstTimer = await timers.FirstCreated.Task.WaitAsync(
            TimeSpan.FromSeconds(2), testCancellation);
        firstTimer.Pulse();
        await positioner.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);

        await clock.AdvanceAsync(TimeSpan.FromSeconds(30));
        await positioner.FirstFinished.Task.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);
        await registries.FirstWorkCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(2), testCancellation);
        var secondTimer = await timers.SecondCreated.Task.WaitAsync(
            TimeSpan.FromSeconds(2), testCancellation);
        secondTimer.Pulse();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);

        Assert.Equal(RestoreItemStatus.Failed, result.Item("first").Status);
        Assert.Equal(RestoreItemStatus.Succeeded, result.Item("second").Status);
        Assert.Equal([(nint)908, (nint)908], positioner.Handles);
    }

    /// <summary>验证 fingerprint 会规范化窗口类大小写，避免把同一运行期窗口误判为新窗口。</summary>
    [Fact]
    public async Task ExecuteAsyncFingerprint规范化窗口类大小写()
    {
        var scene = App("normalized", @"C:\Apps\normalized.exe", "ToolClass", "Normalized");
        var before = Window(902, scene) with { ProcessId = 100 };
        var after = before with { WindowClass = "toolclass" };
        var clock = new FakeClock();
        var inventory = new MutableInventory { Windows = [before] };
        var launcher = new FakeLauncher { AfterLaunch = _ => inventory.Windows = [after] };
        var positioner = new RecordingPositioner();

        var result = await CreateExecutor(launcher, inventory, positioner, clock).ExecuteAsync(
            Plan(Launch(scene)), CancellationToken.None);

        Assert.Equal(RestoreItemStatus.Failed, result.Item("normalized").Status);
        Assert.Empty(positioner.Handles);
    }

    /// <summary>验证 baseline 只按窗口实例身份排除；同实例标题与 Explorer 路径变化仍不是新窗口。</summary>
    [Fact]
    public async Task ExecuteAsync同实例标题和Explorer路径变化仍属于Baseline()
    {
        var scene = App(
            "baseline", @"C:\Windows\explorer.exe", "CabinetWClass", "New title", @"C:\Work\New");
        var before = Window(903, scene) with
        {
            ProcessId = 100,
            Title = "Old title",
            ExplorerPath = @"C:\Work\Old"
        };
        var after = before with
        {
            Title = "New title",
            ExplorerPath = @"C:\Work\New"
        };
        var clock = new FakeClock();
        var inventory = new MutableInventory { Windows = [before] };
        var launcher = new FakeLauncher { AfterLaunch = _ => inventory.Windows = [after] };
        var positioner = new RecordingPositioner();

        var result = await CreateExecutor(launcher, inventory, positioner, clock).ExecuteAsync(
            Plan(Launch(scene)), CancellationToken.None);

        Assert.Equal(RestoreItemStatus.Failed, result.Item("baseline").Status);
        Assert.Empty(positioner.Handles);
    }

    /// <summary>验证零 HWND 即使身份匹配也不能进入 claim 或 Position。</summary>
    [Fact]
    public async Task ExecuteAsync忽略零Handle候选()
    {
        var scene = App("zero", @"C:\Apps\zero.exe");
        var clock = new FakeClock();
        var inventory = new MutableInventory();
        var launcher = new FakeLauncher { AfterLaunch = _ => inventory.Windows = [Window(0, scene)] };
        var positioner = new RecordingPositioner();

        var result = await CreateExecutor(launcher, inventory, positioner, clock).ExecuteAsync(
            Plan(Launch(scene)), CancellationToken.None);

        Assert.Equal(RestoreItemStatus.Failed, result.Item("zero").Status);
        Assert.Empty(positioner.Handles);
    }

    /// <summary>验证 caller 取消即使 callback 后台抛错仍返回 Cancelled，且故障与败方 timeout 均可观察。</summary>
    [Fact]
    public async Task ExecuteAsyncCaller取消观察后台Callback错误并清理败方Timeout()
    {
        var clock = new FakeClock();
        using var cancellation = new CancellationTokenSource();
        var launcher = new CancelCallbackLauncher(
            new InvalidOperationException("ordinary cancel callback"));
        var executor = CreateExecutor(
            launcher, new MutableInventory(), new RecordingPositioner(), clock);
        var execution = executor.ExecuteAsync(
            Plan(
                Launch(App("current", @"C:\Apps\current.exe")),
                Launch(App("later", @"C:\Apps\later.exe"))),
            cancellation.Token);
        await launcher.Started.Task;

        cancellation.Cancel();

        var result = await execution;
        await launcher.Finished.Task;
        var backgroundFailure = await WaitForBackgroundFailureAsync(executor);

        Assert.Equal(RestoreItemStatus.Cancelled, result.Item("current").Status);
        Assert.Equal(RestoreItemStatus.Cancelled, result.Item("later").Status);
        Assert.Contains("ordinary cancel callback", backgroundFailure.ToString());
        Assert.Equal(0, clock.PendingDelayCount);
    }

    /// <summary>验证 timeout 触发的后台取消 callback 错误不会覆盖 Failed，并写入可观察状态。</summary>
    [Fact]
    public async Task ExecuteAsyncTimeout观察后台CancelCallback错误()
    {
        var clock = new FakeClock();
        var launcher = new CancelCallbackLauncher(
            new InvalidOperationException("timeout cancel callback"));
        var executor = CreateExecutor(
            launcher, new MutableInventory(), new RecordingPositioner(), clock);
        var execution = executor.ExecuteAsync(
            Plan(Launch(App("timeout", @"C:\Apps\timeout.exe"))), CancellationToken.None);
        await launcher.Started.Task;

        await clock.AdvanceAsync(TimeSpan.FromSeconds(30));
        var result = await execution;
        await launcher.Finished.Task;
        var backgroundFailure = await WaitForBackgroundFailureAsync(executor);

        Assert.Equal(RestoreItemStatus.Failed, result.Item("timeout").Status);
        Assert.Contains("timeout cancel callback", backgroundFailure.ToString());
    }

    /// <summary>验证后台取消 Aggregate 中的 fatal 可观察，但不能改变 caller Cancelled 语义。</summary>
    [Fact]
    public async Task ExecuteAsyncCancelAggregate中的致命异常后台可观察()
    {
        var clock = new FakeClock();
        using var cancellation = new CancellationTokenSource();
        #pragma warning disable CA2201 // 有意构造运行时保留异常，验证取消 Aggregate 不会吞 fatal。
        var launcher = new CancelCallbackLauncher(
            new AggregateException(
                new InvalidOperationException("ordinary"),
                new OutOfMemoryException("fatal cancel callback")));
        #pragma warning restore CA2201
        var executor = CreateExecutor(
            launcher, new MutableInventory(), new RecordingPositioner(), clock);
        var execution = executor.ExecuteAsync(
            Plan(Launch(App("fatal", @"C:\Apps\fatal.exe"))), cancellation.Token);
        await launcher.Started.Task;

        cancellation.Cancel();
        var result = await execution;
        await launcher.Finished.Task;
        var backgroundFailure = await WaitForBackgroundFailureAsync(executor);

        Assert.Equal(RestoreItemStatus.Cancelled, result.Item("fatal").Status);
        Assert.Contains("fatal cancel callback", backgroundFailure.ToString());
    }

    /// <summary>验证 timeout 返回后 late work 仍持有 CTS，直到任务真正结束才释放。</summary>
    [Fact]
    public async Task ExecuteAsync迟到Work完成前保留Cts完成后释放()
    {
        var clock = new FakeClock();
        var launcher = new LateCompletingLauncher(throwLateFault: false);
        var execution = CreateExecutor(
            launcher, new MutableInventory(), new RecordingPositioner(), clock).ExecuteAsync(
            Plan(Launch(App("late", @"C:\Apps\late.exe"))), CancellationToken.None);
        await launcher.Started.Task;

        await clock.AdvanceAsync(TimeSpan.FromSeconds(30));
        var result = await execution;
        await launcher.CancellationObserved.Task;
        Assert.True(launcher.CapturedToken.WaitHandle.WaitOne(0));

        launcher.Release.TrySetResult();
        await launcher.Finished.Task;
        await WaitForTokenDisposedAsync(launcher.CapturedToken);

        Assert.Equal(RestoreItemStatus.Failed, result.Item("late").Status);
        Assert.Throws<ObjectDisposedException>(() => _ = launcher.CapturedToken.WaitHandle);
    }

    /// <summary>验证 timeout 后迟到普通 fault 被观察且不能改写已经返回的 Failed 结果。</summary>
    [Fact]
    public async Task ExecuteAsync观察迟到Fault且不改写Timeout结果()
    {
        var clock = new FakeClock();
        var launcher = new LateCompletingLauncher(throwLateFault: true);
        var executor = CreateExecutor(
            launcher, new MutableInventory(), new RecordingPositioner(), clock);
        var execution = executor.ExecuteAsync(
            Plan(Launch(App("late-fault", @"C:\Apps\late-fault.exe"))), CancellationToken.None);
        await launcher.Started.Task;

        await clock.AdvanceAsync(TimeSpan.FromSeconds(30));
        var result = await execution;
        launcher.Release.TrySetResult();
        await launcher.Finished.Task;
        var backgroundFailure = await WaitForBackgroundFailureAsync(executor);
        await WaitForTokenDisposedAsync(launcher.CapturedToken);

        Assert.Equal(RestoreItemStatus.Failed, result.Item("late-fault").Status);
        Assert.Contains("late worker fault", backgroundFailure.ToString());
        Assert.Throws<ObjectDisposedException>(() => _ = launcher.CapturedToken.WaitHandle);
    }

    /// <summary>验证 timeout 触发的阻塞取消 callback 不占用硬预算，后项仍可启动并成功。</summary>
    [Fact]
    public async Task ExecuteAsyncTimeout不等待阻塞CancelCallback并继续后项()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        var inventory = new MutableInventory();
        var launcher = new BlockingCancelCallbackLauncher(
            new InvalidOperationException("blocking callback fault"))
        {
            AfterNextLaunch = scene => inventory.Windows = [Window(1101, scene)]
        };
        var executor = CreateExecutor(launcher, inventory, new RecordingPositioner(), clock);
        var execution = executor.ExecuteAsync(Plan(
            Launch(App("blocked", @"C:\Apps\blocked.exe")),
            Launch(App("next", @"C:\Apps\next.exe"))), CancellationToken.None);
        await launcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);

        var advance = Task.Run(
            () => clock.AdvanceAsync(TimeSpan.FromSeconds(30)), testCancellation);
        await launcher.CallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);
        var completedWithinBudget = await Task.WhenAny(
            execution, Task.Delay(TimeSpan.FromMilliseconds(250), testCancellation));
        launcher.Release.Set();
        await advance.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);
        await launcher.CallbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);
        await launcher.WorkFinished.Task.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);
        var backgroundFailure = await WaitForBackgroundFailureAsync(executor);

        Assert.Same(execution, completedWithinBudget);
        Assert.Equal(RestoreItemStatus.Failed, result.Item("blocked").Status);
        Assert.Equal(RestoreItemStatus.Succeeded, result.Item("next").Status);
        Assert.Equal(["blocked", "next"], launcher.LaunchedIds);
        Assert.Contains("blocking callback fault", backgroundFailure.ToString());
    }

    /// <summary>验证 caller 取消不等待阻塞 callback，当前和剩余项都及时标记 Cancelled。</summary>
    [Fact]
    public async Task ExecuteAsyncCaller取消不等待阻塞CancelCallback()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        using var cancellation = new CancellationTokenSource();
        var launcher = new BlockingCancelCallbackLauncher();
        var executor = CreateExecutor(
            launcher, new MutableInventory(), new RecordingPositioner(), clock);
        var execution = executor.ExecuteAsync(Plan(
            Launch(App("blocked", @"C:\Apps\blocked.exe")),
            Launch(App("later", @"C:\Apps\later.exe"))), cancellation.Token);
        await launcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);

        cancellation.Cancel();
        await launcher.CallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);
        var completedAfterCallerCancel = await Task.WhenAny(
            execution, Task.Delay(TimeSpan.FromMilliseconds(250), testCancellation));
        launcher.Release.Set();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);
        await launcher.CallbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);
        await launcher.WorkFinished.Task.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);

        Assert.Same(execution, completedAfterCallerCancel);
        Assert.Equal(RestoreItemStatus.Cancelled, result.Item("blocked").Status);
        Assert.Equal(RestoreItemStatus.Cancelled, result.Item("later").Status);
        Assert.Equal(["blocked"], launcher.LaunchedIds);
    }

    /// <summary>验证 future reservation 的重验证 Capture 仍包含在该项从排队开始的 30 秒预算内。</summary>
    [Fact]
    public async Task ExecuteAsyncReservationValidationCapture受30秒预算约束()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var current = App("current", @"C:\Apps\current.exe", "CurrentClass");
        var future = App("future", @"C:\Apps\future.exe", "FutureClass");
        var clock = new FakeClock();
        var inventory = new BlockingReservationValidationInventory(Window(1201, future));
        var launcher = new FakeLauncher();
        var positioner = new RecordingPositioner();
        var execution = CreateExecutor(launcher, inventory, positioner, clock).ExecuteAsync(
            Plan(Launch(current), Launch(future)), CancellationToken.None);
        await inventory.ValidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);

        await clock.AdvanceAsync(TimeSpan.FromSeconds(30));
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);
        await inventory.ValidationFinished.Task.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);

        Assert.Equal(RestoreItemStatus.Failed, result.Item("current").Status);
        Assert.Equal(RestoreItemStatus.Failed, result.Item("future").Status);
        Assert.Equal(["current"], launcher.LaunchedIds);
        Assert.Empty(positioner.Handles);
    }

    /// <summary>验证当前项同时出现多个唯一归属候选时保守不随机选择。</summary>
    [Fact]
    public async Task ExecuteAsync当前项多个候选保持安全Ambiguity()
    {
        var scene = App("ambiguous", @"C:\Apps\ambiguous.exe");
        var clock = new FakeClock();
        var inventory = new MutableInventory();
        var launcher = new FakeLauncher
        {
            AfterLaunch = _ => inventory.Windows = [Window(1001, scene), Window(1002, scene)]
        };
        var positioner = new RecordingPositioner();

        var result = await CreateExecutor(launcher, inventory, positioner, clock).ExecuteAsync(
            Plan(Launch(scene)), CancellationToken.None);

        Assert.Equal(RestoreItemStatus.Failed, result.Item("ambiguous").Status);
        Assert.Empty(positioner.Handles);
    }

    /// <summary>验证未来项多个 reservation 同样保守失败且不二次 Launch 或随机 Position。</summary>
    [Fact]
    public async Task ExecuteAsync未来项多个Reservations保持安全Ambiguity()
    {
        var current = App("current", @"C:\Apps\current.exe");
        var future = App("future", @"C:\Apps\future.exe", "FutureClass", "Future");
        var clock = new FakeClock();
        var inventory = new MutableInventory();
        var launcher = new FakeLauncher
        {
            AfterLaunch = scene =>
            {
                if (scene.Id == "current")
                {
                    inventory.Windows = [Window(1101, future), Window(1102, future)];
                }
            }
        };
        var positioner = new RecordingPositioner();

        var result = await CreateExecutor(launcher, inventory, positioner, clock).ExecuteAsync(
            Plan(Launch(current), Launch(future)), CancellationToken.None);

        Assert.Equal(RestoreItemStatus.Failed, result.Item("current").Status);
        Assert.Equal(RestoreItemStatus.Failed, result.Item("future").Status);
        Assert.Equal(["current"], launcher.LaunchedIds);
        Assert.Empty(positioner.Handles);
    }

    /// <summary>创建默认使用虚拟 500ms tick 的执行器。</summary>
    private static RestoreExecutor CreateExecutor(
        IAppLauncher launcher,
        IWindowInventory inventory,
        IWindowPositioner positioner,
        FakeClock clock) => new(launcher, inventory, positioner, clock, new AdvancingPollingTimerFactory(clock));

    /// <summary>以测试 watchdog 等待后台 fault observer 写入，不假设 continuation 调度顺序。</summary>
    private static async Task<Exception> WaitForBackgroundFailureAsync(RestoreExecutor executor)
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var timeout = Task.Delay(TimeSpan.FromSeconds(2), testCancellation);
        Exception? failure;
        while ((failure = executor.LastBackgroundFailure) is null)
        {
            if (timeout.IsCompleted)
            {
                throw new TimeoutException("后台取消故障未在测试期限内变为可观察状态。");
            }

            await Task.Yield();
        }

        return failure;
    }

    /// <summary>等待 work 与后台 cancel 均完成后 CTS 最终释放，不假设 continuation 顺序。</summary>
    private static async Task WaitForTokenDisposedAsync(CancellationToken cancellationToken)
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var timeout = Task.Delay(TimeSpan.FromSeconds(2), testCancellation);
        while (true)
        {
            try
            {
                _ = cancellationToken.WaitHandle;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (timeout.IsCompleted)
            {
                throw new TimeoutException("item CTS 未在 work 与 cancel task 完成后释放。");
            }

            await Task.Yield();
        }
    }

    /// <summary>创建不可变恢复计划。</summary>
    private static RestorePlan Plan(params RestorePlanItem[] items) => new(items);

    /// <summary>创建 Launch 计划项。</summary>
    private static RestorePlanItem Launch(SceneItem scene) => new(scene, RestoreDisposition.Launch, null);

    /// <summary>创建固定布局的普通场景项。</summary>
    private static SceneItem App(
        string id,
        string executablePath,
        string windowClass = "ToolClass",
        string? title = null,
        string? explorerPath = null) => new(
            id, executablePath, windowClass, title, explorerPath,
            new WindowBounds(10, 20, 800, 600), SceneWindowState.Normal,
            new MonitorIdentity(@"\\.\DISPLAY1", new WindowBounds(0, 0, 1920, 1040), 96, 96), false);

    /// <summary>从场景项创建严格身份一致的当前窗口候选。</summary>
    private static WindowCandidate Window(nint handle, SceneItem scene) => new(
        handle, (int)handle, scene.ExecutablePath, scene.WindowClass, scene.TitleHint, scene.ExplorerPath,
        scene.Bounds, scene.State, scene.Monitor, true, false, false, false, false);

    private sealed class FakeLauncher(Action<SceneItem>? launch = null) : IAppLauncher
    {
        internal List<string> LaunchedIds { get; } = [];

        internal Action<SceneItem>? AfterLaunch { get; set; }

        /// <summary>记录启动并执行测试注入行为。</summary>
        public Task LaunchAsync(SceneItem sceneItem, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LaunchedIds.Add(sceneItem.Id);
            launch?.Invoke(sceneItem);
            AfterLaunch?.Invoke(sceneItem);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingFirstLauncher : IAppLauncher
    {
        internal List<string> LaunchedIds { get; } = [];

        internal Action<SceneItem>? AfterSuccessfulLaunch { get; set; }

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>首项无限等待取消，后续项立即启动。</summary>
        public async Task LaunchAsync(SceneItem sceneItem, CancellationToken cancellationToken)
        {
            LaunchedIds.Add(sceneItem.Id);
            if (sceneItem.Id == "blocked")
            {
                Entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    Finished.TrySetResult();
                }

                return;
            }

            AfterSuccessfulLaunch?.Invoke(sceneItem);
        }
    }

    private sealed class SynchronouslyBlockingLauncher : IAppLauncher
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ManualResetEventSlim Release { get; } = new(false);

        /// <summary>在返回 Task 前同步阻塞，模拟 Shell 启动调用卡住。</summary>
        public Task LaunchAsync(SceneItem sceneItem, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            Release.Wait(CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class CancellationCleanupFailingLauncher : IAppLauncher
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>等待取消后模拟依赖清理失败。</summary>
        public async Task LaunchAsync(SceneItem sceneItem, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("模拟取消清理失败");
            }
        }
    }

    private sealed class CancelCallbackLauncher(Exception callbackException) : IAppLauncher
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>登记会抛指定异常的取消 callback，然后等待 token 取消。</summary>
        public async Task LaunchAsync(SceneItem sceneItem, CancellationToken cancellationToken)
        {
            try
            {
                using var registration = cancellationToken.Register(() => throw callbackException);
                Started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                Finished.TrySetResult();
            }
        }
    }

    private sealed class LateCompletingLauncher(bool throwLateFault) : IAppLauncher
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationToken CapturedToken { get; private set; }

        /// <summary>故意忽略取消直到测试释放，再取消或抛迟到 fault。</summary>
        public async Task LaunchAsync(SceneItem sceneItem, CancellationToken cancellationToken)
        {
            CapturedToken = cancellationToken;
            using var cancellationRegistration = cancellationToken.Register(
                () => CancellationObserved.TrySetResult());
            Started.TrySetResult();
            try
            {
                await Release.Task;
                if (throwLateFault)
                {
                    throw new InvalidOperationException("late worker fault");
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                Finished.TrySetResult();
            }
        }
    }

    private sealed class BlockingCancelCallbackLauncher(Exception? callbackException = null) : IAppLauncher
    {
        internal List<string> LaunchedIds { get; } = [];

        internal Action<SceneItem>? AfterNextLaunch { get; init; }

        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource CallbackEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource CallbackFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource WorkFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ManualResetEventSlim Release { get; } = new(false);

        /// <summary>首项注册同步阻塞 callback；后项立即启动供序列继续验证。</summary>
        public async Task LaunchAsync(SceneItem sceneItem, CancellationToken cancellationToken)
        {
            LaunchedIds.Add(sceneItem.Id);
            if (sceneItem.Id != "blocked")
            {
                AfterNextLaunch?.Invoke(sceneItem);
                return;
            }

            using var registration = cancellationToken.Register(() =>
            {
                CallbackEntered.TrySetResult();
                Release.Wait(CancellationToken.None);
                CallbackFinished.TrySetResult();
                if (callbackException is not null)
                {
                    throw callbackException;
                }
            });
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                WorkFinished.TrySetResult();
            }
        }
    }

    private sealed class MutableInventory : IWindowInventory
    {
        internal IReadOnlyList<WindowCandidate> Windows { get; set; } = [];

        internal int CaptureCount { get; private set; }

        /// <summary>返回当前受控窗口集合的副本。</summary>
        public Task<IReadOnlyList<WindowCandidate>> CaptureAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCount++;
            return Task.FromResult<IReadOnlyList<WindowCandidate>>(Windows.ToArray());
        }
    }

    private sealed class ReservationRevalidationInventory(
        WindowCandidate reserved,
        WindowCandidate replacement,
        WindowCandidate launched) : IWindowInventory
    {
        private int captureCount;

        internal bool FutureWasLaunched { get; set; }

        internal bool FutureValidationWasCaptured { get; private set; }

        /// <summary>先提供 future reservation；其 item 开始时切换到替代实例，启动后提供新安全窗口。</summary>
        public Task<IReadOnlyList<WindowCandidate>> CaptureAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            captureCount++;
            if (FutureWasLaunched)
            {
                return Task.FromResult<IReadOnlyList<WindowCandidate>>([launched]);
            }

            // 当前项 baseline 加 59 次有效轮询 capture 后，下一次 capture 即 future reservation validation。
            if (captureCount > 60)
            {
                FutureValidationWasCaptured = true;
                return Task.FromResult<IReadOnlyList<WindowCandidate>>([replacement]);
            }

            return Task.FromResult<IReadOnlyList<WindowCandidate>>(
                captureCount == 1 ? [] : [reserved]);
        }
    }

    private sealed class BlockingReservationValidationInventory(
        WindowCandidate reserved) : IWindowInventory
    {
        private int captureCount;

        internal TaskCompletionSource ValidationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ValidationFinished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>当前项提供 reservation，未来项的第一个重验证 Capture 持续等待预算取消。</summary>
        public async Task<IReadOnlyList<WindowCandidate>> CaptureAsync(
            CancellationToken cancellationToken)
        {
            captureCount++;
            if (captureCount > 60)
            {
                ValidationStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    ValidationFinished.TrySetResult();
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return captureCount == 1 ? [] : [reserved];
        }
    }

    private sealed class RecordingPositioner(IEnumerable<nint>? failingHandles = null) : IWindowPositioner
    {
        private readonly HashSet<nint> failures = failingHandles?.ToHashSet() ?? [];

        internal List<nint> Handles { get; } = [];

        /// <summary>记录定位；为指定句柄模拟平台失败。</summary>
        public Task PositionAsync(nint windowHandle, SceneItem sceneItem, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Handles.Add(windowHandle);
            return failures.Contains(windowHandle)
                ? Task.FromException(new InvalidOperationException("模拟定位失败"))
                : Task.CompletedTask;
        }
    }

    private sealed class BlockingPositioner : IWindowPositioner
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>等待单项预算取消。</summary>
        public async Task PositionAsync(
            nint windowHandle,
            SceneItem sceneItem,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class LateFirstPositioner : IWindowPositioner
    {
        private readonly object syncRoot = new();
        private readonly List<nint> handles = [];

        internal IReadOnlyList<nint> Handles
        {
            get
            {
                lock (syncRoot)
                {
                    return handles.ToArray();
                }
            }
        }

        internal TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FirstFinished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>首项忽略取消保持 Position in-flight，后项立即记录完成。</summary>
        public async Task PositionAsync(
            nint windowHandle,
            SceneItem sceneItem,
            CancellationToken cancellationToken)
        {
            lock (syncRoot)
            {
                handles.Add(windowHandle);
            }

            if (sceneItem.Id != "first")
            {
                return;
            }

            FirstStarted.TrySetResult();
            try
            {
                await ReleaseFirst.Task;
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                FirstFinished.TrySetResult();
            }
        }
    }

    private sealed class FirstFaultPositioner : IWindowPositioner
    {
        internal List<nint> Handles { get; } = [];

        /// <summary>首项定位 fault，后项同 HWND 新 PID 正常完成。</summary>
        public Task PositionAsync(
            nint windowHandle,
            SceneItem sceneItem,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Handles.Add(windowHandle);
            return sceneItem.Id == "first"
                ? Task.FromException(new InvalidOperationException("first position fault"))
                : Task.CompletedTask;
        }
    }

    private sealed class FirstCancellationPositioner : IWindowPositioner
    {
        internal List<nint> Handles { get; } = [];

        internal TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FirstFinished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>首项等待预算取消并在 finally 发信号，后项立即完成。</summary>
        public async Task PositionAsync(
            nint windowHandle,
            SceneItem sceneItem,
            CancellationToken cancellationToken)
        {
            Handles.Add(windowHandle);
            if (sceneItem.Id != "first")
            {
                return;
            }

            FirstStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                FirstFinished.TrySetResult();
            }
        }
    }

    private sealed class ControlledPollingTimerFactory : IWindowPollingTimerFactory
    {
        private int createdCount;

        internal TaskCompletionSource<ControlledPollingTimer> FirstCreated { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<ControlledPollingTimer> SecondCreated { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>依创建顺序暴露前两个手动 tick timer。</summary>
        public IWindowPollingTimer Create(TimeSpan interval)
        {
            var timer = new ControlledPollingTimer();
            var index = Interlocked.Increment(ref createdCount);
            if (index == 1)
            {
                FirstCreated.TrySetResult(timer);
            }
            else if (index == 2)
            {
                SecondCreated.TrySetResult(timer);
            }

            return timer;
        }

        internal sealed class ControlledPollingTimer : IWindowPollingTimer
        {
            private readonly SemaphoreSlim ticks = new(0);
            private int waitCount;

            internal TaskCompletionSource SecondWaitStarted { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>释放一个受控 tick。</summary>
            internal void Pulse() => ticks.Release();

            /// <summary>等待测试释放 tick，并暴露第二次等待作为首次 claim 失败的证据。</summary>
            public async ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref waitCount) == 2)
                {
                    SecondWaitStarted.TrySetResult();
                }

                await ticks.WaitAsync(cancellationToken);
                return true;
            }

            /// <summary>测试 timer 无 native 资源。</summary>
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class CompletionTrackingRegistryFactory : IHandleRegistryFactory
    {
        internal TaskCompletionSource FirstWorkCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>装饰真实 registry，只把 item0 CompleteWork 暴露为测试同步点。</summary>
        public IHandleRegistry Create() => new CompletionTrackingRegistry(this, new HandleRegistry());

        private sealed class CompletionTrackingRegistry(
            CompletionTrackingRegistryFactory owner,
            IHandleRegistry inner) : IHandleRegistry
        {
            /// <summary>代理 lease 激活。</summary>
            public HandleLease Activate(int itemIndex) => inner.Activate(itemIndex);

            /// <summary>代理 lease 失活。</summary>
            public void Deactivate(HandleLease lease) => inner.Deactivate(lease);

            /// <summary>代理 work 完成并在真实 gate 释放后通知测试。</summary>
            public void CompleteWork(HandleLease lease)
            {
                inner.CompleteWork(lease);
                if (lease.ItemIndex == 0)
                {
                    owner.FirstWorkCompleted.TrySetResult();
                }
            }

            /// <summary>代理实例 claim。</summary>
            public bool TryClaim(HandleLease lease, RuntimeWindowFingerprint fingerprint) =>
                inner.TryClaim(lease, fingerprint);

            /// <summary>代理未来 reservation。</summary>
            public bool TryReserve(
                HandleLease lease,
                int futureItemIndex,
                RuntimeWindowFingerprint fingerprint) =>
                inner.TryReserve(lease, futureItemIndex, fingerprint);

            /// <summary>代理 reservation 快照。</summary>
            public IReadOnlyList<RuntimeWindowFingerprint> GetReservationSnapshots(HandleLease lease) =>
                inner.GetReservationSnapshots(lease);

            /// <summary>代理精确 reservation 删除。</summary>
            public bool RemoveReservationIfMatches(
                HandleLease lease,
                RuntimeWindowFingerprint reservedFingerprint) =>
                inner.RemoveReservationIfMatches(lease, reservedFingerprint);

            /// <summary>代理重验证后的 reservation claim。</summary>
            public bool TryClaimReservation(
                HandleLease lease,
                RuntimeWindowFingerprint reservedFingerprint,
                RuntimeWindowFingerprint currentFingerprint) =>
                inner.TryClaimReservation(lease, reservedFingerprint, currentFingerprint);
        }
    }

    private sealed class AdvancingPollingTimerFactory(FakeClock clock) : IWindowPollingTimerFactory
    {
        internal List<TimeSpan> RequestedIntervals { get; } = [];

        internal int TickCount { get; private set; }

        /// <summary>创建每次 tick 推进虚拟时钟的轮询计时器。</summary>
        public IWindowPollingTimer Create(TimeSpan interval)
        {
            RequestedIntervals.Add(interval);
            return new AdvancingPollingTimer(this, clock, interval);
        }

        private sealed class AdvancingPollingTimer(
            AdvancingPollingTimerFactory owner,
            FakeClock clock,
            TimeSpan interval) : IWindowPollingTimer
        {
            /// <summary>推进一格虚拟时间并返回 tick。</summary>
            public async ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.TickCount++;
                await clock.AdvanceAsync(interval);
                cancellationToken.ThrowIfCancellationRequested();
                return true;
            }

            /// <summary>虚拟计时器无非托管资源。</summary>
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class BarrierHandleRegistryFactory(int itemIndex, nint handle) : IHandleRegistryFactory
    {
        private int ItemIndex { get; } = itemIndex;

        private nint Handle { get; } = handle;

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ManualResetEventSlim Release { get; } = new(false);

        /// <summary>创建在指定 item/handle 的 TryClaim 前停顿的真实 registry 装饰器。</summary>
        public IHandleRegistry Create() => new BarrierHandleRegistry(this, new HandleRegistry());

        private sealed class BarrierHandleRegistry(
            BarrierHandleRegistryFactory owner,
            IHandleRegistry inner) : IHandleRegistry
        {
            /// <summary>代理 lease 创建。</summary>
            public HandleLease Activate(int index) => inner.Activate(index);

            /// <summary>代理 lease 失活。</summary>
            public void Deactivate(HandleLease lease) => inner.Deactivate(lease);

            /// <summary>代理 work 最终完成。</summary>
            public void CompleteWork(HandleLease lease) => inner.CompleteWork(lease);

            /// <summary>在指定 claim 前建立可控竞态，再调用真实原子 registry。</summary>
            public bool TryClaim(HandleLease lease, RuntimeWindowFingerprint fingerprint)
            {
                if (lease.ItemIndex == owner.ItemIndex && fingerprint.Handle == owner.Handle)
                {
                    owner.Entered.TrySetResult();
                    owner.Release.Wait(CancellationToken.None);
                    try
                    {
                        return inner.TryClaim(lease, fingerprint);
                    }
                    finally
                    {
                        owner.Finished.TrySetResult();
                    }
                }

                return inner.TryClaim(lease, fingerprint);
            }

            /// <summary>代理未来 reservation。</summary>
            public bool TryReserve(
                HandleLease lease,
                int futureItemIndex,
                RuntimeWindowFingerprint fingerprint) =>
                inner.TryReserve(lease, futureItemIndex, fingerprint);

            /// <summary>代理 reservation 快照。</summary>
            public IReadOnlyList<RuntimeWindowFingerprint> GetReservationSnapshots(HandleLease lease) =>
                inner.GetReservationSnapshots(lease);

            /// <summary>代理精确 reservation 删除。</summary>
            public bool RemoveReservationIfMatches(
                HandleLease lease,
                RuntimeWindowFingerprint reservedFingerprint) =>
                inner.RemoveReservationIfMatches(lease, reservedFingerprint);

            /// <summary>代理重验证后的 reservation claim。</summary>
            public bool TryClaimReservation(
                HandleLease lease,
                RuntimeWindowFingerprint reservedFingerprint,
                RuntimeWindowFingerprint currentFingerprint) =>
                inner.TryClaimReservation(lease, reservedFingerprint, currentFingerprint);
        }
    }
}
