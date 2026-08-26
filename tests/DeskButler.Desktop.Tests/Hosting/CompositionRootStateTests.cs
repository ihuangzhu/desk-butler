using DeskButler.Application.Events;
using DeskButler.Application.Modules;
using DeskButler.Application.Commands;
using DeskButler.Core.Capture;
using DeskButler.Core.Diagnostics;
using DeskButler.Core.ResidentApps;
using DeskButler.Core.Scenes;
using DeskButler.Core.Time;
using DeskButler.Core.Settings;
using DeskButler.Desktop.Hosting;
using DeskButler.Desktop.ViewModels;
using DeskButler.Desktop.Tests.ViewModels;
using DeskButler.Infrastructure.Windows.Startup;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace DeskButler.Desktop.Tests.Hosting;

public sealed class CompositionRootStateTests
{
    /// <summary>常驻自动批次只能在既有三个启动阶段成功后非阻塞建立，阶段失败时不得调用。</summary>
    [Fact]
    public async Task ResidentBatchStartsOnlyAfterExistingStartupStagesSucceed()
    {
        var calls = new List<string>();
        var desktopBoundary = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var startup = new CompositionStartupCoordinator(
            _ =>
            {
                calls.Add("module:start");
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            () => calls.Add("session:start"),
            () => { },
            async _ =>
            {
                calls.Add("desktop:start");
                await desktopBoundary.Task;
            },
            () => ValueTask.CompletedTask);
        var cleanup = new BestEffortAsyncCleanup(
        [
            new CleanupStep("resident-start-test", () => ValueTask.CompletedTask)
        ]);

        var start = CompositionRoot.StartCoreAsync(
            startup,
            cleanup,
            () => calls.Add("resident:start"),
            CancellationToken.None);
        Assert.Equal(["module:start", "session:start", "desktop:start"], calls);
        Assert.False(start.IsCompleted);
        Assert.DoesNotContain("resident:start", calls);

        desktopBoundary.TrySetResult();
        await start;

        Assert.Equal(
            ["module:start", "session:start", "desktop:start", "resident:start"],
            calls);
    }

    /// <summary>生产常驻命令注册集必须让候选确认和所有列表编辑共享唯一设置协调器。</summary>
    [Fact]
    public async Task ResidentCommandRegistrationSharesCoordinatorAndRegistersEveryMutation()
    {
        var store = new InMemorySettingsStore(ButlerSettings.Default);
        using var settings = new SettingsCoordinator(store);
        var candidates = new ResidentCandidateCoordinator(new EmptyResidentDiscovery(), store, settings);
        var handlers = ResidentAppCommandHandlerSet.Create(candidates, settings, new AllowingResidentExecutablePolicy());
        var commandBus = new InProcessCommandBus();

        handlers.Register(commandBus);
        var result = await commandBus.SendAsync(
            new SetResidentApplicationsEnabledCommand(false), CancellationToken.None);
        var added = await commandBus.SendAsync(
            new AddResidentApplicationCommand(@"C:\Apps\chat.exe", "Chat"), CancellationToken.None);
        var setEntry = await commandBus.SendAsync(
            new SetResidentApplicationEnabledCommand(@"C:\Apps\chat.exe", false), CancellationToken.None);
        var moved = await commandBus.SendAsync(
            new MoveResidentApplicationCommand(@"C:\Apps\chat.exe", -1), CancellationToken.None);
        var replaced = await commandBus.SendAsync(
            new ReplaceResidentApplicationPathCommand(@"C:\Apps\chat.exe", @"C:\Apps\chat-new.exe"), CancellationToken.None);
        var removed = await commandBus.SendAsync(
            new RemoveResidentApplicationCommand(@"C:\Apps\chat-new.exe"), CancellationToken.None);

        Assert.Same(settings, handlers.Confirm.SettingsCoordinator);
        Assert.Same(settings, handlers.SetApplicationsEnabled.SettingsCoordinator);
        Assert.Same(settings, handlers.SetApplicationEnabled.SettingsCoordinator);
        Assert.Same(settings, handlers.Remove.SettingsCoordinator);
        Assert.Same(settings, handlers.Move.SettingsCoordinator);
        Assert.Same(settings, handlers.Add.SettingsCoordinator);
        Assert.Same(settings, handlers.Replace.SettingsCoordinator);
        Assert.True(result.Changed);
        Assert.False(result.ResidentApplicationsEnabled);
        Assert.True(added.Changed);
        Assert.True(setEntry.Changed);
        Assert.False(moved.Changed);
        Assert.True(replaced.Changed);
        Assert.True(removed.Changed);
    }

    /// <summary>构造中途失败时必须把已取得的资源按逆构造顺序各释放一次。</summary>
    [Fact]
    public async Task ConstructionFailureDisposesOwnedResourcesOnceInReverseOrder()
    {
        var calls = new List<string>();
        var constructionFailure = new InvalidOperationException("window construction failed");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CompositionResourceOwner.BuildAsync<object>(owner =>
            {
                owner.Own("diagnostic", "diagnostic", resource => RecordCleanupAsync(calls, resource));
                owner.Own("repository", "repository", resource => RecordCleanupAsync(calls, resource));
                owner.Own("view model", "view-model", resource => RecordCleanupAsync(calls, resource));
                return Task.FromException<object>(constructionFailure);
            }));

        Assert.Same(constructionFailure, thrown);
        Assert.Equal(["view-model", "repository", "diagnostic"], calls);
    }

