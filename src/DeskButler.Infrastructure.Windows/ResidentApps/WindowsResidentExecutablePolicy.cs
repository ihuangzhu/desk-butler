using System.Security;
using DeskButler.Core.ResidentApps;

namespace DeskButler.Infrastructure.Windows.ResidentApps;

internal interface IExecutableDriveTypeProvider
{
    /// <summary>读取最终路径所在卷的类型。</summary>
    DriveType GetDriveType(string path);
}

internal sealed class WindowsExecutableDriveTypeProvider : IExecutableDriveTypeProvider
{
    /// <summary>通过最终路径的卷根查询 Windows 驱动器类型。</summary>
    public DriveType GetDriveType(string path)
    {
        var root = Path.GetPathRoot(path)
            ?? throw new IOException("最终路径没有卷根。");
        return new DriveInfo(root).DriveType;
    }
}

public sealed class WindowsResidentExecutablePolicy : IResidentExecutablePolicy
{
    private readonly IExecutableFinalPathResolver finalPathResolver;
    private readonly IExecutableDriveTypeProvider driveTypeProvider;
    private readonly IExecutableElevationInspector elevationInspector;

    /// <summary>创建使用真实 Windows 最终路径、卷类型和 manifest 边界的策略。</summary>
    public WindowsResidentExecutablePolicy()
        : this(
            new WindowsExecutableFinalPathResolver(),
            new WindowsExecutableDriveTypeProvider(),
            new WindowsExecutableElevationInspector())
    {
    }

    /// <summary>创建使用可控平台边界的策略，供 Windows 集成测试隔离风险分支。</summary>
    internal WindowsResidentExecutablePolicy(
        IExecutableFinalPathResolver finalPathResolver,
        IExecutableDriveTypeProvider driveTypeProvider,
        IExecutableElevationInspector elevationInspector)
    {
        this.finalPathResolver = finalPathResolver;
        this.driveTypeProvider = driveTypeProvider;
        this.elevationInspector = elevationInspector;
    }

    /// <summary>只允许最终仍位于本地固定卷、非禁止目录且无需提升的普通 exe。</summary>
    public ResidentExecutableValidation Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Reject(ResidentExecutableRejection.InvalidPath);
        }

        string sourcePath;
        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return Reject(ResidentExecutableRejection.NotAbsolutePath);
            }

            sourcePath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Reject(ResidentExecutableRejection.InvalidPath);
        }

        if (sourcePath.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
        {
            return Reject(ResidentExecutableRejection.NetworkPath);
        }

        if (HasNamedDataStream(sourcePath))
        {
            return Reject(ResidentExecutableRejection.InvalidPath);
        }

        if (!Path.GetExtension(sourcePath).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(sourcePath))
        {
            return Reject(ResidentExecutableRejection.NotExecutableFile);
        }

        try
        {
            var resolution = finalPathResolver.Resolve(sourcePath);
            var finalPath = Path.GetFullPath(resolution.FinalPath);
            if (!Path.IsPathFullyQualified(finalPath))
            {
                return Reject(ResidentExecutableRejection.ValidationFailed);
            }

            if (HasNamedDataStream(finalPath))
            {
                return Reject(ResidentExecutableRejection.InvalidPath);
            }

            if (!Path.GetExtension(finalPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return Reject(ResidentExecutableRejection.ValidationFailed);
            }

            if (driveTypeProvider.GetDriveType(finalPath) != DriveType.Fixed)
            {
                return Reject(ResidentExecutableRejection.NonFixedDrive);
            }

            if (IsInProhibitedDirectory(finalPath))
            {
                return Reject(ResidentExecutableRejection.ProhibitedDirectory);
            }

            if (resolution.HasReparsePoint)
            {
                return Reject(ResidentExecutableRejection.ReparsePoint);
            }

            var elevation = elevationInspector.Inspect(finalPath);
            if (!elevation.IsReliable)
            {
                return Reject(ResidentExecutableRejection.ValidationFailed);
            }

            return elevation.Level == ExecutableElevationLevel.AsInvoker
                ? new ResidentExecutableValidation(true, finalPath, ResidentExecutableRejection.None)
                : Reject(ResidentExecutableRejection.RequiresElevation);
        }
        catch (FileNotFoundException)
        {
            return Reject(ResidentExecutableRejection.FileNotFound);
        }
        catch (DirectoryNotFoundException)
        {
            return Reject(ResidentExecutableRejection.FileNotFound);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
        {
            return Reject(ResidentExecutableRejection.AccessDenied);
        }
        catch
        {
            // 策略依赖异常只暴露稳定分类，不把本地路径或底层异常文本带入诊断。
            return Reject(ResidentExecutableRejection.ValidationFailed);
        }
    }

    /// <summary>允许卷根自身的盘符冒号，但拒绝卷根之后表示 NTFS 命名数据流的任何冒号。</summary>
    private static bool HasNamedDataStream(string path)
    {
        var root = Path.GetPathRoot(path)
            ?? throw new IOException("路径没有卷根。");
        return path.IndexOf(':', root.Length) >= 0;
    }

    /// <summary>解析并验证所有固定禁止根；任一系统目录获取失败会由外层 fail-closed。</summary>
    private static bool IsInProhibitedDirectory(string path)
    {
        var windows = RequireDirectory(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var localAppData = RequireDirectory(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var programData = RequireDirectory(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        var roots = new[]
        {
            windows,
            RequireDirectory(Path.GetTempPath()),
            Path.Combine(localAppData, "Temp"),
            Path.Combine(windows, "Installer"),
            Path.Combine(programData, "Package Cache")
        };
        return roots.Any(root => IsWithinDirectory(path, root));
    }

    /// <summary>要求环境目录可解析为绝对路径，否则禁止继续做安全判断。</summary>
    private static string RequireDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new IOException("无法可靠解析 Windows 安全目录。");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>使用目录分隔符边界判断相等或后代路径，避免 WindowsOld 前缀误判。</summary>
    private static bool IsWithinDirectory(string path, string directory)
    {
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return path.Equals(normalizedDirectory, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(
                   normalizedDirectory + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>构造不携带路径和异常文本的拒绝结果。</summary>
    private static ResidentExecutableValidation Reject(ResidentExecutableRejection reason) =>
        new(false, null, reason);
}
