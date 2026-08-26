# DeskButler Resident Applications Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 DeskButler 增加全局“常驻应用”管理，使 QQ、微信、富途牛牛等没有普通可见主窗口的应用可经用户确认加入列表，并在每个 Windows 登录会话中安全、依序自动启动一次。

**Architecture:** Core 定义不可变设置、候选、发现和登录批次契约；Persistence 负责兼容旧 JSON 及原子会话文件；Infrastructure.Windows 只负责 Windows 进程/窗口/注册表观察、路径安全、Authentication LUID 和启动边界。Desktop.Hosting 组合手动保存后的 latest-wins 候选确认、统一设置事务和可取消登录启动协调器，WPF ViewModel/XAML 只投影状态与发送命令。

**Tech Stack:** C# 14、.NET 10、WPF、WinForms NotifyIcon、Win32、Windows Registry、System.Text.Json、xUnit v3、Microsoft Testing Platform、Inno Setup 7.1.0。

**Spec:** `docs/superpowers/specs/2026-08-26-resident-apps-design.md`

## Global Constraints

- 最低目标仍为 `net10.0-windows10.0.17763.0`；必须在 Windows 10 实机或虚拟机完成主要验收，并保留 Windows 11 外部门禁。
- 不新增第三方依赖，不读取进程命令行、账号、窗口正文或应用数据，不使用 Explorer 私有工具栏内存结构枚举通知区域图标。
- 不为第三方应用创建 `Run` 值、计划任务、服务或快捷方式；只由 DeskButler 在登录后执行一次固定批次。
- 只接受本地固定磁盘上的普通 `.exe`；拒绝 UNC、网络共享、可移动盘、系统目录、临时目录、安装器缓存、无法访问和需要提升权限的入口。
- 所有运行期设置修改必须共享生产对象图中的同一 `SettingsCoordinator`；不得用整份旧设置覆盖并发字段。
- 每个新增或修改的方法写中文方法级注释，非显然并发、隐私和故障降级规则写中文意图说明。
- 每个行为先写测试并实际观察预期 RED，再写最小实现并观察 GREEN；不通过 skip、重跑、放宽断言或真实延时掩盖失败。
- 不安装、卸载、重启、注销、关机、启动真实 QQ/微信/富途，或删除真实 `%LOCALAPPDATA%\DeskButler`；这些操作只在最终专用虚拟机清单中由用户执行。
- 仓库根固定为 `D:/Projects/Php/client_dev/WebService/Html/DeskButler`；保持无 remote，不 push、tag 或 add remote。
- 真实重启、Windows 11、长时资源测试、签名和干净账户安装/卸载仍是外部门禁，不得以自动测试代替。

---

### Task 1: 常驻应用领域模型与设置正规化

**Files:**
- Create: `src/DeskButler.Core/ResidentApps/ResidentApplication.cs`
- Create: `src/DeskButler.Core/ResidentApps/ResidentAppCandidate.cs`
- Create: `src/DeskButler.Core/ResidentApps/ResidentAppContracts.cs`
- Create: `src/DeskButler.Core/ResidentApps/ResidentApplicationNormalizer.cs`
- Modify: `src/DeskButler.Core/Settings/ButlerSettings.cs`
- Create: `tests/DeskButler.Core.Tests/ResidentApps/ResidentApplicationNormalizerTests.cs`

**Interfaces:**
- Consumes: Windows 大小写不敏感路径语义和既有不可变 `ButlerSettings` record。
- Produces: `ResidentApplication`、`ResidentAppCandidate`、`ResidentCandidateConfidence`、`ResidentDiscoveryResult`、平台无关 resident 契约、`ResidentApplicationNormalizer.Normalize(...)`，供后续持久化、发现、命令和启动任务使用。

- [ ] **Step 1: 写设置默认值与正规化失败测试**

新增测试覆盖：总开关默认 `true`、列表默认空；路径转换为绝对路径；`KnownProcessPaths` 必须包含 `LaunchPath`；启动入口重复时保留第一项；仅识别路径冲突时从后项移除；后项启动入口若被前项占用则整项无效；有效项按 `LaunchOrder`、路径稳定重排为连续顺序。

```csharp
[Fact]
public void NormalizeKeepsFirstLaunchIdentityAndRemovesLaterKnownPathConflict()
{
    var first = App(@"C:\Apps\QQ\QQ.exe", [@"C:\Apps\QQ\QQ.exe", @"C:\Apps\QQ\helper.exe"], 0);
    var second = App(@"C:\Apps\Futu\Futu.exe", [@"C:\Apps\Futu\Futu.exe", @"C:\Apps\QQ\helper.exe"], 1);

    var result = ResidentApplicationNormalizer.Normalize([first, second]);

    Assert.Equal([@"C:\Apps\QQ\QQ.exe", @"C:\Apps\Futu\Futu.exe"],
        result.Applications.Select(item => item.LaunchPath));
    Assert.DoesNotContain(@"C:\Apps\QQ\helper.exe", result.Applications[1].KnownProcessPaths);
    Assert.Equal([0, 1], result.Applications.Select(item => item.LaunchOrder));
    Assert.Contains(result.Diagnostics, item => item.Kind == ResidentNormalizationIssue.KnownPathConflict);
}
```

- [ ] **Step 2: 运行 Core RED**

Run: `dotnet test tests/DeskButler.Core.Tests/DeskButler.Core.Tests.csproj -c Debug --no-restore -- --filter-class "*ResidentApplicationNormalizerTests"`

Expected: FAIL；常驻类型及 `ButlerSettings.ResidentApplicationsEnabled` 尚不存在。

- [ ] **Step 3: 实现稳定领域类型**

保持设置条目不承载图标或进程命令行；候选允许 `LaunchPath` 为空，从而表达“发现了产品但必须人工选择入口”。

```csharp
public sealed record ResidentApplication(
    string LaunchPath,
    IReadOnlySet<string> KnownProcessPaths,
    string DisplayName,
    bool Enabled,
    int LaunchOrder);

public enum ResidentCandidateConfidence { Low, High }
public enum ResidentCandidateKind { NewApplication, PathReplacement }

public sealed record ResidentAppCandidate(
    string CandidateId,
    string DisplayName,
    string? LaunchPath,
    IReadOnlySet<string> KnownProcessPaths,
    ResidentCandidateConfidence Confidence,
    ResidentCandidateKind Kind,
    string? ReplacesLaunchPath)
{
    public bool IsSelectedByDefault => Confidence == ResidentCandidateConfidence.High && LaunchPath is not null;
}

public sealed record ResidentDiscoveryResult(
    IReadOnlyList<ResidentAppCandidate> Candidates,
    IReadOnlyList<ResidentDiscoveryDiagnostic> Diagnostics);

public interface IResidentAppDiscovery
{
    Task<ResidentDiscoveryResult> DiscoverAsync(
        IReadOnlySet<string> ordinaryWindowPaths,
        IReadOnlyList<ResidentApplication> existing,
        CancellationToken cancellationToken);
}

public interface IResidentExecutablePolicy
{
    ResidentExecutableValidation Validate(string path);
}

public interface ILogonSessionIdentityProvider
{
    string GetCurrent();
}

public interface IResidentProcessRuntime
{
    Task<ResidentRunningCheck> CheckRunningAsync(
        IReadOnlySet<string> knownProcessPaths,
        CancellationToken cancellationToken);
    Task StartAsync(string executablePath, CancellationToken cancellationToken);
}

public sealed record ButlerSettings(
    bool CaptureEnabled,
    bool StartupEnabled,
    int RecoveryCardDismissSeconds,
    IReadOnlySet<string> ExcludedExecutablePaths,
    bool ResidentApplicationsEnabled,
    IReadOnlyList<ResidentApplication> ResidentApplications);
```

`ResidentRunningCheck` 包含 `ResidentRunningState.Running/NotRunning/Unknown` 和匹配到的脱敏路径。运行检查必须针对调用方传入的目标路径：无关进程访问拒绝不影响结果；名称可能匹配目标但无法读取完整路径时才返回 `Unknown`，不能用全系统一次访问失败让所有应用永久跳过。`ResidentExecutableValidation`、`ResidentExecutableRejection` 和发现诊断枚举也在 `ResidentAppContracts.cs` 声明，Infrastructure 只实现这些契约。

为避免修改大量旧调用方时填错默认值，在 `ButlerSettings` 增加明确的兼容工厂 `CreateLegacy(...)`，然后逐个更新生产和测试构造点；禁止增加会把 `false` 与“字段缺失”混淆的可选 bool 构造参数。

- [ ] **Step 4: 实现正规化结果和冲突诊断**

