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
    Task<ResidentRunningPathsResult> GetRunningExecutablePathsAsync(CancellationToken cancellationToken);
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

`ResidentRunningPathsResult` 同时包含 `CanReliablyDetermine` 和大小写不敏感路径集合；无法完整枚举时不能用空集合冒充“没有运行”。`ResidentExecutableValidation`、`ResidentExecutableRejection` 和发现诊断枚举也在 `ResidentAppContracts.cs` 声明，Infrastructure 只实现这些契约。

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

`FromSettings` 对每个稳定 DTO 调用 `JsonSerializer.SerializeToElement`；`ToSettings` 在逐项 try/catch 中调用 `element.Deserialize<ResidentApplicationDocument>`，随后一次调用正规化器。`JsonSettingsStore` 构造函数新增可选 `Action<ResidentNormalizationDiagnostic>`；对每条隔离/冲突诊断调用一次，诊断 sink 自身失败不得改变加载结果，且参数不包含原始异常 message。

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

测试覆盖不存在返回 null、原子保存往返、覆盖旧 LUID、临时文件 finally 清理、损坏文件改名为 `resident-launch-session.corrupt-<timestamp>-<guid>.json`，以及 `RecoverCorruptAsync(currentLuid)` 写入当前 LUID 的已完成空计划。

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
    Task RecoverCorruptAsync(string currentLogonSessionId, CancellationToken cancellationToken);
}
```

`RecoverCorruptAsync` 先保留故障文件，再保存 `Completed=true, Plan=[]`；保留失败不得删除原文件。

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

在测试专属临时目录创建真实 `.exe` fixture，使用可注入的磁盘类型查询器和 manifest 提升检查器验证本地固定盘、`asInvoker` 程序通过；以下逐项拒绝并返回稳定原因枚举：相对路径、目录、非 exe、UNC、`DriveType.Network`、`Removable`、Windows 目录、`%TEMP%`、`%LOCALAPPDATA%\Temp`、安装器缓存、文件不存在、访问失败、`requireAdministrator`/`highestAvailable` manifest、策略判断异常。

```csharp
var result = policy.Validate(fixtureExe);
Assert.True(result.IsAllowed);
Assert.Equal(Path.GetFullPath(fixtureExe), result.NormalizedPath);
```

- [ ] **Step 2: 运行路径 RED 并实现最小策略**

Run: `dotnet test tests/DeskButler.Infrastructure.Windows.Tests/DeskButler.Infrastructure.Windows.Tests.csproj -c Debug --no-restore -- --filter-class "*WindowsResidentExecutablePolicyTests"`

Expected: FAIL；策略类型尚不存在。

实现 `ResidentExecutableValidation(bool IsAllowed, string? NormalizedPath, ResidentExecutableRejection Reason)`。目录边界比较必须先 `Path.GetFullPath` 并补目录分隔符，避免把 `C:\WindowsOld` 错判为 `C:\Windows` 子项。生产 `WindowsExecutableElevationInspector` 使用 `LoadLibraryEx(LOAD_LIBRARY_AS_DATAFILE)` 读取 RT_MANIFEST 并解析 `requestedExecutionLevel`；资源缺失按 `asInvoker`，解析/访问失败按不可靠并拒绝。测试通过 fake inspector 覆盖三种 level，另用专属 fixture manifest 做一条集成断言。

- [ ] **Step 3: 写并实现 Authentication LUID 测试**

使用 `OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY)`、`GetTokenInformation(TokenStatistics)` 读取 `TOKEN_STATISTICS.AuthenticationId`，格式固定为高低 32 位各 8 位大写十六进制：`XXXXXXXX-XXXXXXXX`。测试断言同一进程重复读取相同、非空且符合 `^[0-9A-F]{8}-[0-9A-F]{8}$`；句柄使用 `SafeAccessTokenHandle`。

Run: `dotnet test tests/DeskButler.Infrastructure.Windows.Tests/DeskButler.Infrastructure.Windows.Tests.csproj -c Debug --no-restore -- --filter-class "*WindowsLogonSessionIdentityProviderTests"`

Expected: 首次 RED 后 GREEN；不把用户名、SID 或 token 内容写入结果。

- [ ] **Step 4: 写运行识别与安全启动 RED**

启动 `DeskButler.ResidentFixture.exe --wait` 仅供测试持有进程，断言运行路径枚举包含 fixture；已退出进程、其他 Session 和访问拒绝单项不使整体失败。`StartAsync` 只接收已验证绝对路径并使用：

```csharp
new ProcessStartInfo(normalizedPath)
{
    UseShellExecute = true,
    WorkingDirectory = Path.GetDirectoryName(normalizedPath)!
};
```

测试记录 fixture 启动 marker，断言没有附带参数、没有 `Verb="runas"`；不得启动 QQ、微信或富途。

- [ ] **Step 5: 运行 Infrastructure 聚焦回归**

Run:

```text
dotnet test tests/DeskButler.Infrastructure.Windows.Tests/DeskButler.Infrastructure.Windows.Tests.csproj -c Debug --no-restore -- --filter-namespace "*ResidentApps*"
dotnet test tests/DeskButler.Infrastructure.Windows.Tests/DeskButler.Infrastructure.Windows.Tests.csproj -c Debug --no-restore
```

Expected: 路径、LUID、运行枚举与 fixture 启动测试通过；现有窗口、恢复、启动注册和会话测试无失败。

- [ ] **Step 6: 提交 Task 3**

```text
git add src/DeskButler.Infrastructure.Windows tests/DeskButler.Infrastructure.Windows.Tests
git commit -m "feat: add safe Windows resident process boundaries"
```

---

### Task 4: 进程候选发现、产品分组与可信度

**Files:**
- Create: `src/DeskButler.Infrastructure.Windows/ResidentApps/ResidentProcessObservation.cs`
- Create: `src/DeskButler.Infrastructure.Windows/ResidentApps/IResidentProcessSnapshotSource.cs`
- Create: `src/DeskButler.Infrastructure.Windows/ResidentApps/WindowsResidentProcessSnapshotSource.cs`
- Create: `src/DeskButler.Infrastructure.Windows/ResidentApps/InstalledApplicationCatalog.cs`
- Create: `src/DeskButler.Infrastructure.Windows/ResidentApps/WindowsResidentAppDiscovery.cs`
- Modify: `src/DeskButler.Infrastructure.Windows/Native/NativeMethods.cs`
- Create: `tests/DeskButler.Infrastructure.Windows.Tests/ResidentApps/WindowsResidentAppDiscoveryTests.cs`
- Create: `tests/DeskButler.Infrastructure.Windows.Tests/ResidentApps/WindowsResidentProcessSnapshotSourceTests.cs`

**Interfaces:**
- Consumes: `IResidentAppDiscovery`、`IResidentExecutablePolicy`、普通窗口 exe 集合和现有 `ResidentApplication` 列表。
- Produces: 安全筛选、同产品归组、主入口选择及高低可信候选；单进程失败只形成分类诊断。

- [ ] **Step 1: 写纯快照发现 RED**

通过 fake `IResidentProcessSnapshotSource` 构造进程观察，不依赖机器实际安装的软件。覆盖当前会话/Session 0、DeskButler 自身、系统/临时/缓存/不可访问路径、已有普通窗口、已有常驻项、同 exe 多实例和不同产品分离。

```csharp
var candidates = await discovery.DiscoverAsync(
    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Apps\Editor\Editor.exe" },
    existingResidents,
    CancellationToken.None);

