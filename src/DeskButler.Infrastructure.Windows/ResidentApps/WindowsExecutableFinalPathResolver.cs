using System.ComponentModel;
using System.Runtime.InteropServices;
using DeskButler.Infrastructure.Windows.Native;

namespace DeskButler.Infrastructure.Windows.ResidentApps;

internal sealed record ExecutableFinalPathResolution(string FinalPath, bool HasReparsePoint);

internal interface IExecutableFinalPathResolver
{
    /// <summary>用只读句柄解析真实最终路径，并报告 source 路径链中的 reparse point。</summary>
    ExecutableFinalPathResolution Resolve(string path);
}

internal sealed class WindowsExecutableFinalPathResolver : IExecutableFinalPathResolver
{
    private const int MaximumWindowsPathLength = 32768;

    /// <summary>拒绝删除共享地持有文件，解析句柄最终路径后检查 source 路径链。</summary>
    public ExecutableFinalPathResolution Resolve(string path)
    {
        // 解析期间不共享删除；句柄在本方法返回即释放，调用方仍须在启动前重新验证以缩小 TOCTOU 窗口。
        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            FileOptions.None);
        var finalPath = ReadFinalPath(handle);
        return new ExecutableFinalPathResolution(finalPath, ContainsReparsePoint(path));
    }

    /// <summary>从已持有文件句柄读取 DOS 形式的最终绝对路径。</summary>
    private static string ReadFinalPath(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var buffer = new char[MaximumWindowsPathLength];
        var length = NativeMethods.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
        {
            throw new IOException(
                "无法可靠解析可执行文件最终路径。",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return NormalizeDevicePath(new string(buffer, 0, (int)length));
    }

    /// <summary>把 Win32 extended DOS/UNC 前缀转换为普通绝对路径。</summary>
    private static string NormalizeDevicePath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string devicePrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        return path.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase)
            ? path[devicePrefix.Length..]
            : path;
    }

    /// <summary>逐段检查 source 路径，目录联接点和文件符号链接都按非普通入口报告。</summary>
    private static bool ContainsReparsePoint(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new IOException("可执行文件路径没有卷根。");
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
