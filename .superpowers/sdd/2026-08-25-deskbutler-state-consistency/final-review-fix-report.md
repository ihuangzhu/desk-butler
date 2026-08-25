# DeskButler 状态一致性最终审查修复报告

## 范围与起点

- 仓库：`D:/Projects/Php/client_dev/WebService/Html/DeskButler`
- 起始 HEAD：`7c8e3c1a1a62042c48acce171f55064c65a79eb4`
- 本轮只修复最终审查简报中的三个 finding；未安装、卸载、重启、push、tag、添加 remote 或访问真实 `%LOCALAPPDATA%/DeskButler` 数据。
- 未派发子代理。

## Finding 1：过期恢复项排除

### 测试捕获的 production mutation

1. 删除排除动作在取得 `actionGate` 后对 item 发布代次的校验，会让新卡片发布后仍按旧 item 路径持久化。
2. 删除对同一 item 实例仍属于当前 `Items` 的校验，会允许同代次但非当前集合成员的对象改变状态并发出命令。

### RED

命令：

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*StaleExclusionQueuedBehindNewShowDoesNotPersistOldItem"
```

结果：1 总计，0 成功，1 失败。期望仅持久化 `C:\Apps\GateHolder.exe`，实际还持久化了 `C:\Apps\Stale.exe`。

在暂时仅保留发布代次校验、尚未加入成员校验时运行：

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*ExclusionRequiresSamePublishedItemInstance"
```

结果：1 总计，0 成功，1 失败。伪造同代次 item 被错误取消选择，证明成员校验独立必要。

### 最小修复

- 每次 `ShowAsync` 候选 item 携带内部不可变发布代次，发布时与 `scene`、`Items` 一起更新当前代次。
- `ExcludePermanentlyAsync` 在等待门前捕获 item 的发布代次；取得门后同时核对当前发布代次和同一对象仍在 `Items`。
- 不匹配时在取消选择、发送命令或写入错误状态之前返回。
- 保持原公开 `RecoveryItemViewModel(SceneItem, bool)` 构造签名，发布代次构造入口仅为 internal。

### GREEN

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*StaleExclusionQueuedBehindNewShowDoesNotPersistOldItem"
```

结果：1/1 通过。

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*ExclusionRequiresSamePublishedItemInstance"
```

结果：1/1 通过。

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*RecoveryCardViewModelTests"
```

最终结果：22 总计，22 成功，0 失败，0 跳过。

## Finding 2：并发清理 single-flight

### 测试捕获的 production mutation

1. 不共享 in-flight pass，会让两个并发 `RunAsync` 各自执行同一个尚未完成步骤。
2. 失败 pass 尚未完全结束时允许第二个调用独立枚举，会让第二个调用看不到相同失败并提前把步骤标成完成。
3. 启动失败回滚与释放边界重叠时若绕过 single-flight，同一清理步骤会执行两次。

### RED

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*BestEffortAsyncCleanupTests"
```

结果：4 总计，2 成功，2 失败。成功步骤实际调用 2 次；失败场景的第二个并发调用未抛出共享的 `AggregateException`。

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*StartupFailureCleanupOverlappingDisposeRunsEachStepOnce"
```

结果：1 总计，0 成功，1 失败；启动回滚与外部释放重叠时清理步骤实际调用 2 次。

### 最小修复

- `BestEffortAsyncCleanup` 在同步边界内先发布共享 in-flight `Task`，再在锁外执行资源代码。
- 所有并发调用 await 同一 pass，并观察同一成功或失败结果。
- 每个成功位与 `IsComplete` 在同一锁下读写；`IsComplete` 在 pass 仍 in-flight 时保持 false。
- pass 继续按既定逆序尝试全部未完成步骤并聚合错误；成功步骤不重跑，失败步骤只在整个失败 pass 已完成并移除 in-flight 后由后续调用重试。
- 未在 `CompositionStartupCoordinator` 或 `CompositionRoot` 添加平行锁，所有权不变量集中在 cleanup owner。

### GREEN

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*BestEffortAsyncCleanupTests"
```

结果：4 总计，4 成功，0 失败，0 跳过。

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*StartupFailureCleanupOverlappingDisposeRunsEachStepOnce"
```

结果：1/1 通过。

## Finding 3：补偿后置条件

### 测试捕获的 production mutation

1. 删除注册回滚后的注册后置条件检查，会把 silent no-op 注册回滚视为成功。
2. 删除设置回滚后的无取消重载与原值核对，会把 silent no-op 设置回滚视为成功。
3. 任一补偿 mismatch 短路另一补偿，会丢失第二条独立诊断；测试要求原始异常始终位于聚合首位并验证 gate 释放。

### RED

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*Silent*"
```

