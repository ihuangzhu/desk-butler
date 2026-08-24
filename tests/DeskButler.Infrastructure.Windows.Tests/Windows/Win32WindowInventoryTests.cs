using System.ComponentModel;
using System.Runtime.InteropServices;
using DeskButler.Core.Scenes;
using DeskButler.Infrastructure.Windows.Native;
using DeskButler.Infrastructure.Windows.Windows;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskButler.Infrastructure.Windows.Tests.Windows;

public sealed class Win32WindowInventoryTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new NativeIntJsonConverter() }
    };

    /// <summary>验证真实 Win32 边界可捕获受控 WPF 主窗口且模型未暴露命令行。</summary>
    [WindowsFact]
    [Trait("Category", "WindowsIntegration")]
    public async Task CaptureAsync返回受控可见测试窗口且不含命令行()
    {
        await using var app = await TestWindowProcess.StartAsync("DeskButler Capture Probe");

        var windows = await new Win32WindowInventory().CaptureAsync(CancellationToken.None);

        var window = Assert.Single(windows, candidate => candidate.ProcessId == app.ProcessId);
        Assert.Equal("DeskButler Capture Probe", window.Title);
        Assert.NotNull(window.ExecutablePath);
        Assert.DoesNotContain("commandLine", JsonSerializer.Serialize(window, SerializerOptions), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>验证捕获层排除非普通主窗口，避免系统、临时、自身及辅助窗口进入场景。</summary>
    [Fact]
    public async Task CaptureAsync仅返回普通可见主窗口()
    {
        var native = new FakeWindowNativeFacade(
            创建快照(1),
            创建快照(2) with { IsVisible = false },
            创建快照(3) with { IsOwned = true },
            创建快照(4) with { IsToolWindow = true },
            创建快照(5) with { IsCloaked = true },
            创建快照(6) with { WindowClass = "Shell_TrayWnd" },
            创建快照(7) with { WindowClass = "tooltips_class32" },
            创建快照(8) with { ProcessId = 999 });
        var inventory = new Win32WindowInventory(native, new FakeExplorerWindowReader(), new FakeMonitorCatalog(), 999);

        var windows = await inventory.CaptureAsync(CancellationToken.None);

        var window = Assert.Single(windows);
        Assert.Equal((nint)1, window.Handle);
        Assert.True(window.IsVisibleMainWindow);
        Assert.False(window.IsSystemWindow);
        Assert.False(window.IsTemporaryWindow);
        Assert.False(window.IsDeskButlerWindow);
    }

    /// <summary>验证真实映射器保留允许的窗口身份、受限标题提示、位置、状态、目录和显示器信息。</summary>
    [Fact]
    public async Task CaptureAsync映射允许的捕获字段()
    {
        var snapshot = 创建快照(42) with
        {
            ProcessId = 123,
            ExecutablePath = @"C:\Apps\Editor.exe",
            WindowClass = "EditorMainWindow",
            Title = "受限标题提示",
            Bounds = new WindowBounds(-20, 30, 900, 700),
            State = SceneWindowState.Maximized
        };
        var monitor = new MonitorIdentity(@"\\.\DISPLAY2", new WindowBounds(-1920, 0, 1920, 1040), 144, 144);
        var inventory = new Win32WindowInventory(
            new FakeWindowNativeFacade(snapshot),
            new FakeExplorerWindowReader((42, @"C:\Work")),
            new FakeMonitorCatalog(monitor),
            999);

        var window = Assert.Single(await inventory.CaptureAsync(CancellationToken.None));

        Assert.Equal(123, window.ProcessId);
        Assert.Equal(@"C:\Apps\Editor.exe", window.ExecutablePath);
        Assert.Equal("EditorMainWindow", window.WindowClass);
        Assert.Equal("受限标题提示", window.Title);
        Assert.Equal(@"C:\Work", window.ExplorerPath);
        Assert.Equal(new WindowBounds(-20, 30, 900, 700), window.Bounds);
        Assert.Equal(SceneWindowState.Maximized, window.State);
        Assert.Equal(monitor, window.Monitor);
    }

    /// <summary>验证访问被拒或进程已退出时单个候选会安全降级，而不会中止整次捕获。</summary>
    [Fact]
    public async Task CaptureAsync进程不可访问时保留候选并标记降级()
    {
        var inaccessible = 创建快照(10) with
        {
            ProcessId = 456,
            ExecutablePath = null,
            WasElevatedOrInaccessible = true
        };
        var inventory = new Win32WindowInventory(
            new FakeWindowNativeFacade(inaccessible, 创建快照(11)),
            new FakeExplorerWindowReader(),
            new FakeMonitorCatalog(),
            999);

        var windows = await inventory.CaptureAsync(CancellationToken.None);

        Assert.Equal(2, windows.Count);
        var window = Assert.Single(windows, candidate => candidate.Handle == (nint)10);
        Assert.Null(window.ExecutablePath);
        Assert.True(window.WasElevatedOrInaccessible);
    }

    /// <summary>验证单个窗口的 native 读取抛出异常时跳过该项并继续捕获后续窗口。</summary>
    [Fact]
    public async Task CaptureAsync原生单窗口异常时继续捕获后续窗口()
    {
        var native = new FakeWindowNativeFacade(创建快照(1), 创建快照(2), 创建快照(3));
        native.设置分类读取异常(2, () => new RecoverableComFailureException("模拟窗口在分类读取期间消失。"));
        var explorer = new FakeExplorerWindowReader();
        var monitors = new FakeMonitorCatalog();
        var inventory = new Win32WindowInventory(native, explorer, monitors, 999);

        var windows = await inventory.CaptureAsync(CancellationToken.None);

        Assert.Equal([(nint)1, (nint)3], windows.Select(window => window.Handle));
    }

    /// <summary>验证 unexpected 异常让 callback 停止，回到托管层后重新抛出原异常且不访问后续 HWND。</summary>
    [Fact]
    public void EnumerateTopLevelWindows意外异常停止并重新抛出原异常()
    {
        var facade = new Win32NativeFacade(new FakeWindowEnumerationNativeApi(1, 2, 3));
        var visited = new List<nint>();
        var original = new InvalidOperationException("模拟 production 不变量错误。");

        var exception = Assert.Throws<InvalidOperationException>(() => facade.EnumerateTopLevelWindows(handle =>
        {
            if (handle == 2)
            {
                throw original;
            }

            visited.Add(handle);
        }, CancellationToken.None));

        Assert.Same(original, exception);
        Assert.Equal([(nint)1], visited);
    }

    /// <summary>验证调用方取消不会作为可恢复窗口失败继续，并以原 CancellationToken 抛取消。</summary>
    [Fact]
    public void EnumerateTopLevelWindows调用方取消停止且保留Token()
    {
        using var cancellation = new CancellationTokenSource();
        var facade = new Win32NativeFacade(new FakeWindowEnumerationNativeApi(1, 2));
        var visited = new List<nint>();

        var exception = Assert.Throws<OperationCanceledException>(() => facade.EnumerateTopLevelWindows(handle =>
        {
            visited.Add(handle);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal([(nint)1], visited);
    }

    /// <summary>验证所有排除判断先于可执行路径、标题等详情读取。</summary>
    [Fact]
    public async Task CaptureAsync被排除窗口不读取详情()
    {
        var native = new FakeWindowNativeFacade(
            创建快照(1) with { IsVisible = false },
            创建快照(2) with { IsOwned = true },
            创建快照(3) with { IsToolWindow = true },
            创建快照(4) with { IsCloaked = true },
            创建快照(5) with { WindowClass = "Shell_TrayWnd" },
            创建快照(6) with { WindowClass = "tooltips_class32" },
            创建快照(7) with { ProcessId = 999 },
            创建快照(8));
        var explorer = new FakeExplorerWindowReader();
        var monitors = new FakeMonitorCatalog();
        var inventory = new Win32WindowInventory(native, explorer, monitors, 999);

        var windows = await inventory.CaptureAsync(CancellationToken.None);

        Assert.Equal((nint)8, Assert.Single(windows).Handle);
        Assert.Equal(["details:8"], native.Calls.Where(call => call.StartsWith("details:", StringComparison.Ordinal)));
        Assert.Equal([(nint)8], explorer.Calls);
        Assert.Equal([(nint)8], monitors.Calls);
    }

    /// <summary>验证 Explorer 或 monitor 在单项映射中抛异常时跳过该窗口并继续后续项。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CaptureAsync映射依赖单项异常时继续捕获后续窗口(bool explorerThrows)
    {
        var native = new FakeWindowNativeFacade(创建快照(1), 创建快照(2));
        IExplorerWindowReader explorer = explorerThrows
            ? new ThrowingExplorerWindowReader(1)
            : new FakeExplorerWindowReader();
        IMonitorCatalog monitors = explorerThrows
            ? new FakeMonitorCatalog()
            : new ThrowingMonitorCatalog(1);
        var inventory = new Win32WindowInventory(native, explorer, monitors, 999);

        var windows = await inventory.CaptureAsync(CancellationToken.None);

        Assert.Equal((nint)2, Assert.Single(windows).Handle);
    }

    /// <summary>创建默认有效的原生窗口快照，供各测试只覆盖关心的差异字段。</summary>
    private static NativeWindowSnapshot 创建快照(int handle)
    {
        return new NativeWindowSnapshot(
            handle,
            100 + handle,
            @"C:\Apps\Example.exe",
            "ExampleMainWindow",
            "Example",
            new WindowBounds(10, 20, 800, 600),
            SceneWindowState.Normal,
            true,
            false,
            false,
            false,
            false);
    }

    private sealed class FakeWindowNativeFacade : IWindowNativeFacade
    {
        private readonly IReadOnlyDictionary<nint, NativeWindowSnapshot> windows;
        private readonly Dictionary<nint, Func<Exception>> classificationFailures = [];
        private readonly List<string> calls = [];

        /// <summary>创建返回指定窗口快照的测试 native 边界。</summary>
        /// <param name="windows">受控顶层窗口快照。</param>
        public FakeWindowNativeFacade(params NativeWindowSnapshot[] windows)
        {
            this.windows = windows.ToDictionary(window => window.Handle);
        }

        internal IReadOnlyList<string> Calls => calls;

        /// <summary>设置指定句柄在读取分类字段时抛出受控异常。</summary>
        public void 设置分类读取异常(nint windowHandle, Func<Exception> exceptionFactory)
        {
            classificationFailures[windowHandle] = exceptionFactory;
        }

        /// <summary>依次把受控 HWND 交给真实 production 捕获回调。</summary>
        public void EnumerateTopLevelWindows(Action<nint> visitor, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var windowHandle in windows.Keys)
            {
                visitor(windowHandle);
            }
        }

        /// <summary>读取足以提前过滤窗口的受控分类字段。</summary>
        public bool TryReadClassification(nint windowHandle, out NativeWindowClassification classification)
        {
            calls.Add($"classification:{windowHandle}");
            if (classificationFailures.TryGetValue(windowHandle, out var exceptionFactory))
            {
                throw exceptionFactory();
            }

            var window = windows[windowHandle];
            classification = new NativeWindowClassification(
                window.Handle,
                window.ProcessId,
                window.WindowClass,
                window.IsVisible,
                window.IsOwned,
                window.IsToolWindow,
                window.IsCloaked);
            return true;
        }

        /// <summary>读取通过分类过滤后才允许访问的受控详情字段。</summary>
        public bool TryReadDetails(nint windowHandle, int processId, out NativeWindowDetails details)
        {
            calls.Add($"details:{windowHandle}");
            var window = windows[windowHandle];
            details = new NativeWindowDetails(
                window.ExecutablePath,
                window.Title,
                window.Bounds,
                window.State,
                window.WasElevatedOrInaccessible);
            return true;
        }
    }

    /// <summary>创建返回指定 HWND/目录映射的测试 Explorer 读取器。</summary>
    /// <param name="paths">受控 HWND 与目录映射。</param>
    private sealed class FakeExplorerWindowReader(params (nint Handle, string Path)[] paths) : IExplorerWindowReader
    {
        private readonly List<nint> calls = [];

        internal IReadOnlyList<nint> Calls => calls;

        /// <summary>按窗口句柄返回受控资源管理器目录。</summary>
        public string? TryGetFolderPath(nint windowHandle)
        {
            calls.Add(windowHandle);
            return paths.FirstOrDefault(item => item.Handle == windowHandle).Path;
        }
    }

    private sealed class FakeMonitorCatalog : IMonitorCatalog
    {
        private readonly MonitorIdentity monitor;
        private readonly List<nint> calls = [];

        /// <summary>创建返回指定显示器身份的测试目录。</summary>
        public FakeMonitorCatalog(MonitorIdentity? monitor = null)
        {
            this.monitor = monitor ?? new MonitorIdentity(@"\\.\DISPLAY1", new WindowBounds(0, 0, 1920, 1040), 96, 96);
        }

        internal IReadOnlyList<nint> Calls => calls;

        /// <summary>为任意测试窗口返回固定显示器身份。</summary>
        public MonitorIdentity GetForWindow(nint windowHandle)
        {
            calls.Add(windowHandle);
            return monitor;
        }
    }

    /// <summary>创建为指定句柄抛出异常的 Explorer 测试读取器。</summary>
    /// <param name="throwingHandle">触发异常的窗口句柄。</param>
    private sealed class ThrowingExplorerWindowReader(nint throwingHandle) : IExplorerWindowReader
    {
        /// <summary>为指定句柄模拟 Explorer COM 映射失败，其余句柄返回空目录。</summary>
        public string? TryGetFolderPath(nint windowHandle)
        {
            return windowHandle == throwingHandle
                ? throw new RecoverableComFailureException("模拟 Explorer 单窗口映射失败。")
                : null;
        }
    }

    /// <summary>提供不会触发保留异常构造规则、但仍进入 production COM 恢复分支的测试信号。</summary>
    private sealed class RecoverableComFailureException(string message) : COMException(message);

    /// <summary>创建为指定句柄抛出异常的 monitor 测试目录。</summary>
    /// <param name="throwingHandle">触发异常的窗口句柄。</param>
    private sealed class ThrowingMonitorCatalog(nint throwingHandle) : IMonitorCatalog
    {
        /// <summary>为指定句柄模拟 monitor 映射失败，其余句柄返回有效身份。</summary>
        public MonitorIdentity GetForWindow(nint windowHandle)
        {
            return windowHandle == throwingHandle
                ? throw new Win32Exception(1400, "模拟 monitor 单窗口映射失败。")
                : new MonitorIdentity(@"\\.\DISPLAY1", new WindowBounds(0, 0, 1920, 1040), 96, 96);
        }
    }

    /// <summary>创建按顺序调用 callback、收到 FALSE 后停止的 fake EnumWindows API。</summary>
    /// <param name="windowHandles">受控 HWND 序列。</param>
    private sealed class FakeWindowEnumerationNativeApi(params nint[] windowHandles) : IWindowEnumerationNativeApi
    {
        /// <summary>模拟 EnumWindows，严格服从 callback 的继续/停止返回值。</summary>
        public bool EnumerateWindows(NativeMethods.EnumWindowsProc callback)
        {
            foreach (var windowHandle in windowHandles)
            {
                if (!callback(windowHandle, 0))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>fake callback 停止不代表 Win32 失败，因此返回零错误码。</summary>
        public int GetLastError() => 0;
    }

    private sealed class NativeIntJsonConverter : JsonConverter<nint>
    {
        /// <summary>反序列化不属于本测试用途，因此明确拒绝误用。</summary>
        public override nint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            throw new NotSupportedException("测试转换器仅用于序列化窗口字段。");
        }

        /// <summary>把平台句柄按其 64 位数值写入测试 JSON。</summary>
        public override void Write(Utf8JsonWriter writer, nint value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value.ToInt64());
        }
    }
}
