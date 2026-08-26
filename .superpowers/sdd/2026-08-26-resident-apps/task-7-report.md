# Task 7：常驻列表设置命令与安全编辑报告

## 状态

已完成。常驻列表设置命令全部通过共享 `SettingsCoordinator.UpdateAsync` 提交，并由生产 `InProcessCommandBus` 注册。

## TDD（RED / GREEN）

- RED 1：先创建 `ResidentAppCommandTests`，运行聚焦测试得到预期编译失败：设置命令、处理器、`ResidentSettingsError` 与类型化结果尚不存在。
- GREEN 1：加入命令、结果枚举、原子 mutation 基类和处理器后，发现移动测试仍失败；原因是交换数组位置后遗留旧 `LaunchOrder`，被正规化器按旧顺序重新排序。
- GREEN 2：移动后立即按新可见顺序重编号，再交给正规化器；聚焦测试通过。
- RED 2：根据裁定新增“已启用条目仍拒绝跨已启用识别路径冲突”测试，预期失败（实际返回 `None`）。
- GREEN 3：将启用冲突检查移至已是目标状态的 no-op 判断之前；并补充“已启用条目仍会重新执行 executable policy”测试。最终 10 个 ResidentAppCommandTests 通过。

## 并发证据

- `ConcurrentSettingsFieldsAndResidentMutationPreserveEveryChange` 使用首个读取屏障并发执行捕获开关、登录启动、排除项及添加常驻条目，断言四项字段最终均保留。
- `ConcurrentCandidateConfirmationAndMovePreserveEntriesAndContinuousOrder` 在候选确认的设置事务读取处设置屏障，同时排队列表移动；断言候选未丢失，顺序为 `Second → First → Candidate`，编号为连续 `0,1,2`。
- 候选确认、所有新增列表命令均经同一个 `SettingsCoordinator`；`ResidentAppCommandHandlerSet` 在组合时拒绝不同协调器实例，组合根状态测试验证确认 handler 与每个列表 handler 的引用相同。

## 验证命令与结果

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*ResidentAppCommandTests"
# PASS: 10 / 10

dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*MainViewModelTests"
# PASS: 16 / 16

dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*CompositionRootStateTests"
# PASS: 31 / 31

dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Release --no-restore -- --filter-class "*ResidentAppCommandTests"
# PASS: 10 / 10

dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore
# PASS: 183 / 183
```

## 变更文件

- `src/DeskButler.Desktop/Hosting/ResidentAppCommands.cs`
- `src/DeskButler.Desktop/Hosting/ResidentCandidateCoordinator.cs`
- `src/DeskButler.Desktop/Hosting/CompositionRoot.cs`
- `tests/DeskButler.Desktop.Tests/Hosting/ResidentAppCommandTests.cs`
- `tests/DeskButler.Desktop.Tests/Hosting/CompositionRootStateTests.cs`

## 自审与担忧

- 确认没有新增依赖、没有访问真实第三方应用，也没有安装、重启、注销或关机动作。
- 每个列表写 handler 的唯一写路径是 `SettingsCoordinator.UpdateAsync`，结果快照取自该调用返回的设置值。
- 明确采纳裁定：设为 `Enabled=true` 即使已启用仍运行 policy 和跨已启用路径冲突检查；总开关同值、边界移动、相同替换入口均返回 `Changed=false/Error=None`；禁用且本已禁用直接 no-op。
- 当前范围只提供命令层与生产注册，后续 ViewModel/UI 任务应只消费 `ResidentSettingsMutationResult`，不要恢复异常文本解析。

---

## Task 7 修复第 1 轮

### RED / GREEN

- RED：增加 `DisabledPeerKnownPathConflictDoesNotBlockEnableOrDisable` 后，Debug 聚焦测试失败；启用目标在仅存在停用 peer 的遗留识别路径冲突下返回 `Changed=false`。根因是单项启停通过 enabled-only 检查后，仍把全集交给 `ResidentApplicationNormalizer`，后者不区分 `Enabled`。
- GREEN：启停事务通过 policy/仅 Enabled peer 冲突检查后，只替换目标项的 `Enabled`，保留原列表顺序和路径，不再全表正规化。上述测试及全部新分支测试通过。

### 新增精确分支

- 总开关同值返回 `Changed=false/Error=None`。
- 首/尾边界相邻移动返回 `false/None`；非法偏移仍为参数错误。
- 添加重复启动入口返回 `DuplicateLaunchPath`。
- 已启用再设为启用会重新调用 policy；policy 或 Enabled peer 冲突失败时返回类型化错误，成功时 `false/None`。
- 已停用再设为停用短路 policy，返回 `false/None`。
- 同一正规化身份的 replace 仍调用 policy：成功 `false/None`，拒绝为 `ExecutablePathRejected`。
- 停用 peer 的 known-path 冲突不会阻止目标启用或之后停用；已启用 peer 的冲突仍被拒绝。

### 修复验证

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*ResidentAppCommandTests"
# PASS: 18 / 18

dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*CompositionRootStateTests"
# PASS: 31 / 31

dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Release --no-restore -- --filter-class "*ResidentAppCommandTests"
# PASS: 18 / 18

dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore
# PASS: 191 / 191
```

### 修复文件与自审

- 修改：`src/DeskButler.Desktop/Hosting/ResidentAppCommands.cs`
- 修改：`tests/DeskButler.Desktop.Tests/Hosting/ResidentAppCommandTests.cs`
- 修改：本报告。
- 自审：唯一写入口仍是 `SettingsCoordinator.UpdateAsync`；本修复没有新增依赖、外部进程操作或真实应用访问。Minor（多个 `HandleAsync` 仅有 `<inheritdoc/>`）依任务要求暂缓。
