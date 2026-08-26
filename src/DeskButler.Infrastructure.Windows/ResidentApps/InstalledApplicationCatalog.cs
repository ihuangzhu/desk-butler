using System.Collections.Immutable;
using System.Globalization;
using DeskButler.Core.ResidentApps;

namespace DeskButler.Infrastructure.Windows.ResidentApps;

/// <summary>定义只读已安装应用目录边界。</summary>
internal interface IInstalledApplicationCatalog
{
    /// <summary>读取允许公开的卸载目录字段。</summary>
    Task<InstalledApplicationSnapshot> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>把只读卸载注册表字段正规化为后续发现可消费的应用目录。</summary>
internal sealed class InstalledApplicationCatalog : IInstalledApplicationCatalog
{
    private readonly IUninstallRegistryReader registryReader;

    /// <summary>创建使用真实 Windows 卸载注册表读取器的目录。</summary>
    internal InstalledApplicationCatalog()
        : this(new WindowsUninstallRegistryReader())
    {
    }

    /// <summary>创建使用可控只读注册表边界的目录。</summary>
    internal InstalledApplicationCatalog(IUninstallRegistryReader registryReader)
    {
        this.registryReader = registryReader;
    }

    /// <summary>只保留可正规化公开字段，不归组、去重或解释任何卸载命令。</summary>
    public Task<InstalledApplicationSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registrySnapshot = registryReader.Read(cancellationToken);
        var entries = ImmutableArray.CreateBuilder<InstalledApplicationEntry>();
        var diagnostics = ImmutableArray.CreateBuilder<ResidentDiscoveryDiagnostic>();
        diagnostics.AddRange(registrySnapshot.Diagnostics);

        foreach (var registryEntry in registrySnapshot.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var displayName = NormalizeText(registryEntry.DisplayName);
            if (displayName is null)
            {
                diagnostics.Add(new ResidentDiscoveryDiagnostic(ResidentDiscoveryIssue.InvalidPath));
                continue;
            }

            var publisher = NormalizeText(registryEntry.Publisher);
            var installRoot = NormalizeOptionalPath(registryEntry.InstallLocation, diagnostics);
            var displayIconPath = NormalizeDisplayIcon(registryEntry.DisplayIcon, diagnostics);
            entries.Add(new InstalledApplicationEntry(displayName, publisher, installRoot, displayIconPath));
        }

        return Task.FromResult(new InstalledApplicationSnapshot(
            entries
                .OrderBy(entry => entry.DisplayName, StringComparer.Ordinal)
                .ThenBy(entry => entry.Publisher, StringComparer.Ordinal)
                .ThenBy(entry => entry.InstallRoot, StringComparer.Ordinal)
                .ThenBy(entry => entry.DisplayIconPath, StringComparer.Ordinal)
                .ToImmutableArray(),
            diagnostics.OrderBy(diagnostic => diagnostic.Kind).ToImmutableArray()));
    }

    /// <summary>正规化可选安装根；无值保持空，畸形值只产生分类诊断。</summary>
    private static string? NormalizeOptionalPath(
        string? value,
        ImmutableArray<ResidentDiscoveryDiagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return TryNormalizeAbsolutePath(value.Trim(), out var path)
            ? path
            : AddInvalidPath(diagnostics);
    }

    /// <summary>仅从 DisplayIcon 安全剥离外围引号和末尾数值索引，不解析命令行语法。</summary>
    private static string? NormalizeDisplayIcon(
        string? value,
        ImmutableArray<ResidentDiscoveryDiagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = RemoveTrailingIconIndex(value.Trim());
        if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"')
        {
            candidate = candidate[1..^1].Trim();
        }

        return TryNormalizeAbsolutePath(candidate, out var path)
            ? path
            : AddInvalidPath(diagnostics);
    }

    /// <summary>只移除标准的逗号加整数图标索引；其它字符全部保持为路径输入。</summary>
    private static string RemoveTrailingIconIndex(string value)
    {
        var comma = value.LastIndexOf(',');
        if (comma < 0 || !int.TryParse(
                value[(comma + 1)..].Trim(),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out _))
        {
            return value;
        }

        return value[..comma].TrimEnd();
    }

    /// <summary>只接受无控制字符的绝对路径，不访问文件系统、不展开环境变量也不验证可执行性。</summary>
    private static bool TryNormalizeAbsolutePath(string value, out string? path)
    {
        path = null;
        if (value.Any(char.IsControl) || !Path.IsPathFullyQualified(value))
        {
            return false;
        }

        try
        {
            path = Path.GetFullPath(value);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>去除公开文本的首尾空白并拒绝控制字符，避免把注册表原文透传到模型。</summary>
    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Any(char.IsControl) ? null : normalized;
    }

    /// <summary>追加不含畸形原文的路径分类诊断并返回空字段。</summary>
    private static string? AddInvalidPath(ImmutableArray<ResidentDiscoveryDiagnostic>.Builder diagnostics)
    {
        diagnostics.Add(new ResidentDiscoveryDiagnostic(ResidentDiscoveryIssue.InvalidPath));
        return null;
    }
}