`Normalize` 逐项隔离 `ArgumentException`、`NotSupportedException`、`PathTooLongException`，使用 `Path.GetFullPath`、`Path.TrimEndingDirectorySeparator` 和 `StringComparer.OrdinalIgnoreCase`。它返回有效列表和不含原始异常消息的分类诊断：

```csharp
public sealed record ResidentNormalizationResult(
    IReadOnlyList<ResidentApplication> Applications,
    IReadOnlyList<ResidentNormalizationDiagnostic> Diagnostics);

public static ResidentNormalizationResult Normalize(IEnumerable<ResidentApplication> source);
```

空名称回退到 `Path.GetFileNameWithoutExtension(LaunchPath)`；负顺序只影响输入排序，输出始终重编号为 `0..N-1`。

- [ ] **Step 5: 运行 Core GREEN 与全 Core 回归**

Run:

```text
dotnet test tests/DeskButler.Core.Tests/DeskButler.Core.Tests.csproj -c Debug --no-restore -- --filter-class "*ResidentApplicationNormalizerTests"
dotnet test tests/DeskButler.Core.Tests/DeskButler.Core.Tests.csproj -c Debug --no-restore
```

Expected: 新增默认值、重复、冲突、畸形路径和稳定顺序测试全部通过；既有 Core 测试无失败。

- [ ] **Step 6: 提交 Task 1**

```text
git add src/DeskButler.Core tests/DeskButler.Core.Tests
git commit -m "feat: add resident application domain model"
```

---

### Task 2: 设置 JSON 兼容与登录批次原子存储

**Files:**
- Modify: `src/DeskButler.Persistence/Json/JsonSettingsStore.cs`
- Modify: `src/DeskButler.Persistence/Paths/AppDataPaths.cs`
- Create: `src/DeskButler.Core/ResidentApps/ResidentLaunchSession.cs`
- Create: `src/DeskButler.Core/ResidentApps/IResidentLaunchSessionStore.cs`
- Create: `src/DeskButler.Persistence/Json/JsonResidentLaunchSessionStore.cs`
- Modify: `tests/DeskButler.Persistence.Tests/Json/JsonSettingsStoreTests.cs`
- Create: `tests/DeskButler.Persistence.Tests/Json/JsonResidentLaunchSessionStoreTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `ResidentApplicationNormalizer` 和 `ButlerSettings` 新字段、`AppDataPaths.RootDirectory`。
- Produces: `AppDataPaths.ResidentLaunchSessionFilePath`；`IResidentLaunchSessionStore.LoadAsync/SaveAsync`；格式版本 1 的固定计划模型。

- [ ] **Step 1: 写旧设置兼容和单项隔离 RED**

直接写入缺少新字段的旧 JSON，断言加载为 `ResidentApplicationsEnabled=true`、空列表；再写入一份含一个畸形条目和两个有效条目的 JSON，断言有效项保留，捕获、登录启动和排除字段不回退。

```csharp
Assert.True(loaded.ResidentApplicationsEnabled);
Assert.Empty(loaded.ResidentApplications);
Assert.False(loaded.CaptureEnabled);
```

往返测试必须断言 `launchPath`、`knownProcessPaths`、`displayName`、`enabled` 和 `launchOrder`，而不是只比较条目数量。

- [ ] **Step 2: 运行设置持久化 RED**

Run: `dotnet test tests/DeskButler.Persistence.Tests/DeskButler.Persistence.Tests.csproj -c Debug --no-restore -- --filter-class "*JsonSettingsStoreTests"`

Expected: FAIL；JSON 文档尚未声明或映射常驻字段。

- [ ] **Step 3: 扩展 SettingsDocument 并运行 GREEN**

`ResidentApplicationsEnabled` 的 CLR 属性初始化为 `true`。常驻列表使用 `JsonElement` 逐项解析：单项对象缺字段、字段类型错误或路径损坏只跳过该项；JSON 根损坏或顶层设置字段类型错误才沿用整个 `settings.corrupt-*` 降级。

```csharp
public bool ResidentApplicationsEnabled { get; set; } = true;
public List<JsonElement> ResidentApplications { get; set; } = [];
```

`FromSettings` 对每个稳定 DTO 调用 `JsonSerializer.SerializeToElement`；`ToSettings` 在逐项 try/catch 中调用 `element.Deserialize<ResidentApplicationDocument>`，随后一次调用正规化器。`JsonSettingsStore` 构造函数新增可选 `Action<ResidentNormalizationDiagnostic>`；对每条隔离/冲突诊断调用一次，诊断 sink 自身失败不得改变加载结果，且参数不包含原始异常 message。由于捕获流程会频繁 `LoadAsync`，存储对本次已经读取的小型 JSON 字节计算 SHA-256，并与诊断种类集合组成线程安全进程内指纹：同一内容只报告一次，文件被原子替换为新内容后允许报告新的问题，不依赖文件时间戳精度。

Run: `dotnet test tests/DeskButler.Persistence.Tests/DeskButler.Persistence.Tests.csproj -c Debug --no-restore -- --filter-class "*JsonSettingsStoreTests"`

Expected: PASS；旧 JSON、null 列表、畸形单项和完整往返均通过。

- [ ] **Step 4: 写会话存储 RED**

模型固定为：

```csharp
public sealed record ResidentLaunchSession(
    int FormatVersion,
    string LogonSessionId,
    bool Completed,
    IReadOnlyList<ResidentLaunchPlanItem> Plan);

