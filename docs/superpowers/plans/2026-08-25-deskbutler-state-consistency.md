# DeskButler V1 State Consistency Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 关闭恢复卡、设置写入和模块事件中的五个状态一致性缺口，使 DeskButler V1 可以重新进入完整发布审查。

**Architecture:** 恢复卡使用“请求代次 + 单一异步门”原子发布和消费快照；所有设置读改写收口到一份 `SettingsCoordinator`；模块生命周期与状态事件发布拆开，事件观察失败只进入既有诊断日志。三个边界互不共享可变状态，按任务顺序独立提交和审查。

**Tech Stack:** C# 14、.NET 10、WPF、Win32、SQLite、xUnit v3、Microsoft Testing Platform、Inno Setup 7.1.0。

**Spec:** `docs/superpowers/specs/2026-08-25-deskbutler-state-consistency-design.md`

## Global Constraints

- 不新增第三方依赖，不实现 AI、远程控制、动态插件、云同步或新的恢复等级。
- 每个新增或修改的方法必须有中文方法级注释；重要状态和非显然逻辑写中文意图说明。
- 每个行为先写测试并实际观察预期 RED，再写最小实现并观察 GREEN；报告保存命令、失败原因和结果。
- 不通过 skip、串行测试集合、重跑或放宽断言掩盖并发失败。
- 不安装、卸载、重启、注销、关机或删除 `%LOCALAPPDATA%\DeskButler` 等真实用户数据。
- 仓库根必须是 `D:/Projects/Php/client_dev/WebService/Html/DeskButler`，无 remote，不 push/tag/add remote。
- Windows 11、clean account、真实重启、诊断 ZIP UI 和产品签名继续保持外部门禁，不折算为 PASS。

---

### Task 1: 原子恢复卡状态与保护原因呈现

**Files:**
- Modify: `src/DeskButler.Desktop/ViewModels/RecoveryCardViewModel.cs`
- Modify: `src/DeskButler.Desktop/Views/RecoveryCardWindow.xaml`
- Modify: `tests/DeskButler.Desktop.Tests/ViewModels/RecoveryCardViewModelTests.cs`
- Create: `tests/DeskButler.Desktop.Tests/Views/RecoveryCardWindowTests.cs`

**Interfaces:**
- Consumes: `IFailureHistoryStore.LoadAsync(CancellationToken)`、`RestoreSceneCommand`、现有 `AsyncCommand` 与 15 秒显示代次。
- Produces: `RecoveryCardViewModel.ShowAsync(SceneSnapshot)` 保证最后发起的请求胜出；所有恢复/跳过/排除动作消费同一原子发布状态；`ProtectionReason` 在 XAML 可见且为空时折叠。

- [ ] **Step 1: 写最后请求胜出的失败测试**

在 `RecoveryCardViewModelTests` 增加可控失败历史存储：第一次 `LoadAsync` 阻塞，第二次立即返回。先启动旧场景 `ShowAsync`，再启动新场景，允许新场景完成后释放旧调用；断言最终只有新场景项目。

```csharp
[Fact]
public async Task LatestShowRequestWinsWhenOlderHistoryLoadCompletesLast()
{
    var history = new SequencedFailureHistoryStore();
    var vm = new RecoveryCardViewModel(new RecordingCommandBus(), new FakeClock(), 15, history);
    var older = SceneFactory.Create("00000000-0000-0000-0000-000000000051", DateTimeOffset.UtcNow,
        @"C:\Apps\Old.exe");
    var newer = SceneFactory.Create("00000000-0000-0000-0000-000000000052", DateTimeOffset.UtcNow,
        @"C:\Apps\New.exe");

    var oldShow = vm.ShowAsync(older);
    await history.FirstLoadStarted.Task;
    await vm.ShowAsync(newer);
    history.ReleaseFirstLoad.TrySetResult();
    await oldShow;

    Assert.Equal([newer.Items[0].Id], vm.Items.Select(item => item.Item.Id));
}
```

- [ ] **Step 2: 运行 RED 并确认旧调用覆盖新状态**

Run: `dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*LatestShowRequestWinsWhenOlderHistoryLoadCompletesLast"`

