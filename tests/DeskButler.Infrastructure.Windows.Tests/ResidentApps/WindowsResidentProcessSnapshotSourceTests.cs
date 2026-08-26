using System.ComponentModel;
using System.Collections.Immutable;
using DeskButler.Core.ResidentApps;
using DeskButler.Infrastructure.Windows.Native;
using DeskButler.Infrastructure.Windows.ResidentApps;

namespace DeskButler.Infrastructure.Windows.Tests.ResidentApps;

public sealed class WindowsResidentProcessSnapshotSourceTests
{
    /// <summary>验证仅保留当前交互 Session 的进程，并聚合其顶层窗口分类。</summary>
    [Fact]
    public async Task CaptureAsync只保留当前交互Session并聚合窗口分类()
    {
        var source = CreateSource(
            currentSessionId: 7,
            windows:
            [
                new ResidentTopLevelWindow(20, IsVisible: false, IsOwned: false, IsToolWindow: false, IsCloaked: false),
                new ResidentTopLevelWindow(20, IsVisible: true, IsOwned: true, IsToolWindow: true, IsCloaked: true),
                new ResidentTopLevelWindow(10, IsVisible: true, IsOwned: false, IsToolWindow: false, IsCloaked: false)
            ],
            processes:
            [
                Process(20, 7, @"C:\Apps\Second.exe", "Second Product", "Second Co", "Second Description"),
                Process(10, 7, @"C:\Apps\First.exe", "First Product", "First Co", "First Description"),
                Process(30, 8, @"C:\Apps\OtherSession.exe", "Other", "Other", "Other"),
                Process(40, 0, @"C:\Apps\Service.exe", "Service", "Service", "Service")
            ]);

        var snapshot = await source.CaptureAsync(CancellationToken.None);

        Assert.Equal([10, 20], snapshot.Observations.Select(item => item.ProcessId));
        var first = snapshot.Observations[0];
        Assert.Equal(@"C:\Apps\First.exe", first.ExecutablePath);
        Assert.Equal("First Product", first.ProductName);
        Assert.Equal("First Co", first.CompanyName);
        Assert.Equal("First Description", first.FileDescription);
        Assert.True(first.WindowTraits.HasVisibleTopLevelWindow);
        Assert.False(first.WindowTraits.HasHiddenTopLevelWindow);

        var second = snapshot.Observations[1];
        Assert.True(second.WindowTraits.HasVisibleTopLevelWindow);
        Assert.True(second.WindowTraits.HasHiddenTopLevelWindow);
        Assert.True(second.WindowTraits.HasOwnedTopLevelWindow);
        Assert.True(second.WindowTraits.HasToolWindow);
        Assert.True(second.WindowTraits.HasCloakedWindow);
        Assert.IsType<ImmutableArray<ResidentProcessObservation>>(snapshot.Observations);
        Assert.Empty(snapshot.Diagnostics);
    }

    /// <summary>验证进程退出、访问拒绝和版本元数据失败只形成无载荷的分类诊断。</summary>
    [Fact]
    public async Task CaptureAsync单进程失败产生分类诊断并继续()
    {
        var source = CreateSource(
            currentSessionId: 7,
            windows: [],
            processes:
            [
                Process(1, 7, @"C:\Apps\Good.exe", "Good", "Co", "Description"),
                new FakeResidentProcess(2, 7, () => throw new Win32Exception(5), () => null),
                new FakeResidentProcess(3, 7, () => @"C:\Apps\Exited.exe", () => null, hasExited: true),
                new FakeResidentProcess(4, 7, () => @"C:\Apps\Metadata.exe", () => throw new InvalidOperationException())
            ]);

        var snapshot = await source.CaptureAsync(CancellationToken.None);

        Assert.Equal([1, 4], snapshot.Observations.Select(item => item.ProcessId));
        Assert.Null(snapshot.Observations[1].ProductName);
        Assert.Equal(
            [ResidentDiscoveryIssue.ProcessExited, ResidentDiscoveryIssue.AccessDenied, ResidentDiscoveryIssue.MetadataUnavailable],
            snapshot.Diagnostics.Select(item => item.Kind));
        Assert.All(snapshot.Diagnostics, diagnostic => Assert.Single(diagnostic.GetType().GetProperties()));
    }

