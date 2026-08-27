# 常驻应用最终审查后续修复报告

## 范围与基线

- 工作树：`D:\Projects\Php\client_dev\WebService\Html\DeskButler\.worktrees\resident-apps`
- 分支：`codex/resident-apps`
- 授权基线：`c98e6a6e8085a6549a0de189ba563bf07dc5350c`
- 范围只包含两个 residual Important：同身份启动结果共享、已保存条目图标策略门控；未处理账本中的 parked minors。
- 仓库无 remote；未 push、tag 或新增 remote；未真实安装、卸载、重启、注销或启动第三方程序。

## Important A：同身份启动结果共享

### Phase 1/2 根因与模式

`ResidentLaunchCoordinator.applicationFlights` 原为按 launch identity 保存的 `SemaphoreSlim`。它只串行化两次完整协议：首个调用启动成功并释放 semaphore 后，等待调用会重新执行运行检查；当 Windows 进程枚举尚未观察到新进程时，等待调用会再次进入 `StartAsync`。原测试在第 3 次检查人为返回 `Running`，因此只能证明串行，不能证明 single-flight 结果共享。

第 1 轮中间修复采用短生命周期的按身份共享 Task：重叠消费者共享同一协议终态；协议完成后仍保留到最后一个已加入消费者取得结果，再移除身份项。这样稍后的非重叠手动请求会建立新协议并重新检查，不形成永久缓存。但该版仍在加入 flight 前等待自动 attempted 回调；第 2 轮独立复审证明这会留下反向交错窗口，最终修正见下文。

### RED

旧测试的误通过基线：

```text
dotnet test tests\DeskButler.Desktop.Tests\DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*AutomaticAndManualStartAttemptsAreGloballySpaced"
PASS: 1/1
```

把测试改为确定性屏障，并让首个启动后的所有运行检查持续返回 `NotRunning`：

```text
dotnet test tests\DeskButler.Desktop.Tests\DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*OverlappingAutomaticAndManualCallsShareLaunchOutcomeWhenRunningChecksLag"
FAIL: 0 passed, 1 failed, 0 skipped
Failure: ResidentLaunchCoordinatorTests.OverlappingAutomaticAndManualCallsShareLaunchOutcomeWhenRunningChecksLag
Reason: Assert.Single()；实际有 2 次启动尝试，时间分别为 2026-08-26T00:00:05Z 和 2026-08-26T00:00:06Z。
```

### GREEN

最终测试名补充了短生命周期语义，并验证重叠调用只启动一次、随后同身份非重叠手动请求可再次启动：

```text
dotnet test tests\DeskButler.Desktop.Tests\DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*OverlappingAutomaticAndManualCallsShareTransientLaunchOutcomeWhenRunningChecksLag"
PASS: 1/1

dotnet test tests\DeskButler.Desktop.Tests\DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*ResidentLaunchCoordinatorTests"
PASS: 15/15

dotnet test tests\DeskButler.Desktop.Tests\DeskButler.Desktop.Tests.csproj -c Release --no-restore -- --filter-class "*ResidentLaunchCoordinatorTests"
PASS: 15/15
```

## Important B：已保存条目图标策略门控

### Phase 1/2 根因与模式

`ResidentApplicationViewModel` 构造函数先取得 `ResidentExecutableValidation`，随后却无条件把原始 `Application.LaunchPath` 传给 `IExecutableIconProvider.GetIcon`。因此 UNC、禁止目录、相对或无效路径仍可能在 WPF 线程触发网络、磁盘或 Shell I/O。现有 `ResidentCandidateViewModel.GetValidatedIcon` 已提供正确模式：仅在策略允许且存在 `NormalizedPath` 时调用 provider。

### RED

```text
dotnet test tests\DeskButler.Desktop.Tests\DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*SavedApplication*"
FAIL: 0 passed, 5 failed, 0 skipped
Failures:
- RejectedSavedApplicationPathNeverReachesIconProvider（4 组：UNC/NetworkPath、Windows/ProhibitedDirectory、relative/NotAbsolutePath、invalid-NUL/InvalidPath）：provider 各收到 1 次原始路径。
- AcceptedSavedApplicationIconReceivesNormalizedPathExactlyOnce：期望 C:\Apps\Agent.exe，实际收到 C:\Apps\.\Agent.exe。
```

### GREEN