public sealed record ResidentLaunchPlanItem(string LaunchIdentity, bool Attempted);
```

测试覆盖不存在返回 null、原子保存往返、覆盖旧 LUID、临时文件 finally 清理、损坏文件改名为 `resident-launch-session.corrupt-<timestamp>-<guid>.json`，以及 `RecoverCorruptAsync(currentLuid)` 写入当前 LUID 的已完成空计划。另用拒绝移动的文件系统 fake 断言返回 `PreservationFailedFailClosed`、原故障字节不变且没有新 session 文件覆盖证据。

- [ ] **Step 5: 运行会话存储 RED**

Run: `dotnet test tests/DeskButler.Persistence.Tests/DeskButler.Persistence.Tests.csproj -c Debug --no-restore -- --filter-class "*JsonResidentLaunchSessionStoreTests"`

Expected: FAIL；路径属性、接口和存储尚不存在。

- [ ] **Step 6: 实现原子会话存储**

沿用 `JsonSettingsStore` 的 `FileOptions.WriteThrough`、`Flush(true)`、`File.Replace/File.Move` 和 `.tmp` finally 清理。解析失败不返回空对象伪装正常，明确返回：

```csharp
public interface IResidentLaunchSessionStore
{
    Task<ResidentLaunchSession?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(ResidentLaunchSession session, CancellationToken cancellationToken);
    Task<ResidentLaunchRecoveryResult> RecoverCorruptAsync(
        string currentLogonSessionId, CancellationToken cancellationToken);
}
```

`ResidentLaunchRecoveryResult` 固定为 `RecoveredWithEmptyPlan` 或 `PreservationFailedFailClosed`。`RecoverCorruptAsync` 先保留故障文件，再保存 `Completed=true, Plan=[]`；保留失败时不得删除或覆盖原文件，返回 fail-closed 结果。协调器对两种结果本次登录都不启动，后一种在后续重启时仍会重新识别为损坏并安全跳过。

- [ ] **Step 7: 运行 Persistence GREEN 与回归**

Run:

```text
dotnet test tests/DeskButler.Persistence.Tests/DeskButler.Persistence.Tests.csproj -c Debug --no-restore -- --filter-class "*JsonSettingsStoreTests"
dotnet test tests/DeskButler.Persistence.Tests/DeskButler.Persistence.Tests.csproj -c Debug --no-restore -- --filter-class "*JsonResidentLaunchSessionStoreTests"
dotnet test tests/DeskButler.Persistence.Tests/DeskButler.Persistence.Tests.csproj -c Debug --no-restore
```

Expected: 新旧 JSON 及会话故障恢复测试通过；既有 SQLite、日志和诊断测试无失败。

- [ ] **Step 8: 提交 Task 2**

```text
git add src/DeskButler.Core/ResidentApps src/DeskButler.Persistence tests/DeskButler.Persistence.Tests
git commit -m "feat: persist resident settings and launch sessions"
```

---

### Task 3: Windows 路径安全、登录身份和运行边界

**Files:**
- Create: `src/DeskButler.Infrastructure.Windows/ResidentApps/WindowsResidentExecutablePolicy.cs`
- Create: `src/DeskButler.Infrastructure.Windows/ResidentApps/WindowsExecutableFinalPathResolver.cs`
- Create: `src/DeskButler.Infrastructure.Windows/ResidentApps/WindowsExecutableElevationInspector.cs`
- Create: `src/DeskButler.Infrastructure.Windows/ResidentApps/WindowsLogonSessionIdentityProvider.cs`
- Create: `src/DeskButler.Infrastructure.Windows/ResidentApps/WindowsResidentProcessRuntime.cs`
- Modify: `src/DeskButler.Infrastructure.Windows/Native/NativeMethods.cs`
- Create: `tests/DeskButler.Infrastructure.Windows.Tests/ResidentApps/WindowsResidentExecutablePolicyTests.cs`
- Create: `tests/DeskButler.Infrastructure.Windows.Tests/ResidentApps/WindowsLogonSessionIdentityProviderTests.cs`
- Create: `tests/DeskButler.Infrastructure.Windows.Tests/ResidentApps/WindowsResidentProcessRuntimeTests.cs`
- Create: `tests/DeskButler.Infrastructure.Windows.Tests/TestApps/DeskButler.ResidentFixture/DeskButler.ResidentFixture.csproj`
- Create: `tests/DeskButler.Infrastructure.Windows.Tests/TestApps/DeskButler.ResidentFixture/Program.cs`
- Modify: `tests/DeskButler.Infrastructure.Windows.Tests/DeskButler.Infrastructure.Windows.Tests.csproj`

**Interfaces:**
- Consumes: Task 1 的 `IResidentExecutablePolicy`、`ILogonSessionIdentityProvider` 和 `IResidentProcessRuntime`；`IClock` 不参与平台边界。
- Produces: `WindowsLogonSessionIdentityProvider.GetCurrent()` 返回 Authentication LUID 的稳定十六进制字符串；`WindowsResidentProcessRuntime` 实现可靠性显式可见的运行枚举与 `StartAsync`。

- [ ] **Step 1: 写路径矩阵 RED**

在测试专属临时目录创建真实 `.exe` fixture，使用可注入的最终路径解析器、磁盘类型查询器和 manifest 提升检查器验证本地固定盘、无重解析跳转、`asInvoker` 程序通过；以下逐项拒绝并返回稳定原因枚举：相对路径、目录、非 exe、UNC、`DriveType.Network`、`Removable`、Windows 目录、`%TEMP%`、`%LOCALAPPDATA%\Temp`、安装器缓存、文件不存在、访问失败、符号链接/目录联接点跳向禁止目录或非固定卷、`requireAdministrator`/`highestAvailable` manifest、策略判断异常。

```csharp
var result = policy.Validate(fixtureExe);
Assert.True(result.IsAllowed);
Assert.Equal(Path.GetFullPath(fixtureExe), result.NormalizedPath);
```

- [ ] **Step 2: 恢复新增 fixture 项目依赖图**

Run: `dotnet restore tests/DeskButler.Infrastructure.Windows.Tests/DeskButler.Infrastructure.Windows.Tests.csproj`

Expected: exit 0；新 `DeskButler.ResidentFixture.csproj` 生成自己的 `project.assets.json`，后续 `--no-restore` 命令不依赖旧项目图侥幸通过。

- [ ] **Step 3: 运行路径 RED 并实现最小策略**

Run: `dotnet test tests/DeskButler.Infrastructure.Windows.Tests/DeskButler.Infrastructure.Windows.Tests.csproj -c Debug --no-restore -- --filter-class "*WindowsResidentExecutablePolicyTests"`

Expected: FAIL；策略类型尚不存在。

实现 `ResidentExecutableValidation(bool IsAllowed, string? NormalizedPath, ResidentExecutableRejection Reason)`。先用拒绝删除共享的只读文件句柄调用 `GetFinalPathNameByHandleW`，再对最终路径检查卷类型和禁止目录；目录边界比较必须补目录分隔符，避免把 `C:\WindowsOld` 错判为 `C:\Windows` 子项。启动协调器在 `Process.Start` 前再次调用同一策略缩小 TOCTOU 窗口。生产 `WindowsExecutableElevationInspector` 使用 `LoadLibraryEx(LOAD_LIBRARY_AS_DATAFILE)` 读取 RT_MANIFEST 并解析 `requestedExecutionLevel`；资源缺失按 `asInvoker`，解析/访问失败按不可靠并拒绝。测试通过 fake inspector 覆盖三种 level，另用专属 fixture manifest 做一条集成断言。

- [ ] **Step 4: 写并实现 Authentication LUID 测试**

使用 `OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY)`、`GetTokenInformation(TokenStatistics)` 读取 `TOKEN_STATISTICS.AuthenticationId`，格式固定为高低 32 位各 8 位大写十六进制：`XXXXXXXX-XXXXXXXX`。测试断言同一进程重复读取相同、非空且符合 `^[0-9A-F]{8}-[0-9A-F]{8}$`；句柄使用 `SafeAccessTokenHandle`。

Run: `dotnet test tests/DeskButler.Infrastructure.Windows.Tests/DeskButler.Infrastructure.Windows.Tests.csproj -c Debug --no-restore -- --filter-class "*WindowsLogonSessionIdentityProviderTests"`

Expected: 首次 RED 后 GREEN；不把用户名、SID 或 token 内容写入结果。

- [ ] **Step 5: 写运行识别与安全启动 RED**

启动 `DeskButler.ResidentFixture.exe --wait` 仅供测试持有进程。对 fixture 的 `KnownProcessPaths` 返回 `Running`；目标文件名相同但路径无法读取时返回 `Unknown`；无关的系统/高权限进程访问拒绝仍返回 `NotRunning`。已退出进程和其他 Session 不参与匹配。`StartAsync` 只接收刚刚重新验证的最终绝对路径并使用：

```csharp
new ProcessStartInfo(normalizedPath)
{
    UseShellExecute = true,
    WorkingDirectory = Path.GetDirectoryName(normalizedPath)!
};
```

实现时先把 `KnownProcessPaths` 转成正规化路径集合及文件名集合；只对当前会话中 `ProcessName` 可能匹配目标文件名的进程读取 `MainModule.FileName`。匹配名称的进程若路径访问拒绝则为 `Unknown`，成功读取后按完整路径判断 `Running/NotRunning`；非匹配名称进程的访问失败忽略。这样 QQ 的检查不会被无关系统进程降级。

测试把 fixture exe 复制到唯一临时目录；无参数启动时 fixture 在同目录写固定 marker，断言没有附带参数、没有 `Verb="runas"`。`Process.Start` 返回的 `Process` 立即 Dispose 以释放 DeskButler 持有的进程句柄，但不 Wait、CloseMainWindow 或 Kill；不得启动 QQ、微信或富途。

- [ ] **Step 6: 运行 Infrastructure 聚焦回归**

Run:

```text
dotnet test tests/DeskButler.Infrastructure.Windows.Tests/DeskButler.Infrastructure.Windows.Tests.csproj -c Debug --no-restore -- --filter-namespace "*ResidentApps*"
dotnet test tests/DeskButler.Infrastructure.Windows.Tests/DeskButler.Infrastructure.Windows.Tests.csproj -c Debug --no-restore
```

Expected: 路径、LUID、运行枚举与 fixture 启动测试通过；现有窗口、恢复、启动注册和会话测试无失败。

- [ ] **Step 7: 提交 Task 3**

```text
git add src/DeskButler.Infrastructure.Windows tests/DeskButler.Infrastructure.Windows.Tests
git commit -m "feat: add safe Windows resident process boundaries"
```

---

### Task 4: Windows 进程观察与已安装应用目录

**Files:**
- Create: `src/DeskButler.Infrastructure.Windows/ResidentApps/ResidentProcessObservation.cs`
- Create: `src/DeskButler.Infrastructure.Windows/ResidentApps/IResidentProcessSnapshotSource.cs`
- Create: `src/DeskButler.Infrastructure.Windows/ResidentApps/WindowsResidentProcessSnapshotSource.cs`
- Create: `src/DeskButler.Infrastructure.Windows/ResidentApps/InstalledApplicationCatalog.cs`
- Create: `src/DeskButler.Infrastructure.Windows/ResidentApps/WindowsUninstallRegistryReader.cs`
- Modify: `src/DeskButler.Infrastructure.Windows/Native/NativeMethods.cs`
- Create: `tests/DeskButler.Infrastructure.Windows.Tests/ResidentApps/WindowsResidentProcessSnapshotSourceTests.cs`
- Create: `tests/DeskButler.Infrastructure.Windows.Tests/ResidentApps/InstalledApplicationCatalogTests.cs`

**Interfaces:**
- Consumes: Task 3 的安全路径规则和当前 Windows 会话。
- Produces: `IResidentProcessSnapshotSource.CaptureAsync(CancellationToken)`、`IInstalledApplicationCatalog.ReadAsync(CancellationToken)`；只返回公开元数据和分类诊断，不做产品归组或候选选择。

```csharp
internal sealed record ResidentProcessObservation(
    int ProcessId,
    string ExecutablePath,
    string? ProductName,
    string? CompanyName,
    string? FileDescription,
    ResidentWindowTraits WindowTraits);

