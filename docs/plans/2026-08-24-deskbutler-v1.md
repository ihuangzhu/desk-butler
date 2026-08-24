# DeskButler V1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 构建一个可安装、托盘常驻、完全本地运行的 Windows 10/11 x64 管家，自动保存最近 3 次普通桌面现场，并由用户手动、安全地恢复程序、资源管理器目录和窗口布局。

**Architecture:** 使用 C#、.NET 10、WPF 和隔离的 Win32 适配层。业务规则通过平台无关接口测试；Windows 枚举和定位、SQLite、托盘界面及 Inno Setup 安装器分别隔离，最终由桌面宿主组合。

**Tech Stack:** C# 14、.NET 10、WPF、Win32/COM、Microsoft.Data.Sqlite 10.0.11、xunit.v3 4.0.0、Inno Setup 7.1.0、Git。

**Spec:** `docs/superpowers/specs/2026-08-24-deskbutler-design.md`

## Global Constraints

- 项目根目录固定为 `D:\Projects\Php\client_dev\WebService\Html\DeskButler`。
- DeskButler 根目录必须拥有独立 `.git`；所有提交前运行 `git rev-parse --show-toplevel`，结果必须正好是 DeskButler 根目录。
- 不向外层 `client_dev` 仓库提交，不添加未确认的 remote，不推送公开仓库。
- 目标框架为 `net10.0-windows10.0.17763.0`，发布运行时为 `win-x64`，首版不支持 ARM64。
- 默认普通用户权限运行，不安装服务或驱动，不注入其他进程，不提供任意命令执行。
- 首版仅实现基础平台和 A 级现场恢复；AI、远程控制、动画、语音和 B 级恢复不得进入实现。
- 默认只捕获普通可见主窗口；不记录键盘、屏幕、文档内容、剪贴板或完整命令行。
- 自动快照保留 3 份；窗口变化静止 10 秒保存，持续变化满 60 秒强制检查点。
- 登录恢复卡片 15 秒后收起，但绝不自动执行恢复。
- 单项恢复默认超时 30 秒，同次不重试；连续失败 3 次后默认取消勾选。
- 所有新增或修改方法必须有中文方法级注释，重要状态字段和非显然逻辑必须写中文意图说明。
- 每个行为先写失败测试，再写最小实现；不得删除或弱化失败测试。
- 每个任务完成后只在 DeskButler 私有仓库创建一次聚焦提交。

---

## Planned File Structure

```text
DeskButler/
├── .git/
├── .gitignore
├── Directory.Build.props
├── Directory.Packages.props
├── DeskButler.slnx
├── README.md
├── docs/
│   ├── design/2026-08-24-deskbutler-design.md
│   ├── plans/2026-08-24-deskbutler-v1.md
│   ├── user-guide.md
│   └── compatibility.md
├── installer/
│   ├── DeskButler.iss
│   └── build-installer.cmd
├── src/
│   ├── DeskButler.Core/
│   │   ├── Scenes/SceneSnapshot.cs
│   │   ├── Scenes/SceneItem.cs
│   │   ├── Scenes/WindowBounds.cs
│   │   ├── Scenes/WindowState.cs
│   │   ├── Scenes/MonitorIdentity.cs
│   │   ├── Capture/WindowCandidate.cs
│   │   ├── Capture/IWindowInventory.cs
│   │   ├── Diagnostics/FailureHistory.cs
│   │   ├── Time/IClock.cs
│   │   ├── Persistence/ISceneRepository.cs
│   │   ├── Restore/IRestorePlanner.cs
│   │   ├── Restore/RestorePlan.cs
│   │   ├── Restore/RestoreResult.cs
│   │   └── Settings/ButlerSettings.cs
│   ├── DeskButler.Application/
│   │   ├── Commands/ICommandBus.cs
│   │   ├── Events/IEventBus.cs
│   │   ├── Modules/IModule.cs
│   │   └── Hosting/ModuleHost.cs
│   ├── DeskButler.Persistence/
│   │   ├── Sqlite/SqliteSceneRepository.cs
│   │   ├── Sqlite/DatabaseMigrator.cs
│   │   ├── Json/JsonSettingsStore.cs
│   │   └── Paths/AppDataPaths.cs
│   ├── DeskButler.Infrastructure.Windows/
│   │   ├── Native/NativeMethods.cs
│   │   ├── Windows/Win32WindowInventory.cs
│   │   ├── Windows/ExplorerWindowReader.cs
│   │   ├── Windows/MonitorCatalog.cs
│   │   ├── Restore/WindowsAppLauncher.cs
│   │   ├── Restore/WindowsWindowPositioner.cs
│   │   ├── Session/WindowsSessionEvents.cs
│   │   └── Startup/RegistryStartupRegistration.cs
│   ├── DeskButler.Modules.WorkspaceRecovery/
│   │   ├── Capture/CaptureCoordinator.cs
│   │   ├── Capture/SnapshotScheduler.cs
│   │   ├── Capture/SceneFilter.cs
│   │   ├── Restore/RestorePlanner.cs
│   │   ├── Restore/RestoreExecutor.cs
│   │   └── WorkspaceRecoveryModule.cs
│   └── DeskButler.Desktop/
│       ├── App.xaml
│       ├── App.xaml.cs
│       ├── Hosting/CompositionRoot.cs
│       ├── Tray/TrayIconService.cs
│       ├── Views/MainWindow.xaml
│       ├── Views/RecoveryCardWindow.xaml
│       ├── ViewModels/MainViewModel.cs
│       ├── ViewModels/RecoveryCardViewModel.cs
│       └── Diagnostics/CrashSentinel.cs
└── tests/
    ├── DeskButler.Core.Tests/
    ├── DeskButler.Application.Tests/
    ├── DeskButler.Persistence.Tests/
    ├── DeskButler.Infrastructure.Windows.Tests/
    ├── DeskButler.Modules.WorkspaceRecovery.Tests/
    └── DeskButler.Desktop.Tests/
```

文件按职责拆分；不得把所有 P/Invoke、恢复逻辑或 ViewModel 合并成单个大文件。

---

### Task 1: 独立仓库、解决方案骨架与依赖锁定

**Files:**
- Create: `DeskButler/.git/`
- Create: `DeskButler/.gitignore`
- Create: `DeskButler/Directory.Build.props`
- Create: `DeskButler/Directory.Packages.props`
- Create: `DeskButler/DeskButler.slnx`
- Create: `DeskButler/README.md`
- Create: all `src/*/*.csproj` and `tests/*/*.csproj` listed above
- Copy: approved spec and this plan into `DeskButler/docs/`