构造逻辑只在 `IsAllowed` 且 `NormalizedPath` 非空时调用一次 provider，否则使用现有内存 `null` fallback：

```text
dotnet test tests\DeskButler.Desktop.Tests\DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*SavedApplication*"
PASS: 5/5

dotnet test tests\DeskButler.Desktop.Tests\DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*ResidentCandidateViewModelTests"
PASS: 23/23

dotnet test tests\DeskButler.Desktop.Tests\DeskButler.Desktop.Tests.csproj -c Release --no-restore -- --filter-class "*ResidentCandidateViewModelTests"
PASS: 23/23
```

## Important A 第 2 轮：反向交错与 attempted 门禁

### Phase 1/2 根因与最终模式

生产 `JsonResidentLaunchSessionStore.SaveAsync` 在写入临时文件时依次执行异步序列化、`FlushAsync` 和同步 `Flush(flushToDisk: true)`。第 1 轮的 `ProcessApplicationAsync` 在等待该 attempted 保存完成后才注册/计数 flight；因此自动调用可能停在磁盘刷新，手动调用却先建立、完成并移除 flight，自动恢复后再建立第二个 flight。第 1 轮正向测试只把自动调用停在已注册 flight 内的运行检查，无法覆盖该顺序。

最终模式在任何 per-caller 异步 attempted 保存前注册 consumer。协议尚未开始时注册的每个自动 consumer 都增加 `PendingAutomaticPrerequisites`；手动 consumer 不能在 pending 非零时领跑。最后一个自动前置条件成功后才原子设置 `ProtocolStarted` 并建立唯一共享协议。协议已开始或完成后才加入的自动调用不会反向增加 pending，但仍先完成自身 attempted，再等待/消费既有结果。前置保存失败会 fail-closed 完成本轮共享结果；调用方取消只释放自身 consumer，lifetime 取消仍终止共享协议；最后一个 consumer 仅按当前 flight 实例清理未启动或已完成项，避免 identity/ABA 误删。后续非重叠请求仍新建协议。

### RED

测试用第二次 session save 的 `TaskCompletionSource` 精确阻塞自动 attempted 边界；第三次 settings load 的同步 continuation 确保手动调用已推进到同身份协议。所有运行检查始终返回 `NotRunning`，没有真实延迟、时间窗口或伪造 `Running`。

```text
dotnet test tests\DeskButler.Desktop.Tests\DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*SlowAutomaticAttemptPersistenceBlocksManualSideEffectAndSharesLaunchOutcome"
FAIL: 0 passed, 1 failed, 0 skipped
Failure: ResidentLaunchCoordinatorTests.SlowAutomaticAttemptPersistenceBlocksManualSideEffectAndSharesLaunchOutcome
Reason: Assert.Equal；期望（attempted 保存释放前 0 次启动，总计 1 次），实际（释放前 1 次启动，总计 2 次）。
```

### GREEN 与提交前回归

```text
dotnet test tests\DeskButler.Desktop.Tests\DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*SlowAutomaticAttemptPersistenceBlocksManualSideEffectAndSharesLaunchOutcome"
PASS: 1/1

dotnet test tests\DeskButler.Desktop.Tests\DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-method "*OverlappingAutomaticAndManualCallsShareTransientLaunchOutcomeWhenRunningChecksLag"
PASS: 1/1

dotnet test tests\DeskButler.Desktop.Tests\DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*ResidentLaunchCoordinatorTests"
PASS: 19/19

dotnet test tests\DeskButler.Desktop.Tests\DeskButler.Desktop.Tests.csproj -c Release --no-restore -- --filter-class "*ResidentLaunchCoordinatorTests"
PASS: 19/19

dotnet test tests\DeskButler.Desktop.Tests\DeskButler.Desktop.Tests.csproj -c Debug --no-restore
PASS: 274/274

dotnet test tests\DeskButler.Desktop.Tests\DeskButler.Desktop.Tests.csproj -c Release --no-restore
PASS: 272/272

dotnet test DeskButler.slnx -c Debug --no-restore
PASS: 655 total, 652 passed, 3 explicitly gated skips, 0 failed
```

同一 barrier 还覆盖三条状态分支：`FailedAutomaticAttemptPersistencePreventsSharedManualLaunch` 验证 attempted 保存异常 fail-closed 并释放手动等待者；`CanceledManualWaiterDoesNotCancelPendingAutomaticFlight` 验证 caller 取消隔离；`DisposeDuringPendingAutomaticPrerequisiteCancelsSharedFlightWithoutLaunch` 验证 lifetime 取消不会挂起或越过副作用门禁。