internal sealed record InstalledApplicationEntry(
    string DisplayName,
    string? Publisher,
    string? InstallRoot,
    string? DisplayIconPath);

internal interface IResidentProcessSnapshotSource
{
    Task<ResidentProcessSnapshot> CaptureAsync(CancellationToken cancellationToken);
}

internal interface IInstalledApplicationCatalog
{
    Task<InstalledApplicationSnapshot> ReadAsync(CancellationToken cancellationToken);
}
```

两个 snapshot 分别包含稳定排序的条目和 `ResidentDiscoveryDiagnostic` 集合；集合类型不可变，大小写不敏感路径比较只在消费方显式创建。

- [ ] **Step 1: 写进程观察 RED**

通过可注入的进程/窗口 native reader 构造当前会话、其他会话、Session 0、隐藏窗口、tool window、DWM cloaked 窗口、进程退出和访问拒绝样本。断言只保留当前交互会话，并返回 PID、exe、产品/厂商/文件描述和窗口分类；模型中不存在标题、命令行或账号字段。

- [ ] **Step 2: 运行进程观察 RED**

Run: `dotnet test tests/DeskButler.Infrastructure.Windows.Tests/DeskButler.Infrastructure.Windows.Tests.csproj -c Debug --no-restore -- --filter-class "*WindowsResidentProcessSnapshotSourceTests"`

Expected: FAIL；观察模型和 snapshot source 尚不存在。

- [ ] **Step 3: 实现公开进程信息采集**

`WindowsResidentProcessSnapshotSource` 只读取 PID、SessionId、主模块 exe、`FileVersionInfo.ProductName/CompanyName/FileDescription` 和该 PID 拥有的顶层窗口分类。使用 `EnumWindows`、`GetWindowThreadProcessId`、`IsWindowVisible`、`GetWindow(hwnd, GW_OWNER)`、`GetWindowLongPtr(GWL_EXSTYLE)`、`DwmGetWindowAttribute(DWMWA_CLOAKED)`；不调用 `GetWindowText`，不读取命令行。单进程失败转换为分类诊断，取消和进程级致命异常继续抛出。

- [ ] **Step 4: 写已安装应用目录 RED**

fake registry reader 分别提供 HKCU/HKLM、32/64 位 Uninstall 项，覆盖正常 `DisplayIcon`、带引号和 `,0` 图标索引、空 InstallLocation、无效路径、重复产品和逐键访问拒绝。断言 catalog 只返回可正规化的显示名、厂商、安装根和候选 exe，不解析 `UninstallString`。

- [ ] **Step 5: 实现只读 registry catalog**

`WindowsUninstallRegistryReader` 只读四个视图；`InstalledApplicationCatalog` 提取 `DisplayName`、`Publisher`、`InstallLocation` 和可安全剥离外围引号/尾部图标索引的 `DisplayIcon`。每个 registry key 单独 try/catch，访问拒绝只形成分类诊断。禁止执行、展开或记录卸载命令。

- [ ] **Step 6: 运行观察层 GREEN**

Run:

```text
dotnet test tests/DeskButler.Infrastructure.Windows.Tests/DeskButler.Infrastructure.Windows.Tests.csproj -c Debug --no-restore -- --filter-class "*WindowsResidentProcessSnapshotSourceTests"
dotnet test tests/DeskButler.Infrastructure.Windows.Tests/DeskButler.Infrastructure.Windows.Tests.csproj -c Debug --no-restore -- --filter-class "*InstalledApplicationCatalogTests"
```

Expected: 进程竞态、访问拒绝、窗口分类和四 registry 视图测试通过；不依赖本机实际安装列表。

- [ ] **Step 7: 提交 Task 4**

```text
git add src/DeskButler.Infrastructure.Windows/ResidentApps src/DeskButler.Infrastructure.Windows/Native tests/DeskButler.Infrastructure.Windows.Tests
git commit -m "feat: observe resident application processes"
```

---

### Task 5: 常驻候选筛选、产品分组与可信度

**Files:**
- Create: `src/DeskButler.Infrastructure.Windows/ResidentApps/WindowsResidentAppDiscovery.cs`
- Create: `tests/DeskButler.Infrastructure.Windows.Tests/ResidentApps/WindowsResidentAppDiscoveryTests.cs`

**Interfaces:**
- Consumes: Task 4 的进程观察和已安装应用目录、`IResidentAppDiscovery`、`IResidentExecutablePolicy`、普通窗口 exe 集合和现有 `ResidentApplication`。
- Produces: 安全筛选、同产品归组、主入口选择、高低可信及路径替换候选；纯算法测试不读取真实 registry 或第三方进程。

- [ ] **Step 1: 写筛选 RED**

通过 fake source 构造当前会话/Session 0、DeskButler 自身、系统/临时/缓存/不可访问路径、已有普通窗口、已有常驻项、同 exe 多实例和不同产品。断言只有允许的第三方后台观察进入后续分组。

```csharp
var candidates = await discovery.DiscoverAsync(
    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Apps\Editor\Editor.exe" },
    existingResidents,
    CancellationToken.None);

Assert.DoesNotContain(candidates.Candidates,
    item => item.KnownProcessPaths.Contains(@"C:\Apps\Editor\Editor.exe"));
```

- [ ] **Step 2: 写分组、入口、可信度和替换 RED**

建立 QQ 风格主进程、renderer、updater、crash reporter 四个观察：相同产品/厂商和最近安装根；隐藏/tool/cloaked 窗口属于主进程；catalog 给出 DisplayIcon。断言只产生一个候选、主 exe 优先、产品专属路径折叠显示，通用 updater/crash reporter 不进入 `KnownProcessPaths`。

再覆盖：无窗口且无稳定产品信息为 Low；隐藏窗口加稳定安装信息为 High；入口无法可靠选择时 `LaunchPath=null`；虚拟机辅助工具永远 Low。旧 `LaunchPath` 不存在、当前产品显示名一致且新旧路径处于同一 catalog 安装根时，产生 `PathReplacement` Low 候选；根或名称不匹配时不建议替换。

- [ ] **Step 3: 运行发现 RED**

Run: `dotnet test tests/DeskButler.Infrastructure.Windows.Tests/DeskButler.Infrastructure.Windows.Tests.csproj -c Debug --no-restore -- --filter-class "*WindowsResidentAppDiscoveryTests"`

Expected: FAIL；发现算法尚不存在。

- [ ] **Step 4: 实现稳定分组和入口排序**

分组键必须同时包含最近安装根、产品名和厂商名；任一稳定字段缺失时退回 exe 路径，禁止只按宽泛厂商目录合并多个产品。入口评分固定为：隐藏/tool/cloaked 窗口所有者 300，catalog DisplayIcon 200，产品根非 helper 主程序 100；同分按正规化路径排序。最高分不唯一或小于 100 时保持 `LaunchPath=null`。

helper token 固定为 `helper/updater/update/crash/reporter/renderer`，不得单独成为高可信入口。`CandidateId` 对候选种类、排序后的产品键、路径和正规化 `ReplacesLaunchPath` 做 SHA-256，不使用 PID；新增和替换身份不得碰撞。

- [ ] **Step 5: 运行发现 GREEN 和 ResidentApps Release 回归**

Run:

```text
dotnet test tests/DeskButler.Infrastructure.Windows.Tests/DeskButler.Infrastructure.Windows.Tests.csproj -c Debug --no-restore -- --filter-class "*WindowsResidentAppDiscoveryTests"
dotnet test tests/DeskButler.Infrastructure.Windows.Tests/DeskButler.Infrastructure.Windows.Tests.csproj -c Release --no-restore -- --filter-namespace "*ResidentApps*"
```

Expected: 筛选、归组、评分、可信度、替换、CandidateId 和单观察失败隔离全部通过；无真实第三方应用依赖。

- [ ] **Step 6: 提交 Task 5**

```text
git add src/DeskButler.Infrastructure.Windows/ResidentApps tests/DeskButler.Infrastructure.Windows.Tests/ResidentApps
git commit -m "feat: discover resident application candidates"
```

---

### Task 6: 手动保存结果、候选代次和确认事务

**Files:**
- Create: `src/DeskButler.Modules.WorkspaceRecovery/Capture/CaptureOutcome.cs`
- Modify: `src/DeskButler.Modules.WorkspaceRecovery/Capture/CaptureCoordinator.cs`
- Modify: `src/DeskButler.Desktop/Hosting/SettingsAwareWindowInventory.cs`
- Create: `src/DeskButler.Desktop/Hosting/ResidentCandidateCoordinator.cs`
- Create: `src/DeskButler.Desktop/Hosting/ResidentAppCommands.cs`
- Modify: `src/DeskButler.Desktop/Hosting/WorkspaceCommands.cs`
- Modify: `src/DeskButler.Desktop/Hosting/CompositionRoot.cs`
- Modify: `tests/DeskButler.Modules.WorkspaceRecovery.Tests/Capture/CaptureCoordinatorTests.cs`
- Create: `tests/DeskButler.Desktop.Tests/Hosting/ResidentCandidateCoordinatorTests.cs`
- Modify: `tests/DeskButler.Desktop.Tests/Hosting/CompositionRootStateTests.cs`

**Interfaces:**
- Consumes: Task 5 的 `IResidentAppDiscovery`、共享 `SettingsCoordinator`、现有手动和自动捕获入口。
- Produces: `CaptureOutcome`、`ManualSaveResult`、latest-wins `ResidentCandidateCoordinator`、候选确认/忽略/查找命令。

- [ ] **Step 1: 写手动捕获结果 RED**

新增：

```csharp
public enum CaptureSkipReason { None, Disabled, NoCandidates, NoItems, Unchanged, Failed }
public sealed record CaptureOutcome(
    bool SnapshotSaved,
    CaptureSkipReason SkipReason,
    IReadOnlySet<string> WindowExecutablePaths);