**Interfaces:**
- Consumes: 已批准的设计规格与本实施计划。
- Produces: 可恢复、可测试、可发布的独立 .NET 解决方案和私有 Git 边界。

- [ ] **Step 1: 验证工具链和外层仓库状态**

Run:

```powershell
dotnet --info
git -C D:\Projects\Php\client_dev rev-parse --show-toplevel
```

Expected: .NET SDK 列表包含 10.x；外层仓库根为 `D:/Projects/Php/client_dev`。若缺少 .NET 10 SDK，停止本任务并安装官方 .NET 10 SDK 后重新验证。

- [ ] **Step 2: 创建项目目录并初始化独立 Git**

Run:

```powershell
New-Item -ItemType Directory -Path 'D:\Projects\Php\client_dev\WebService\Html\DeskButler'
git init -b main 'D:\Projects\Php\client_dev\WebService\Html\DeskButler'
git -C 'D:\Projects\Php\client_dev\WebService\Html\DeskButler' rev-parse --show-toplevel
git -C 'D:\Projects\Php\client_dev\WebService\Html\DeskButler' remote -v
```

Expected: top-level 精确指向 DeskButler；`remote -v` 无输出。

- [ ] **Step 3: 生成解决方案和项目**

Run from DeskButler root:

```powershell
dotnet new sln --name DeskButler --format slnx
dotnet new install xunit.v3.templates::4.0.0
dotnet new classlib -n DeskButler.Core -o src/DeskButler.Core -f net10.0
dotnet new classlib -n DeskButler.Application -o src/DeskButler.Application -f net10.0
dotnet new classlib -n DeskButler.Persistence -o src/DeskButler.Persistence -f net10.0
dotnet new classlib -n DeskButler.Infrastructure.Windows -o src/DeskButler.Infrastructure.Windows -f net10.0
dotnet new classlib -n DeskButler.Modules.WorkspaceRecovery -o src/DeskButler.Modules.WorkspaceRecovery -f net10.0
dotnet new wpf -n DeskButler.Desktop -o src/DeskButler.Desktop -f net10.0
dotnet new xunit3 -n DeskButler.Core.Tests -o tests/DeskButler.Core.Tests -f net10.0
dotnet new xunit3 -n DeskButler.Application.Tests -o tests/DeskButler.Application.Tests -f net10.0
dotnet new xunit3 -n DeskButler.Persistence.Tests -o tests/DeskButler.Persistence.Tests -f net10.0
dotnet new xunit3 -n DeskButler.Infrastructure.Windows.Tests -o tests/DeskButler.Infrastructure.Windows.Tests -f net10.0
dotnet new xunit3 -n DeskButler.Modules.WorkspaceRecovery.Tests -o tests/DeskButler.Modules.WorkspaceRecovery.Tests -f net10.0
dotnet new xunit3 -n DeskButler.Desktop.Tests -o tests/DeskButler.Desktop.Tests -f net10.0
dotnet sln DeskButler.slnx add src/*/*.csproj tests/*/*.csproj
```

Expected: 所有项目成功创建并加入解决方案。

- [ ] **Step 4: 写入全局构建约束和集中包版本**

Create `Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>14.0</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
```

Create `Directory.Packages.props`:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.Data.Sqlite" Version="10.0.11" />
    <PackageVersion Include="xunit.v3.mtp-v2" Version="4.0.0" />
  </ItemGroup>
</Project>
```

Keep the xUnit v3 template's `<PackageReference Include="xunit.v3.mtp-v2" />`; add `<PackageReference Include="Microsoft.Data.Sqlite" />` only to Persistence. Set `DeskButler.Infrastructure.Windows`, `DeskButler.Desktop`, their test projects, and later EndToEnd project to `net10.0-windows10.0.17763.0`; keep Core, Application, Persistence and WorkspaceRecovery projects at `net10.0`.

- [ ] **Step 5: 添加项目引用并建立干净忽略规则**

Run:

```powershell
dotnet add src/DeskButler.Application reference src/DeskButler.Core
dotnet add src/DeskButler.Persistence reference src/DeskButler.Core
dotnet add src/DeskButler.Infrastructure.Windows reference src/DeskButler.Core
dotnet add src/DeskButler.Modules.WorkspaceRecovery reference src/DeskButler.Core src/DeskButler.Application
dotnet add src/DeskButler.Desktop reference src/DeskButler.Core src/DeskButler.Application src/DeskButler.Persistence src/DeskButler.Infrastructure.Windows src/DeskButler.Modules.WorkspaceRecovery
dotnet add tests/DeskButler.Core.Tests reference src/DeskButler.Core
dotnet add tests/DeskButler.Application.Tests reference src/DeskButler.Application
dotnet add tests/DeskButler.Persistence.Tests reference src/DeskButler.Persistence
dotnet add tests/DeskButler.Infrastructure.Windows.Tests reference src/DeskButler.Infrastructure.Windows
dotnet add tests/DeskButler.Modules.WorkspaceRecovery.Tests reference src/DeskButler.Modules.WorkspaceRecovery
dotnet add tests/DeskButler.Desktop.Tests reference src/DeskButler.Desktop
```

Create `.gitignore` containing:

```gitignore
.vs/
.idea/
bin/
obj/
artifacts/
TestResults/
*.user
*.suo
*.pfx
*.snk
*.key
*.token
*.db
*.db-shm
*.db-wal
*.log
diagnostics/
user-data/
```

- [ ] **Step 6: 把批准文档复制进私有仓库并验证初始构建**

Copy the approved documents to:

```text
docs/design/2026-08-24-deskbutler-design.md
docs/plans/2026-08-24-deskbutler-v1.md
```

Run:

```powershell
dotnet restore DeskButler.slnx
dotnet build DeskButler.slnx -c Debug --no-restore
dotnet test DeskButler.slnx -c Debug --no-build
```

Expected: restore/build/test all succeed.

- [ ] **Step 7: 首次私有仓库提交**

Run:

```powershell
git rev-parse --show-toplevel
git remote -v
git status --short
git add .
git diff --cached --check
git commit -m "chore: initialize DeskButler solution"
```

Expected: top-level 为 DeskButler、无 remote、提交仅包含 DeskButler 文件。

---

### Task 2: 核心场景模型、设置和捕获过滤规则

**Files:**
- Create: `src/DeskButler.Core/Scenes/WindowBounds.cs`
- Create: `src/DeskButler.Core/Scenes/MonitorIdentity.cs`
- Create: `src/DeskButler.Core/Scenes/WindowState.cs`
- Create: `src/DeskButler.Core/Scenes/SceneItem.cs`
- Create: `src/DeskButler.Core/Scenes/SceneSnapshot.cs`
- Create: `src/DeskButler.Core/Capture/WindowCandidate.cs`
- Create: `src/DeskButler.Core/Time/IClock.cs`
- Create: `src/DeskButler.Core/Settings/ButlerSettings.cs`
- Create: `src/DeskButler.Modules.WorkspaceRecovery/Capture/SceneFilter.cs`
- Test: `tests/DeskButler.Modules.WorkspaceRecovery.Tests/Capture/SceneFilterTests.cs`
- Create: `tests/DeskButler.Modules.WorkspaceRecovery.Tests/Capture/CandidateFactory.cs`

**Interfaces:**
- Consumes: 无外部业务接口。
- Produces: `SceneSnapshot`, `SceneItem`, `ButlerSettings`, `SceneFilter.ShouldCapture(WindowCandidate)`。

- [ ] **Step 1: 写过滤规则失败测试**

Create `SceneFilterTests.cs` with tests proving that a visible normal window is included and system, temporary, self, missing-path and user-excluded windows are rejected:

```csharp
[Fact]
public void ShouldCapture_RejectsExcludedExecutable()
{
    var settings = ButlerSettings.Default with
    {
        ExcludedExecutablePaths = [@"C:\Tools\ignored.exe"]
    };
    var candidate = CandidateFactory.Normal(@"C:\Tools\ignored.exe", "Ignored");

    Assert.False(new SceneFilter(settings).ShouldCapture(candidate));
}
```

- [ ] **Step 2: 运行测试确认失败**

Run:

```powershell
dotnet test tests/DeskButler.Modules.WorkspaceRecovery.Tests --filter FullyQualifiedName~SceneFilterTests
```

Expected: FAIL because the types do not exist.

- [ ] **Step 3: 实现不可变核心模型和过滤器**

Use these signatures:

```csharp
public readonly record struct WindowBounds(int Left, int Top, int Width, int Height);
public sealed record MonitorIdentity(string DeviceName, WindowBounds WorkArea, uint DpiX, uint DpiY);
public enum SceneWindowState { Normal, Minimized, Maximized }