Important B 在本轮保持关闭，相关生产代码与测试均未修改；parked minors 未处理。

## 修复提交前回归

```text
dotnet test tests\DeskButler.Desktop.Tests\DeskButler.Desktop.Tests.csproj -c Debug --no-restore
PASS: 270/270

dotnet test tests\DeskButler.Desktop.Tests\DeskButler.Desktop.Tests.csproj -c Release --no-restore
PASS（最终完整运行）: 268/268

dotnet test DeskButler.slnx -c Debug --no-restore
PASS: 651 total, 648 passed, 3 explicitly gated skips, 0 failed

git diff --check
PASS: exit 0

git remote -v
PASS: no output
```

首次 Desktop Release 完整运行曾出现一个不在本次 diff 内的瞬时失败：`CrashSentinelTests.MarkCleanExitRemovesMarkerIdempotently`，统计为 267 passed / 1 failed。按系统化调试核对后，精确用例 1/1、`CrashSentinelTests` 全类 9/9 均通过，相关生产/测试文件自早期提交以来未改；再次完整 Release 为 268/268。未为该 Windows delete-on-close 瞬时可见性修改越界代码，也未以 skip、放宽断言或真实延时掩盖。

三个 Debug skip 仍是既有外部门禁：两个需要显式启用的交互式真实窗口 E2E，以及一个需要显式启用的 30 分钟资源采样。

## 第 1 轮 Fresh Release/安装器证据（已作废）

- 代码修复提交：`5aa27bf fix: share resident launch outcomes safely`
- 唯一一次 fresh 命令：`scripts\verify-release.cmd`
- 结果：exit 0；Release build 0 warning / 0 error；Release tests 649 total、646 passed、3 explicitly gated skips、0 failed；self-contained win-x64 publish PASS；Inno Setup 7.1.0 PASS。
- 路径：`artifacts\installer\DeskButler-Setup-0.1.0.exe`
- 大小：`64,575,904` 字节
- SHA-256：`E754E70172A07AF74C37683C38773E390D62BD8577F1085BA4403E0221D4B2AB`
- `Get-FileHash -Algorithm SHA256` 与验证脚本输出一致。第 2 轮再次修改产品代码后，该 artifact 已作废，不再作为最终发布证据。

## 第 2 轮 Fresh Release/安装器证据

- 第 2 轮代码修复提交：`b20f674 fix: gate shared resident launches on attempts`
- 首次 `scripts\verify-release.cmd`：Release build 0 warning / 0 error；Release tests 653 total、649 passed、3 explicitly gated skips、1 failed。失败为既有 `WindowsResidentProcessRuntimeTests.CheckRunningAsyncFindsOwnedFixtureInCurrentSession` 清理唯一项目 fixture 目录时，`Directory.Delete` 对 `DeskButler.ResidentFixture.exe` 抛出 `UnauthorizedAccessException`。脚本在 tests 阶段 exit 1，未执行 publish/Inno，未生成新的第 2 轮 artifact。
- 系统化诊断：本次 diff 未修改该测试或 Infrastructure resident runtime；相关测试文件最后修改于 `50601ab`。tasklist 无残留 fixture 进程；精确用例 1/1、所属类 9/9、Infrastructure Release 138/138、全 Release 653 total/650 passed/3 skips/0 failed。证据支持既有 fixture 退出后文件释放与目录删除之间的瞬态，而非本次产品变更；未修改越界测试或加入重试/延迟。
- 经条件授权执行第二次且最后一次完整 `scripts\verify-release.cmd`：exit 0；Release build 0 warning / 0 error；Release tests 653 total、650 passed、3 explicitly gated skips、0 failed；self-contained win-x64 publish PASS；Inno Setup 7.1.0 PASS。首次失败未生成 artifact，本轮最终 artifact 仅生成一次。
- 路径：`artifacts\installer\DeskButler-Setup-0.1.0.exe`
- 大小：`64,531,942` 字节
- SHA-256：`893CDAAA1B467B690B27B4DCB9C433C9606C55968045D5D5C8E1D90CA193FD17`
- `Get-FileHash -Algorithm SHA256` 与最终验证脚本输出一致。此后不再重建安装器。