```

测试断言自动捕获在暂停时仍不枚举；手动工作流在暂停、无窗口、过滤后为空和现场未变化时均返回明确原因，并在有普通窗口时返回正规化 exe 集合。手动路径不得因此保存禁用状态下的快照。

- [ ] **Step 2: 运行 Capture RED**

Run: `dotnet test tests/DeskButler.Modules.WorkspaceRecovery.Tests/DeskButler.Modules.WorkspaceRecovery.Tests.csproj -c Debug --no-restore -- --filter-class "*CaptureCoordinatorTests"`

Expected: FAIL；现有 `SaveNowAsync` 返回 `Task` 且提前返回不说明原因。

- [ ] **Step 3: 增加独立手动捕获入口**

保留 `SaveNowAsync(reason, token)` 给自动调度和 session-ending；新增接收同一批已观察候选的保存入口：

```csharp
public Task<CaptureOutcome> SaveObservedAsync(
    string reason,
    IReadOnlyList<WindowCandidate> candidates,
    bool saveEnabled,
    CancellationToken cancellationToken);
```

`SettingsAwareWindowInventory` 新增 `CaptureForManualAsync`：加载一次最新设置，无论 `CaptureEnabled` 为何都调用底层清单，并继续应用排除项，返回 `ManualWindowObservation(bool CaptureEnabled, IReadOnlyList<WindowCandidate> Candidates)`。手动 handler 把这同一批候选和开关值传给 `SaveObservedAsync`；Modules 项目不引用 Desktop 类型，捕获保存和 exe 集合也不得二次枚举。

- [ ] **Step 4: 写 latest-wins 与确认 RED**

用第一次发现阻塞、第二次立即完成的 fake，断言旧结果晚到不能替换新代次。确认命令在 `SettingsCoordinator.UpdateAsync` 的同步 update 回调内重新锁定候选状态，并再次校验 generation、CandidateId 和替换目标；旧确认、旧忽略和旧路径修正均 no-op。测试用 barrier 让新发现分别发生在确认取得设置门之前、update 回调线性化之后和设置保存完成之前，证明只有在 update 回调核验时仍为当前代次的确认可以提交，且保存完成后不会清掉更新候选。保存失败保留同代候选，成功确认才清空。本次忽略不写设置或永久黑名单；下一次主动保存仍可重新发现同一候选。

```csharp
var oldTask = coordinator.DiscoverAsync(oldWindowPaths, token);
await discovery.FirstStarted.Task;
var latest = await coordinator.DiscoverAsync(newWindowPaths, token);
discovery.ReleaseFirst();
await oldTask;

Assert.Equal(latest.Generation, coordinator.Current.Generation);
Assert.Equal("New", Assert.Single(coordinator.Current.Candidates).DisplayName);
```

- [ ] **Step 5: 实现候选协调器与命令**

接口固定为：

```csharp
public Task<ResidentDiscoveryBatch> DiscoverAsync(
    IReadOnlySet<string> ordinaryWindowPaths, CancellationToken cancellationToken);
public Task<bool> ConfirmAsync(
    long generation, IReadOnlyList<ResidentCandidateSelection> selections,
    CancellationToken cancellationToken);
public bool Dismiss(long generation);
```

其中批次和 UI 回传形状固定为：

```csharp
public sealed record ResidentDiscoveryBatch(
    long Generation,
    IReadOnlyList<ResidentAppCandidate> Candidates,
    bool DiscoveryFailed);

public sealed record ResidentCandidateSelection(
    string CandidateId,
    string? FinalLaunchPath,
    bool IsSelected);
```

候选状态使用同步 `lock stateSync`，发现只在完成平台 await 后短暂取得该锁发布；禁止持有该锁等待设置门或执行 I/O。确认先复制请求参数，再调用 `SettingsCoordinator.UpdateAsync`；其同步 update 回调取得 `stateSync`，完成 generation、CandidateId 和替换目标核验，并把该时刻定义为提交线性化点。回调从当前候选复制 `KnownProcessPaths/DisplayName/Kind/ReplacesLaunchPath`，不能信任 UI 回传这些字段；`NewApplication` 追加正规化条目，`PathReplacement` 只在旧入口仍一致时替换。保存成功后再次取得 `stateSync`，仅在 generation 未变化时清空；保存失败不清空。此锁顺序永远是“设置门 → 短暂 stateSync”，发现和忽略绝不取得设置门，避免反向锁序。

- [ ] **Step 6: 把手动保存和独立查找接入命令总线**

`SaveSceneNowCommand` 返回：

```csharp
public sealed record ManualSaveResult(
    CaptureOutcome Capture,
    ResidentDiscoveryBatch Discovery);
```

处理器始终先调用 `SettingsAwareWindowInventory.CaptureForManualAsync`，再调用 `CaptureCoordinator.SaveObservedAsync("manual", ...)`；无论 `SnapshotSaved` 为何都调用一次候选发现。手动观察或保存出现可恢复异常时写诊断并构造 `CaptureSkipReason.Failed`、空窗口路径，随后仍执行发现；发现器系统级失败则构造 `DiscoveryFailed=true`，不得把已经保存的现场回滚或改报未保存。新增 `FindResidentCandidatesCommand` 直接以空普通窗口集合发现；`ConfirmResidentCandidatesCommand` 和 `DismissResidentCandidatesCommand` 路由到同一协调器。自动快照、session-ending 和安全检查点不引用 `IResidentAppDiscovery`。

- [ ] **Step 7: 运行 GREEN、并发设置回归和提交 Task 6**

Run:

```text
dotnet test tests/DeskButler.Modules.WorkspaceRecovery.Tests/DeskButler.Modules.WorkspaceRecovery.Tests.csproj -c Debug --no-restore -- --filter-class "*CaptureCoordinatorTests"
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*ResidentCandidateCoordinatorTests"
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*CompositionRootStateTests"
```

Expected: 暂停/无窗口/未变化仍发现，自动入口不发现，候选代次、确认重试和并发设置字段全部通过。

```text
git add src/DeskButler.Modules.WorkspaceRecovery src/DeskButler.Desktop/Hosting tests/DeskButler.Modules.WorkspaceRecovery.Tests tests/DeskButler.Desktop.Tests
git commit -m "feat: coordinate resident candidate confirmation"
```

---

### Task 7: 常驻列表设置命令与安全编辑

**Files:**
- Modify: `src/DeskButler.Desktop/Hosting/ResidentAppCommands.cs`
- Modify: `src/DeskButler.Desktop/Hosting/ResidentCandidateCoordinator.cs`
- Modify: `src/DeskButler.Desktop/Hosting/CompositionRoot.cs`
- Create: `tests/DeskButler.Desktop.Tests/Hosting/ResidentAppCommandTests.cs`
- Modify: `tests/DeskButler.Desktop.Tests/Hosting/CompositionRootStateTests.cs`

**Interfaces:**
- Consumes: 共享 `SettingsCoordinator`、`IResidentExecutablePolicy` 和正规化器。
- Produces: 总开关、启停、删除、上下移、浏览添加、路径替换的命令处理器；所有命令返回最新常驻列表快照。

- [ ] **Step 1: 写命令事务 RED**

分别覆盖：

```csharp
SetResidentApplicationsEnabledCommand(bool IsEnabled)
SetResidentApplicationEnabledCommand(string LaunchPath, bool IsEnabled)
RemoveResidentApplicationCommand(string LaunchPath)
MoveResidentApplicationCommand(string LaunchPath, int Offset)
AddResidentApplicationCommand(string LaunchPath, string? DisplayName)
ReplaceResidentApplicationPathCommand(string OldLaunchPath, string NewLaunchPath)
```

断言：总开关只改自己的字段；上下移只能接受 `-1/+1` 并重编号；删除不存在路径为幂等 no-op；启用会重新验证入口并拒绝跨项已知路径冲突；浏览添加拒绝策略不允许的路径；替换保留显示名、启用状态和顺序，但 `KnownProcessPaths` 重置为新入口，直到以后发现再次确认。

- [ ] **Step 2: 运行命令 RED**

Run: `dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*ResidentAppCommandTests"`

