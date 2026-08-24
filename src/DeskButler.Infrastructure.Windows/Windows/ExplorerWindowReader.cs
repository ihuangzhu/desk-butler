using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DeskButler.Infrastructure.Windows.Windows;

/// <summary>按窗口句柄读取资源管理器公开的本地目录位置。</summary>
public sealed class ExplorerWindowReader : IExplorerWindowReader
{
    private readonly IExplorerWindowSource source;
    private readonly Func<string, bool> directoryExists;

    /// <summary>创建使用 Shell Windows COM 枚举的读取器。</summary>
    public ExplorerWindowReader()
        : this(new ComExplorerWindowSource(), Directory.Exists)
    {
    }

    /// <summary>创建使用可控 Shell 来源和目录验证函数的读取器。</summary>
    internal ExplorerWindowReader(IExplorerWindowSource source, Func<string, bool> directoryExists)
    {
        this.source = source;
        this.directoryExists = directoryExists;
    }

    /// <summary>返回匹配 HWND 且确实存在的本地 file 目录；远程位置与失败均返回空。</summary>
    public string? TryGetFolderPath(nint windowHandle)
    {
        foreach (var location in source.ReadLocations())
        {
            if (location.Handle != windowHandle ||
                !Uri.TryCreate(location.LocationUrl, UriKind.Absolute, out var uri) ||
                !uri.IsFile ||
                uri.IsUnc ||
                !uri.IsLoopback)
            {
                continue;
            }

            var path = uri.LocalPath;
            if (directoryExists(path))
            {
                return path;
            }
        }

        return null;
    }
}

/// <summary>定义 Explorer HWND 到本地目录的受限读取边界。</summary>
public interface IExplorerWindowReader
{
    /// <summary>尝试读取指定 Explorer 窗口当前公开的本地目录。</summary>
    string? TryGetFolderPath(nint windowHandle);
}

internal interface IExplorerWindowSource
{
    /// <summary>读取 Shell Windows 公开的 HWND 和位置 URL。</summary>
    IReadOnlyList<ExplorerWindowLocation> ReadLocations();
}

/// <summary>保存 Shell Windows 公开的 HWND 与位置 URL。</summary>
/// <param name="Handle">Explorer 窗口句柄。</param>
/// <param name="LocationUrl">Shell 公开的位置 URL。</param>
internal sealed record ExplorerWindowLocation(nint Handle, string? LocationUrl);

internal sealed class ComExplorerWindowSource : IExplorerWindowSource
{
    /// <summary>通过 Shell.Application 枚举 Explorer 窗口，并明确释放每个 COM 对象。</summary>
    public IReadOnlyList<ExplorerWindowLocation> ReadLocations()
    {
        var locations = new List<ExplorerWindowLocation>();
        object? shell = null;
        object? windows = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application", throwOnError: false);
            if (shellType is null)
            {
                return locations;
            }

            shell = Activator.CreateInstance(shellType);
            windows = Invoke(shell, "Windows", BindingFlags.InvokeMethod);
            var countValue = Invoke(windows, "Count", BindingFlags.GetProperty);
            var count = Convert.ToInt32(countValue, CultureInfo.InvariantCulture);
            for (var index = 0; index < count; index++)
            {
                object? window = null;
                try
                {
                    window = Invoke(windows, "Item", BindingFlags.InvokeMethod, index);
                    if (window is null)
                    {
                        continue;
                    }

                    var handle = Convert.ToInt64(Invoke(window, "HWND", BindingFlags.GetProperty), CultureInfo.InvariantCulture);
                    var locationUrl = Convert.ToString(Invoke(window, "LocationURL", BindingFlags.GetProperty), CultureInfo.InvariantCulture);
                    locations.Add(new ExplorerWindowLocation((nint)handle, locationUrl));
                }
                catch (Exception exception) when (IsRecoverableComFailure(exception))
                {
                    // Shell 窗口可能在枚举期间关闭；只忽略该项并继续捕获其余窗口。
                }
                finally
                {
                    ReleaseComObject(window);
                }
            }
        }
        catch (Exception exception) when (IsRecoverableComFailure(exception))
        {
            // Explorer 未运行或 COM 暂时不可用时，不影响普通窗口清单捕获。
        }
        finally
        {
            // 释放顺序与获取顺序相反，避免 Shell RCW 跨捕获周期存活。
            ReleaseComObject(windows);
            ReleaseComObject(shell);
        }

        return locations;
    }

    /// <summary>调用 Shell IDispatch 成员，集中约束反射绑定方式。</summary>
    private static object? Invoke(object? target, string member, BindingFlags flags, params object?[]? arguments)
    {
        return target?.GetType().InvokeMember(member, flags, null, target, arguments, CultureInfo.InvariantCulture);
    }

    /// <summary>判断异常是否属于可安全降级的 Shell/反射失败。</summary>
    private static bool IsRecoverableComFailure(Exception exception)
    {
        return exception is COMException or
            InvalidComObjectException or
            TargetInvocationException or
            MissingMemberException or
            ArgumentException or
            InvalidOperationException;
    }

    /// <summary>释放当前方法持有的 COM 引用；托管对象无需处理。</summary>
    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try
            {
                Marshal.FinalReleaseComObject(value);
            }
            catch (Exception exception) when (exception is COMException or InvalidComObjectException or ArgumentException)
            {
                // Shell 可能已在枚举期间退出；RCW 已失效时无需再次释放。
            }
        }
    }
}