Expected: FAIL；最终项目来自 `older`，证明现有 `ShowAsync` 在 await 前写 `scene`、await 后无代次校验。

- [ ] **Step 3: 写恢复动作与 ShowAsync 互斥的失败测试**

用阻塞 `RestoreSceneCommand` 的命令总线启动旧场景恢复，同时调用新场景 `ShowAsync`。释放恢复后断言已发送命令的 `Scene.Id` 和 `SelectedItemIds` 全部属于旧场景，随后界面完整切换到新场景。

```csharp
Assert.Equal(older.Id, sent.Scene.Id);
Assert.All(sent.SelectedItemIds, id => Assert.Contains(id, older.Items.Select(item => item.Id)));
Assert.Equal([newer.Items[0].Id], vm.Items.Select(item => item.Item.Id));
```

- [ ] **Step 4: 运行 RED 并确认状态可交错**

Run: `dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*RestoreUsesOnePublishedSceneWhileNewShowWaits"`

Expected: FAIL；新 `ShowAsync` 可在恢复命令完成前替换 `scene` 或 `Items`。

- [ ] **Step 5: 实现请求代次和单一状态门**

在 ViewModel 增加单调请求代次；`ShowAsync` 在门外加载局部候选、门内仅发布最新请求。`RestoreAsync`、`SkipAsync`、`ExcludePermanentlyAsync` 和发布阶段使用同一 `actionGate`，所有释放置于 `finally`。

```csharp
private long latestShowRequest;

public async Task ShowAsync(SceneSnapshot snapshot)
{
    ArgumentNullException.ThrowIfNull(snapshot);
    var request = Interlocked.Increment(ref latestShowRequest);
    var history = failureHistoryStore is null
        ? FailureHistory.Empty
        : await failureHistoryStore.LoadAsync(CancellationToken.None);
    var candidateItems = snapshot.Items
        .Select(item => new RecoveryItemViewModel(item, history.CountFor(item.Id) >= 3))
        .ToArray();

    await actionGate.WaitAsync();
    try
    {
        if (request != Volatile.Read(ref latestShowRequest)) return;
        scene = snapshot;
        Items.Clear();
        foreach (var item in candidateItems) Items.Add(item);
        ErrorMessage = null;
        IsVisible = true;
        StartDismissTimer();
    }
    finally
    {
        actionGate.Release();
    }
}
```

不得在取得门前修改 `scene`、`Items`、`IsVisible` 或计时器。恢复动作在持门期间复制并发送同一已发布状态，完成后再允许新显示发布。

- [ ] **Step 6: 绑定保护原因并验证可访问性**

把单一 `CheckBox Content` 改为仍可点击的纵向内容，提示绑定 `ProtectionReason`，空值通过 `TargetNullValue`/style trigger 折叠，并设置 `AutomationProperties.HelpText`。

```xml
<CheckBox IsChecked="{Binding IsSelected}" Focusable="True"
          AutomationProperties.Name="{Binding DisplayName}"
          AutomationProperties.HelpText="{Binding ProtectionReason}">
    <StackPanel>
        <TextBlock Text="{Binding DisplayName}" />
        <TextBlock Text="{Binding ProtectionReason}" Foreground="#FFB54708" TextWrapping="Wrap">
            <TextBlock.Style>
                <Style TargetType="TextBlock">
                    <Setter Property="Visibility" Value="Visible" />
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding ProtectionReason}" Value="{x:Null}">
                            <Setter Property="Visibility" Value="Collapsed" />
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </TextBlock.Style>
        </TextBlock>
    </StackPanel>
</CheckBox>
```

- [ ] **Step 7: 验证 GREEN 与回归**

