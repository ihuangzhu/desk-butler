using System.Diagnostics;
using DeskButler.Core.Restore;
using DeskButler.Core.Scenes;

namespace DeskButler.Infrastructure.Windows.Restore;

/// <summary>通过 Windows Shell 安全启动普通程序或明确的本地 Explorer 目录。</summary>
public sealed class WindowsAppLauncher : IAppLauncher
{
    private readonly IProcessStarter processStarter;
    private readonly Func<string, bool> directoryExists;
    private readonly string windowsDirectory;

    /// <summary>创建使用真实 Process.Start、目录检查和 WINDIR 的适配器。</summary>
    public WindowsAppLauncher()
        : this(new ProcessStarter(), Directory.Exists,
            Environment.GetEnvironmentVariable("WINDIR")
            ?? throw new InvalidOperationException("WINDIR 环境变量不可用。"))
    {
    }

    /// <summary>创建使用可控进程和文件系统边界的适配器。</summary>
    internal WindowsAppLauncher(
        IProcessStarter processStarter,
        Func<string, bool> directoryExists,
        string windowsDirectory)
    {
        this.processStarter = processStarter ?? throw new ArgumentNullException(nameof(processStarter));
        this.directoryExists = directoryExists ?? throw new ArgumentNullException(nameof(directoryExists));
        this.windowsDirectory = string.IsNullOrWhiteSpace(windowsDirectory)
            ? throw new ArgumentException("Windows 目录不能为空。", nameof(windowsDirectory))
            : windowsDirectory;
    }

    /// <summary>构造受限 ProcessStartInfo，启动后立即释放借用的 Process 对象。</summary>
    public Task LaunchAsync(SceneItem sceneItem, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sceneItem);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = string.IsNullOrWhiteSpace(sceneItem.ExplorerPath)
            ? CreateApplicationStartInfo(sceneItem.ExecutablePath)
            : CreateExplorerStartInfo(sceneItem.ExplorerPath);
        using var process = processStarter.Start(startInfo);
        return Task.CompletedTask;
    }

    /// <summary>普通程序只使用捕获的可执行路径，不携带命令行或任意参数。</summary>
    private static ProcessStartInfo CreateApplicationStartInfo(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException("普通程序启动路径必须是绝对路径。", nameof(executablePath));
        }

        return new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true
        };
    }

    /// <summary>Explorer 固定使用 WINDIR 下的 exe，并通过 ArgumentList 传递唯一目录参数。</summary>
    private ProcessStartInfo CreateExplorerStartInfo(string explorerPath)
    {
        var normalizedPath = NormalizeLocalDirectory(explorerPath);
        if (!directoryExists(normalizedPath))
        {
            throw new DirectoryNotFoundException($"Explorer 目录不存在：{normalizedPath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(windowsDirectory, "explorer.exe"),
            UseShellExecute = true
        };
        // ArgumentList 让 .NET 按 Windows 参数规则处理空格和引号，避免手工拼接注入。
        startInfo.ArgumentList.Add(normalizedPath);
        return startInfo;
    }

    /// <summary>只接受盘符根的绝对本地目录，明确拒绝 UNC、URI、相对路径和非法字符。</summary>
    private static string NormalizeLocalDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length < 3 ||
            !char.IsLetter(path[0]) ||
            path[1] != Path.VolumeSeparatorChar ||
            !Path.EndsInDirectorySeparator(path[..3]) ||
            HasInvalidLocalPathCharacters(path) ||
            !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Explorer 仅允许绝对本地目录。", nameof(path));
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("Explorer 目录格式无效。", nameof(path), exception);
        }
    }

    /// <summary>拒绝控制字符、Windows 非法字符，以及盘符前缀之外的任意冒号。</summary>
    private static bool HasInvalidLocalPathCharacters(string path)
    {
        for (var index = 0; index < path.Length; index++)
        {
            var character = path[index];
            if (char.IsControl(character) ||
                character is '"' or '<' or '>' or '|' or '?' or '*' ||
                character == ':' && index != 1)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>隔离 Process.Start，便于验证完整启动信息而不启动真实程序。</summary>
internal interface IProcessStarter
{
    /// <summary>启动进程并返回仅供立即释放的托管包装；Shell 不返回包装时允许为空。</summary>
    IDisposable? Start(ProcessStartInfo startInfo);
}

/// <summary>真实 Process.Start 适配器。</summary>
internal sealed class ProcessStarter : IProcessStarter
{
    /// <summary>调用 Windows Shell 启动并把 Process 生命周期交回调用方立即释放。</summary>
    public IDisposable? Start(ProcessStartInfo startInfo) => Process.Start(startInfo);
}