Assert.DoesNotContain(candidates.Candidates,
    item => item.KnownProcessPaths.Contains(@"C:\Apps\Editor\Editor.exe"));
```

- [ ] **Step 2: 写分组、入口与可信度 RED**

建立 QQ 风格主进程、renderer、updater、crash reporter 四个观察：相同产品/厂商和产品根目录；隐藏/工具型/DWM cloaked 窗口属于主进程；安装目录 catalog 给出 DisplayIcon。断言只产生一个候选、主 exe 优先、四条产品专属路径折叠显示，但通用 updater/crash reporter 不进入 `KnownProcessPaths`。

再覆盖：无窗口且无稳定产品信息为 Low；隐藏窗口加稳定安装信息为 High；入口无法可靠选择时 `LaunchPath=null` 且默认不选；虚拟机辅助工具永远 Low。

为路径更新补一组测试：现有条目的 `LaunchPath` 已不存在，当前观察到的产品显示名一致，且新 exe 与旧路径拥有同一稳定安装根时，产生 `Kind=PathReplacement`、`ReplacesLaunchPath=旧入口` 的 Low 候选；根目录或产品名不匹配时不建议替换。用户未确认前设置保持原路径。

- [ ] **Step 3: 运行发现 RED**

Run: `dotnet test tests/DeskButler.Infrastructure.Windows.Tests/DeskButler.Infrastructure.Windows.Tests.csproj -c Debug --no-restore -- --filter-class "*WindowsResidentAppDiscoveryTests"`

Expected: FAIL；观察、catalog 和发现器尚不存在。

- [ ] **Step 4: 实现公开信息采集边界**

`WindowsResidentProcessSnapshotSource` 只读取 PID、SessionId、主模块 exe、`FileVersionInfo.ProductName/CompanyName/FileDescription` 和该 PID 拥有的顶层窗口分类。使用 `EnumWindows`、`GetWindowThreadProcessId`、`IsWindowVisible`、`GetWindow(hwnd, GW_OWNER)`、`GetWindowLongPtr(GWL_EXSTYLE)`、`DwmGetWindowAttribute(DWMWA_CLOAKED)`；不读取标题正文或命令行。

`InstalledApplicationCatalog` 只读 HKCU/HKLM 的 32/64 位 Uninstall 视图，提取 `DisplayName`、`Publisher`、`InstallLocation`、可解析为本地 exe 的 `DisplayIcon`。逐键访问拒绝只跳过该键。

- [ ] **Step 5: 实现稳定分组和入口排序**

分组键优先使用正规化安装根 + 产品名 + 厂商名；缺字段时退回 exe 路径。入口评分固定为：隐藏/工具/cloaked 窗口所有者 300，catalog DisplayIcon 200，产品根目录非 helper 主程序 100；同分按正规化路径排序。最高分不唯一或最高分小于 100 时保持 `LaunchPath=null`。

helper 名称匹配只使用文件名 token：`helper`、`updater`、`update`、`crash`、`reporter`、`renderer`；它们不得单独成为高可信入口。候选 `CandidateId` 对排序后的产品键和路径做 SHA-256，不使用 PID。

- [ ] **Step 6: 写真实快照最小安全测试并运行 GREEN**

真实测试只观察当前 `DeskButler.ResidentFixture`，断言可返回其路径或在访问受限时返回分类诊断，不断言本机其他进程数量和名称。

Run:

```text
dotnet test tests/DeskButler.Infrastructure.Windows.Tests/DeskButler.Infrastructure.Windows.Tests.csproj -c Debug --no-restore -- --filter-class "*WindowsResidentAppDiscoveryTests"
dotnet test tests/DeskButler.Infrastructure.Windows.Tests/DeskButler.Infrastructure.Windows.Tests.csproj -c Debug --no-restore -- --filter-class "*WindowsResidentProcessSnapshotSourceTests"
dotnet test tests/DeskButler.Infrastructure.Windows.Tests/DeskButler.Infrastructure.Windows.Tests.csproj -c Release --no-restore -- --filter-namespace "*ResidentApps*"
```

Expected: 所有筛选、归组、评分、可信度、竞态进程退出和访问拒绝测试通过；无真实第三方应用依赖。

- [ ] **Step 7: 提交 Task 4**

```text
git add src/DeskButler.Infrastructure.Windows/ResidentApps src/DeskButler.Infrastructure.Windows/Native tests/DeskButler.Infrastructure.Windows.Tests
git commit -m "feat: discover resident application candidates"
```

---

### Task 5: 手动保存结果、候选代次和确认事务

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
- Consumes: Task 4 的 `IResidentAppDiscovery`、共享 `SettingsCoordinator`、现有手动和自动捕获入口。
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

用第一次发现阻塞、第二次立即完成的 fake，断言旧结果晚到不能替换新代次。确认命令在取得 `SettingsCoordinator` 门后再次校验 generation 和候选 identity；旧确认、旧忽略和旧路径修正均 no-op。保存失败保留同代候选，成功确认才清空。本次忽略不写设置或永久黑名单；下一次主动保存仍可重新发现同一候选。

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

确认时对选中项重新执行路径策略，并从协调器当前候选复制 `KnownProcessPaths/DisplayName/Kind/ReplacesLaunchPath`，不能信任 UI 回传这些字段。`NewApplication` 追加正规化条目；`PathReplacement` 只在旧入口仍与候选记录一致时原子替换路径。所有设置写入通过共享 `SettingsCoordinator.UpdateAsync`。

- [ ] **Step 6: 把手动保存和独立查找接入命令总线**

`SaveSceneNowCommand` 返回：

```csharp
public sealed record ManualSaveResult(
    CaptureOutcome Capture,
    ResidentDiscoveryBatch Discovery);