Run:

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*RecoveryCardViewModelTests"
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Release --no-restore -- --filter-class "*RecoveryCardViewModelTests"
```

Expected: 新增并发测试、保护原因绑定测试及全部既有恢复卡测试通过；无 hang、无 skip。

- [ ] **Step 8: 提交 Task 1**

```text
git add src/DeskButler.Desktop/ViewModels/RecoveryCardViewModel.cs src/DeskButler.Desktop/Views/RecoveryCardWindow.xaml tests/DeskButler.Desktop.Tests
git commit -m "fix: make recovery card state atomic"
```

---

### Task 2: 统一设置写入和独立补偿

**Files:**
- Modify: `src/DeskButler.Desktop/Hosting/WorkspaceCommands.cs`
- Modify: `src/DeskButler.Desktop/Hosting/CompositionRoot.cs`
- Modify: `src/DeskButler.Desktop/ViewModels/MainViewModel.cs`（仅在实际状态无法核实时禁用开关）
- Modify: `tests/DeskButler.Desktop.Tests/ViewModels/MainViewModelTests.cs`
- Create: `tests/DeskButler.Desktop.Tests/Hosting/CompositionRootStateTests.cs`

**Interfaces:**
- Consumes: 一份生产 `SettingsCoordinator`、`ISettingsStore`、`IStartupRegistration`、`SetCaptureEnabledCommand`、`PersistExclusionCommand`。
- Produces: `SettingsCoordinator.SetStartupEnabledAsync(IStartupRegistration, bool, CancellationToken)`；所有运行期设置修改共享同一 `mutationGate`，任何补偿失败都不阻止其他补偿。

- [ ] **Step 1: 写并发字段不丢失的失败测试**

在 `MainViewModelTests` 增加可控设置存储，通过 barrier 让旧登录启动处理器加载旧值后停住，同时尝试捕获/排除修改；最后断言三个字段都保留。

```csharp
Assert.True(store.Current.StartupEnabled);
Assert.False(store.Current.CaptureEnabled);
Assert.Contains(@"C:\Apps\Editor.exe", store.Current.ExcludedExecutablePaths);
```

所有三个 handler 必须显式使用同一 `SettingsCoordinator`；测试不得靠延时猜测顺序。

- [ ] **Step 2: 运行 RED 并确认登录启动覆盖并发字段**

Run: `dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*ConcurrentStartupCaptureAndExclusionPreserveAllFields"`

Expected: FAIL；现有 `SetStartupEnabledCommandHandler` 绕过协调器并写回旧完整设置。

- [ ] **Step 3: 写全部补偿都尝试且根因保留的失败测试**

构造保存目标值失败、旧 JSON 恢复也失败，同时注册状态恢复失败的 fake。断言 `SaveAsync` 的补偿调用与 `Disable/Enable` 补偿调用都发生，抛出的 `AggregateException.InnerExceptions[0]` 是原始保存异常，其后包含两个补偿异常。

```csharp
var error = await Assert.ThrowsAsync<AggregateException>(() => handler.HandleAsync(command, token));
Assert.Same(originalSaveFailure, error.InnerExceptions[0]);
Assert.True(store.RollbackAttempted);
Assert.True(registration.RollbackAttempted);
Assert.Equal(3, error.InnerExceptions.Count);
```

- [ ] **Step 4: 运行 RED 并确认首个补偿失败会短路后续补偿**

Run: `dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*StartupRollbackAttemptsSettingsAndRegistryAndPreservesOriginalFailure"`

Expected: FAIL；现有 catch 在 settings 回滚抛错后不会调用注册表回滚，且原始异常被覆盖。

- [ ] **Step 5: 把登录启动事务移入 SettingsCoordinator**

保持通用 `UpdateAsync` 供捕获和排除使用，增加聚焦方法，在同一个 `mutationGate` 内读取最新设置、应用注册、保存设置，并独立补偿。

```csharp
public async Task<bool> SetStartupEnabledAsync(
    IStartupRegistration registration, bool enabled, CancellationToken cancellationToken)
{
    await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        var original = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var originalRegistration = registration.IsEnabled;
        SetRegistration(registration, enabled);
        try
        {
            await store.SaveAsync(original with { StartupEnabled = enabled }, cancellationToken)
                .ConfigureAwait(false);
            return registration.IsEnabled;
        }
        catch (Exception originalFailure)
        {
            var failures = new List<Exception> { originalFailure };
            try { SetRegistration(registration, originalRegistration); }
            catch (Exception rollbackFailure) { failures.Add(rollbackFailure); }
            try { await store.SaveAsync(original, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception rollbackFailure) { failures.Add(rollbackFailure); }
            throw failures.Count == 1 ? originalFailure : new AggregateException("登录启动设置失败且补偿未全部完成。", failures);
        }
    }
    finally
    {
        mutationGate.Release();
    }
}
```