结果：3 总计，0 成功，3 失败。三个场景分别直接重抛原 `InvalidOperationException` 或 `IOException`，没有把静默注册、静默设置及双静默 mismatch 纳入聚合。

### 最小修复

- 注册回滚调用后复用 `EnsureRegistrationState` 核对 `originalRegistration`。
- 设置回滚保存后使用 `CancellationToken.None` 重新 `LoadAsync`，核对 `StartupEnabled == originalSettings.StartupEnabled`。
- 注册与设置补偿继续位于两个独立 try/catch 中；验证失败与调用异常同等作为独立补偿诊断。
- 原始失败保持 `failures[0]`；全部补偿成功时仍通过 `ExceptionDispatchInfo` 保留同一异常实例与原堆栈。

### GREEN

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*Silent*"
```

结果：3 总计，3 成功，0 失败，0 跳过。

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*CompositionRootStateTests"
```

最终结果：29 总计，29 成功，0 失败，0 跳过。

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*Startup*"
```

结果：24 总计，24 成功，0 失败，0 跳过。

## 完整验证

### Desktop 测试

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore
```

首轮结果：147 总计，146 成功，1 失败。唯一失败为未修改路径中的 `CrashSentinelTests.MarkCleanExitRemovesMarkerIdempotently`，`MarkCleanExit` 返回后 Windows 路径仍短暂存在。该测试使用唯一 GUID 临时目录，本轮未修改 `CrashSentinel` 或其测试。

诊断隔离命令：

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*MarkCleanExitRemovesMarkerIdempotently"
```

结果：1/1 通过。随后重新运行完整 Desktop 命令，最终结果为 147 总计，147 成功，0 失败，0 跳过。首轮瞬态未通过未被计入本轮修复范围，也未修改或放宽测试。

### 全解决方案 Release 测试

```text
dotnet test DeskButler.slnx -c Release --no-restore
```

结果：434 总计，431 成功，0 失败，3 个既有显式外部门禁跳过：交互场景恢复、真实 150% DPI 窗口、30 分钟资源稳定性。

### Release 构建

```text
dotnet build DeskButler.slnx -c Release --no-restore
```

结果：exit 0，0 个警告，0 个错误。

### 变更文件格式

```text
dotnet format DeskButler.slnx whitespace --verify-no-changes --no-restore --include src/DeskButler.Desktop/Hosting/LifecycleCleanup.cs src/DeskButler.Desktop/Hosting/WorkspaceCommands.cs src/DeskButler.Desktop/ViewModels/RecoveryCardViewModel.cs tests/DeskButler.Desktop.Tests/Hosting/BestEffortAsyncCleanupTests.cs tests/DeskButler.Desktop.Tests/Hosting/CompositionRootStateTests.cs tests/DeskButler.Desktop.Tests/ViewModels/RecoveryCardViewModelTests.cs
dotnet format DeskButler.slnx --verify-no-changes --no-restore --include src/DeskButler.Desktop/Hosting/LifecycleCleanup.cs src/DeskButler.Desktop/Hosting/WorkspaceCommands.cs src/DeskButler.Desktop/ViewModels/RecoveryCardViewModel.cs tests/DeskButler.Desktop.Tests/Hosting/BestEffortAsyncCleanupTests.cs tests/DeskButler.Desktop.Tests/Hosting/CompositionRootStateTests.cs tests/DeskButler.Desktop.Tests/ViewModels/RecoveryCardViewModelTests.cs
```

结果：两条命令均 exit 0，无格式差异。

## 自审结论

- 锁顺序：恢复卡仍只允许 `actionGate → lifecycleSync`；cleanup 的 `syncRoot` 从不跨资源委托或 await 持有；设置 gate 的释放仍在 finally。
- 死锁：cleanup 先发布 in-flight 再在锁外启动 pass；并发调用只 await 共享 Task，不引入与 WPF Dispatcher 的同步互等。
- 过期状态：排除动作在等待前捕获不可变发布代次，取得门后同时校验当前代次与对象成员身份；no-op 路径不改选择、命令或错误。
- 异常顺序：原始启动设置失败保持聚合首位；注册补偿诊断先于设置补偿诊断；全部补偿成功时原异常实例和堆栈保持。
- 取消令牌：补偿注册、设置保存和设置重载不受原请求取消影响；新增测试 barrier 使用测试框架取消令牌防止挂起。
- 重试语义：成功 cleanup 步骤只执行一次；失败步骤在共享 pass 完全结束后可重试；所有步骤仍尽力执行并聚合失败。
- 已知外部项：全解决方案保留 3 个既有显式门禁 skip；Desktop 首轮出现一次未修改 CrashSentinel delete-on-close 瞬态，隔离与随后完整测试均通过。