```

处理器始终先调用 `SettingsAwareWindowInventory.CaptureForManualAsync`，再调用 `CaptureCoordinator.SaveObservedAsync("manual", ...)`；无论 `SnapshotSaved` 为何都调用一次候选发现。手动观察或保存出现可恢复异常时写诊断并构造 `CaptureSkipReason.Failed`、空窗口路径，随后仍执行发现；发现器系统级失败则构造 `DiscoveryFailed=true`，不得把已经保存的现场回滚或改报未保存。新增 `FindResidentCandidatesCommand` 直接以空普通窗口集合发现；`ConfirmResidentCandidatesCommand` 和 `DismissResidentCandidatesCommand` 路由到同一协调器。自动快照、session-ending 和安全检查点不引用 `IResidentAppDiscovery`。

- [ ] **Step 7: 运行 GREEN、并发设置回归和提交**

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

### Task 6: 常驻列表设置命令与安全编辑

**Files:**
- Modify: `src/DeskButler.Desktop/Hosting/ResidentAppCommands.cs`
- Modify: `src/DeskButler.Desktop/Hosting/ResidentCandidateCoordinator.cs`
- Create: `tests/DeskButler.Desktop.Tests/Hosting/ResidentAppCommandTests.cs`

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

- [ ] **Step 5: 提交 Task 6**

```text
git add src/DeskButler.Desktop/Hosting tests/DeskButler.Desktop.Tests/Hosting
git commit -m "feat: manage resident application settings"
```

---

### Task 7: 每登录会话一次的固定启动批次

**Files:**
- Create: `src/DeskButler.Desktop/Hosting/ResidentLaunchCoordinator.cs`
- Create: `tests/DeskButler.Desktop.Tests/Hosting/ResidentLaunchCoordinatorTests.cs`

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

覆盖任一 `KnownProcessPaths` 已运行则跳过；运行状态无法可靠枚举则跳过；策略拒绝/文件缺失/单项启动失败继续下一项；任意两个外部启动尝试的开始时刻至少间隔 1 秒。会话 JSON 损坏时调用 `RecoverCorruptAsync(currentLuid)`，本次不启动，下一次同 LUID 也不启动。

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

- [ ] **Step 6: 写退出取消和 exactly-once 清理测试**

在 5 秒等待及 1 秒间隔分别调用 `DisposeAsync`，断言尚未开始的项目取消、已启动第三方进程不被终止；两个并发 Dispose 加入同一清理任务且每个资源释放一次。晚到异常必须被观察，不得形成未观察任务异常。

- [ ] **Step 7: 运行启动 GREEN 与 Release 聚焦测试**

Run:

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*ResidentLaunchCoordinatorTests"
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Release --no-restore -- --filter-class "*ResidentLaunchCoordinatorTests"
```