实现时不得用 `throw originalFailure` 破坏原堆栈；单一失败可使用 `ExceptionDispatchInfo.Capture(originalFailure).Throw()`，多个失败按原始异常在首位构造 `AggregateException`。`SetStartupEnabledCommandHandler` 只依赖共享 `SettingsCoordinator` 和注册边界。

- [ ] **Step 6: 组合根只注入同一协调器**

把生产注册改为：

```csharp
commandBus.Register(new SetStartupEnabledCommandHandler(settingsCoordinator, startupRegistration));
```

核对 `SetCaptureEnabledCommandHandler`、`PersistExclusionCommandHandler` 和登录启动 handler 都引用 `CompositionRoot.CreateAsync` 中同一个局部 `settingsCoordinator`，并由组合根释放一次。

- [ ] **Step 7: 验证 UI 失败状态**

补充 MainViewModel 测试：命令失败后重新加载实际设置；若加载成功，开关恢复为实际值并保持可用；若无法加载或 JSON/注册状态无法证明一致，设置 `IsStartupToggleEnabled=false` 并显示错误。不得让异常导致绑定 setter 再次递归发命令。

- [ ] **Step 8: 验证 GREEN 与回归**

Run:

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*MainViewModelTests"
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Release --no-restore -- --filter-class "*MainViewModelTests"
```

Expected: 启用、禁用、注册失败、保存失败、两个补偿失败、并发三字段与 UI 实际状态测试全部通过。

- [ ] **Step 9: 提交 Task 2**

```text
git add src/DeskButler.Desktop/Hosting/WorkspaceCommands.cs src/DeskButler.Desktop/Hosting/CompositionRoot.cs src/DeskButler.Desktop/ViewModels/MainViewModel.cs tests/DeskButler.Desktop.Tests
git commit -m "fix: serialize settings mutations and compensation"
```

---

### Task 3: 隔离模块生命周期与观察事件并完成发布验证

**Files:**
- Modify: `src/DeskButler.Application/Hosting/ModuleHost.cs`
- Modify: `src/DeskButler.Desktop/Hosting/CompositionRoot.cs`
- Modify: `tests/DeskButler.Application.Tests/Hosting/ModuleHostTests.cs`
- Modify: `tests/DeskButler.Desktop.Tests/Hosting/CompositionRootStateTests.cs`
- Modify: `docs/user-guide.md`
- Modify: `docs/compatibility.md`
- Modify: `README.md`

**Interfaces:**
- Consumes: `IEventBus.PublishAsync<TEvent>` 返回 `EventPublishResult`、既有 `IDiagnosticLog`、同一生产事件总线。
- Produces: `ModuleHost(IEnumerable<IModule>, IEventBus, Action<Exception>?)`；生命周期异常为主结果，事件订阅失败只交给诊断接收器且永不污染生命周期。

- [ ] **Step 1: 写 Running 订阅失败不误报模块的失败测试**

订阅者对 Running 抛错并记录收到的状态；诊断 sink 收集异常。断言模块仅启动一次、`StartAsync` 正常返回、没有 Failed 状态、sink 收到订阅异常。

```csharp
await host.StartAsync(CancellationToken.None);
Assert.Equal(["start:ok"], calls);
Assert.DoesNotContain(statuses, status => status.State == ModuleRunState.Failed);
Assert.Single(observedFailures);
```

- [ ] **Step 2: 写启动异常优先于 Failed 事件异常的失败测试**

模块抛出预先保存的 `startFailure`，订阅者对 Failed 也失败。断言 `Assert.ThrowsAsync` 返回同一个 `startFailure` 实例，诊断 sink 收到事件异常。

```csharp
var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(token));
Assert.Same(startFailure, thrown);
Assert.Single(observedFailures);
```

- [ ] **Step 3: 写 Stop 对称行为测试并运行 RED**

覆盖停止成功但 Stopped 事件失败、停止失败且 Failed 事件也失败。运行：

```text
dotnet test tests/DeskButler.Application.Tests/DeskButler.Application.Tests.csproj -c Debug --no-restore -- --filter-class "*ModuleHostTests"
```

Expected: 新测试 FAIL；现有同一 try/catch 会把 Running/Stopped 发布失败转换为 Failed，并可能用第二个 AggregateException 覆盖原始生命周期异常。

- [ ] **Step 4: 分离生命周期操作和尽力事件发布**

新增可选诊断接收器，并实现不抛出的发布边界：

```csharp
private readonly Action<Exception> reportEventFailure;