Expected: FAIL；常驻设置命令尚未注册。

- [ ] **Step 3: 实现共享处理基类和明确结果**

不要让 UI 解析异常文本；返回：

```csharp
public sealed record ResidentSettingsMutationResult(
    bool Changed,
    ResidentSettingsError Error,
    IReadOnlyList<ResidentApplication> Applications,
    bool ResidentApplicationsEnabled);
```

路径拒绝、重复入口、识别路径冲突、条目不存在分别使用枚举。每个 handler 的唯一写入口是 `SettingsCoordinator.UpdateAsync`，并从该调用返回的 `ButlerSettings` 构造结果。

把全部新增设置 handler 注册到生产 `InProcessCommandBus`，并在 `CompositionRootStateTests` 断言它们和候选确认 handler 引用同一 `SettingsCoordinator`；不得留到 UI 任务再补注册。

- [ ] **Step 4: 写并发字段保护测试并运行 GREEN**

用 barrier 并发执行捕获开关、DeskButler 登录启动开关、排除项和常驻列表操作，断言四类修改均保留；再并发确认候选和移动列表，断言不丢项且顺序连续。

Run:

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*ResidentAppCommandTests"
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*MainViewModelTests"
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*CompositionRootStateTests"
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Release --no-restore -- --filter-class "*ResidentAppCommandTests"
```

Expected: 所有 mutation 与并发字段保护测试通过。

- [ ] **Step 5: 提交 Task 7**

```text
git add src/DeskButler.Desktop/Hosting tests/DeskButler.Desktop.Tests/Hosting
git commit -m "feat: manage resident application settings"
```

---

### Task 8: 每登录会话一次的固定启动批次

**Files:**
- Create: `src/DeskButler.Desktop/Hosting/ResidentLaunchCoordinator.cs`
- Modify: `src/DeskButler.Desktop/Hosting/CompositionRoot.cs`
- Create: `tests/DeskButler.Desktop.Tests/Hosting/ResidentLaunchCoordinatorTests.cs`
- Modify: `tests/DeskButler.Desktop.Tests/Hosting/CompositionRootStateTests.cs`

**Interfaces:**
- Consumes: `ISettingsStore`、`IResidentLaunchSessionStore`、`ILogonSessionIdentityProvider`、`IResidentProcessRuntime`、`IResidentExecutablePolicy`、`IClock`、`IDiagnosticLog`。
- Produces: 非阻塞且幂等的 `Start()`、可观察的 `Completion`、`LaunchEnabledNowAsync(CancellationToken)` 和 exactly-once `DisposeAsync()`；自动批次 5 秒初始延迟、项目间 1 秒。

- [ ] **Step 1: 写固定计划与 LUID RED**

使用 fake clock/store/runtime 覆盖：首次启动等待 5 秒后建立按顺序固定计划；同 LUID 已完成立即结束；不同 LUID 替换旧批次；总开关关闭写已完成空计划；批次建立后新增、启用、改序不进入本批。

```csharp
coordinator.Start();
var run = coordinator.Completion;
Assert.Empty(runtime.StartedPaths);
await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
await run;
Assert.Equal([qqPath, wechatPath], runtime.StartedPaths);
```

- [ ] **Step 2: 写先登记再启动和崩溃续跑 RED**

让第一个 `runtime.StartAsync` 抛错，读取会话文件断言第一项已经 `Attempted=true`；构造新的协调器模拟 DeskButler 重启，断言只启动第二项。若用户随后退出第一项，同 LUID 重启也不得再次启动。

另测会话续跑时条目已删除、禁用或路径改变：计划项标记已尝试并跳过，绝不按会话文件中的旧路径启动。

- [ ] **Step 3: 写去重、故障隔离和损坏恢复 RED**

对每一项调用 `CheckRunningAsync(KnownProcessPaths)`：`Running` 和 `Unknown` 都跳过，只有 `NotRunning` 才可能启动；无关进程访问拒绝不得产生 `Unknown`。策略拒绝、文件缺失和单项启动失败继续下一项；任意两个外部启动尝试的开始时刻至少间隔 1 秒。会话 JSON 损坏时调用 `RecoverCorruptAsync(currentLuid)`；无论返回 `RecoveredWithEmptyPlan` 还是 `PreservationFailedFailClosed`，本次都不启动，后者不得覆盖故障证据。

- [ ] **Step 4: 运行启动 RED**

Run: `dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*ResidentLaunchCoordinatorTests"`

Expected: FAIL；启动协调器尚不存在。

- [ ] **Step 5: 实现 single-flight 自动与手动协议**

自动 `Start()` 在锁内只建立一次 `Completion = RunAutomaticBatchAsync(lifetimeToken)` 并立即返回；手动 `LaunchEnabledNowAsync` 使用独立 single-flight task 防止双击。两者共享逐项执行方法。自动计划中的删除、禁用、路径无效、已运行和无法可靠核实等跳过结果都必须原子标记 attempted；实际启动也必须在外部调用前先标记 attempted。这样任何终端决定后即使 DeskButler 崩溃，同一登录会话也不会重新处理该项。手动路径不读写会话批次，但仍读取最新设置、验证路径、枚举全部已知进程并去重。

```csharp
await sessionStore.SaveAsync(session with
{
    Plan = session.Plan.Select(item => item.LaunchIdentity == identity
        ? item with { Attempted = true }
        : item).ToArray()
}, cancellationToken).ConfigureAwait(false);
await runtime.StartAsync(application.LaunchPath, cancellationToken).ConfigureAwait(false);
```

诊断属性只允许 `displayName`、经既有脱敏器处理的路径、`result` 和 `exceptionType`；不得记录 LUID、命令行或异常 message。

在本任务完成最小生产接线：组合根用已经存在的 Windows policy/runtime、`WindowsLogonSessionIdentityProvider` 和 `JsonResidentLaunchSessionStore` 创建唯一协调器，注册进资源所有权栈，并在 `CompositionRoot.StartAsync` 的既有模块/托盘启动成功后调用非阻塞 `Start()`。MainViewModel 后续通过显式委托调用同一实例的 `LaunchEnabledNowAsync`；Task 11 只把这些直接构造重构为可注入 `ResidentPlatformServices` 并补强部分失败测试，不延迟功能接线。

- [ ] **Step 6: 写退出取消和 exactly-once 清理测试**

在 5 秒等待及 1 秒间隔分别调用 `DisposeAsync`，断言尚未开始的项目取消、已启动第三方进程不被终止；两个并发 Dispose 加入同一清理任务且每个资源释放一次。晚到异常必须被观察，不得形成未观察任务异常。

- [ ] **Step 7: 运行启动 GREEN 与 Release 聚焦测试**

