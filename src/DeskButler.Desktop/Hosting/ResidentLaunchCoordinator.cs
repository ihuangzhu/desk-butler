using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.Collections.Concurrent;
using DeskButler.Core.Diagnostics;
using DeskButler.Core.ResidentApps;
using DeskButler.Core.Settings;
using DeskButler.Core.Time;

namespace DeskButler.Desktop.Hosting;

/// <summary>在单次登录会话内按固定计划启动用户确认的常驻应用。</summary>
internal sealed class ResidentLaunchCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ItemDelay = TimeSpan.FromSeconds(1);

    private readonly object syncRoot = new();
    private readonly ISettingsStore settingsStore;
    private readonly IResidentLaunchSessionStore sessionStore;
    private readonly ILogonSessionIdentityProvider logonSessionIdentityProvider;
    private readonly IResidentProcessRuntime runtime;
    private readonly IResidentExecutablePolicy executablePolicy;
    private readonly IClock clock;
    private readonly IDiagnosticLog diagnosticLog;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim startAttemptGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> applicationFlights = new(StringComparer.Ordinal);
    private Task? automaticCompletion;
    private Task? manualCompletion;
    private Task? disposeCompletion;
    private bool disposeStarted;
    private DateTimeOffset? lastStartAttempt;

    /// <summary>创建共享设置、会话、平台策略和诊断边界的启动协调器。</summary>
    internal ResidentLaunchCoordinator(
        ISettingsStore settingsStore,
        IResidentLaunchSessionStore sessionStore,
        ILogonSessionIdentityProvider logonSessionIdentityProvider,
        IResidentProcessRuntime runtime,
        IResidentExecutablePolicy executablePolicy,
        IClock clock,
        IDiagnosticLog diagnosticLog)
    {
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        this.sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        this.logonSessionIdentityProvider = logonSessionIdentityProvider ??
            throw new ArgumentNullException(nameof(logonSessionIdentityProvider));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.executablePolicy = executablePolicy ?? throw new ArgumentNullException(nameof(executablePolicy));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.diagnosticLog = diagnosticLog ?? throw new ArgumentNullException(nameof(diagnosticLog));
    }

    /// <summary>获取已建立的自动批次；尚未启动时返回已完成任务。</summary>
    internal Task Completion
    {
        get
        {
            lock (syncRoot)
            {
                return automaticCompletion ?? Task.CompletedTask;
            }
        }
    }

    /// <summary>供组合测试验证 Debug smoke 或 fake 平台 runtime 的真实引用。</summary>
    internal IResidentProcessRuntime ProcessRuntime => runtime;

    /// <summary>只在首次调用时建立后台批次并立即返回。</summary>
    internal void Start()
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(disposeStarted, this);
            // 锁内只发布唯一任务引用；实际 I/O 在线程池取得执行权后才开始。
            automaticCompletion ??= Task.Run(
                () => RunAutomaticBatchAsync(lifetime.Token),
                CancellationToken.None);
        }
    }

    /// <summary>以独立 single-flight 立即启动当前 enabled 条目，且完全不读取或写入登录会话。</summary>
    internal Task LaunchEnabledNowAsync(CancellationToken cancellationToken)
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(disposeStarted, this);
            if (manualCompletion is null || manualCompletion.IsCompleted)
            {
                // 锁内只登记本轮共享任务，设置读取和平台调用均在线程池边界之后发生。
                manualCompletion = Task.Run(
                    () => RunManualBatchAsync(cancellationToken),
                    CancellationToken.None);
            }

            return manualCompletion;
        }
    }

    /// <summary>取消尚未开始的工作，并等待已建立批次离开协调器边界。</summary>
    public ValueTask DisposeAsync()
    {
        lock (syncRoot)
        {
            if (disposeCompletion is null)
            {
                disposeStarted = true;
                disposeCompletion = Task.Run(DisposeCoreAsync, CancellationToken.None);
            }

            return new ValueTask(disposeCompletion);
        }
    }

    /// <summary>读取或建立当前 LUID 的固定计划，并逐项用最新设置解析启动入口。</summary>
    private async Task RunAutomaticBatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var currentLuid = logonSessionIdentityProvider.GetCurrent();
            ResidentLaunchSession? session;
            try
            {
                session = await sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException loadFailure)
            {
                try
                {
                    var recovery = await sessionStore.RecoverCorruptAsync(
                        currentLuid,
                        cancellationToken).ConfigureAwait(false);
                    await ReportBatchResultAsync(
                        recovery == ResidentLaunchRecoveryResult.RecoveredWithEmptyPlan
                            ? "corrupt-recovered-empty"
                            : "corrupt-preservation-failed",
                        loadFailure,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception recoveryFailure) when (recoveryFailure is not OperationCanceledException)
                {
                    await ReportBatchResultAsync(
                        "corrupt-recovery-failed",
                        recoveryFailure,
                        CancellationToken.None).ConfigureAwait(false);
                }

                // 恢复只决定证据处置；本次进程无论结果如何都 fail-closed，不再启动。
                return;
            }
            if (session is not null &&
                StringComparer.Ordinal.Equals(session.LogonSessionId, currentLuid) &&
                session.Completed)
            {
                return;
            }

            await clock.DelayAsync(InitialDelay, cancellationToken).ConfigureAwait(false);
            var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!settings.ResidentApplicationsEnabled)
            {
                await sessionStore.SaveAsync(
                    new ResidentLaunchSession(1, currentLuid, true, []),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (session is null || !StringComparer.Ordinal.Equals(session.LogonSessionId, currentLuid))
            {
                var plan = ResidentApplicationNormalizer.Normalize(settings.ResidentApplications)
                    .Applications
                    .Where(application => application.Enabled)
                    .Select(application => new ResidentLaunchPlanItem(
                        CreateLaunchIdentity(application.LaunchPath),
                        false))
                    .ToArray();
                session = new ResidentLaunchSession(1, currentLuid, plan.Length == 0, plan);
                await sessionStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);
            }

            if (session.Completed)
            {
                return;
            }

            var handledAny = false;
            foreach (var planItem in session.Plan.Where(item => !item.Attempted))
            {
                if (handledAny)
                {
                    await clock.DelayAsync(ItemDelay, cancellationToken).ConfigureAwait(false);
                }

                handledAny = true;
                ResidentApplication? application;
                try
                {
                    settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                    application = ResidentApplicationNormalizer.Normalize(settings.ResidentApplications)
                        .Applications
                        .FirstOrDefault(candidate =>
                            StringComparer.Ordinal.Equals(
                                CreateLaunchIdentity(candidate.LaunchPath),
                                planItem.LaunchIdentity));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception settingsFailure)
                {
                    session = await MarkAttemptedAsync(session, planItem, cancellationToken).ConfigureAwait(false);
                    await ReportBatchResultAsync(
                        "settings-load-failed",
                        settingsFailure,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (application is null || !application.Enabled)
                {
                    session = await MarkAttemptedAsync(session, planItem, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var currentSession = session;
                await ProcessApplicationAsync(
                    application,
                    async () =>
                    {
                        currentSession = await MarkAttemptedAsync(
                            currentSession,
                            planItem,
                            cancellationToken).ConfigureAwait(false);
                    },
                    cancellationToken).ConfigureAwait(false);
                session = currentSession;
            }

            await sessionStore.SaveAsync(session with { Completed = true }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 生命周期取消只停止尚未越过边界的后续工作，不传播为未观察后台异常。
        }
        catch (Exception exception)
        {
            await ReportBatchResultAsync("batch-failed", exception, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>读取调用时最新设置并复用单项协议；总开关仅约束登录自动批次，不阻断显式手动操作。</summary>
    private async Task RunManualBatchAsync(CancellationToken callerToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, callerToken);
        var cancellationToken = linked.Token;
        try
        {
            var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var applications = ResidentApplicationNormalizer.Normalize(settings.ResidentApplications)
                .Applications
                .Where(application => application.Enabled)
                .ToArray();
            for (var index = 0; index < applications.Length; index++)
            {
                if (index > 0)
                {
                    await clock.DelayAsync(ItemDelay, cancellationToken).ConfigureAwait(false);
                }

                await ProcessApplicationAsync(
                    applications[index],
                    static () => Task.CompletedTask,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方或宿主退出只终止本轮尚未开始的手动项目。
        }
        catch (Exception exception)
        {
            await ReportBatchResultAsync(
                "manual-batch-failed",
                exception,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>先原子持久化 attempted，再允许任何后续外部启动边界执行。</summary>
    private async Task<ResidentLaunchSession> MarkAttemptedAsync(
        ResidentLaunchSession session,
        ResidentLaunchPlanItem target,
        CancellationToken cancellationToken)
    {
        var updated = session with
        {
            Plan = session.Plan
                .Select(item => ReferenceEquals(item, target) ||
                                StringComparer.Ordinal.Equals(item.LaunchIdentity, target.LaunchIdentity)
                    ? item with { Attempted = true }
                    : item)
                .ToArray()
        };
        await sessionStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <summary>共享单项的运行检查、策略重验和启动协议；自动调用方注入先登记边界。</summary>
    private async Task ProcessApplicationAsync(
        ResidentApplication application,
        Func<Task> markAttemptedAsync,
        CancellationToken cancellationToken)
    {
        var flight = applicationFlights.GetOrAdd(
            CreateLaunchIdentity(application.LaunchPath),
            static _ => new SemaphoreSlim(1, 1));
        await flight.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ProcessApplicationWithinFlightAsync(application, markAttemptedAsync, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            flight.Release();
        }
    }

    /// <summary>在同一应用身份的 single-flight 内完成检查、验证、登记、复查与启动。</summary>
    private async Task ProcessApplicationWithinFlightAsync(
        ResidentApplication application,
        Func<Task> markAttemptedAsync,
        CancellationToken cancellationToken)
    {
        var knownPaths = new HashSet<string>(
            application.KnownProcessPaths,
            StringComparer.OrdinalIgnoreCase)
        {
            application.LaunchPath
        };
        ResidentRunningCheck running;
        try
        {
            running = await runtime.CheckRunningAsync(knownPaths, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception checkFailure)
        {
            await markAttemptedAsync().ConfigureAwait(false);
            await ReportApplicationResultAsync(
                application,
                "running-check-failed",
                checkFailure,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (running.State != ResidentRunningState.NotRunning)
        {
            await markAttemptedAsync().ConfigureAwait(false);
            await ReportApplicationResultAsync(
                application,
                running.State == ResidentRunningState.Running ? "already-running" : "running-unknown",
                exception: null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        ResidentExecutableValidation validation;
        try
        {
            validation = executablePolicy.Validate(application.LaunchPath);
        }
        catch (Exception policyFailure)
        {
            await markAttemptedAsync().ConfigureAwait(false);
            await ReportApplicationResultAsync(
                application,
                "policy-failed",
                policyFailure,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!validation.IsAllowed || string.IsNullOrWhiteSpace(validation.NormalizedPath))
        {
            await markAttemptedAsync().ConfigureAwait(false);
            await ReportApplicationResultAsync(
                application,
                $"policy-rejected-{validation.Reason}",
                exception: null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // 启动意图先持久化；即使随后的外部边界崩溃，同一 LUID 也不会重复处理。
        await markAttemptedAsync().ConfigureAwait(false);
        try
        {
            var finalRunning = await runtime.CheckRunningAsync(knownPaths, cancellationToken).ConfigureAwait(false);
            if (finalRunning.State != ResidentRunningState.NotRunning)
            {
                await ReportApplicationResultAsync(
                    application,
                    finalRunning.State == ResidentRunningState.Running ? "already-running" : "running-unknown",
                    exception: null,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await StartWithPacingAsync(validation.NormalizedPath, cancellationToken).ConfigureAwait(false);
            await ReportApplicationResultAsync(
                application,
                "started",
                exception: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception startFailure)
        {
            await ReportApplicationResultAsync(
                application,
                "start-failed",
                startFailure,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>让自动与手动批次共享实际启动边界节流；不持锁等待第三方启动任务完成。</summary>
    private async Task StartWithPacingAsync(string executablePath, CancellationToken cancellationToken)
    {
        Task startTask;
        await startAttemptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (lastStartAttempt.HasValue)
            {
                var remaining = ItemDelay - (clock.UtcNow - lastStartAttempt.Value);
                if (remaining > TimeSpan.Zero)
                {
                    await clock.DelayAsync(remaining, cancellationToken).ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            lastStartAttempt = clock.UtcNow;
            // 只在 gate 内发起调用并取得 Task；异步完成、异常和第三方生命周期均在 gate 外观察。
            startTask = runtime.StartAsync(executablePath, cancellationToken);
        }
        finally
        {
            startAttemptGate.Release();
        }

        await startTask.ConfigureAwait(false);
    }

    /// <summary>以 Windows 大小写语义把启动路径转换为不含原文负载的稳定身份。</summary>
    private static string CreateLaunchIdentity(string launchPath)
    {
        var normalized = Path.GetFullPath(launchPath).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    /// <summary>诊断失败不得反向污染自动批次，并且不写异常消息或会话身份。</summary>
    private async Task ReportBatchResultAsync(
        string result,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await diagnosticLog.WriteAsync(
                new DiagnosticEvent(
                    clock.UtcNow,
                    DiagnosticLevel.Warning,
                    "resident-launch",
                    "常驻应用启动批次发生可恢复故障。",
                    new Dictionary<string, object?>
                    {
                        ["result"] = result,
                        ["exceptionType"] = exception.GetType().FullName
                    }),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 诊断是最终旁路，任何同步或异步失败都在此观察并隔离。
        }
    }

    /// <summary>仅写白名单属性；路径沿用诊断导出的用户目录脱敏表示，不含命令行或异常消息。</summary>
    private async Task ReportApplicationResultAsync(
        ResidentApplication application,
        string result,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        try
        {
            var properties = new Dictionary<string, object?>
            {
                ["displayName"] = application.DisplayName,
                ["path"] = RedactPath(application.LaunchPath),
                ["result"] = result
            };
            if (exception is not null)
            {
                properties["exceptionType"] = exception.GetType().FullName;
            }

            await diagnosticLog.WriteAsync(
                new DiagnosticEvent(
                    clock.UtcNow,
                    exception is null ? DiagnosticLevel.Information : DiagnosticLevel.Warning,
                    "resident-launch",
                    "常驻应用启动项目已完成终端处理。",
                    properties),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 诊断写入是最终观察边界；失败不得回滚 attempted 或中止后续项目。
        }
    }

    /// <summary>复用诊断导出约定，把当前用户目录替换为稳定占位符。</summary>
    private static string RedactPath(string path)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile)
            ? "[已脱敏]"
            : path.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>恰好一次取消并释放协调器自身资源。</summary>
    private async Task DisposeCoreAsync()
    {
        lifetime.Cancel();
        Task? automatic;
        Task? manual;
        lock (syncRoot)
        {
            automatic = automaticCompletion;
            manual = manualCompletion;
        }

        var pending = new[] { automatic, manual }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (pending.Length > 0)
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }

        startAttemptGate.Dispose();
        foreach (var flight in applicationFlights.Values)
        {
            flight.Dispose();
        }
        lifetime.Dispose();
    }
}
