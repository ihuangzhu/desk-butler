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
    private const string UninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>读取四个卸载注册表视图；单个 key 的访问失败不会阻止其它 key。</summary>
    public UninstallRegistrySnapshot Read(CancellationToken cancellationToken)
    {
        var entries = ImmutableArray.CreateBuilder<UninstallRegistryEntry>();
        var diagnostics = ImmutableArray.CreateBuilder<ResidentDiscoveryDiagnostic>();
        foreach (var (hive, view) in Views)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadView(hive, view, entries, diagnostics, cancellationToken);
        }

        return new UninstallRegistrySnapshot(entries.ToImmutable(), diagnostics.ToImmutable());
    }

    /// <summary>逐个打开子项并隔离访问拒绝，避免一个损坏安装项丢弃完整目录。</summary>
    private static void ReadView(
        RegistryHive hive,
        RegistryView view,
        ImmutableArray<UninstallRegistryEntry>.Builder entries,
        ImmutableArray<ResidentDiscoveryDiagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(UninstallSubKey, writable: false);
            if (uninstallKey is null)
            {
                return;
            }

            foreach (var keyName in uninstallKey.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var applicationKey = uninstallKey.OpenSubKey(keyName, writable: false);
                    if (applicationKey is null)
                    {
                        continue;
                    }

                    entries.Add(new UninstallRegistryEntry(
                        ReadString(applicationKey, "DisplayName"),
                        ReadString(applicationKey, "Publisher"),
                        ReadString(applicationKey, "InstallLocation"),
                        ReadString(applicationKey, "DisplayIcon")));
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

    /// <summary>读取字符串值且禁止环境变量展开，不读取或解释 UninstallString。</summary>
    private static string? ReadString(RegistryKey key, string valueName) =>
        key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

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