Run:

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*ResidentLaunchCoordinatorTests"
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*CompositionRootStateTests"
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Release --no-restore -- --filter-class "*ResidentLaunchCoordinatorTests"
```

Expected: 延时、计划固定、LUID、先登记、续跑、去重、失败隔离、手动启动、取消和双重清理全部通过，无真实程序启动。

- [ ] **Step 8: 提交 Task 8**

```text
git add src/DeskButler.Desktop/Hosting/ResidentLaunchCoordinator.cs src/DeskButler.Desktop/Hosting/CompositionRoot.cs tests/DeskButler.Desktop.Tests/Hosting/ResidentLaunchCoordinatorTests.cs tests/DeskButler.Desktop.Tests/Hosting/CompositionRootStateTests.cs
git commit -m "feat: launch resident apps once per logon"
```

---

### Task 9: 常驻候选与设置 ViewModel

**Files:**
- Create: `src/DeskButler.Desktop/ViewModels/ResidentCandidateViewModel.cs`
- Create: `src/DeskButler.Desktop/ViewModels/ResidentApplicationViewModel.cs`
- Create: `src/DeskButler.Desktop/Hosting/IExecutablePicker.cs`
- Create: `src/DeskButler.Desktop/Hosting/WindowsExecutablePicker.cs`
- Create: `src/DeskButler.Desktop/Hosting/IExecutableIconProvider.cs`
- Create: `src/DeskButler.Desktop/Hosting/FallbackExecutableIconProvider.cs`
- Modify: `src/DeskButler.Desktop/Hosting/CompositionRoot.cs`
- Modify: `src/DeskButler.Desktop/ViewModels/MainViewModel.cs`
- Create: `tests/DeskButler.Desktop.Tests/ViewModels/ResidentCandidateViewModelTests.cs`
- Modify: `tests/DeskButler.Desktop.Tests/ViewModels/MainViewModelTests.cs`

**Interfaces:**
- Consumes: 手动保存/候选命令、常驻设置命令和 `ResidentLaunchCoordinator.LaunchEnabledNowAsync`。
- Produces: 可由 XAML 绑定的候选、常驻列表、总开关和命令状态；`ResidentCandidatesAvailable` 只由手动发现触发。

- [ ] **Step 1: 写 ViewModel 映射和命令 RED**

测试 `SaveNowAsync` 分别显示“现场已保存”“捕获已暂停，仍完成常驻查找”“现场未变化”“现场保存失败，仍完成常驻查找”或“常驻应用发现失败”；High 且入口明确默认选中，Low 默认不选，入口为空时确认不可执行。`PathReplacement` 显示旧/新路径、默认不选并发送同一 generation。

`ConfirmResidentCandidatesAsync` 成功后只清空同代候选并重新加载设置；过期 no-op 不清空新候选。`FindResidentCandidatesAsync` 不发送保存命令；`LaunchResidentsNowAsync` 只调用手动启动委托。

- [ ] **Step 2: 运行 ViewModel RED**

Run:

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*MainViewModelTests"
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*ResidentCandidateViewModelTests"
```

Expected: FAIL；resident 集合、命令和结果映射尚不存在。

- [ ] **Step 3: 实现候选和常驻条目 ViewModel**

```csharp
public ObservableCollection<ResidentCandidateViewModel> ResidentCandidates { get; } = [];
public ObservableCollection<ResidentApplicationViewModel> ResidentApplications { get; } = [];
public bool HasResidentCandidates => ResidentCandidates.Count > 0;
public bool ResidentApplicationsEnabled { get; private set; }
public AsyncCommand ConfirmResidentCandidatesCommand { get; }
public AsyncCommand DismissResidentCandidatesCommand { get; }
public AsyncCommand FindResidentCandidatesCommand { get; }
public AsyncCommand AddResidentApplicationCommand { get; }
public AsyncCommand LaunchResidentsNowCommand { get; }
```

条目启停、删除、上下移通过父级委托发送 Task 7 命令，不在属性 setter 写设置。`ResidentApplicationViewModel` 暴露 `PathStatusText` 和 `CanEnable`：路径缺失、拒绝或无法访问时禁用启用动作，但仍允许改路径和删除。`IExecutableIconProvider` 在单元测试由 fake 注入；生产组合根暂时注入不持有文件句柄的 `FallbackExecutableIconProvider`，具体 Windows 图标实现在下一任务替换，保证本任务提交可独立构建运行。

- [ ] **Step 4: 实现浏览、事件和状态刷新**

`WindowsExecutablePicker` 使用 `Microsoft.Win32.OpenFileDialog`，过滤器固定为 `应用程序 (*.exe)|*.exe`，返回绝对路径；最终策略仍由设置 handler 核验。组合根把该 picker、fallback icon provider 和 Task 8 的 `LaunchEnabledNowAsync` 委托传入 MainViewModel。`SaveNowAsync` 只有在手动结果包含非空当前代候选时触发 `ResidentCandidatesAvailable`，后台仓库 `SceneSaved` 事件刷新不得触发。

- [ ] **Step 5: 运行 ViewModel GREEN**

Run:

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*MainViewModelTests"
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*ResidentCandidateViewModelTests"
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Release --no-restore -- --filter-class "*MainViewModelTests"
```

Expected: 文案、默认选择、代次保护、浏览取消、列表 mutation 和手动事件边界测试通过。

- [ ] **Step 6: 提交 Task 9**

```text
git add src/DeskButler.Desktop/ViewModels src/DeskButler.Desktop/Hosting tests/DeskButler.Desktop.Tests/ViewModels
git commit -m "feat: add resident application view models"
```

---

### Task 10: WPF 视图、图标、托盘与候选聚焦

**Files:**
- Create: `src/DeskButler.Desktop/Hosting/WindowsExecutableIconProvider.cs`
- Modify: `src/DeskButler.Desktop/Hosting/CompositionRoot.cs`
- Modify: `src/DeskButler.Desktop/Views/MainWindow.xaml`
- Modify: `src/DeskButler.Desktop/Tray/TrayIconService.cs`
- Create: `tests/DeskButler.Desktop.Tests/Views/ResidentApplicationViewTests.cs`

**Interfaces:**
- Consumes: Task 9 的 bindable 状态、命令、`ResidentCandidatesAvailable` 和 `IExecutableIconProvider`。
- Produces: 首页确认区、设置页管理区、内存图标、托盘立即启动，以及仅手动发现触发的打开/聚焦行为。

- [ ] **Step 1: 写 XAML 与可访问性 RED**

静态 XAML 测试断言：首页确认区绑定候选复选框、名称、可信度、路径、浏览修正、确认和本次忽略；设置页存在总开关、查找、浏览添加、立即启动、每项启停/上下移/删除。所有按钮有中文 Content 或 `AutomationProperties.Name`，路径 TextBox 可键盘聚焦并横向滚动。

- [ ] **Step 2: 写图标所有权和聚焦 RED**

反复加载临时 fixture 图标后移动 exe，断言没有文件句柄泄漏；缺失/损坏 exe 返回内置图标。触发 `ResidentCandidatesAvailable` 断言主窗口打开并聚焦命名确认容器；普通 `SceneSaved` 和自动刷新不打开窗口。

- [ ] **Step 3: 运行视图 RED**

Run: `dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*ResidentApplicationViewTests"`

Expected: FAIL；XAML、Windows 图标提供器和聚焦接线尚不存在。

- [ ] **Step 4: 实现布局和图标**

设置页使用 `ScrollViewer`，确认区使用 `ItemsControl`。Low 显示“低可信，默认不添加”，空入口显示“请选择主程序 .exe”，替换候选显示“发现可能的新路径，需要你确认”。设置项不得显示命令行。

`WindowsExecutableIconProvider` 使用 `Icon.ExtractAssociatedIcon` 和 `Imaging.CreateBitmapSourceFromHIcon`，冻结 `BitmapSource` 后只 Dispose 所拥有的 `Icon`，不得重复 `DestroyIcon`。图标不进入 JSON。

组合根在本任务把 Task 9 的 fallback 替换为唯一 `WindowsExecutableIconProvider`；fallback 类型继续供缺失图标和隔离测试使用，不参与正常 exe 的图标提取。

- [ ] **Step 5: 接通托盘与手动候选聚焦**

`CompositionRoot` 订阅 ViewModel 事件后调用 `ShowMainWindow()`，再由 Dispatcher 聚焦具名确认容器；窗口已可见时只移动焦点。托盘增加“立即启动常驻应用”，执行 `LaunchResidentsNowCommand`；不增加持续守护开关。

- [ ] **Step 6: 运行视图 GREEN**

Run:

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*ResidentApplicationViewTests"
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Release --no-restore -- --filter-class "*ResidentApplicationViewTests"
```

Expected: XAML 绑定、键盘可访问性、图标释放、托盘命令和手动聚焦测试通过。

- [ ] **Step 7: 提交 Task 10**

```text
git add src/DeskButler.Desktop/Hosting src/DeskButler.Desktop/Views src/DeskButler.Desktop/Tray tests/DeskButler.Desktop.Tests/Views
git commit -m "feat: add resident application views"
```

---

### Task 11: 生产对象图、生命周期和调试冒烟接线

**Files:**
- Create: `src/DeskButler.Desktop/Hosting/ResidentPlatformServices.cs`
- Modify: `src/DeskButler.Desktop/Hosting/CompositionRoot.cs`
- Modify: `tests/DeskButler.Desktop.Tests/Hosting/CompositionRootStateTests.cs`
- Modify: `tests/DeskButler.Desktop.Tests/AppSmokeOptionsTests.cs`

**Interfaces:**
- Consumes: 全部生产实现和现有 `CompositionResourceOwner`/`CompositionStartupCoordinator`。
- Produces: 唯一生产实例、启动后 5 秒后台批次、退出可取消清理、Debug smoke 不启动任何第三方程序。

- [ ] **Step 1: 写对象图唯一性和禁用 smoke RED**

构造隔离 `AppDataPaths` 和 fake `ResidentPlatformServices` 的测试组合入口，断言：所有 resident handler 和候选协调器持有同一 `SettingsCoordinator`；只有一份 `ResidentLaunchCoordinator`；`CreateDebugAsync(... runSmoke ...)` 注入禁用 runtime，Debug smoke 不建立真实进程。

- [ ] **Step 2: 写部分启动失败和清理 RED**

让 resident 启动协调器在创建后、主模块启动后分别失败，断言 `CompositionResourceOwner.BuildAsync` 逆序释放已拥有资源；正常退出时启动任务在设置、日志和窗口释放前取消并等待完成，且第三方 runtime 没有 Stop/Kill 调用。

- [ ] **Step 3: 运行 Composition RED**

Run:

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*CompositionRootStateTests"
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*AppSmokeOptionsTests"
```

