namespace DeskButler.Core.ResidentApps;

/// <summary>表示常驻应用设置正规化时的稳定分类问题。</summary>
public enum ResidentNormalizationIssue
{
    /// <summary>启动路径或识别路径无效。</summary>
    InvalidPath,

    /// <summary>后续条目的启动入口与前项启动入口重复。</summary>
    DuplicateLaunchPath,

    /// <summary>后续条目的启动入口已被前项作为识别路径占用。</summary>
    LaunchPathConflict,

    /// <summary>后续条目的识别路径已被前项占用。</summary>
    KnownPathConflict
}

/// <summary>保存不含原始异常消息的正规化诊断。</summary>
/// <param name="Kind">分类问题。</param>
/// <param name="SourceIndex">问题条目在输入中的从零开始位置。</param>
public sealed record ResidentNormalizationDiagnostic(ResidentNormalizationIssue Kind, int SourceIndex);

/// <summary>保存常驻应用设置正规化后的有效条目和诊断。</summary>
public sealed record ResidentNormalizationResult(
    IReadOnlyList<ResidentApplication> Applications,
    IReadOnlyList<ResidentNormalizationDiagnostic> Diagnostics);

/// <summary>将常驻应用设置转换为稳定、可持久化的形式。</summary>
public static class ResidentApplicationNormalizer
{
    /// <summary>逐项正规化常驻应用，并隔离单项路径错误和路径冲突。</summary>
    /// <param name="source">待正规化的常驻应用条目。</param>
    /// <returns>按稳定启动顺序排列的有效条目及不含异常消息的诊断。</returns>
    public static ResidentNormalizationResult Normalize(IEnumerable<ResidentApplication> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var diagnostics = new List<ResidentNormalizationDiagnostic>();
        var normalized = new List<NormalizedApplication>();
        var launchPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceIndex = 0;

        foreach (var application in source)
        {
            if (!TryNormalizePath(application.LaunchPath, out var launchPath))
            {
                diagnostics.Add(new(ResidentNormalizationIssue.InvalidPath, sourceIndex));
                sourceIndex++;
                continue;
            }

            var currentKnownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var knownProcessPath in application.KnownProcessPaths)
            {
                if (!TryNormalizePath(knownProcessPath, out var knownPath))
                {
                    diagnostics.Add(new(ResidentNormalizationIssue.InvalidPath, sourceIndex));
                    continue;
                }

                currentKnownPaths.Add(knownPath);
            }

            currentKnownPaths.Add(launchPath);
            if (!launchPaths.Add(launchPath))
            {
                diagnostics.Add(new(ResidentNormalizationIssue.DuplicateLaunchPath, sourceIndex));
                sourceIndex++;
                continue;
            }

            if (knownPaths.Contains(launchPath))
            {
                launchPaths.Remove(launchPath);
                diagnostics.Add(new(ResidentNormalizationIssue.LaunchPathConflict, sourceIndex));
                sourceIndex++;
                continue;
            }

            var hasKnownPathConflict = currentKnownPaths.Overlaps(knownPaths);
            currentKnownPaths.RemoveWhere(knownPaths.Contains);
            if (hasKnownPathConflict)
            {
                diagnostics.Add(new(ResidentNormalizationIssue.KnownPathConflict, sourceIndex));
            }

            currentKnownPaths.Add(launchPath);
            knownPaths.UnionWith(currentKnownPaths);
            var displayName = string.IsNullOrWhiteSpace(application.DisplayName)
                ? Path.GetFileNameWithoutExtension(launchPath)
                : application.DisplayName;
            normalized.Add(new(
                new ResidentApplication(
                    launchPath,
                    currentKnownPaths,
                    displayName,
                    application.Enabled,
                    application.LaunchOrder),
                sourceIndex));
            sourceIndex++;
        }

        var applications = normalized
            .OrderBy(item => item.Application.LaunchOrder)
            .ThenBy(item => item.Application.LaunchPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SourceIndex)
            .Select((item, launchOrder) => item.Application with { LaunchOrder = launchOrder })
            .ToArray();

        return new(applications, diagnostics);
    }

    /// <summary>按 Windows 路径语义将单一路径转换为绝对且无冗余尾部分隔符的形式。</summary>
    /// <param name="path">待转换的路径。</param>
    /// <param name="normalizedPath">成功时得到的正规化路径。</param>
    /// <returns>路径是否可安全正规化。</returns>
    private static bool TryNormalizePath(string path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    private sealed record NormalizedApplication(ResidentApplication Application, int SourceIndex);
}