public sealed record SceneItem(
    string Id,
    string ExecutablePath,
    string WindowClass,
    string? TitleHint,
    string? ExplorerPath,
    WindowBounds Bounds,
    SceneWindowState State,
    MonitorIdentity Monitor,
    bool WasElevated);

public sealed record SceneSnapshot(
    Guid Id,
    int FormatVersion,
    DateTimeOffset CapturedAt,
    string CaptureReason,
    IReadOnlyList<SceneItem> Items);

public sealed record WindowCandidate(
    nint Handle,
    int ProcessId,
    string? ExecutablePath,
    string WindowClass,
    string? Title,
    string? ExplorerPath,
    WindowBounds Bounds,
    SceneWindowState State,
    MonitorIdentity Monitor,
    bool IsVisibleMainWindow,
    bool IsSystemWindow,
    bool IsTemporaryWindow,
    bool IsDeskButlerWindow,
    bool WasElevatedOrInaccessible);

public interface IClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed record ButlerSettings(
    bool CaptureEnabled,
    bool StartupEnabled,
    int RecoveryCardDismissSeconds,
    IReadOnlySet<string> ExcludedExecutablePaths)
{
    public static ButlerSettings Default { get; } =
        new(true, true, 15, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}
```

`SceneFilter.ShouldCapture` must normalize paths with `Path.GetFullPath`, compare case-insensitively, and reject `IsSystemWindow`, `IsTemporaryWindow`, `IsDeskButlerWindow`, `ExecutablePath is null`, or `!IsVisibleMainWindow`.

- [ ] **Step 4: 运行测试并提交**

Run:

```powershell
dotnet test tests/DeskButler.Modules.WorkspaceRecovery.Tests --filter FullyQualifiedName~SceneFilterTests
dotnet build DeskButler.slnx -c Debug
git rev-parse --show-toplevel
git add src/DeskButler.Core src/DeskButler.Modules.WorkspaceRecovery tests/DeskButler.Modules.WorkspaceRecovery.Tests
git diff --cached --check
git commit -m "feat: define scene capture model"
```

Expected: tests and build pass; commit is in DeskButler root.

---

### Task 3: 命令总线、事件总线和模块宿主

**Files:**
- Create: `src/DeskButler.Application/Commands/ICommand.cs`
- Create: `src/DeskButler.Application/Commands/ICommandHandler.cs`
- Create: `src/DeskButler.Application/Commands/ICommandBus.cs`
- Create: `src/DeskButler.Application/Commands/InProcessCommandBus.cs`
- Create: `src/DeskButler.Application/Events/IEventBus.cs`
- Create: `src/DeskButler.Application/Events/InProcessEventBus.cs`
- Create: `src/DeskButler.Application/Modules/IModule.cs`
- Create: `src/DeskButler.Application/Hosting/ModuleHost.cs`
- Test: `tests/DeskButler.Application.Tests/Hosting/ModuleHostTests.cs`

**Interfaces:**
- Consumes: `CancellationToken` and compile-time registered modules.
- Produces: `ICommandBus.SendAsync<TResponse>()`, `IEventBus.PublishAsync<TEvent>()`, `ModuleHost.StartAsync/StopAsync()`.

- [ ] **Step 1: 写模块顺序和命令路由失败测试**

```csharp
[Fact]
public async Task Host_StopsModulesInReverseStartOrder()
{
    var calls = new List<string>();
    var host = new ModuleHost([new FakeModule("a", calls), new FakeModule("b", calls)]);

    await host.StartAsync(CancellationToken.None);
    await host.StopAsync(CancellationToken.None);

    Assert.Equal(["start:a", "start:b", "stop:b", "stop:a"], calls);
}
```

Add a second test that an unregistered command throws `CommandHandlerNotFoundException` rather than silently doing nothing.

- [ ] **Step 2: 运行测试确认失败**

Run `dotnet test tests/DeskButler.Application.Tests --filter "FullyQualifiedName~ModuleHostTests|FullyQualifiedName~CommandBusTests"`.

Expected: FAIL because host and bus are absent.

- [ ] **Step 3: 实现最小同步边界**

Use these contracts:

```csharp
public interface ICommand<TResponse> { }
public interface ICommandHandler<TCommand, TResponse> where TCommand : ICommand<TResponse>
{
    Task<TResponse> HandleAsync(TCommand command, CancellationToken cancellationToken);
}
public interface ICommandBus
{
    Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken);
}
public interface IModule
{
    string Id { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
```

The event bus copies its subscriber list before invoking handlers and isolates handler failure by returning an aggregate result to the caller; it must not swallow exceptions without logging metadata.

- [ ] **Step 4: 验证并提交**

Run full Application tests and build, then commit:

```powershell
dotnet test tests/DeskButler.Application.Tests
dotnet build DeskButler.slnx -c Debug
git rev-parse --show-toplevel
git add src/DeskButler.Application tests/DeskButler.Application.Tests
git commit -m "feat: add module host and command bus"
```

---

### Task 4: SQLite 快照仓库、三份保留和 JSON 设置

**Files:**
- Create: `src/DeskButler.Core/Persistence/ISceneRepository.cs`
- Create: `src/DeskButler.Core/Settings/ISettingsStore.cs`
- Create: `src/DeskButler.Persistence/Paths/AppDataPaths.cs`
- Create: `src/DeskButler.Persistence/Sqlite/DatabaseMigrator.cs`
- Create: `src/DeskButler.Persistence/Sqlite/SqliteSceneRepository.cs`
- Create: `src/DeskButler.Persistence/Json/JsonSettingsStore.cs`
- Test: `tests/DeskButler.Persistence.Tests/Sqlite/SqliteSceneRepositoryTests.cs`
- Test: `tests/DeskButler.Persistence.Tests/Json/JsonSettingsStoreTests.cs`

**Interfaces:**
- Consumes: `SceneSnapshot`, `ButlerSettings`.
- Produces: atomic `SaveAsync`, newest-first `GetRecentAsync`, `MarkInvalidAsync`, and settings load/save.

- [ ] **Step 1: 写仓库失败测试**

```csharp
[Fact]
public async Task SaveAsync_KeepsOnlyThreeNewestValidSnapshots()
{
    await using var fixture = await RepositoryFixture.CreateAsync();
    foreach (var minute in Enumerable.Range(1, 4))
        await fixture.Repository.SaveAsync(SceneFactory.AtMinute(minute), CancellationToken.None);

    var snapshots = await fixture.Repository.GetRecentAsync(10, CancellationToken.None);

    Assert.Equal([4, 3, 2], snapshots.Select(x => x.CapturedAt.Minute));
}
```

Add tests for transaction rollback on invalid JSON, fallback to default settings when the JSON file is missing, and preservation of the corrupt settings file as `settings.corrupt-<timestamp>.json`.

- [ ] **Step 2: 运行测试确认失败**

Run `dotnet test tests/DeskButler.Persistence.Tests`.

Expected: FAIL because persistence contracts are absent.

- [ ] **Step 3: 实现数据库 schema 和原子保存**

Use schema version 1:

```sql
CREATE TABLE schema_info(version INTEGER NOT NULL);
CREATE TABLE scene_snapshots(
  id TEXT PRIMARY KEY,
  captured_at TEXT NOT NULL,
  capture_reason TEXT NOT NULL,
  format_version INTEGER NOT NULL,
  payload_json TEXT NOT NULL,
  is_valid INTEGER NOT NULL DEFAULT 1,
  invalid_reason TEXT NULL
);
CREATE INDEX ix_scene_snapshots_recent
  ON scene_snapshots(is_valid, captured_at DESC);
CREATE TABLE restore_runs(
  id TEXT PRIMARY KEY,
  scene_id TEXT NOT NULL,
  started_at TEXT NOT NULL,
  completed_at TEXT NULL,
  result_json TEXT NULL
);
```

`SaveAsync` must insert and delete old valid rows inside one SQLite transaction. Call `PRAGMA journal_mode=WAL;` and `PRAGMA foreign_keys=ON;` on initialization. Serialize with `System.Text.Json` using explicit camelCase options.

- [ ] **Step 4: 实现 JSON 设置原子替换**

Write to `settings.json.tmp`, flush with `FileStream.Flush(true)`, then replace the target. App data root is `%LocalAppData%\DeskButler`; tests inject a temporary root and never touch real user data.

- [ ] **Step 5: 验证并提交**

Run:

```powershell
dotnet test tests/DeskButler.Persistence.Tests
dotnet build DeskButler.slnx -c Debug
git rev-parse --show-toplevel
git add src/DeskButler.Core src/DeskButler.Persistence tests/DeskButler.Persistence.Tests
git commit -m "feat: persist scenes and settings locally"
```

---

### Task 5: Windows 窗口、进程、显示器和资源管理器捕获适配层

**Files:**
- Create: `src/DeskButler.Core/Capture/IWindowInventory.cs`
- Create: `src/DeskButler.Infrastructure.Windows/Native/NativeMethods.cs`
- Create: `src/DeskButler.Infrastructure.Windows/Windows/Win32WindowInventory.cs`
- Create: `src/DeskButler.Infrastructure.Windows/Windows/ExplorerWindowReader.cs`
- Create: `src/DeskButler.Infrastructure.Windows/Windows/MonitorCatalog.cs`
- Test: `tests/DeskButler.Infrastructure.Windows.Tests/Windows/Win32WindowInventoryTests.cs`
- Create: `tests/DeskButler.Infrastructure.Windows.Tests/WindowsFactAttribute.cs`
- Create: `tests/DeskButler.Infrastructure.Windows.Tests/TestApps/DeskButler.TestWindow/`

**Interfaces:**
- Consumes: current process id, current Windows session and native window handles.
- Produces: `Task<IReadOnlyList<WindowCandidate>> CaptureAsync(CancellationToken)` with no command line or content data.

- [ ] **Step 1: 创建可控测试窗口并写 Windows 集成失败测试**

The test app exposes arguments `--title`, `--left`, `--top`, `--width`, `--height` and shows one WPF main window. Test:

```csharp
[WindowsFact]
public async Task CaptureAsync_ReturnsVisibleTestWindowWithoutCommandLine()
{
    await using var app = await TestWindowProcess.StartAsync("DeskButler Capture Probe");
    var windows = await new Win32WindowInventory().CaptureAsync(CancellationToken.None);

    var window = Assert.Single(windows, x => x.ProcessId == app.ProcessId);
    Assert.Equal("DeskButler Capture Probe", window.Title);
    Assert.NotNull(window.ExecutablePath);
    Assert.DoesNotContain("commandLine", JsonSerializer.Serialize(window), StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run the Infrastructure test project on Windows. Expected: FAIL because adapter is absent.

- [ ] **Step 3: 实现最小 P/Invoke 集合**

`NativeMethods.cs` contains only declarations and safe wrappers for `EnumWindows`, `IsWindowVisible`, `GetWindowThreadProcessId`, `GetWindowRect`, `GetWindowPlacement`, `GetClassName`, `GetWindowText`, `DwmGetWindowAttribute`, `MonitorFromWindow`, and `GetMonitorInfo`.

Every acquired process is disposed. Access-denied and exited-process races return a candidate marked `WasElevatedOrInaccessible`, not an application crash.

- [ ] **Step 4: 实现资源管理器目录解析**

Wrap Shell Windows COM enumeration behind:

```csharp
public interface IExplorerWindowReader
{
    string? TryGetFolderPath(nint windowHandle);
}
```

Accept only `file:` locations that resolve to an existing local directory. Release COM objects in `finally`; do not persist HTTP URLs in A-level recovery.

- [ ] **Step 5: 验证系统窗口过滤所需字段并提交**

Run the test app integration suite twice to catch timing races, then full build. Commit only Windows adapter files with message `feat: capture Windows desktop inventory`.

---

### Task 6: 事件防抖、60 秒检查点和现场捕获协调器

**Files:**
- Create: `src/DeskButler.Modules.WorkspaceRecovery/Capture/SnapshotScheduler.cs`
- Create: `src/DeskButler.Modules.WorkspaceRecovery/Capture/CaptureCoordinator.cs`
- Create: `src/DeskButler.Modules.WorkspaceRecovery/WorkspaceRecoveryModule.cs`
- Test: `tests/DeskButler.Modules.WorkspaceRecovery.Tests/Capture/SnapshotSchedulerTests.cs`
- Test: `tests/DeskButler.Modules.WorkspaceRecovery.Tests/Capture/CaptureCoordinatorTests.cs`

**Interfaces:**
- Consumes: `IWindowInventory`, `SceneFilter`, `ISceneRepository`, `IClock`.
- Produces: `NotifyDesktopChanged()`, `SaveNowAsync(reason)`, module start/stop lifecycle.

- [ ] **Step 1: 写虚拟时钟失败测试**

```csharp
[Fact]
public async Task ContinuousChanges_ForceCheckpointAtSixtySeconds()
{
    var clock = new FakeClock();
    var saves = new List<DateTimeOffset>();
    var scheduler = new SnapshotScheduler(clock, _ => { saves.Add(clock.UtcNow); return Task.CompletedTask; });

    for (var second = 0; second < 70; second += 5)
    {
        scheduler.NotifyDesktopChanged();
        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
    }

    Assert.Contains(saves, time => time == clock.Start + TimeSpan.FromSeconds(60));
}
```

Add a test that quieting for 10 seconds makes one save and does not create duplicates.

- [ ] **Step 2: 运行测试确认失败**

Run Capture tests. Expected: FAIL because scheduler is absent.

- [ ] **Step 3: 实现 scheduler 和捕获映射**

Use one cancellable background loop; do not create a timer per event. `CaptureCoordinator.SaveNowAsync` serializes saves with `SemaphoreSlim(1,1)`, maps filtered candidates to `SceneItem`, and skips writing when the normalized scene is identical to the newest snapshot.

- [ ] **Step 4: 添加模块启停和最终检查点**

`WorkspaceRecoveryModule.StartAsync` subscribes to desktop/session change events; `StopAsync` unsubscribes first, then attempts one `SaveNowAsync("module-stop")` with a bounded token.

- [ ] **Step 5: 验证并提交**

Run WorkspaceRecovery tests plus full build; commit `feat: schedule automatic scene snapshots`.

---

### Task 7: 保守匹配和恢复计划

**Files:**
- Create: `src/DeskButler.Core/Restore/RestoreDisposition.cs`
- Create: `src/DeskButler.Core/Restore/RestorePlanItem.cs`
- Create: `src/DeskButler.Core/Restore/RestorePlan.cs`
- Create: `src/DeskButler.Core/Restore/IRestorePlanner.cs`
- Create: `src/DeskButler.Core/Diagnostics/FailureHistory.cs`
- Create: `src/DeskButler.Modules.WorkspaceRecovery/Restore/RestorePlanner.cs`
- Test: `tests/DeskButler.Modules.WorkspaceRecovery.Tests/Restore/RestorePlannerTests.cs`

**Interfaces:**
- Consumes: selected `SceneSnapshot`, current `WindowCandidate` collection, failure history, safe-mode flag.
- Produces: immutable `RestorePlan` with `Reuse`, `Launch`, `SkipAmbiguous`, `SkipUnsafe`, and `MissingPath` dispositions.

- [ ] **Step 1: 写关键安全行为失败测试**

```csharp
[Fact]
public void Build_AmbiguousExistingWindows_DoesNotLaunchDuplicate()
{
    var scene = SceneFactory.WithOneApp(@"C:\Apps\tool.exe");
    var current = new[]
    {
        WindowFactory.ForApp(@"C:\Apps\tool.exe", "one"),
        WindowFactory.ForApp(@"C:\Apps\tool.exe", "two")
    };

    var plan = new RestorePlanner().Build(scene, current, FailureHistory.Empty, safeMode: false);

    Assert.Equal(RestoreDisposition.SkipAmbiguous, Assert.Single(plan.Items).Disposition);
}
```

Add tests for exact Explorer-path reuse, missing executable, elevated item, 3 consecutive failures, safe-mode filtering, and current unrelated programs remaining absent from the plan.

- [ ] **Step 2: 运行测试确认失败**

Run RestorePlanner tests. Expected: FAIL because planner types are absent.

- [ ] **Step 3: 实现稳定匹配顺序**

Match in this order:

1. normalized Explorer directory path;
2. normalized executable path + window class + constrained title hint;
3. normalized executable path when exactly one scene item and one current window exist;
4. otherwise skip as ambiguous.

Never use PID as persisted identity. Never add a “close current window” operation to the plan.

Define failure history before the planner uses it:

```csharp
public sealed record FailureHistory(IReadOnlyDictionary<string, int> ConsecutiveFailures)
{
    public static FailureHistory Empty { get; } = new(new Dictionary<string, int>());
    public int CountFor(string sceneItemId) => ConsecutiveFailures.GetValueOrDefault(sceneItemId);
}
```

- [ ] **Step 4: 验证并提交**

Run all WorkspaceRecovery tests and commit `feat: build conservative restore plans`.

---

### Task 8: 启动、窗口等待、屏幕回收和可取消恢复执行器

**Files:**
- Create: `src/DeskButler.Core/Restore/IAppLauncher.cs`
- Create: `src/DeskButler.Core/Restore/IWindowPositioner.cs`
- Create: `src/DeskButler.Core/Restore/RestoreResult.cs`
- Create: `src/DeskButler.Infrastructure.Windows/Restore/WindowsAppLauncher.cs`
- Create: `src/DeskButler.Infrastructure.Windows/Restore/WindowsWindowPositioner.cs`
- Create: `src/DeskButler.Modules.WorkspaceRecovery/Restore/RestoreExecutor.cs`
- Test: `tests/DeskButler.Modules.WorkspaceRecovery.Tests/Restore/RestoreExecutorTests.cs`
- Test: `tests/DeskButler.Infrastructure.Windows.Tests/Restore/WindowsWindowPositionerTests.cs`

**Interfaces:**
- Consumes: user-approved `RestorePlan`, launcher, inventory, positioner, 30-second timeout, cancellation token.
- Produces: `RestoreResult` containing success, skipped, failed and cancelled item results; never closes windows.

- [ ] **Step 1: 写部分失败和取消失败测试**

```csharp
[Fact]
public async Task ExecuteAsync_ContinuesAfterOneLaunchFails()
{
    var launcher = new FakeLauncher(failingIds: ["bad"]);
    var executor = RestoreExecutorFactory.Create(launcher);

    var result = await executor.ExecuteAsync(PlanFactory.WithLaunches("bad", "good"), CancellationToken.None);

    Assert.Equal(RestoreItemStatus.Failed, result.Item("bad").Status);
    Assert.Equal(RestoreItemStatus.Succeeded, result.Item("good").Status);
}
```

Add a cancellation test proving no later item is launched and no already launched process is killed.

- [ ] **Step 2: 运行测试确认失败**

Run executor and positioner tests. Expected: FAIL.

- [ ] **Step 3: 实现安全启动**

Use `ProcessStartInfo` with `UseShellExecute = true`, executable path only, and no captured arguments. Explorer folders launch through `%WINDIR%\explorer.exe` with one quoted local directory argument constructed by the adapter, not by arbitrary user input.

- [ ] **Step 4: 实现窗口等待和位置回收**

Poll the inventory every 500 ms for at most 30 seconds using `PeriodicTimer`. When the original monitor is missing, constrain bounds to the primary monitor work area so at least the title bar and 200x120 pixels remain visible. Restore maximized state only after placing normal bounds.

- [ ] **Step 5: 验证并提交**

Run executor unit tests and Windows positioner integration tests twice; commit `feat: execute safe scene restoration`.

---

### Task 9: 单实例、登录启动、会话事件和崩溃安全模式

**Files:**
- Create: `src/DeskButler.Infrastructure.Windows/Startup/IStartupRegistration.cs`
- Create: `src/DeskButler.Infrastructure.Windows/Startup/RegistryStartupRegistration.cs`
- Create: `src/DeskButler.Infrastructure.Windows/Session/WindowsSessionEvents.cs`
- Create: `src/DeskButler.Desktop/Diagnostics/CrashSentinel.cs`
- Create: `src/DeskButler.Desktop/Hosting/SingleInstanceGuard.cs`
- Test: `tests/DeskButler.Desktop.Tests/Diagnostics/CrashSentinelTests.cs`
- Test: `tests/DeskButler.Infrastructure.Windows.Tests/Startup/RegistryStartupRegistrationTests.cs`

**Interfaces:**
- Consumes: current-user registry, `%LocalAppData%\DeskButler`, application executable path.
- Produces: `IsPreviousRunUnclean`, `MarkCleanExit`, startup enable/disable/status, session-ending event.

- [ ] **Step 1: 写崩溃标记和启动注册失败测试**

Use a temporary data directory and disposable test registry key. Prove that a leftover `run.lock` enters safe mode, a clean exit removes it, and startup disable removes only the `DeskButler` value.

- [ ] **Step 2: 运行测试确认失败**

Run targeted tests. Expected: FAIL.

- [ ] **Step 3: 实现单实例和崩溃标记**

Use named mutex `Local\DeskButler.SingleInstance.v1`. Create `run.lock` atomically after acquiring the mutex; on clean application exit flush state, remove the marker, then release the mutex. A second instance sends no arbitrary IPC in V1; it exits after notifying the user.

- [ ] **Step 4: 实现 HKCU 登录启动和会话结束桥接**

Manage only `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\DeskButler`. Quote the executable path. `WindowsSessionEvents` turns `SystemEvents.SessionEnding` into a bounded final-checkpoint request and never blocks shutdown indefinitely.

- [ ] **Step 5: 验证并提交**

Run the targeted tests, full build and commit `feat: add startup and crash-safe hosting`.

---

### Task 10: 托盘、恢复卡片、历史快照和排除列表界面

**Files:**
- Modify: `src/DeskButler.Desktop/DeskButler.Desktop.csproj`
- Modify: `src/DeskButler.Desktop/App.xaml`
- Modify: `src/DeskButler.Desktop/App.xaml.cs`
- Create: `src/DeskButler.Desktop/Hosting/CompositionRoot.cs`
- Create: `src/DeskButler.Desktop/Tray/TrayIconService.cs`
- Create: `src/DeskButler.Desktop/ViewModels/ObservableObject.cs`
- Create: `src/DeskButler.Desktop/ViewModels/AsyncCommand.cs`
- Create: `src/DeskButler.Desktop/ViewModels/MainViewModel.cs`
- Create: `src/DeskButler.Desktop/ViewModels/RecoveryCardViewModel.cs`
- Create: `src/DeskButler.Desktop/Views/MainWindow.xaml`
- Create: `src/DeskButler.Desktop/Views/RecoveryCardWindow.xaml`
- Test: `tests/DeskButler.Desktop.Tests/ViewModels/RecoveryCardViewModelTests.cs`
- Test: `tests/DeskButler.Desktop.Tests/ViewModels/MainViewModelTests.cs`

**Interfaces:**
- Consumes: command bus, scene repository, settings store, restore planner/executor.
- Produces: tray commands, non-automatic 15-second card, manual restore of any of 3 scenes, per-item uncheck and permanent exclusion.

- [ ] **Step 1: 写卡片绝不自动恢复的失败测试**

```csharp
[Fact]
public async Task AutoDismiss_HidesCardWithoutSendingRestoreCommand()
{
    var commands = new RecordingCommandBus();
    var clock = new FakeClock();
    var vm = new RecoveryCardViewModel(commands, clock, dismissSeconds: 15);

    await vm.ShowAsync(SceneFactory.OneItem());
    await clock.AdvanceAsync(TimeSpan.FromSeconds(15));

    Assert.False(vm.IsVisible);
    Assert.Empty(commands.SentCommands);
}
```

Add tests for explicit immediate restore, safe restore, skip, restoring older snapshot, and permanent exclusion persistence.

- [ ] **Step 2: 运行 ViewModel 测试确认失败**

Run Desktop tests. Expected: FAIL.

- [ ] **Step 3: 配置 WPF + WinForms 托盘宿主**

Set `<UseWPF>true</UseWPF>` and `<UseWindowsForms>true</UseWindowsForms>` in Desktop. `TrayIconService` owns one `System.Windows.Forms.NotifyIcon`, disposes it on exit, and exposes menu actions for save, recent scenes, pause/resume, open, and exit.

- [ ] **Step 4: 实现极简主窗口和恢复卡片**

Main window tabs: 首页、现场、模块、设置、诊断. Recovery card is topmost, `ShowActivated="False"`, keyboard accessible, and has four explicit actions. It starts a 15-second dismiss timer that only hides the window.

- [ ] **Step 5: 组合依赖并完成 UI 冒烟测试**

`CompositionRoot` manually creates concrete services; no reflection-based container is needed. Launch with a temporary data root flag available only in Debug builds, create a fixture snapshot, verify tray/card/main-window interactions, then close cleanly.

- [ ] **Step 6: 验证并提交**

Run Desktop tests, full test suite and build; commit `feat: add tray-first recovery interface`.

---

### Task 11: 轮换日志、失败历史、数据库回退和诊断包

**Files:**
- Create: `src/DeskButler.Core/Diagnostics/IDiagnosticLog.cs`
- Create: `src/DeskButler.Core/Diagnostics/IFailureHistoryStore.cs`
- Create: `src/DeskButler.Persistence/Diagnostics/RollingJsonLog.cs`
- Create: `src/DeskButler.Persistence/Diagnostics/DiagnosticBundleExporter.cs`
- Create: `src/DeskButler.Persistence/Sqlite/DatabaseRecovery.cs`
- Test: `tests/DeskButler.Persistence.Tests/Diagnostics/RollingJsonLogTests.cs`
- Test: `tests/DeskButler.Persistence.Tests/Diagnostics/DiagnosticBundleExporterTests.cs`
- Test: `tests/DeskButler.Persistence.Tests/Sqlite/DatabaseRecoveryTests.cs`

**Interfaces:**
- Consumes: structured events, restore results, paths and titles marked sensitive.
- Produces: bounded JSONL logs, 3-failure history, user-previewable redacted ZIP, `.corrupt-<timestamp>` database backup.

- [ ] **Step 1: 写日志边界和脱敏失败测试**

Prove that five 1 MB writes with a 3 MB total cap retain at most 3 MB plus one active-record margin. Prove that `C:\Users\Alice\Secret\plan.docx` exports as `%USERPROFILE%\Secret\plan.docx` and fields named `commandLine`, `token`, `password`, and `clipboard` are absent.

- [ ] **Step 2: 运行测试确认失败**

Run Persistence diagnostics tests. Expected: FAIL.

- [ ] **Step 3: 实现轮换和诊断导出**

Use one active `deskbutler.jsonl` plus numbered archives, UTF-8, exclusive writer lock, 1 MB per file and 3 MB total default. Export only approved files into a ZIP after producing a manifest that the UI can preview.

- [ ] **Step 4: 实现数据库回退**

On `SQLITE_CORRUPT` or migration failure, close all connections, copy the DB plus WAL/SHM when present to a timestamped diagnostic directory, create a fresh DB, and expose a health warning. Never delete the only corrupt copy.

- [ ] **Step 5: 验证并提交**

Run Persistence tests and full suite; commit `feat: add bounded diagnostics and recovery`.

---

### Task 12: 自包含发布和干净安装卸载

**Files:**
- Modify: `src/DeskButler.Desktop/DeskButler.Desktop.csproj`
- Modify: `src/DeskButler.Desktop/App.xaml.cs`
- Create: `installer/DeskButler.iss`
- Create: `installer/build-installer.cmd`
- Create: `tests/installer/verify-install.ps1`
- Create: `tests/installer/verify-uninstall.ps1`

**Interfaces:**
- Consumes: `artifacts/publish/win-x64` self-contained output.
- Produces: per-user x64 `DeskButler-Setup-<version>.exe`, upgrade path, startup cleanup, and preserve/delete-data uninstall choice.

- [ ] **Step 1: 添加发布属性并生成自包含产物**

Add to Desktop project:

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <SelfContained>true</SelfContained>
  <PublishSingleFile>false</PublishSingleFile>
  <PublishReadyToRun>true</PublishReadyToRun>
  <DebugType>embedded</DebugType>
</PropertyGroup>
```

Run:

```powershell
dotnet publish src/DeskButler.Desktop -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64
```

Expected: `DeskButler.Desktop.exe` launches on a Windows test account without a separately installed .NET runtime.

- [ ] **Step 2: 安装或验证 Inno Setup 7.1.0 编译器**

Run `ISCC.exe /?`. If unavailable, install official Inno Setup 7.1.0, then rerun. Record the compiler version in build output.

- [ ] **Step 3: 编写非管理员安装脚本**

The `[Setup]` section must contain:

```ini
[Setup]
AppId=DeskButler
AppName=DeskButler
AppVersion=0.1.0
DefaultDirName={localappdata}\Programs\DeskButler
DefaultGroupName=DeskButler
PrivilegesRequired=lowest
SetupArchitecture=x64
OutputBaseFilename=DeskButler-Setup-0.1.0
Compression=lzma2
SolidCompression=yes
```

Install all publish files, create Start Menu shortcuts, and register no service/driver. Startup is managed by the app. `[UninstallRun]` invokes `DeskButler.Desktop.exe --prepare-uninstall` to remove its own HKCU Run value and exit.

- [ ] **Step 4: 添加卸载数据选择**

In `[Code]`, prompt once during interactive uninstall: “是否同时删除 DeskButler 的设置、快照和日志？” Store the answer in `DeleteUserData`. During `usPostUninstall`, call `DelTree(ExpandConstant('{localappdata}\DeskButler'), True, True, True)` only when the answer is yes and the expanded path ends exactly with `\DeskButler`. Silent uninstall defaults to preserving user data unless `/DELETEUSERDATA=1` is explicitly supplied.

- [ ] **Step 5: 编译并在隔离测试账户验证**

Verify install, launch, startup toggle, upgrade from 0.1.0 test build to a higher test version, uninstall-preserve, reinstall data recovery, and uninstall-delete. Scripts must assert that program files, shortcuts and the DeskButler Run value are removed, and that unrelated HKCU Run values remain.

- [ ] **Step 6: 提交安装器**

Run build/tests, verify Git root and ignored artifacts, then commit `build: add clean per-user installer`.

---

### Task 13: Windows 10/11 端到端、DPI、多屏和长期资源验证

**Files:**
- Create: `tests/DeskButler.EndToEnd/DeskButler.EndToEnd.csproj`
- Create: `tests/DeskButler.EndToEnd/SceneRoundTripTests.cs`
- Create: `tests/DeskButler.EndToEnd/MonitorFallbackTests.cs`
- Create: `tests/DeskButler.EndToEnd/ResourceStabilityTests.cs`
- Create: `tests/manual/windows-10-checklist.md`
- Create: `tests/manual/windows-11-checklist.md`
- Create: `tests/manual/restart-checklist.md`

**Interfaces:**
- Consumes: installed DeskButler build and controlled test window apps.
- Produces: repeatable evidence for capture/restore, display fallback, restart, cancellation and resource stability.

- [ ] **Step 1: 写端到端场景往返测试**

Create the test project and add it to the solution before writing the test:

```powershell
dotnet new xunit3 -n DeskButler.EndToEnd -o tests/DeskButler.EndToEnd -f net10.0
dotnet add tests/DeskButler.EndToEnd reference src/DeskButler.Core src/DeskButler.Persistence src/DeskButler.Infrastructure.Windows src/DeskButler.Modules.WorkspaceRecovery
dotnet sln DeskButler.slnx add tests/DeskButler.EndToEnd/DeskButler.EndToEnd.csproj
```

Set its target framework to `net10.0-windows10.0.17763.0`.

Launch two controlled windows and one Explorer temp directory, save a scene, move windows, close one test process, execute restore, and assert that the missing process returns and both windows are within 8 physical pixels of expected DPI-adjusted bounds.

- [ ] **Step 2: 写显示器缺失测试**

Inject a snapshot whose monitor identity is absent. Assert the restored bounds are fully constrained to the primary work area with a visible title bar and at least 200x120 visible content.

- [ ] **Step 3: 写长期资源稳定测试**

For 30 minutes, generate 10,000 debounced desktop-change notifications and 100 captures using test adapters. Sample process private bytes, handle count and DB size every minute. Fail if the last five samples show monotonic handle growth or if DB retention exceeds three valid snapshots.

- [ ] **Step 4: 执行真实重启检查点**

On Windows 10 baseline, install the package, open the documented fixture apps, wait for a saved snapshot, restart manually, sign in, verify the card does not auto-restore, let it dismiss, open DeskButler, manually restore, and export diagnostics. Repeat the core path on Windows 11 before release.

- [ ] **Step 5: 记录兼容性结果并提交**

Document OS build, DPI, monitor layout, app versions, pass/fail and diagnostic bundle id. Commit tests and checklists as `test: add Windows recovery acceptance suite`.

---

### Task 14: 用户文档、兼容性声明和发布门槛

**Files:**
- Modify: `README.md`
- Create: `docs/user-guide.md`
- Create: `docs/compatibility.md`
- Create: `docs/privacy.md`
- Create: `docs/troubleshooting.md`
- Create: `scripts/verify-release.cmd`

**Interfaces:**
- Consumes: tested installer, automated test results, manual Windows checklists.
- Produces: 用户可理解的安装/恢复/排除/卸载说明和一条发布验证命令。

- [ ] **Step 1: 编写用户指南和明确限制**

Document tray actions, 3 snapshots, manual-only restore, safe restore, exclusions, diagnostics, uninstall data choice, and the exact non-goals: no unsaved-content recovery, browser-tab guarantee, full command line, screen/keyboard recording, remote, AI or animation.

- [ ] **Step 2: 编写隐私和故障排除说明**

List every stored field and local path. Explain elevated-window limitations, missing monitor fallback, ambiguous multi-instance behavior, corrupt DB backup, and how to preview a diagnostic bundle before sharing.

- [ ] **Step 3: 创建发布验证脚本**

`scripts/verify-release.cmd` runs, in order:

```bat
dotnet restore DeskButler.slnx
dotnet build DeskButler.slnx -c Release --no-restore
dotnet test DeskButler.slnx -c Release --no-build
dotnet publish src\DeskButler.Desktop -c Release -r win-x64 --self-contained true -o artifacts\publish\win-x64
call installer\build-installer.cmd
```

It exits on the first failure and prints the final installer SHA-256 using `certutil -hashfile`.

- [ ] **Step 4: 执行发布前完整验证**

Run `scripts\verify-release.cmd`, the installer verification scripts, Windows 10 checklist and Windows 11 checklist. Confirm `git status --short` contains no secrets, user data, database, logs or build artifacts.

- [ ] **Step 5: 创建首版候选提交**

Run:

```powershell
git rev-parse --show-toplevel
git remote -v
git status --short
git add README.md docs scripts
git diff --cached --check
git commit -m "docs: complete DeskButler v1 release guide"
```

Expected: commit remains only in the DeskButler repository. Do not tag or push until the user explicitly approves the tested release candidate and supplies a private remote if desired.

---

## Execution Notes

- Use a fresh review checkpoint after every task; do not batch commits across task boundaries.
- The first real reboot test is not replaceable by mocks. Save all work and obtain explicit user confirmation immediately before rebooting.
- If Windows 10 behavior conflicts with Windows 11 behavior, preserve Windows 10 baseline compatibility and isolate version-specific code in `DeskButler.Infrastructure.Windows`.
- If a task reveals that complete Explorer path recovery or safe per-user uninstall cannot meet the spec, stop implementation and update the design rather than silently weakening acceptance criteria.
