# 常驻应用整分支最终修复报告

## 范围与提交

- 基线：`98a20cf`
- 主修复提交：`b7e60ed fix: harden resident application workflows`
- 未启动 QQ、微信、富途或其它真实第三方应用；未安装、卸载、重启、注销或关机。
- 仓库没有 remote，未 push、tag 或新增 remote。

## Important 1：候选确认安全事务

- RED：整分支审查证明 `ResidentCandidateCoordinator` 仅检查非空路径，UNC、非固定盘、禁止目录、缺失、提权或替换路径可绕过发现阶段策略后写入。
- GREEN：生产组合根向发现、设置命令和确认协调器注入同一 `IResidentExecutablePolicy`。确认先在短锁中捕获 generation/CandidateId 可信快照，锁外验证全部选中路径，再在设置事务线性化点重验代次、身份和替换目标；持久化只使用 `NormalizedPath`。拒绝保留候选且不改设置，latest-wins 不变。
- 覆盖：`Confirm只持久化策略正规化路径且拒绝不安全路径时保留候选`，以及既有 generation/replacement/barrier 测试。

## Important 2：自动/手动启动 single-flight

- RED：审查与原并发测试证明 gate 只包围 `StartAsync` 发起点，自动和手动可在 gate 外都得到 `NotRunning` 并各启动一次；旧测试错误地期待两次启动。
- GREEN：按稳定启动身份建立 single-flight，把首次运行检查、策略验证、自动 attempted 持久化、最终运行复查和启动纳入同一身份门；不同批次/条目仍保持既有顺序、1 秒启动 pacing 与取消语义。
- 覆盖：`AutomaticAndManualStartAttemptsAreGloballySpaced` 改为同身份 barrier 场景并断言最终只有一次启动；全类 15/15 通过。

## Important 3/4：发现过滤、分组和 CandidateId

- RED：独立 Find 传空 ordinary path 时，普通可见窗口观察仍参与候选；产品键使用区分大小写的 `|` 拼接，大小写变体分裂且字段含 `|` 可歧义。
- GREEN：逐 HWND 记录 `HasOrdinaryVisibleTopLevelWindow`；任何产品成员存在普通可见、无 owner、非 tool、非 cloaked 主窗口时抑制整个产品组，隐藏/owned/tool/cloaked/纯隐藏仍参与。分组改为字段化复合键，路径和产品文本按 Windows 忽略大小写语义，优先 catalog 规范元数据；CandidateId 使用字段长度编码后 SHA-256，不含 PID。
- 覆盖：普通成员整组抑制、大小写合并、分隔符不碰撞、PID 不变、replacement path 敏感测试。

## Important 5：图标安全与编辑 I/O

- RED：候选 ViewModel 构造和每次文本 setter 都会在策略拒绝前同步调用图标 provider。
- GREEN：只有策略 `Accepted` 且带 `NormalizedPath` 才调用 provider；拒绝路径使用内存空 fallback。文本逐字编辑只更新草稿/确认状态，不再触碰文件或 Shell；选择器提交边界才刷新图标。
- 覆盖：UNC、相对、禁止/拒绝路径不调用 provider；允许路径只传正规化值。

## Important 6：显式 Find 导航

- RED：设置页显式 Find 只更新集合和笼统文案，不发窗口导航信号。
- GREEN：显式 Find 有候选时显示精确数量并复用窗口内候选导航事件，组合根选择 Home 并聚焦确认区；无候选显示“未发现新的常驻应用候选”。真实 STA WPF 测试覆盖隐藏/可见窗口和非 Home 页。

## Minor 1–8

1. 空候选确认面板 `Collapsed`，因此不进入 Tab 导航；非空时恢复显示和焦点。
2. 进程快照用外层 `finally` 释放全部包装，中途异常时未访问包装也释放；单个 Dispose 失败不妨碍其余释放。
3. final-path resolver 注释明确 deny-delete 句柄只活到解析方法返回，启动前仍需重验。
4. CandidateId 测试直接比较不同 PID 相等、不同 replacement path 不等。
5. Task 7 各 `HandleAsync` 补充具体中文 method summary。
6. Installer contract lexer 在字符串/子表达式/赋值后的 `#` 与 `<#` 边界正确识别，覆盖 `x"y"#`、`x'y'<#`、`x$(1)#`、`$x=<#...#>1; exit`，既有合法 here-string 测试继续通过。
7. 用户指南明确当前固定批次设置只能抑制原计划项，不能增项或改序。
8. replacement path 用 `Exists/Missing/Inaccessible` 三态；只有明确 Missing 才建议替换，访问拒绝/未知不建议。

## 验证证据

```text
dotnet test tests\DeskButler.Infrastructure.Windows.Tests\DeskButler.Infrastructure.Windows.Tests.csproj -c Debug --no-restore
PASS: 138/138

dotnet test tests\DeskButler.Desktop.Tests\DeskButler.Desktop.Tests.csproj -c Debug --no-restore
PASS（后续 Important A 第 2 轮修复后）: 274/274

dotnet test DeskButler.slnx -c Debug --no-restore
PASS（后续 Important A 第 2 轮修复后的最终 Debug 回归）: 655 total, 652 passed, 3 explicitly gated skips, 0 failed

scripts\verify-release.cmd
Release build: 0 warnings, 0 errors
Release tests（后续 Important A 第 2 轮修复后的最终 fresh artifact）: 653 total, 650 passed, 3 explicitly gated skips, 0 failed
Publish: PASS
Inno Setup 7.1.0: PASS
```

## 安装器

- 路径：`artifacts\installer\DeskButler-Setup-0.1.0.exe`
- 大小：`64,531,942` 字节
- SHA-256：`893CDAAA1B467B690B27B4DCB9C433C9606C55968045D5D5C8E1D90CA193FD17`
- `Get-FileHash -Algorithm SHA256` 与验证脚本结果一致。

## 尚未关闭的外部门禁/风险

- Windows 10 专用 VM 的 QQ/微信/富途发现、缩托盘、重启顺序、去重、主动退出与卸载存活链。
- Windows 11 独立验收。
- 干净账户真实安装/默认保留卸载/显式删除卸载。
- 本构建真实 30 分钟资源验证。
- 真实重启、诊断 ZIP 用户界面导出、产品代码签名。
- 这些门禁保持 PENDING/BLOCK/NEEDS_USER_CONFIRMATION，没有被测试 skip 或文档改写为通过。