Expected: FAIL；生产对象图尚未拥有 resident 服务。

- [ ] **Step 4: 接入生产服务和命令**

新增仅供组合根和测试使用的不可变依赖包，避免 `CreateCoreAsync` 内部再次硬编码平台对象：

```csharp
internal sealed record ResidentPlatformServices(
    IResidentExecutablePolicy ExecutablePolicy,
    ILogonSessionIdentityProvider LogonIdentity,
    IResidentProcessRuntime ProcessRuntime,
    IResidentAppDiscovery Discovery,
    IResidentLaunchSessionStore LaunchSessionStore);
```

公开 `CreateAsync/CreateDebugAsync` 调用私有生产工厂 `CreateResidentPlatformServices(paths)`；增加 internal `CreateForTestsAsync(..., ResidentPlatformServices resident, ...)`，只对 `InternalsVisibleTo(DeskButler.Desktop.Tests)` 可见。禁止在构造器中使用 service locator 或可空平台依赖。

本任务把 Tasks 6–8 已经完成的直接 Windows 构造收口到该依赖包，不改变 Task 9–10 的 picker/icon UI 边界。生产创建顺序固定为：设置/诊断 → resident 平台依赖 → candidate coordinator → resident commands → resident launch coordinator → ViewModel/窗口/托盘。`JsonSettingsStore` 的正规化诊断 sink 和候选发现摘要都写入既有 `IDiagnosticLog`，只含分类、数量和脱敏路径；异步日志任务加入既有尽力清理边界，并按 Task 2 的内容指纹去重。启动协调器继续注册为拥有资源：

```csharp
var residentLaunch = ownership.Own(
    "resident launch",
    new ResidentLaunchCoordinator(...),
    static coordinator => coordinator.DisposeAsync());
```

`CompositionRoot.StartAsync` 在主模块、session events、desktop changes 和托盘对象均已成功后调用 `residentLaunch.Start()`；不等待完整 5 秒批次，因此不阻塞应用启动。协调器的 `Completion` 观察全部后台异常，`DisposeAsync` 取消并等待同一任务。

- [ ] **Step 5: 运行对象图 GREEN 和 Debug UI smoke**

Run:

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*CompositionRootStateTests"
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*AppSmokeOptionsTests"
dotnet build DeskButler.slnx -c Debug --no-restore
dotnet run --project src/DeskButler.Desktop/DeskButler.Desktop.csproj -c Debug --no-build -- --smoke-ui --data-root artifacts/smoke/resident-plan
```

Expected: 对象图测试通过；smoke exit 0 并写既有成功 marker；没有外部第三方进程启动，没有写真实用户数据根。

- [ ] **Step 6: 提交 Task 11**

```text
git add src/DeskButler.Desktop tests/DeskButler.Desktop.Tests
git commit -m "feat: wire resident application lifecycle"
```

---

### Task 12: 文档、卸载契约与完整发布验证

**Files:**
- Modify: `docs/privacy.md`
- Modify: `docs/user-guide.md`
- Modify: `docs/troubleshooting.md`
- Modify: `docs/compatibility.md`
- Modify: `README.md`
- Modify: `tests/manual/windows-10-checklist.md`
- Modify: `tests/manual/windows-11-checklist.md`
- Modify: `tests/manual/restart-checklist.md`
- Modify: `tests/DeskButler.Desktop.Tests/InstallerContractTests.cs`
- Modify: `tests/installer/verify-uninstall.ps1`

**Interfaces:**
- Consumes: 最终实际行为、既有交互/静默卸载语义和 `scripts/verify-release.cmd`。
- Produces: 用户可理解的功能边界、隐私清单、排错流程、Windows 10/11 验收步骤和发布证据。

- [ ] **Step 1: 写文档与卸载契约 RED**

扩展 `InstallerContractTests`，断言卸载脚本仍不含 `taskkill`、第三方 exe 名、第三方 Run/Task/Service 操作；普通卸载保留整个数据根，`/DELETEUSERDATA=1` 删除整个精确根，因此自动覆盖 `settings.json` 和 `resident-launch-session.json`。

扩展脚本 fixture：数据根内同时创建常驻设置 marker 和 `resident-launch-session.json`；默认静默卸载后两者仍在，删除数据卸载后两者均不存在。只在脚本既有“无真实安装/数据目录”安全门通过时执行。

- [ ] **Step 2: 更新隐私、用户指南和排错文档**

隐私文档明确列出：常驻总开关、显示名、`LaunchPath`、`KnownProcessPaths`、顺序、Authentication LUID 字符串、固定计划/attempted/completed、`.tmp` 和 `resident-launch-session.corrupt-*`；说明 LUID 是本地登录会话标识，不是账号 token。

用户指南写清“主动保存才发现”“高可信默认勾选、低可信默认不选”“自动每次登录一批”“主动退出不拉起”“立即启动例外”“设置修改下次登录自动生效”。排错文档给出漏检、helper 误识别、路径更新、权限、重复实例和损坏会话记录的具体界面操作。

- [ ] **Step 3: 更新 README 和人工清单**

README 的功能图加入候选确认和常驻列表；测试总数必须从本任务最终 Release 输出填写，不预先猜测。Windows 10 清单加入 QQ、微信、富途缩托盘发现、确认、重启顺序、`KnownProcessPaths` 去重、退出微信后仅重启 DeskButler不拉起、立即启动可手动拉起、卸载不终止三者。

Windows 11 清单复制同一行为但保留独立证据；restart checklist 强调任何真实重启前必须即时征得用户确认。

- [ ] **Step 4: 运行各项目 Debug 测试**

Run:

```text
dotnet test tests/DeskButler.Core.Tests/DeskButler.Core.Tests.csproj -c Debug --no-restore
dotnet test tests/DeskButler.Persistence.Tests/DeskButler.Persistence.Tests.csproj -c Debug --no-restore
dotnet test tests/DeskButler.Infrastructure.Windows.Tests/DeskButler.Infrastructure.Windows.Tests.csproj -c Debug --no-restore
dotnet test tests/DeskButler.Modules.WorkspaceRecovery.Tests/DeskButler.Modules.WorkspaceRecovery.Tests.csproj -c Debug --no-restore
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore
```

Expected: 所有非显式外部门禁测试通过，0 fail；没有新增 skip。

- [ ] **Step 5: 运行完整 Release 验证**

Run:

```text
scripts\verify-release.cmd
git diff --check
git status --short
git remote -v
```

Expected: 脚本 exit 0；Release build 0 warning/0 error；所有非外部门禁测试通过；publish、Inno Setup、安装器大小和 SHA-256 成功输出；只存在预期源码/文档修改；`git remote -v` 无输出。

- [ ] **Step 6: 扫描敏感数据和意外持久化**

Run:

```text
git ls-files | rg "(^|/)(artifacts|bin|obj|deskbutler\.db|settings\.json|resident-launch-session\.json|diagnostics)(/|$)"
git grep -n -I -E "sk-[A-Za-z0-9_-]{20,}|BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY|QQ\.exe|WeChat\.exe|Futu.*\.exe"
```

Expected: 不跟踪构建产物、真实设置、会话文件、数据库或诊断数据；产品 exe 名只允许出现在文档/测试 fixture 的预期断言，不得硬编码为生产发现白名单。

- [ ] **Step 7: 提交 Task 12**

先把 README 测试数量、安装器大小和 SHA-256 更新为 Step 5 的实际输出，再提交：

```text
git add docs README.md tests/manual tests/DeskButler.Desktop.Tests/InstallerContractTests.cs tests/installer/verify-uninstall.ps1
git commit -m "docs: document resident application workflow"
```

- [ ] **Step 8: 交付专用虚拟机验收，不擅自执行**

把 `artifacts/installer/DeskButler-Setup-0.1.0.exe`、实际大小和 SHA-256 交给用户。由用户在 Windows 10 专用虚拟机按清单执行真实安装、QQ/微信/富途验证和重启；每次重启前必须再次取得即时确认。Windows 11、30 分钟资源测试和签名继续单独报告为未关闭门禁，直到有真实证据。
