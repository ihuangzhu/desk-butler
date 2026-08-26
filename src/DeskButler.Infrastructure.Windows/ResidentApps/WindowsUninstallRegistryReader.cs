using System.Collections.Immutable;
using System.Security;
using DeskButler.Core.ResidentApps;
using Microsoft.Win32;

namespace DeskButler.Infrastructure.Windows.ResidentApps;

/// <summary>定义只读取四个 Windows 卸载注册表视图的边界。</summary>
internal interface IUninstallRegistryReader
{
    /// <summary>读取卸载项公开字段和无敏感负载的逐键诊断。</summary>
    UninstallRegistrySnapshot Read(CancellationToken cancellationToken);
}

/// <summary>定义卸载目录 reader 实际需要的最小注册表值读取边界。</summary>
internal interface IUninstallRegistryNativeApi
{
    /// <summary>读取指定 hive/view 下 Uninstall 的子键名。</summary>
    IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, RegistryView view);

    /// <summary>读取指定卸载子键的一个非展开字符串值。</summary>
    string? ReadString(RegistryHive hive, RegistryView view, string keyName, string valueName);
}

/// <summary>保存单一卸载注册表项允许读取的字段。</summary>
internal sealed record UninstallRegistryEntry(
    string? DisplayName,
    string? Publisher,
    string? InstallLocation,
    string? DisplayIcon);

/// <summary>保存注册表读取器的不可变原始公开条目与分类诊断。</summary>
internal sealed record UninstallRegistrySnapshot(
    ImmutableArray<UninstallRegistryEntry> Entries,
    ImmutableArray<ResidentDiscoveryDiagnostic> Diagnostics);

/// <summary>只读 HKCU/HKLM 32/64 位 Uninstall 注册表项，不读取卸载命令。</summary>
internal sealed class WindowsUninstallRegistryReader : IUninstallRegistryReader
{
    private readonly IUninstallRegistryNativeApi native;

    /// <summary>创建使用真实只读注册表边界的读取器。</summary>
    internal WindowsUninstallRegistryReader()
        : this(new WindowsUninstallRegistryNativeApi())
    {
    }

    /// <summary>创建使用可控最小注册表边界的读取器，供测试验证四视图和逐键隔离。</summary>
    internal WindowsUninstallRegistryReader(IUninstallRegistryNativeApi native)
    {
        this.native = native;
    }

    /// <summary>读取四个卸载注册表视图；单个 key 的访问失败不会阻止其它 key。</summary>
    public UninstallRegistrySnapshot Read(CancellationToken cancellationToken)
    {
        var entries = ImmutableArray.CreateBuilder<UninstallRegistryEntry>();
        var diagnostics = ImmutableArray.CreateBuilder<ResidentDiscoveryDiagnostic>();
        foreach (var (hive, view) in Views)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadView(native, hive, view, entries, diagnostics, cancellationToken);
        }

        return new UninstallRegistrySnapshot(entries.ToImmutable(), diagnostics.ToImmutable());
    }

    /// <summary>逐个打开子项并隔离访问拒绝，避免一个损坏安装项丢弃完整目录。</summary>
    private static void ReadView(
        IUninstallRegistryNativeApi native,
        RegistryHive hive,
        RegistryView view,
        ImmutableArray<UninstallRegistryEntry>.Builder entries,
        ImmutableArray<ResidentDiscoveryDiagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var keyName in native.GetSubKeyNames(hive, view))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    entries.Add(new UninstallRegistryEntry(
                        native.ReadString(hive, view, keyName, "DisplayName"),
                        native.ReadString(hive, view, keyName, "Publisher"),
                        native.ReadString(hive, view, keyName, "InstallLocation"),
                        native.ReadString(hive, view, keyName, "DisplayIcon")));
                }
                catch (Exception exception) when (IsAccessDenied(exception))
                {
                    // 注册表拒绝诊断绝不包含 key 名、值或异常文本，避免泄露安装信息。
                    diagnostics.Add(new ResidentDiscoveryDiagnostic(ResidentDiscoveryIssue.RegistryAccessDenied));
                }
                catch (Exception exception) when (IsRecoverableRegistryFailure(exception))
                {
                    diagnostics.Add(new ResidentDiscoveryDiagnostic(ResidentDiscoveryIssue.SourceFailure));
                }
            }
        }
        catch (Exception exception) when (IsAccessDenied(exception))
        {
            diagnostics.Add(new ResidentDiscoveryDiagnostic(ResidentDiscoveryIssue.RegistryAccessDenied));
        }
        catch (Exception exception) when (IsRecoverableRegistryFailure(exception))
        {
            diagnostics.Add(new ResidentDiscoveryDiagnostic(ResidentDiscoveryIssue.SourceFailure));
        }
    }

    /// <summary>判断注册表访问拒绝这一可预期的单键失败。</summary>
    private static bool IsAccessDenied(Exception exception) =>
        exception is UnauthorizedAccessException or SecurityException;

    /// <summary>判断可安全降级为源故障的注册表 IO 竞态。</summary>
    private static bool IsRecoverableRegistryFailure(Exception exception) =>
        exception is IOException or ArgumentException;

    private static readonly (RegistryHive Hive, RegistryView View)[] Views =
    [
        (RegistryHive.CurrentUser, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.LocalMachine, RegistryView.Registry64)
    ];
}

/// <summary>通过 Windows 注册表 API 实现只读卸载目录的最小值读取边界。</summary>
internal sealed class WindowsUninstallRegistryNativeApi : IUninstallRegistryNativeApi
{
    private const string UninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>只读打开指定视图的 Uninstall 根并返回子键名。</summary>
    public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, RegistryView view)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var uninstallKey = baseKey.OpenSubKey(UninstallSubKey, writable: false);
        return uninstallKey?.GetSubKeyNames() ?? [];
    }

    /// <summary>只读取调用方指定的公开字符串值，禁止环境变量展开且不接触卸载命令。</summary>
    public string? ReadString(RegistryHive hive, RegistryView view, string keyName, string valueName)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var uninstallKey = baseKey.OpenSubKey(UninstallSubKey, writable: false);
        using var applicationKey = uninstallKey?.OpenSubKey(keyName, writable: false);
        return applicationKey?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }
}