private async Task PublishBestEffortAsync(ModuleStatusChanged status, CancellationToken cancellationToken)
{
    try
    {
        var result = await eventBus.PublishAsync(status, cancellationToken).ConfigureAwait(false);
        foreach (var failure in result.Failures) ReportBestEffort(failure.Exception);
    }
    catch (Exception exception)
    {
        ReportBestEffort(exception);
    }
}

private void ReportBestEffort(Exception exception)
{
    try { reportEventFailure(exception); }
    catch { /* 最终观察边界不得污染模块真实结果。 */ }
}
```

`StartAsync` 先单独调用 `module.StartAsync`。成功后仅发布 Running；失败时保存异常、尽力发布 Failed，然后使用裸 `throw;` 重新抛出原异常。`StopAsync` 对称实现。

- [ ] **Step 5: 把生产诊断接收器接入既有日志**

`CompositionRoot` 创建 ModuleHost 时传入一个仅记录模块事件观察失败的委托。新增私有 `ReportModuleEventFailureAsync(IDiagnosticLog, IClock, Exception)`：写入 `Category="module-status"`、固定 `Message="模块状态观察者处理失败。"`，Properties 只含 `exceptionType`；使用 2 秒 `CancellationTokenSource` 调用 `IDiagnosticLog.WriteAsync`，并在该最终观察边界吞掉日志失败。传入 ModuleHost 的同步委托只启动该有界任务并保存到既有 `BestEffortAsyncCleanup` 跟踪边界，不记录路径、异常 Message 或用户数据。

- [ ] **Step 6: 验证生产共享 bus 和状态 ViewModel**

扩充既有组合根/模块状态测试，断言生产创建的 ModuleHost 与模块状态 ViewModel 使用同一个 `InProcessEventBus`；Running/Stopped 仍更新 UI，事件失败不产生第二个 Failed 状态。

- [ ] **Step 7: 更新文档和测试数量**

在用户指南写明连续失败保护提示、登录启动失败会恢复/核实实际状态；README 的测试总数必须从最终 Release 输出读取。兼容性文档只移除本轮真正关闭的内部状态缺口，不改变五个外部门禁。

- [ ] **Step 8: 运行聚焦和完整验证**

Run:

```text
dotnet test tests/DeskButler.Application.Tests/DeskButler.Application.Tests.csproj -c Debug --no-restore -- --filter-class "*ModuleHostTests"
dotnet test tests/DeskButler.Application.Tests/DeskButler.Application.Tests.csproj -c Release --no-restore -- --filter-class "*ModuleHostTests"
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore
scripts\verify-release.cmd
git diff --check
git status --short
git remote -v
```

Expected: Debug/Release 聚焦测试无失败或 skip；完整脚本 exit 0，Release build 0 warning/0 error，所有非外部门禁测试通过，publish/Inno/SHA 成功；工作树只含本任务预期文件，无 remote。

- [ ] **Step 9: 扫描敏感数据和构建产物**

Run:

```text
git ls-files | rg "(^|/)(artifacts|bin|obj|deskbutler\.db|settings\.json|diagnostics)(/|$)"
git grep -n -I -E "sk-[A-Za-z0-9_-]{20,}|BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY|%LOCALAPPDATA%\\DeskButler\\deskbutler\.db"
```

Expected: 不跟踪 artifacts/bin/obj/真实数据库/设置/诊断文件；无密钥命中。文档中的 `%LOCALAPPDATA%` 说明可人工判定为预期，不得自动删除任何文件。

- [ ] **Step 10: 提交 Task 3**

```text
git add src/DeskButler.Application/Hosting/ModuleHost.cs src/DeskButler.Desktop/Hosting/CompositionRoot.cs tests/DeskButler.Application.Tests tests/DeskButler.Desktop.Tests README.md docs/user-guide.md docs/compatibility.md
git commit -m "fix: isolate module lifecycle observations"
```
