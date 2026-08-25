# DeskButler 清理完成发布 follow-up 报告

## 范围与起点

- 仓库：`D:/Projects/Php/client_dev/WebService/Html/DeskButler`
- 起始 HEAD：`02985689b809a84535f19e0dc7acf24717a52f72`
- 本轮只修复 `BestEffortAsyncCleanup` 的完成发布与 stale `inFlight` 竞态。
- 未派发子代理；未安装、卸载、重启、push、tag、添加 remote，未访问真实 `%LOCALAPPDATA%/DeskButler` 数据。

## 测试捕获的 production mutation

1. `ExternallyCompletedFailedPassCannotBeReturnedForRetry` 捕获 `Enter` 只按 `inFlight != null` 判断 live、把已经对外失败的旧 Task 再次返回的 mutation。
2. `CallersAtExternallyCompletedBoundaryShareOneRetryPass` 捕获两个重试调用方都加入已完成旧 Task、没有恰好一个调用方建立新 pass 的 mutation。
3. `ExternallyCompletedSuccessfulPassReportsComplete` 捕获 `IsComplete` 把已经对外成功的旧 Task 仍视为 live，导致所有步骤成功后错误返回 false 的 mutation。
4. `SynchronousReentrantRunSharesPublishedPass` 固化既有顺序：若把资源 delegate 移到 live pass 登记之前，回调同步重入会建立第二个 pass，而不是观察同一个 Task。

## RED

生产代码仍为起始 HEAD 时先加入三个确定性边界测试，执行：

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*ExternallyCompleted*"
```

结果：exit 1，构建在三个测试位置分别报 `CS0246`：找不到真实内部生产类型 `CleanupPassCoordinator`。这证明所需的原子协调边界尚不存在；随后才修改生产代码。

为证明断言捕获旧竞态而不仅是新类型存在，在 GREEN 后暂时把 `DiscardCompletedPass` mutation 为保留已完成 `inFlight`，用同一命令重放：

```text
测试运行摘要: 失败!
  总计: 3
  失败: 3
  成功: 0
  已跳过: 0
```

精确失败分别为：失败 pass 的重试 `StartsPass` 实际为 false；两个边界调用方中没有任何一个创建新 pass；成功 pass 后 `IsComplete` 实际为 false。mutation 随即恢复。

## 最小修复

- 提取内部 `CleanupPassCoordinator`，让步骤成功位、live pass 与完成发布共用一个协调锁。
- `Enter` 和 `IsComplete` 把已完成 Task 归一化为非 live 状态，因此 stale 引用不会阻止重试或污染完成判断。
- `Publish` 在同一锁内先清除对应 live pass，再完成使用 `RunContinuationsAsynchronously` 的 TCS；外部调用方取得协调锁时只能看到发布前或发布后的完整状态。
- `BestEffortAsyncCleanup` 仅在新 pass 创建方运行步骤；资源 delegate 和 `await` 始终位于协调锁外。
- 步骤仍按原顺序尽力执行并聚合异常；成功位只写一次，后续 pass 只取得未完成步骤快照。

## GREEN

新增竞态测试：

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*ExternallyCompleted*"
```

结果：3 总计，3 成功，0 失败，0 跳过。

完整 cleanup 测试：

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*BestEffortAsyncCleanupTests"
```

结果：8 总计，8 成功，0 失败，0 跳过。

启动回滚、Dispatcher 与 Dispose overlap 生命周期测试：

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*CompositionRootStateTests"
```

结果：29 总计，29 成功，0 失败，0 跳过。

## 完整验证

- `dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore`：151 总计，151 成功，0 失败，0 跳过。
- `dotnet test DeskButler.slnx -c Release --no-restore`：438 总计，435 成功，0 失败，3 个既有显式外部门禁跳过。
- `dotnet build DeskButler.slnx -c Release --no-restore`：exit 0，0 警告，0 错误。
- `dotnet format DeskButler.slnx whitespace --verify-no-changes --no-restore --include src/DeskButler.Desktop/Hosting/LifecycleCleanup.cs tests/DeskButler.Desktop.Tests/Hosting/BestEffortAsyncCleanupTests.cs`：exit 0，无格式差异。
- `dotnet format DeskButler.slnx --verify-no-changes --no-restore --include src/DeskButler.Desktop/Hosting/LifecycleCleanup.cs tests/DeskButler.Desktop.Tests/Hosting/BestEffortAsyncCleanupTests.cs`：exit 0，无格式或分析器差异。

提交前 fresh 全 Release 首次复验曾在未修改的 `HangingHistoryAndDiagnosticTasksCannotDelayRestoreResultIndefinitely` 出现一次失败：全解决方案并行负载下，测试要求的后台 `Task.Run` 未在 40ms persistence timeout 内开始，`history.RecordCalled` 为 false。该测试随后隔离 Release 运行 1/1 通过，再次执行完整 Release 命令得到上述 438/435/0/3 最终结果；未修改或放宽该无关测试。

## 自审

- 原子性：生产路径只在协调锁内清除 live pass 并完成 TCS；完成 Task 不会与旧 live 登记一起被调用方观察。
- 锁与死锁：资源清理 delegate、Dispatcher 工作与所有 `await` 都在锁外；TCS continuation 强制异步；同步重入测试验证回调只取得共享 Task。
- 重试与 exactly-once：失败步骤只在失败 pass 对外完成后的新 pass 中重试；成功步骤永久跳过；两个边界调用方只创建一个重试 pass。
- 错误语义：同一 live pass 仍共享同一个 Task 和异常实例；逆序所有权、best-effort 聚合以及启动失败原异常传播路径未改变。
- Dispatcher：本轮没有改变 `DispatcherCleanup` 或调用上下文；完整 `CompositionRootStateTests` 覆盖所属线程清理、关闭竞态与 overlap。
- 已知关注项：全解决方案仍有 3 个既有显式外部门禁 skip；另有上述未修改 40ms 调度断言的一次并行负载瞬态，隔离与最终完整复验均通过。本轮没有执行外部场景。