    /// <summary>验证取消不会被降级为单进程诊断。</summary>
    [Fact]
    public async Task CaptureAsync调用方取消时传播原取消令牌()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = CreateSource(7, [], Process(1, 7, @"C:\Apps\Good.exe", "Good", "Co", "Description"));

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => source.CaptureAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    /// <summary>验证非访问拒绝的 Win32 进程错误必须传播，而不能静默降级为诊断。</summary>
    [Fact]
    public async Task CaptureAsync非访问拒绝Win32错误必须传播()
    {
        var original = new Win32Exception(87, "测试用无效参数错误。");
        var source = CreateSource(
            7,
            [],
            new FakeResidentProcess(1, 7, () => throw original, () => null));

        var exception = await Assert.ThrowsAsync<Win32Exception>(
            () => source.CaptureAsync(CancellationToken.None));

        Assert.Same(original, exception);
    }

    /// <summary>验证生产窗口读取器按 PID 归属并解释 visible、owner、tool 与 cloaked 字段。</summary>
    [Fact]
    public void WindowsResidentWindowReader读取最小窗口分类()
    {
        var native = new FakeResidentWindowNativeApi(
            new ResidentWindowNativeSample(1, 20, IsVisible: true, IsOwned: false, IsToolWindow: false, IsCloaked: false),
            new ResidentWindowNativeSample(2, 20, IsVisible: false, IsOwned: true, IsToolWindow: true, IsCloaked: true),
            new ResidentWindowNativeSample(3, 0, IsVisible: true, IsOwned: false, IsToolWindow: false, IsCloaked: false));
        var reader = new WindowsResidentWindowReader(native);

        var windows = reader.Read(CancellationToken.None);

        Assert.Equal(2, windows.Count);
        Assert.Equal(new ResidentTopLevelWindow(20, true, false, false, false), windows[0]);
        Assert.Equal(new ResidentTopLevelWindow(20, false, true, true, true), windows[1]);
        Assert.Equal([(nint)1, (nint)2, (nint)3], native.EnumeratedHandles);
        Assert.Equal([(nint)1, (nint)2, (nint)3], native.ProcessIdReadHandles);
        Assert.Equal([(nint)1, (nint)2], native.VisibleReadHandles);
    }