Expected: 延时、计划固定、LUID、先登记、续跑、去重、失败隔离、手动启动、取消和双重清理全部通过，无真实程序启动。

- [ ] **Step 8: 提交 Task 7**

```text
git add src/DeskButler.Desktop/Hosting/ResidentLaunchCoordinator.cs tests/DeskButler.Desktop.Tests/Hosting/ResidentLaunchCoordinatorTests.cs
git commit -m "feat: launch resident apps once per logon"
```

---

### Task 8: WPF 候选确认与常驻列表管理界面

**Files:**
- Create: `src/DeskButler.Desktop/ViewModels/ResidentCandidateViewModel.cs`
- Create: `src/DeskButler.Desktop/ViewModels/ResidentApplicationViewModel.cs`
- Create: `src/DeskButler.Desktop/Hosting/IExecutablePicker.cs`
- Create: `src/DeskButler.Desktop/Hosting/WindowsExecutablePicker.cs`
- Create: `src/DeskButler.Desktop/Hosting/IExecutableIconProvider.cs`
- Create: `src/DeskButler.Desktop/Hosting/WindowsExecutableIconProvider.cs`
- Modify: `src/DeskButler.Desktop/ViewModels/MainViewModel.cs`
- Modify: `src/DeskButler.Desktop/Views/MainWindow.xaml`
- Modify: `src/DeskButler.Desktop/Tray/TrayIconService.cs`
- Create: `tests/DeskButler.Desktop.Tests/ViewModels/ResidentCandidateViewModelTests.cs`
- Modify: `tests/DeskButler.Desktop.Tests/ViewModels/MainViewModelTests.cs`
- Create: `tests/DeskButler.Desktop.Tests/Views/ResidentApplicationViewTests.cs`

