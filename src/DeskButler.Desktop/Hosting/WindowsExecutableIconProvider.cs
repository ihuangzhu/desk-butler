using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DeskButler.Desktop.Hosting;

/// <summary>把 Windows 可执行文件图标转换为可跨线程绑定的纯内存位图。</summary>
public sealed class WindowsExecutableIconProvider : IExecutableIconProvider
{
    private readonly IExecutableIconProvider fallback;

    /// <summary>创建正式图标提供器；无法提取时仍返回不接触目标文件的内存回退图标。</summary>
    public WindowsExecutableIconProvider(IExecutableIconProvider? fallback = null)
    {
        this.fallback = fallback ?? new FallbackExecutableIconProvider();
    }

    /// <summary>
    /// 提取并冻结关联图标。Icon 由本方法创建并负责 Dispose；绝不额外 DestroyIcon，
    /// 因为 Dispose 已拥有原生句柄所有权，而位图源在冻结前已完成内存复制。
    /// </summary>
    public ImageSource GetIcon(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath) || !HasPortableExecutableHeader(executablePath))
        {
            return fallback.GetIcon(executablePath)!;
        }

        try
        {
            var path = executablePath!;
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return fallback.GetIcon(executablePath)!;
            }

            var image = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // 图标提取是纯展示边界；损坏、拒绝访问或 Shell 失败均不能阻断候选确认。
            return fallback.GetIcon(executablePath)!;
        }
    }

    /// <summary>只读取 PE 签名以拒绝损坏伪 .exe；流在返回前释放，不改变图标句柄边界。</summary>
    private static bool HasPortableExecutableHeader(string executablePath)
    {
        try
        {
            using var stream = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: false);
            if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D)
            {
                return false;
            }

            stream.Position = 60;
            var headerOffset = reader.ReadInt32();
            if (headerOffset < 0 || stream.Length < headerOffset + 4)
            {
                return false;
            }

            stream.Position = headerOffset;
            return reader.ReadUInt32() == 0x00004550;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