    /// <summary>验证观察模型不暴露窗口标题、命令行或账号字段。</summary>
    [Fact]
    public void ResidentProcessObservation不暴露敏感字段()
    {
        var names = typeof(ResidentProcessObservation).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(names, name => name.Contains("Title", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Command", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("User", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Account", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>创建完全受控的观察源，不接触本机进程或窗口。</summary>
    private static WindowsResidentProcessSnapshotSource CreateSource(
        int currentSessionId,
        IReadOnlyList<ResidentTopLevelWindow> windows,
        params IResidentProcess[] processes) =>
        new(new FakeResidentProcessReader(currentSessionId, processes), new FakeResidentWindowReader(windows));

    /// <summary>创建返回固定公开元数据的测试进程。</summary>
    private static FakeResidentProcess Process(
        int processId,
        int sessionId,
        string executablePath,
        string productName,
        string companyName,
        string fileDescription) =>
        new FakeResidentProcess(
            processId,
            sessionId,
            () => executablePath,
            () => new ResidentFileVersionInfo(productName, companyName, fileDescription));

    private sealed class FakeResidentProcessReader(int currentSessionId, IReadOnlyList<IResidentProcess> processes)
        : IResidentProcessReader
    {
        /// <summary>返回测试指定的交互 Session。</summary>
        public int GetCurrentSessionId() => currentSessionId;

        /// <summary>返回测试拥有、由 production source 释放的进程包装。</summary>
        public IReadOnlyList<IResidentProcess> GetProcesses() => processes;
    }

    private sealed class FakeResidentWindowReader(IReadOnlyList<ResidentTopLevelWindow> windows) : IResidentWindowReader
    {
        /// <summary>返回不包含窗口内容的固定顶层窗口分类。</summary>
        public IReadOnlyList<ResidentTopLevelWindow> Read(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return windows;
        }
    }

    private sealed record ResidentWindowNativeSample(
        nint Handle,
        int ProcessId,
        bool IsVisible,
        bool IsOwned,
        bool IsToolWindow,
        bool IsCloaked);

    private sealed class FakeResidentWindowNativeApi(params ResidentWindowNativeSample[] windows) : IResidentWindowNativeApi
    {
        private readonly Dictionary<nint, ResidentWindowNativeSample> byHandle = windows.ToDictionary(window => window.Handle);
        private readonly List<nint> enumeratedHandles = [];
        private readonly List<nint> processIdReadHandles = [];
        private readonly List<nint> visibleReadHandles = [];

        internal IReadOnlyList<nint> EnumeratedHandles => enumeratedHandles;

        internal IReadOnlyList<nint> ProcessIdReadHandles => processIdReadHandles;

        internal IReadOnlyList<nint> VisibleReadHandles => visibleReadHandles;

        /// <summary>依次调用生产 callback，并遵循其停止信号。</summary>
        public bool EnumerateWindows(NativeMethods.EnumWindowsProc callback)
        {
            foreach (var window in windows)
            {
                enumeratedHandles.Add(window.Handle);
                if (!callback(window.Handle, 0))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>fake 枚举正常完成时没有 Win32 错误。</summary>
        public int GetLastError() => 0;

        /// <summary>读取受控 HWND 的所属 PID；零 PID 模拟失效窗口。</summary>
        public bool TryGetProcessId(nint windowHandle, out int processId)
        {
            processIdReadHandles.Add(windowHandle);
            processId = byHandle[windowHandle].ProcessId;
            return processId != 0;
        }

        /// <summary>读取受控可见分类。</summary>
        public bool IsWindowVisible(nint windowHandle)
        {
            visibleReadHandles.Add(windowHandle);
            return byHandle[windowHandle].IsVisible;
        }

        /// <summary>读取受控 owner 分类。</summary>
        public bool TryGetOwner(nint windowHandle, out nint owner)
        {
            owner = byHandle[windowHandle].IsOwned ? 1 : 0;
            return true;
        }

        /// <summary>读取受控工具窗口扩展样式。</summary>
        public bool TryGetExtendedStyle(nint windowHandle, out nint extendedStyle)
        {
            extendedStyle = byHandle[windowHandle].IsToolWindow ? (nint)NativeMethods.WsExToolWindow : 0;
            return true;
        }

        /// <summary>读取受控 DWM cloaked 分类。</summary>
        public bool IsCloaked(nint windowHandle) => byHandle[windowHandle].IsCloaked;
    }

    private sealed class FakeResidentProcess(
        int processId,
        int sessionId,
        Func<string> executablePath,
        Func<ResidentFileVersionInfo?> versionInfo,
        bool hasExited = false) : IResidentProcess
    {
        /// <summary>返回测试指定的进程是否已退出。</summary>
        public bool HasExited => hasExited;

        /// <summary>返回测试指定的进程标识。</summary>
        public int ProcessId => processId;

        /// <summary>返回测试指定的 Windows Session。</summary>
        public int SessionId => sessionId;

        /// <summary>读取测试指定的主模块完整路径。</summary>
        public string GetExecutablePath() => executablePath();

        /// <summary>读取测试指定的公开文件版本字段。</summary>
        public ResidentFileVersionInfo? GetFileVersionInfo(string executablePath) => versionInfo();

        /// <summary>fake 不拥有系统句柄，因此释放为空操作。</summary>
        public void Dispose()
        {
        }
    }
}