**Interfaces:**
- Consumes: Tasks 5–7 的命令和 `ResidentLaunchCoordinator.LaunchEnabledNowAsync`。
- Produces: 首页候选确认区、设置页总开关和条目管理、浏览添加、运行中查找、立即启动；托盘保存发现候选时打开主窗并聚焦确认区；图标只从当前 exe 派生到内存。

- [ ] **Step 1: 写 ViewModel 映射和命令 RED**

测试 `SaveNowAsync` 根据 `ManualSaveResult` 显示“现场已保存”“捕获已暂停，仍完成常驻查找”“现场未变化”“现场保存失败，仍完成常驻查找”或“常驻应用发现失败”；高可信且入口明确默认选中，Low 默认不选，入口为空时确认命令不可执行。`PathReplacement` 显示旧路径和建议新路径，默认不选，确认后发送同一 generation。

`ConfirmResidentCandidatesAsync` 捕获当前 generation，成功后清空同代候选并重新加载设置；过期 no-op 不清空新候选。`FindResidentCandidatesAsync` 不发送 `SaveSceneNowCommand`。`LaunchResidentsNowAsync` 只调用手动启动委托。

- [ ] **Step 2: 运行 ViewModel RED**

Run:

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*MainViewModelTests"
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*ResidentCandidateViewModelTests"
```

Expected: FAIL；集合、命令和结果映射尚不存在。

- [ ] **Step 3: 实现候选和常驻条目 ViewModel**

`MainViewModel` 新增：

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

条目 ViewModel 的启停、删除、上移、下移命令通过父级委托发送 Task 6 命令；不要在属性 setter 中隐式写设置，避免 WPF 重绑定造成递归命令。

`ResidentApplicationViewModel` 暴露 `PathStatusText` 和 `CanEnable`：文件存在且策略允许时为空/true，缺失、拒绝或无法访问时显示固定中文原因并禁用“启用”动作，但仍允许改路径和删除。

- [ ] **Step 4: 写 XAML 结构和可访问性 RED**

静态 XAML 测试断言：首页确认区绑定 `HasResidentCandidates`、候选复选框、名称、可信度、路径、浏览修正、确认和本次忽略；设置页存在总开关、查找、浏览添加、立即启动、每项启停/上下移/删除。所有按钮有中文 Content 或 `AutomationProperties.Name`，路径 TextBox 可键盘聚焦并允许横向滚动。

- [ ] **Step 5: 实现首页和设置页布局**

把设置页 `StackPanel` 改为 `ScrollViewer` 包裹的纵向布局，避免列表增长撑破窗口。确认区使用 `ItemsControl`，Low 候选显示“低可信，默认不添加”；路径为空显示“请选择主程序 .exe”；`PathReplacement` 显示“发现可能的新路径，需要你确认”。设置项不得显示或编辑命令行。

`WindowsExecutablePicker` 使用 `Microsoft.Win32.OpenFileDialog`，过滤器固定为 `应用程序 (*.exe)|*.exe`，返回用户选择的绝对路径；最终是否允许仍由 Task 6 的路径策略决定。

`WindowsExecutableIconProvider` 使用 `Icon.ExtractAssociatedIcon` 和 `Imaging.CreateBitmapSourceFromHIcon`，立即复制并冻结 WPF `BitmapSource`，随后只 Dispose 所拥有的 `Icon`（不得再对同一句柄重复调用 `DestroyIcon`）；缺失或提取失败返回单一内置应用图标。测试反复加载 fixture 图标后能够移动 exe，证明没有文件句柄泄漏。图标只存在于 ViewModel，不进入 `settings.json`。

- [ ] **Step 6: 接通托盘打开与聚焦**

`SaveNowAsync` 返回新候选时触发 `ResidentCandidatesAvailable` 事件。`CompositionRoot` 订阅后调用 `ShowMainWindow()`，再用 Dispatcher 聚焦首页确认区；主窗口保存本来可见时只移动焦点。后台自动保存不触发该事件。

托盘增加“立即启动常驻应用”，直接执行 `LaunchResidentsNowCommand`；不增加“持续守护”开关。

- [ ] **Step 7: 运行 UI GREEN 与回归**

Run:

```text
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*MainViewModelTests"
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*ResidentCandidateViewModelTests"
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Debug --no-restore -- --filter-class "*ResidentApplicationViewTests"
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Release --no-restore -- --filter-class "*MainViewModelTests"
dotnet test tests/DeskButler.Desktop.Tests/DeskButler.Desktop.Tests.csproj -c Release --no-restore -- --filter-class "*ResidentApplicationViewTests"
```

Expected: 状态文案、默认选择、代次保护、管理命令、浏览取消、XAML 绑定和键盘可访问性测试通过。

- [ ] **Step 8: 提交 Task 8**

```text
git add src/DeskButler.Desktop/ViewModels src/DeskButler.Desktop/Views src/DeskButler.Desktop/Tray src/DeskButler.Desktop/Hosting tests/DeskButler.Desktop.Tests
git commit -m "feat: add resident application management UI"
```

---

### Task 9: 生产对象图、生命周期和调试冒烟接线

**Files:**
- Modify: `src/DeskButler.Desktop/Hosting/CompositionRoot.cs`
- Modify: `tests/DeskButler.Desktop.Tests/Hosting/CompositionRootStateTests.cs`
- Modify: `tests/DeskButler.Desktop.Tests/AppSmokeOptionsTests.cs`

**Interfaces:**
- Consumes: 全部生产实现和现有 `CompositionResourceOwner`/`CompositionStartupCoordinator`。
- Produces: 唯一生产实例、启动后 5 秒后台批次、退出可取消清理、Debug smoke 不启动任何第三方程序。

- [ ] **Step 1: 写对象图唯一性和禁用 smoke RED**

构造隔离 `AppDataPaths` 和 fake 平台边界的测试组合入口，断言：所有 resident handler 和候选协调器持有同一 `SettingsCoordinator`；只有一份 `ResidentLaunchCoordinator`；`CreateDebugAsync(... runSmoke ...)` 注入禁用 runtime，Debug smoke 不建立真实进程。

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

生产创建顺序固定为：设置/诊断 → session store → Windows policy/LUID/runtime/discovery → candidate coordinator → resident commands → ViewModel/窗口/托盘 → resident launch coordinator。`JsonSettingsStore` 的正规化诊断 sink 和候选发现摘要都写入既有 `IDiagnosticLog`，只含分类、数量和脱敏路径；异步日志任务加入既有尽力清理边界。把启动协调器注册为拥有资源：

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

- [ ] **Step 6: 提交 Task 9**

```text
git add src/DeskButler.Desktop tests/DeskButler.Desktop.Tests
git commit -m "feat: wire resident application lifecycle"
```

---

### Task 10: 文档、卸载契约与完整发布验证

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

- [ ] **Step 7: 提交 Task 10**

先把 README 测试数量、安装器大小和 SHA-256 更新为 Step 5 的实际输出，再提交：

```text
git add docs README.md tests/manual tests/DeskButler.Desktop.Tests/InstallerContractTests.cs tests/installer/verify-uninstall.ps1
git commit -m "docs: document resident application workflow"
```

- [ ] **Step 8: 交付专用虚拟机验收，不擅自执行**

把 `artifacts/installer/DeskButler-Setup-0.1.0.exe`、实际大小和 SHA-256 交给用户。由用户在 Windows 10 专用虚拟机按清单执行真实安装、QQ/微信/富途验证和重启；每次重启前必须再次取得即时确认。Windows 11、30 分钟资源测试和签名继续单独报告为未关闭门禁，直到有真实证据。