    /// <summary>异步构造失败的回滚必须回到发起构造的 WPF Dispatcher，并且仅清理一次。</summary>
    [Fact]
    public Task ConstructionFailureAfterAsyncBoundaryRollsBackOnOriginatingDispatcherOnce() =>
        RunOnStaDispatcherAsync(async dispatcher =>
        {
            var constructionFailure = new InvalidOperationException("window construction failed");
            var boundary = new TaskCompletionSource<object>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanupCount = 0;
            var cleanupThreadId = 0;
            var originatingThreadId = Environment.CurrentManagedThreadId;

            var build = CompositionResourceOwner.BuildAsync<object>(owner =>
            {
                owner.Own(
                    "dispatcher resource",
                    new object(),
                    _ =>
                    {
                        Interlocked.Increment(ref cleanupCount);
                        cleanupThreadId = Environment.CurrentManagedThreadId;
                        dispatcher.VerifyAccess();
                        return ValueTask.CompletedTask;
                    });
                return boundary.Task;
            });
            Assert.False(build.IsCompleted);

            ThreadPool.QueueUserWorkItem(_ => boundary.TrySetException(constructionFailure));
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => build);

            Assert.Same(constructionFailure, thrown);
            Assert.Equal(1, Volatile.Read(ref cleanupCount));
            Assert.Equal(originatingThreadId, cleanupThreadId);
        });

    /// <summary>异步启动阶段失败的回滚必须回到发起启动的 WPF Dispatcher，并且仅清理一次。</summary>
    [Fact]
    public Task StartupFailureAfterAsyncBoundaryRollsBackOnOriginatingDispatcherOnce() =>
        RunOnStaDispatcherAsync(async dispatcher =>
        {
            var startupFailure = new IOException("desktop start failed");
            var moduleBoundary = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanupCount = 0;
            var cleanupThreadId = 0;
            var originatingThreadId = Environment.CurrentManagedThreadId;
            var cleanup = new BestEffortAsyncCleanup(
            [
                new CleanupStep(
                    "dispatcher resource",
                    () =>
                    {
                        Interlocked.Increment(ref cleanupCount);
                        cleanupThreadId = Environment.CurrentManagedThreadId;
                        dispatcher.VerifyAccess();
                        return ValueTask.CompletedTask;
                    })
            ]);
            var startup = new CompositionStartupCoordinator(
                _ => moduleBoundary.Task,
                _ => Task.CompletedTask,
                () => { },
                () => { },
                _ => Task.FromException(startupFailure),
                () => ValueTask.CompletedTask);

            var start = startup.StartAsync(cleanup, CancellationToken.None);
            Assert.False(start.IsCompleted);

            ThreadPool.QueueUserWorkItem(_ => moduleBoundary.TrySetResult());
            var thrown = await Assert.ThrowsAsync<IOException>(() => start);
            await cleanup.RunAsync();

            Assert.Same(startupFailure, thrown);
            Assert.Equal(1, Volatile.Read(ref cleanupCount));
            Assert.Equal(originatingThreadId, cleanupThreadId);
        });

    /// <summary>窗口清理从后台线程发起时必须异步投递到所属 Dispatcher 并真实关闭一次。</summary>
    [Fact]
    public Task WindowCleanupFromWorkerMarshalsCloseToOwningDispatcherOnce() =>
        RunOnStaDispatcherAsync(async dispatcher =>
        {
            var window = new Window();
            var closeCount = 0;
            var closeThreadId = 0;
            var originatingThreadId = Environment.CurrentManagedThreadId;

            await Task.Run(async () =>
                await DispatcherCleanup.RunAsync(
                    window.Dispatcher,
                    () =>
                    {
                        Interlocked.Increment(ref closeCount);
                        closeThreadId = Environment.CurrentManagedThreadId;
                        window.Close();
                    }));

            Assert.Same(dispatcher, window.Dispatcher);
            Assert.Equal(1, Volatile.Read(ref closeCount));
            Assert.Equal(originatingThreadId, closeThreadId);
        });

    /// <summary>所属 Dispatcher 已停止时窗口清理必须作为 best-effort 跳过且不执行委托。</summary>
    [Fact]
    public async Task WindowCleanupAfterDispatcherShutdownDoesNotMaskFailure()
    {
        var dispatcher = CreateShutdownDispatcher();
        var closeCount = 0;

        await DispatcherCleanup.RunAsync(
            dispatcher,
            () => Interlocked.Increment(ref closeCount));

        Assert.True(dispatcher.HasShutdownFinished);
        Assert.Equal(0, Volatile.Read(ref closeCount));
    }

    /// <summary>清理委托已进入后即使 Dispatcher 开始关闭，其真实失败也必须传播并保持步骤可重试。</summary>
    [Fact]
    public async Task WindowCleanupFailureAfterDelegateStartsAndShutdownPropagatesAndCanRetry()
    {
        using var dispatcherHost = StaDispatcherHost.Start();
        using var releaseCleanup = new ManualResetEventSlim();
        var cleanupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFailure = new IOException("window close failed");
        var cancellationToken = TestContext.Current.CancellationToken;
        var cleanupCount = 0;
        var cleanup = new BestEffortAsyncCleanup(
        [
            new CleanupStep(
                "window",
                () => DispatcherCleanup.RunAsync(
                    dispatcherHost.Dispatcher,
                    () =>
                    {
                        Interlocked.Increment(ref cleanupCount);
                        cleanupEntered.TrySetResult();
                        releaseCleanup.Wait(cancellationToken);
                        dispatcherHost.Dispatcher.InvokeShutdown();
                        throw cleanupFailure;
                    }))
        ]);

        try
        {
            var firstRun = cleanup.RunAsync().AsTask();
            await cleanupEntered.Task.WaitAsync(cancellationToken);
            releaseCleanup.Set();

            var thrown = await Assert.ThrowsAsync<AggregateException>(() => firstRun);
            var stepFailure = Assert.IsType<InvalidOperationException>(
                Assert.Single(thrown.InnerExceptions));

            Assert.Same(cleanupFailure, stepFailure.InnerException);
            Assert.False(cleanup.IsComplete);

            await cleanup.RunAsync();

            Assert.True(cleanup.IsComplete);
            Assert.Equal(1, Volatile.Read(ref cleanupCount));
        }
        finally
        {
            releaseCleanup.Set();
        }
    }

    /// <summary>排队清理尚未进入就被 Dispatcher shutdown 中止时必须静默完成且不调用委托。</summary>
    [Fact]
    public async Task WindowCleanupAbortedBeforeDelegateStartsDuringShutdownCompletesBestEffort()
    {
        using var dispatcherHost = StaDispatcherHost.Start();
        using var releaseDispatcher = new ManualResetEventSlim();
        var dispatcherBlocked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationToken = TestContext.Current.CancellationToken;
        var cleanupCount = 0;

        _ = dispatcherHost.Dispatcher.BeginInvoke(
            () =>
            {
                dispatcherBlocked.TrySetResult();
                releaseDispatcher.Wait(cancellationToken);
                dispatcherHost.Dispatcher.InvokeShutdown();
            },
            DispatcherPriority.Send);

        try
        {
            await dispatcherBlocked.Task.WaitAsync(cancellationToken);
            var cleanup = new BestEffortAsyncCleanup(
            [
                new CleanupStep(
                    "window",
                    () => DispatcherCleanup.RunAsync(
                        dispatcherHost.Dispatcher,
                        () => Interlocked.Increment(ref cleanupCount)))
            ]);
            var run = cleanup.RunAsync().AsTask();
            Assert.False(run.IsCompleted);

            releaseDispatcher.Set();
            await run;

            Assert.True(cleanup.IsComplete);
            Assert.Equal(0, Volatile.Read(ref cleanupCount));
        }
        finally
        {
            releaseDispatcher.Set();
        }
    }

    /// <summary>成功构造只转移一次所有权，原 owner 与重复根清理都不得重复释放。</summary>
    [Fact]
    public async Task SuccessfulConstructionTransfersCleanupWithoutDoubleDispose()
    {
        var calls = new List<string>();
        var cleanup = await CompositionResourceOwner.BuildAsync(owner =>
        {
            owner.Own("resource", "resource", resource => RecordCleanupAsync(calls, resource));
            return Task.FromResult(owner.PrepareCleanup());
        });

        await cleanup.RunAsync();
        await cleanup.RunAsync();

        Assert.Equal(["resource"], calls);
    }

    /// <summary>模块已启动而会话订阅失败时必须停止模块，后续 Dispose 不得重复清理。</summary>
    [Fact]
    public async Task ModuleStartThenSessionFailureStopsModuleAndDisposeIsIdempotent()
    {
        var calls = new List<string>();
        var sessionFailure = new InvalidOperationException("session start failed");
        var runtime = await CreateStartupRuntimeAsync(calls, sessionFailure, null);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.Startup.StartAsync(runtime.Cleanup, CancellationToken.None));
        await runtime.Cleanup.RunAsync();

        Assert.Same(sessionFailure, thrown);
        Assert.Equal(1, calls.Count(call => call == "module:stop"));
        Assert.Equal(1, calls.Count(call => call == "session:stop"));
    }

    /// <summary>会话已启动而桌面变化源失败时，清理必须先停止会话再停止模块。</summary>
    [Fact]
    public async Task SessionStartThenDesktopFailureStopsSessionBeforeModule()
    {
        var calls = new List<string>();
        var desktopFailure = new IOException("desktop start failed");
        var runtime = await CreateStartupRuntimeAsync(calls, null, desktopFailure);

        var thrown = await Assert.ThrowsAsync<IOException>(() =>
            runtime.Startup.StartAsync(runtime.Cleanup, CancellationToken.None));

        Assert.Same(desktopFailure, thrown);
        Assert.True(calls.IndexOf("session:stop") < calls.IndexOf("module:stop"));
        Assert.Equal(1, calls.Count(call => call == "module:stop"));
    }

    /// <summary>部分启动失败必须在释放诊断日志前封存并排空模块诊断 tracker。</summary>
    [Fact]
    public async Task PartialStartDrainsModuleDiagnosticsBeforeDiagnosticLogDispose()
    {
        var calls = new List<string>();
        var runtime = await CreateStartupRuntimeAsync(
            calls, new InvalidOperationException("session start failed"), null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.Startup.StartAsync(runtime.Cleanup, CancellationToken.None));

        Assert.True(calls.IndexOf("diagnostics:drain") < calls.IndexOf("diagnostic-log:dispose"));
        Assert.True(calls.IndexOf("module:stop") < calls.IndexOf("diagnostics:drain"));
    }

    /// <summary>成功启动后多次 Dispose 必须让三个停止阶段和所有资源清理各执行一次。</summary>
    [Fact]
    public async Task SuccessfulStartAndRepeatedDisposeDoNotDoubleStopOrDispose()
    {
        var calls = new List<string>();
        var runtime = await CreateStartupRuntimeAsync(calls, null, null);

        await runtime.Startup.StartAsync(runtime.Cleanup, CancellationToken.None);
        await runtime.Cleanup.RunAsync();
        await runtime.Cleanup.RunAsync();

        Assert.Equal(1, calls.Count(call => call == "module:start"));
        Assert.Equal(1, calls.Count(call => call == "session:start"));
        Assert.Equal(1, calls.Count(call => call == "desktop:start"));
        Assert.Equal(1, calls.Count(call => call == "session:stop"));
        Assert.Equal(1, calls.Count(call => call == "module:stop"));
        Assert.Equal(1, calls.Count(call => call == "desktop:stop"));
        Assert.Equal(1, calls.Count(call => call == "diagnostics:drain"));
        Assert.Equal(1, calls.Count(call => call == "diagnostic-log:dispose"));
    }

    /// <summary>启动失败回滚与外部释放重叠时必须共享清理 pass，并各自观察一致结果。</summary>
    [Fact]
    public async Task StartupFailureCleanupOverlappingDisposeRunsEachStepOnce()
    {
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupCalls = 0;
        var startupFailure = new IOException("desktop start failed");
        var cleanup = new BestEffortAsyncCleanup(
        [
            new CleanupStep("shared", async () =>
            {
                Interlocked.Increment(ref cleanupCalls);
                cleanupStarted.TrySetResult();
                await releaseCleanup.Task.WaitAsync(TestContext.Current.CancellationToken);
            })
        ]);
        var startup = new CompositionStartupCoordinator(
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            () => { },
            () => { },
            _ => Task.FromException(startupFailure),
            () => ValueTask.CompletedTask);

        var start = startup.StartAsync(cleanup, CancellationToken.None);
        await cleanupStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var dispose = cleanup.RunAsync().AsTask();
        try
        {
            Assert.Equal(1, Volatile.Read(ref cleanupCalls));
            Assert.False(start.IsCompleted);
            Assert.False(dispose.IsCompleted);
        }
        finally
        {
            releaseCleanup.TrySetResult();
        }

        var thrown = await Assert.ThrowsAsync<IOException>(() => start);
        await dispose;

        Assert.Same(startupFailure, thrown);
        Assert.Equal(1, Volatile.Read(ref cleanupCalls));
        Assert.True(cleanup.IsComplete);
    }

    /// <summary>Report 正在创建任务时 Drain 必须等待登记完成且不得遗漏该任务。</summary>
    [Fact]
    public async Task ModuleDiagnosticDrainWaitsForTaskCreationAndRegistration()
    {
        var taskCreationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTaskCreation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDiagnostic = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tracker = new ModuleEventDiagnosticTracker(_ =>
        {
            taskCreationEntered.TrySetResult();
            releaseTaskCreation.Task.GetAwaiter().GetResult();
            return releaseDiagnostic.Task;
        });

        var report = Task.Run(
            () => tracker.Report(new InvalidOperationException("observer failure")),
            TestContext.Current.CancellationToken);
        await taskCreationEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var drain = tracker.DrainAsync().AsTask();

        Assert.False(drain.IsCompleted);
        releaseTaskCreation.TrySetResult();
        await report;
        Assert.False(drain.IsCompleted);

        releaseDiagnostic.TrySetResult();
        await drain;
    }

    /// <summary>首次 Drain 必须封存 tracker，多次 Drain 幂等且后续 Report 不再启动任务。</summary>
    [Fact]
    public async Task ModuleDiagnosticDrainSealsTrackerAndConcurrentDrainsAreIdempotent()
    {
        var starts = 0;
        var releaseDiagnostic = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tracker = new ModuleEventDiagnosticTracker(_ =>
        {
            Interlocked.Increment(ref starts);
            return releaseDiagnostic.Task;
        });
        tracker.Report(new InvalidOperationException("before drain"));

        var firstDrain = tracker.DrainAsync().AsTask();
        var secondDrain = tracker.DrainAsync().AsTask();

        Assert.False(firstDrain.IsCompleted);
        Assert.False(secondDrain.IsCompleted);
        releaseDiagnostic.TrySetResult();
        await Task.WhenAll(firstDrain, secondDrain);

        tracker.Report(new InvalidOperationException("after drain"));
        await tracker.DrainAsync();

        Assert.Equal(1, Volatile.Read(ref starts));
    }

    /// <summary>两秒边界必须取消等待，并观察之后才发生的日志 fault。</summary>
    [Fact]
    public async Task ModuleDiagnosticTimeoutObservesLateFaultWithoutEscapingDrain()
    {
        var lateFailure = new IOException("late diagnostic failure");
        var unobserved = 0;
        void HandleUnobserved(object? _, UnobservedTaskExceptionEventArgs eventArgs)
        {
            if (eventArgs.Exception.Flatten().InnerExceptions.Any(exception => ReferenceEquals(exception, lateFailure)))
            {
                Interlocked.Increment(ref unobserved);
            }
        }

        TaskScheduler.UnobservedTaskException += HandleUnobserved;
        try
        {
            var probe = await RunTimedOutDiagnosticAndFailLateAsync(lateFailure);
            for (var attempt = 0; attempt < 10 && probe.LateTask.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert.False(probe.LateTask.IsAlive);
            Assert.Equal(0, Volatile.Read(ref unobserved));
            GC.KeepAlive(probe.Runtime);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= HandleUnobserved;
        }
    }

    /// <summary>生产模块组合必须共享事件总线、保持真实 UI 状态并等待脱敏诊断写入。</summary>
    [Fact]
    public async Task ModuleStateCompositionSharesBusAndTracksSafeObserverDiagnostics()
    {
        var calls = new List<string>();
        var diagnosticLog = new BlockingDiagnosticLog();
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 25, 10, 30, 0, TimeSpan.Zero));
        var module = new RecordingModule("workspace", calls);
        var runtime = CompositionRoot.CreateModuleStateComposition(module, diagnosticLog, clock);
        var statuses = new List<ModuleStatusChanged>();
        using var failingSubscription = runtime.EventBus.Subscribe<ModuleStatusChanged>(
            "failing-observer", (status, _) =>
            {
                statuses.Add(status);
                return Task.FromException(new InvalidOperationException("sensitive observer detail"));
            });
        using var viewModel = new MainViewModel(
            new InMemorySceneRepository(), new RecordingCommandBus(),
            new InMemorySettingsStore(ButlerSettings.Default), new InlineUiDispatcher(),
            eventBus: runtime.EventBus, moduleDescriptor: module.Descriptor);

        await runtime.Host.StartAsync(CancellationToken.None);
        await diagnosticLog.FirstWriteStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await runtime.Host.StopAsync(CancellationToken.None);
        var drain = runtime.DrainDiagnosticsAsync().AsTask();

        Assert.False(drain.IsCompleted);
        Assert.Contains("已停止", viewModel.ModuleStatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(statuses, status => status.State == ModuleRunState.Failed);

        diagnosticLog.ReleaseWrites.TrySetResult();
        await drain;
        await runtime.DrainDiagnosticsAsync();

        Assert.Equal(["start:workspace", "stop:workspace"], calls);
        Assert.Contains("已停止", viewModel.ModuleStatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(statuses, status => status.State == ModuleRunState.Failed);
        Assert.Equal(2, diagnosticLog.Events.Count);
        Assert.All(diagnosticLog.Events, diagnosticEvent =>
        {
            Assert.Equal(clock.UtcNow, diagnosticEvent.Timestamp);
            Assert.Equal(DiagnosticLevel.Warning, diagnosticEvent.Level);
            Assert.Equal("module-status", diagnosticEvent.Category);
            Assert.Equal("模块状态观察者处理失败。", diagnosticEvent.Message);
            var property = Assert.Single(Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
                diagnosticEvent.Properties));
            Assert.Equal("exceptionType", property.Key);
            Assert.Equal("System.InvalidOperationException", property.Value);
            Assert.DoesNotContain("sensitive observer detail", diagnosticEvent.ToString(), StringComparison.Ordinal);
        });
    }

    /// <summary>生产诊断日志自身失败不得改变模块生命周期结果或清理结果。</summary>
    [Fact]
    public async Task ModuleObserverDiagnosticFailureDoesNotEscapeLifecycleOrCleanup()
    {
        var calls = new List<string>();
        var module = new RecordingModule("workspace", calls);
        var runtime = CompositionRoot.CreateModuleStateComposition(
            module,
            new FailingDiagnosticLog(),
            new FixedClock(DateTimeOffset.UnixEpoch));
        using var subscription = runtime.EventBus.Subscribe<ModuleStatusChanged>(
            "failing-observer", (_, _) => Task.FromException(new IOException("observer failure")));

        await runtime.Host.StartAsync(CancellationToken.None);
        await runtime.DrainDiagnosticsAsync();

        Assert.Equal(["start:workspace"], calls);
    }

    /// <summary>注册目标先改变后失败时必须保留根因并独立尝试注册与设置补偿。</summary>
    [Fact]
    public async Task StartupRegistrationTargetFailureAttemptsAllCompensationsAndReleasesGate()
    {
        var calls = new List<string>();
        var targetFailure = new InvalidOperationException("注册目标失败");
        var registrationRollbackFailure = new InvalidOperationException("注册补偿失败");
        var settingsRollbackFailure = new IOException("设置补偿失败");
        var initial = ButlerSettings.Default with { StartupEnabled = false };
        var store = new RegistrationFailureSettingsStore(
            initial, calls, settingsRollbackFailure);
        var registration = new ChangeThenThrowStartupRegistration(
            calls, targetFailure, registrationRollbackFailure);
        using var settings = new SettingsCoordinator(store);
        var handler = new SetStartupEnabledCommandHandler(settings, registration);

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => handler.HandleAsync(new SetStartupEnabledCommand(true), CancellationToken.None));

        Assert.Equal(3, error.InnerExceptions.Count);
        Assert.Same(targetFailure, error.InnerExceptions[0]);
        Assert.Same(registrationRollbackFailure, error.InnerExceptions[1]);
        Assert.Same(settingsRollbackFailure, error.InnerExceptions[2]);
        Assert.Equal(["registration:target", "registration:rollback", "settings:rollback"], calls);
        Assert.False(store.TargetSaveAttempted);
        Assert.True(store.RollbackAttempted);
        Assert.True(registration.RollbackAttempted);

        var updated = await settings.UpdateAsync(
            current => current with { CaptureEnabled = false }, CancellationToken.None);
        Assert.False(updated.CaptureEnabled);
    }

    /// <summary>注册目标失败且补偿成功时必须抛回同一异常实例并保留最初抛出位置。</summary>
    [Fact]
    public async Task StartupRegistrationTargetFailurePreservesOriginalExceptionAndStack()
    {
        var calls = new List<string>();
        var targetFailure = new InvalidOperationException("注册目标失败");
        var initial = ButlerSettings.Default with { StartupEnabled = false };
        var store = new RegistrationFailureSettingsStore(initial, calls);
        var registration = new ChangeThenThrowStartupRegistration(calls, targetFailure);
        using var settings = new SettingsCoordinator(store);
        var handler = new SetStartupEnabledCommandHandler(settings, registration);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(new SetStartupEnabledCommand(true), CancellationToken.None));

        Assert.Same(targetFailure, thrown);
        Assert.Contains(
            nameof(ChangeThenThrowStartupRegistration.ThrowTargetFailure),
            thrown.StackTrace,
            StringComparison.Ordinal);
        Assert.Equal(["registration:target", "registration:rollback", "settings:rollback"], calls);
        Assert.False(store.TargetSaveAttempted);
        Assert.False(registration.IsEnabled);
        Assert.False(store.Current.StartupEnabled);
    }

    /// <summary>仅目标保存失败时必须抛回同一异常实例并保留最初抛出位置。</summary>
    [Fact]
    public async Task StartupSaveFailurePreservesOriginalExceptionAndStack()
    {
        var originalFailure = new IOException("目标保存失败");
        var store = new SingleFailureSettingsStore(
            ButlerSettings.Default with { StartupEnabled = false }, originalFailure);
        var registration = new RecordingStartupRegistration(false);
        using var settings = new SettingsCoordinator(store);
        var handler = new SetStartupEnabledCommandHandler(settings, registration);

        var thrown = await Assert.ThrowsAsync<IOException>(
            () => handler.HandleAsync(new SetStartupEnabledCommand(true), CancellationToken.None));

        Assert.Same(originalFailure, thrown);
        Assert.Contains(nameof(SingleFailureSettingsStore.ThrowOriginalFailure), thrown.StackTrace, StringComparison.Ordinal);
        Assert.False(registration.IsEnabled);
        Assert.False(store.Current.StartupEnabled);
    }

    /// <summary>保存阶段取消后仍须用独立令牌补偿并释放门供后续设置修改。</summary>
    [Fact]
    public async Task StartupSaveCancellationCompensatesAndReleasesMutationGate()
    {
        using var source = new CancellationTokenSource();
        var cancellation = new OperationCanceledException(source.Token);
        var store = new SingleFailureSettingsStore(
            ButlerSettings.Default with { StartupEnabled = false }, cancellation);
        var registration = new RecordingStartupRegistration(false);
        using var settings = new SettingsCoordinator(store);
        var handler = new SetStartupEnabledCommandHandler(settings, registration);

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(
            () => handler.HandleAsync(new SetStartupEnabledCommand(true), source.Token));
        var updated = await settings.UpdateAsync(
            current => current with { CaptureEnabled = false }, CancellationToken.None);

        Assert.Same(cancellation, thrown);
        Assert.Equal(CancellationToken.None, store.RollbackToken);
        Assert.False(registration.IsEnabled);
        Assert.False(store.Current.StartupEnabled);
        Assert.False(updated.CaptureEnabled);
    }

    /// <summary>注册目标失败后的静默无效注册回滚必须作为独立补偿故障，并释放设置门。</summary>
    [Fact]
    public async Task StartupTargetFailureDetectsSilentRegistrationRollbackMismatch()
    {
        var targetFailure = new InvalidOperationException("注册目标失败");
        var store = new InMemorySettingsStore(
            ButlerSettings.Default with { StartupEnabled = false });
        var registration = new ChangeThenThrowNoOpRollbackStartupRegistration(targetFailure);
        using var settings = new SettingsCoordinator(store);

        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            settings.SetStartupEnabledAsync(registration, true, CancellationToken.None));
        var updated = await settings.UpdateAsync(
            current => current with { CaptureEnabled = false }, CancellationToken.None);

        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Same(targetFailure, error.InnerExceptions[0]);
        Assert.Contains("注册", error.InnerExceptions[1].Message, StringComparison.Ordinal);
        Assert.True(registration.RollbackAttempted);
        Assert.True(registration.IsEnabled);
        Assert.False(updated.CaptureEnabled);
    }

    /// <summary>目标保存失败后的静默无效设置回滚必须重载发现错误值，并释放设置门。</summary>
    [Fact]
    public async Task StartupSaveFailureDetectsSilentSettingsRollbackMismatch()
    {
        var targetFailure = new IOException("目标保存失败");
        var store = new SilentRollbackSettingsStore(
            ButlerSettings.Default with { StartupEnabled = false }, targetFailure);
        var registration = new RecordingStartupRegistration(false);
        using var settings = new SettingsCoordinator(store);

        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            settings.SetStartupEnabledAsync(registration, true, CancellationToken.None));
        var updated = await settings.UpdateAsync(
            current => current with { CaptureEnabled = false }, CancellationToken.None);

        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Same(targetFailure, error.InnerExceptions[0]);
        Assert.Contains("设置", error.InnerExceptions[1].Message, StringComparison.Ordinal);
        Assert.False(registration.IsEnabled);
        Assert.True(store.RollbackAttempted);
        Assert.Equal(CancellationToken.None, store.RollbackReloadToken);
        Assert.True(store.Current.StartupEnabled);
        Assert.False(updated.CaptureEnabled);
    }

    /// <summary>注册与设置静默回滚同时失效时必须保留两个诊断且原始失败仍位于首位。</summary>
    [Fact]
    public async Task StartupSaveFailurePreservesBothSilentCompensationMismatches()
    {
        var targetFailure = new IOException("目标保存失败");
        var store = new SilentRollbackSettingsStore(
            ButlerSettings.Default with { StartupEnabled = false }, targetFailure);
        var registration = new NoOpRollbackStartupRegistration(false);
        using var settings = new SettingsCoordinator(store);

        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            settings.SetStartupEnabledAsync(registration, true, CancellationToken.None));
        var updated = await settings.UpdateAsync(
            current => current with { CaptureEnabled = false }, CancellationToken.None);

        Assert.Equal(3, error.InnerExceptions.Count);
        Assert.Same(targetFailure, error.InnerExceptions[0]);
        Assert.Contains("注册", error.InnerExceptions[1].Message, StringComparison.Ordinal);
        Assert.Contains("设置", error.InnerExceptions[2].Message, StringComparison.Ordinal);
        Assert.True(registration.RollbackAttempted);
        Assert.True(registration.IsEnabled);
        Assert.True(store.RollbackAttempted);
        Assert.Equal(CancellationToken.None, store.RollbackReloadToken);
        Assert.True(store.Current.StartupEnabled);
        Assert.False(updated.CaptureEnabled);
    }

    /// <summary>注册调用静默无效时必须在保存目标 JSON 前失败，独立补偿并释放设置门。</summary>
    [Fact]
    public async Task StartupNoOpRegistrationMismatchCompensatesAndReleasesGate()
    {
        var calls = new List<string>();
        var registrationRollbackFailure = new InvalidOperationException("注册补偿失败");
        var settingsRollbackFailure = new IOException("设置补偿失败");
        var initial = ButlerSettings.Default with { StartupEnabled = false };
        var store = new RegistrationFailureSettingsStore(initial, calls, settingsRollbackFailure);
        var registration = new NoOpTargetStartupRegistration(calls, registrationRollbackFailure);
        using var settings = new SettingsCoordinator(store);
        var handler = new SetStartupEnabledCommandHandler(settings, registration);

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => handler.HandleAsync(new SetStartupEnabledCommand(true), CancellationToken.None));
        var mismatch = Assert.IsType<InvalidOperationException>(error.InnerExceptions[0]);
        var updated = await settings.UpdateAsync(
            current => current with { CaptureEnabled = false }, CancellationToken.None);

        Assert.Contains("请求状态", mismatch.Message, StringComparison.Ordinal);
        Assert.Same(registrationRollbackFailure, error.InnerExceptions[1]);
        Assert.Same(settingsRollbackFailure, error.InnerExceptions[2]);
        Assert.Equal(["registration:target", "registration:rollback", "settings:rollback"], calls[..3]);
        Assert.False(store.TargetSaveAttempted);
        Assert.True(store.RollbackAttempted);
        Assert.True(registration.RollbackAttempted);
        Assert.False(updated.CaptureEnabled);
    }

    /// <summary>注册在目标 JSON 保存期间翻转时必须把后置条件失败作为根因并尝试全部补偿。</summary>
    [Fact]
    public async Task StartupRegistrationFlipDuringTargetSaveCompensatesAndReleasesGate()
    {
        var calls = new List<string>();
        var registrationRollbackFailure = new InvalidOperationException("注册补偿失败");
        var settingsRollbackFailure = new IOException("设置补偿失败");
        var registration = new FlippableStartupRegistration(calls, registrationRollbackFailure);
        var store = new FlipAfterTargetSaveSettingsStore(
            ButlerSettings.Default with { StartupEnabled = false },
            registration,
            calls,
            settingsRollbackFailure);
        using var settings = new SettingsCoordinator(store);
        var handler = new SetStartupEnabledCommandHandler(settings, registration);

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => handler.HandleAsync(new SetStartupEnabledCommand(true), CancellationToken.None));
        var mismatch = Assert.IsType<InvalidOperationException>(error.InnerExceptions[0]);
        var updated = await settings.UpdateAsync(
            current => current with { CaptureEnabled = false }, CancellationToken.None);

        Assert.Contains("请求状态", mismatch.Message, StringComparison.Ordinal);
        Assert.Same(registrationRollbackFailure, error.InnerExceptions[1]);
        Assert.Same(settingsRollbackFailure, error.InnerExceptions[2]);
        Assert.Equal(
            ["registration:target", "settings:target", "registration:rollback", "settings:rollback"],
            calls[..4]);
        Assert.True(store.TargetSaveAttempted);
        Assert.True(store.RollbackAttempted);
        Assert.True(registration.RollbackAttempted);
        Assert.False(updated.CaptureEnabled);
    }

    /// <summary>两个提交后置条件都成立时必须返回调用方请求的精确注册状态。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartupSuccessReturnsRequestedRegistrationState(bool enabled)
    {
        var store = new InMemorySettingsStore(
            ButlerSettings.Default with { StartupEnabled = !enabled });
        var registration = new RecordingStartupRegistration(!enabled);
        using var settings = new SettingsCoordinator(store);

        var result = await settings.SetStartupEnabledAsync(
            registration, enabled, CancellationToken.None);

        Assert.Equal(enabled, result);
        Assert.Equal(enabled, registration.IsEnabled);
        Assert.Equal(enabled, store.Current.StartupEnabled);
    }

    /// <summary>手动观察即使捕获暂停也只读取一次最新设置和底层清单，并继续应用永久排除。</summary>
    [Fact]
    public async Task ManualWindowObservationIgnoresPauseButAppliesLatestExclusionsOnce()
    {
        var settings = ButlerSettings.Default with
        {
            CaptureEnabled = false,
            ExcludedExecutablePaths = new HashSet<string>([@"C:\Apps\excluded.exe"], StringComparer.OrdinalIgnoreCase)
        };
        var store = new CountingSettingsStore(settings);
        var inner = new CountingWindowInventory(
        [
            ManualCandidate(1, @"C:\Apps\excluded.exe"),
            ManualCandidate(2, @"C:\Apps\visible.exe")
        ]);
        var inventory = new SettingsAwareWindowInventory(inner, store);

        var observation = await inventory.CaptureForManualAsync(CancellationToken.None);

        Assert.False(observation.CaptureEnabled);
        Assert.Equal(@"C:\Apps\visible.exe", Assert.Single(observation.Candidates).ExecutablePath);
        Assert.Equal(1, store.LoadCount);
        Assert.Equal(1, inner.CaptureCount);
    }

    /// <summary>构造手动观察使用的稳定普通窗口候选。</summary>
    private static WindowCandidate ManualCandidate(int handle, string executablePath) => new(
        handle,
        10 + handle,
        executablePath,
        "WindowClass",
        "Window title",
        null,
        new WindowBounds(10, 10, 800, 600),
        SceneWindowState.Normal,
        new MonitorIdentity("DISPLAY1", new WindowBounds(0, 0, 1920, 1080), 96, 96),
        true,
        false,
        false,
        false,
        false);

    private sealed class CountingSettingsStore(ButlerSettings current) : ISettingsStore
    {
        internal int LoadCount { get; private set; }

        /// <inheritdoc />
        public Task<ButlerSettings> LoadAsync(CancellationToken cancellationToken)
        {
            LoadCount++;
            return Task.FromResult(current);
        }

        /// <inheritdoc />
        public Task SaveAsync(ButlerSettings settings, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CountingWindowInventory(IReadOnlyList<WindowCandidate> candidates) : IWindowInventory
    {
        internal int CaptureCount { get; private set; }

        /// <inheritdoc />
        public Task<IReadOnlyList<WindowCandidate>> CaptureAsync(CancellationToken cancellationToken)
        {
            CaptureCount++;
            return Task.FromResult(candidates);
        }
    }

    private sealed class SingleFailureSettingsStore(ButlerSettings initial, Exception failure) : ISettingsStore
    {
        private int saveCount;
        internal ButlerSettings Current { get; private set; } = initial;
        internal CancellationToken? RollbackToken { get; private set; }

        /// <inheritdoc />
        public Task<ButlerSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Current);

        /// <inheritdoc />
        public Task SaveAsync(ButlerSettings settings, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref saveCount) == 1)
            {
                ThrowOriginalFailure(failure);
            }

            RollbackToken ??= cancellationToken;
            Current = settings;
            return Task.CompletedTask;
        }

        /// <summary>从稳定测试边界抛出原始异常，供堆栈保留断言识别。</summary>
        internal static void ThrowOriginalFailure(Exception failure) => throw failure;
    }

    private sealed class RecordingStartupRegistration(bool enabled) : IStartupRegistration
    {
        /// <inheritdoc />
        public bool IsEnabled { get; private set; } = enabled;

        /// <inheritdoc />
        public void Enable() => IsEnabled = true;

        /// <inheritdoc />
        public void Disable() => IsEnabled = false;
    }

    private sealed class ChangeThenThrowNoOpRollbackStartupRegistration(Exception targetFailure)
        : IStartupRegistration
    {
        /// <inheritdoc />
        public bool IsEnabled { get; private set; }

        internal bool RollbackAttempted { get; private set; }

        /// <inheritdoc />
        public void Enable()
        {
            IsEnabled = true;
            throw targetFailure;
        }

        /// <inheritdoc />
        public void Disable() => RollbackAttempted = true;
    }

    private sealed class NoOpRollbackStartupRegistration(bool enabled) : IStartupRegistration
    {
        /// <inheritdoc />
        public bool IsEnabled { get; private set; } = enabled;

        internal bool RollbackAttempted { get; private set; }

        /// <inheritdoc />
        public void Enable() => IsEnabled = true;

        /// <inheritdoc />
        public void Disable() => RollbackAttempted = true;
    }

    private sealed class SilentRollbackSettingsStore(
        ButlerSettings initial,
        Exception targetFailure) : ISettingsStore
    {
        private int loadCount;
        private int saveCount;

        internal ButlerSettings Current { get; private set; } = initial;

        internal bool RollbackAttempted { get; private set; }

        internal CancellationToken? RollbackReloadToken { get; private set; }

        /// <inheritdoc />
        public Task<ButlerSettings> LoadAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref loadCount) == 2)
            {
                RollbackReloadToken = cancellationToken;
            }

            return Task.FromResult(Current);
        }

        /// <inheritdoc />
        public Task SaveAsync(ButlerSettings settings, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref saveCount);
            if (call == 1)
            {
                Current = settings;
                throw targetFailure;
            }

            if (call == 2)
            {
                RollbackAttempted = true;
                return Task.CompletedTask;
            }

            Current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpTargetStartupRegistration(
        List<string> calls,
        Exception rollbackFailure) : IStartupRegistration
    {
        /// <inheritdoc />
        public bool IsEnabled => false;

        internal bool RollbackAttempted { get; private set; }

        /// <inheritdoc />
        public void Enable() => calls.Add("registration:target");

        /// <inheritdoc />
        public void Disable()
        {
            calls.Add("registration:rollback");
            RollbackAttempted = true;
            throw rollbackFailure;
        }
    }

    private sealed class FlippableStartupRegistration(
        List<string> calls,
        Exception rollbackFailure) : IStartupRegistration
    {
        /// <inheritdoc />
        public bool IsEnabled { get; private set; }

        internal bool RollbackAttempted { get; private set; }

        /// <inheritdoc />
        public void Enable()
        {
            calls.Add("registration:target");
            IsEnabled = true;
        }

        /// <inheritdoc />
        public void Disable()
        {
            calls.Add("registration:rollback");
            RollbackAttempted = true;
            IsEnabled = false;
            throw rollbackFailure;
        }

        /// <summary>模拟外部注册状态在 JSON 保存期间被另一主体翻转。</summary>
        internal void FlipOff() => IsEnabled = false;
    }

    private sealed class FlipAfterTargetSaveSettingsStore(
        ButlerSettings initial,
        FlippableStartupRegistration registration,
        List<string> calls,
        Exception rollbackFailure) : ISettingsStore
    {
        private int rollbackCount;

        internal ButlerSettings Current { get; private set; } = initial;

        internal bool TargetSaveAttempted { get; private set; }

        internal bool RollbackAttempted { get; private set; }

        /// <inheritdoc />
        public Task<ButlerSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Current);

        /// <inheritdoc />
        public Task SaveAsync(ButlerSettings settings, CancellationToken cancellationToken)
        {
            if (settings.StartupEnabled)
            {
                TargetSaveAttempted = true;
                calls.Add("settings:target");
                Current = settings;
                registration.FlipOff();
                return Task.CompletedTask;
            }

            RollbackAttempted = true;
            calls.Add("settings:rollback");
            if (Interlocked.Increment(ref rollbackCount) == 1)
            {
                return Task.FromException(rollbackFailure);
            }

            Current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class RegistrationFailureSettingsStore(
        ButlerSettings initial,
        List<string> calls,
        Exception? firstRollbackFailure = null) : ISettingsStore
    {
        private int originalSaveCount;
        internal ButlerSettings Current { get; private set; } = initial;
        internal bool TargetSaveAttempted { get; private set; }
        internal bool RollbackAttempted { get; private set; }

        /// <inheritdoc />
        public Task<ButlerSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Current);

        /// <inheritdoc />
        public Task SaveAsync(ButlerSettings settings, CancellationToken cancellationToken)
        {
            if (settings.StartupEnabled)
            {
                TargetSaveAttempted = true;
                calls.Add("settings:target");
            }
            else
            {
                RollbackAttempted = true;
                calls.Add("settings:rollback");
                if (Interlocked.Increment(ref originalSaveCount) == 1 && firstRollbackFailure is not null)
                {
                    return Task.FromException(firstRollbackFailure);
                }
            }

            Current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class ChangeThenThrowStartupRegistration(
        List<string> calls,
        Exception targetFailure,
        Exception? rollbackFailure = null) : IStartupRegistration
    {
        /// <inheritdoc />
        public bool IsEnabled { get; private set; }

        internal bool RollbackAttempted { get; private set; }

        /// <inheritdoc />
        public void Enable()
        {
            calls.Add("registration:target");
            IsEnabled = true;
            ThrowTargetFailure(targetFailure);
        }

        /// <inheritdoc />
        public void Disable()
        {
            calls.Add("registration:rollback");
            RollbackAttempted = true;
            IsEnabled = false;
            if (rollbackFailure is not null)
            {
                throw rollbackFailure;
            }
        }

        /// <summary>从稳定测试边界抛出注册目标异常，供堆栈保留断言识别。</summary>
        internal static void ThrowTargetFailure(Exception failure) => throw failure;
    }

    private sealed class RecordingModule(string id, List<string> calls) : IModule
    {
        /// <inheritdoc />
        public string Id { get; } = id;

        /// <inheritdoc />
        public ModuleDescriptor Descriptor { get; } =
            new(id, "工作区恢复", new Version(1, 0), true, [], [], []);

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            calls.Add($"start:{Id}");
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            calls.Add($"stop:{Id}");
            return Task.CompletedTask;
        }
    }

    /// <summary>创建与生产相同清理顺序的可控启动阶段对象图。</summary>
    private static async Task<StartupTestRuntime> CreateStartupRuntimeAsync(
        List<string> calls,
        Exception? sessionFailure,
        Exception? desktopFailure)
    {
        var startup = new CompositionStartupCoordinator(
            _ =>
            {
                calls.Add("module:start");
                return Task.CompletedTask;
            },
            _ =>
            {
                calls.Add("module:stop");
                return Task.CompletedTask;
            },
            () =>
            {
                calls.Add("session:start");
                if (sessionFailure is not null)
                {
                    throw sessionFailure;
                }
            },
            () => calls.Add("session:stop"),
            _ =>
            {
                calls.Add("desktop:start");
                return desktopFailure is null
                    ? Task.CompletedTask
                    : Task.FromException(desktopFailure);
            },
            () => RecordCleanupAsync(calls, "desktop:stop"));
        var cleanup = await CompositionResourceOwner.BuildAsync(owner =>
        {
            owner.Own("diagnostic log", "diagnostic-log:dispose", value => RecordCleanupAsync(calls, value));
            owner.Own("desktop source", startup, coordinator => coordinator.DisposeDesktopAsync());
            owner.Own("module diagnostics", "diagnostics:drain", value => RecordCleanupAsync(calls, value));
            owner.Own("module host", startup, coordinator => coordinator.StopModuleIfStartedAsync());
            owner.Own("session source", startup, coordinator => coordinator.DisposeSessionAsync());
            return Task.FromResult(owner.PrepareCleanup());
        });
        return new StartupTestRuntime(startup, cleanup);
    }

    private sealed record StartupTestRuntime(
        CompositionStartupCoordinator Startup,
        BestEffortAsyncCleanup Cleanup);

    /// <summary>记录一个可控资源清理动作。</summary>
    private static ValueTask RecordCleanupAsync(List<string> calls, string resource)
    {
        calls.Add(resource);
        return ValueTask.CompletedTask;
    }

    /// <summary>在专用 STA 线程上运行真实 WPF Dispatcher，完成后确定性停止消息循环。</summary>
    private static async Task RunOnStaDispatcherAsync(Func<Dispatcher, Task> test)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await test(dispatcher);
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            await completion.Task;
        }
        finally
        {
            thread.Join();
        }
    }

    /// <summary>创建并确定性停止一个专用 STA Dispatcher，供关闭竞态测试使用。</summary>
    private static Dispatcher CreateShutdownDispatcher()
    {
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return dispatcher ?? throw new InvalidOperationException("未能创建测试 Dispatcher。");
    }

    /// <summary>承载可由测试线程控制关闭的专用 STA Dispatcher。</summary>
    private sealed class StaDispatcherHost : IDisposable
    {
        private readonly Thread thread;

        private StaDispatcherHost(Dispatcher dispatcher, Thread thread)
        {
            Dispatcher = dispatcher;
            this.thread = thread;
        }

        internal Dispatcher Dispatcher { get; }

        /// <summary>启动 Dispatcher 消息循环，并在返回前用同步门确保实例已发布。</summary>
        internal static StaDispatcherHost Start()
        {
            Dispatcher? dispatcher = null;
            using var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
            return new StaDispatcherHost(
                dispatcher ?? throw new InvalidOperationException("未能创建测试 Dispatcher。"),
                thread);
        }

        /// <summary>停止仍在运行的 Dispatcher，并等待专用线程确定性退出。</summary>
        public void Dispose()
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }

            thread.Join();
        }
    }

    private sealed class BlockingDiagnosticLog : IDiagnosticLog
    {
        internal List<DiagnosticEvent> Events { get; } = [];
        internal TaskCompletionSource FirstWriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseWrites { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public async Task WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken)
        {
            Events.Add(diagnosticEvent);
            FirstWriteStarted.TrySetResult();
            await ReleaseWrites.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FailingDiagnosticLog : IDiagnosticLog
    {
        /// <inheritdoc />
        public Task WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("diagnostic failure"));
    }

    private sealed class LateFaultDiagnosticLog : IDiagnosticLog
    {
        private TaskCompletionSource? completion = new();
        internal TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public Task WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => CancellationObserved.TrySetResult());
            return completion?.Task ?? Task.CompletedTask;
        }

        /// <summary>在有界等待完成后让底层日志任务迟到失败，并返回不持有任务的弱引用。</summary>
        internal WeakReference FailLate(Exception exception)
        {
            var source = Interlocked.Exchange(ref completion, null)
                ?? throw new InvalidOperationException("迟到日志任务已经完成。");
            var task = source.Task;
            var reference = new WeakReference(task);
            source.TrySetException(exception);
            return reference;
        }
    }

    /// <summary>为空候选场景提供真实协调器所需的最小发现边界。</summary>
    private sealed class EmptyResidentDiscovery : IResidentAppDiscovery
    {
        public Task<ResidentDiscoveryResult> DiscoverAsync(
            IReadOnlySet<string> ordinaryWindowPaths,
            IReadOnlyList<ResidentApplication> existing,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ResidentDiscoveryResult([], []));
    }

    /// <summary>测试中只验证命令对象图，不触碰文件系统或真实第三方应用。</summary>
    private sealed class AllowingResidentExecutablePolicy : IResidentExecutablePolicy
    {
        public ResidentExecutableValidation Validate(string path) =>
            new(true, Path.GetFullPath(path), ResidentExecutableRejection.None);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        /// <inheritdoc />
        public DateTimeOffset UtcNow { get; } = utcNow;

        /// <inheritdoc />
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(delay, cancellationToken);
    }

    /// <summary>运行一次超时诊断并在 tracker 已完成后触发迟到 fault。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<DrainedDiagnosticProbe> RunTimedOutDiagnosticAndFailLateAsync(Exception lateFailure)
    {
        var calls = new List<string>();
        var log = new LateFaultDiagnosticLog();
        var module = new RecordingModule("workspace", calls);
        var runtime = CompositionRoot.CreateModuleStateComposition(
            module, log, new FixedClock(DateTimeOffset.UnixEpoch));
        using var subscription = runtime.EventBus.Subscribe<ModuleStatusChanged>(
            "failing-observer", (_, _) => Task.FromException(new IOException("observer failure")));

        await runtime.Host.StartAsync(CancellationToken.None);
        await log.CancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
        await runtime.DrainDiagnosticsAsync();

        return new DrainedDiagnosticProbe(runtime, log.FailLate(lateFailure));
    }

    private sealed record DrainedDiagnosticProbe(
        ModuleStateComposition Runtime,
        WeakReference LateTask);
}
