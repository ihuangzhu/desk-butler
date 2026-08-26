using System.Collections.Immutable;
using DeskButler.Core.ResidentApps;
using DeskButler.Infrastructure.Windows.ResidentApps;
using Microsoft.Win32;

namespace DeskButler.Infrastructure.Windows.Tests.ResidentApps;

public sealed class InstalledApplicationCatalogTests
{
    private static readonly string[] AllowedValueNames = ["DisplayName", "Publisher", "InstallLocation", "DisplayIcon"];

    /// <summary>验证目录保留重复产品、正规化允许字段并稳定排序。</summary>
    [Fact]
    public async Task ReadAsync保留重复产品并安全正规化DisplayIcon()
    {
        var catalog = new InstalledApplicationCatalog(
            new FakeUninstallRegistryReader(
            [
                new UninstallRegistryEntry(" Zeta ", " Zeta Ltd ", @" C:\Apps\Zeta ", "  \"C:\\Apps\\Zeta\\Zeta.exe\",0  "),
                new UninstallRegistryEntry("Alpha", null, null, @"C:\Apps\Alpha\Alpha.exe"),
                new UninstallRegistryEntry("Alpha", "Second Publisher", @"C:\Apps\Alpha2", "\"C:\\Apps\\Alpha2\\Alpha.exe\",-1")
            ]));

        var snapshot = await catalog.ReadAsync(CancellationToken.None);

        Assert.Equal(["Alpha", "Alpha", "Zeta"], snapshot.Entries.Select(entry => entry.DisplayName));
        Assert.Equal(@"C:\Apps\Alpha\Alpha.exe", snapshot.Entries[0].DisplayIconPath);
        Assert.Equal(@"C:\Apps\Alpha2\Alpha.exe", snapshot.Entries[1].DisplayIconPath);
        Assert.Equal("Second Publisher", snapshot.Entries[1].Publisher);
        Assert.Equal(@"C:\Apps\Zeta", snapshot.Entries[2].InstallRoot);
        Assert.Equal(@"C:\Apps\Zeta\Zeta.exe", snapshot.Entries[2].DisplayIconPath);
        Assert.IsType<ImmutableArray<InstalledApplicationEntry>>(snapshot.Entries);
        Assert.Empty(snapshot.Diagnostics);
    }

    /// <summary>验证空名称和无法正规化的路径不会形成条目，并以分类诊断报告。</summary>
    [Fact]
    public async Task ReadAsync无效条目产生分类诊断而不解析卸载命令()
    {
        var catalog = new InstalledApplicationCatalog(
            new FakeUninstallRegistryReader(
            [
                new UninstallRegistryEntry(" ", "Publisher", @"C:\Apps\Ignored", @"C:\Apps\Ignored\Ignored.exe"),
                new UninstallRegistryEntry("Invalid Path", null, "not-an-absolute-path", "relative.exe"),
                new UninstallRegistryEntry("Valid", "Publisher", null, null)
            ],
            [new ResidentDiscoveryDiagnostic(ResidentDiscoveryIssue.RegistryAccessDenied)]));

        var snapshot = await catalog.ReadAsync(CancellationToken.None);

        Assert.Equal(["Invalid Path", "Valid"], snapshot.Entries.Select(entry => entry.DisplayName));
        Assert.All(snapshot.Entries, entry =>
        {
            Assert.Null(entry.InstallRoot);
            Assert.Null(entry.DisplayIconPath);
        });
        Assert.Equal(
            [
                ResidentDiscoveryIssue.InvalidPath,
                ResidentDiscoveryIssue.InvalidPath,
                ResidentDiscoveryIssue.InvalidPath,
                ResidentDiscoveryIssue.RegistryAccessDenied
            ],
            snapshot.Diagnostics.Select(diagnostic => diagnostic.Kind));
        Assert.DoesNotContain(
            typeof(InstalledApplicationEntry).GetProperties(),
            property => property.Name.Contains("Uninstall", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>验证取消不会被转换为目录诊断。</summary>
    [Fact]
    public async Task ReadAsync调用方取消时传播原取消令牌()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var catalog = new InstalledApplicationCatalog(new FakeUninstallRegistryReader([]));

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => catalog.ReadAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    /// <summary>验证生产注册表读取器访问四个视图，且单键拒绝不阻断其它项或请求卸载命令。</summary>
    [Fact]
    public void WindowsUninstallRegistryReader读取四视图并隔离单键拒绝()
    {
        var native = new FakeUninstallRegistryNativeApi();
        var reader = new WindowsUninstallRegistryReader(native);

        var snapshot = reader.Read(CancellationToken.None);

        Assert.Equal(
            [
                (RegistryHive.CurrentUser, RegistryView.Registry32),
                (RegistryHive.CurrentUser, RegistryView.Registry64),
                (RegistryHive.LocalMachine, RegistryView.Registry32),
                (RegistryHive.LocalMachine, RegistryView.Registry64)
            ],
            native.EnumeratedViews);
        Assert.Equal(4, snapshot.Entries.Length);
        Assert.Equal([ResidentDiscoveryIssue.RegistryAccessDenied], snapshot.Diagnostics.Select(item => item.Kind));
        Assert.All(
            native.RequestedValueNames,
            valueName => Assert.Contains(valueName, AllowedValueNames));
        Assert.DoesNotContain("UninstallString", native.RequestedValueNames, StringComparer.Ordinal);
    }

    private sealed class FakeUninstallRegistryReader(
        IReadOnlyList<UninstallRegistryEntry> entries,
        IReadOnlyList<ResidentDiscoveryDiagnostic>? diagnostics = null) : IUninstallRegistryReader
    {
        /// <summary>返回由测试控制的四视图聚合结果，不读取本机注册表。</summary>
        public UninstallRegistrySnapshot Read(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new UninstallRegistrySnapshot(
                entries.ToImmutableArray(),
                (diagnostics ?? []).ToImmutableArray());
        }
    }

    private sealed class FakeUninstallRegistryNativeApi : IUninstallRegistryNativeApi
    {
        private readonly List<(RegistryHive Hive, RegistryView View)> enumeratedViews = [];
        private readonly List<string> requestedValueNames = [];

        internal IReadOnlyList<(RegistryHive Hive, RegistryView View)> EnumeratedViews => enumeratedViews;

        internal IReadOnlyList<string> RequestedValueNames => requestedValueNames;

        /// <summary>为每个视图返回一个可读项；HKCU 32 位额外返回一个拒绝项。</summary>
        public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, RegistryView view)
        {
            enumeratedViews.Add((hive, view));
            return hive == RegistryHive.CurrentUser && view == RegistryView.Registry32
                ? ["Denied", "CurrentUser32"]
                : [$"{hive}{view}"];
        }

        /// <summary>记录 production 请求的值名，并只在受控单键上模拟访问拒绝。</summary>
        public string? ReadString(RegistryHive hive, RegistryView view, string keyName, string valueName)
        {
            if (keyName == "Denied")
            {
                throw new UnauthorizedAccessException();
            }

            requestedValueNames.Add(valueName);
            return valueName switch
            {
                "DisplayName" => keyName,
                "Publisher" => "Publisher",
                "InstallLocation" => @"C:\Apps",
                "DisplayIcon" => @"C:\Apps\App.exe,0",
                _ => throw new InvalidOperationException("production reader 请求了未允许的注册表值。")
            };
        }
    }
}
