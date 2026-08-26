using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using DeskButler.Infrastructure.Windows.Native;

namespace DeskButler.Infrastructure.Windows.ResidentApps;

internal enum ExecutableElevationLevel
{
    AsInvoker,
    HighestAvailable,
    RequireAdministrator
}

internal sealed record ExecutableElevationInspection(bool IsReliable, ExecutableElevationLevel Level);

internal interface IExecutableElevationInspector
{
    /// <summary>读取可执行文件 manifest 的请求提升级别，并显式报告可靠性。</summary>
    ExecutableElevationInspection Inspect(string path);
}

internal sealed class WindowsExecutableElevationInspector : IExecutableElevationInspector
{
    private const uint LoadLibraryAsDataFile = 0x00000002;
    private const int ResourceTypeManifest = 24;
    private const int DefaultManifestResourceId = 1;
    private const int ErrorResourceDataNotFound = 1812;
    private const int ErrorResourceTypeNotFound = 1813;
    private const int ErrorResourceNameNotFound = 1814;

    /// <summary>以数据文件方式加载 PE，解析 RT_MANIFEST；资源缺失按 asInvoker，其他不可靠情况拒绝。</summary>
    public ExecutableElevationInspection Inspect(string path)
    {
        using var module = NativeMethods.LoadLibraryEx(path, 0, LoadLibraryAsDataFile);
        if (module.IsInvalid)
        {
            return Unreliable();
        }

        Marshal.SetLastPInvokeError(0);
        var resource = NativeMethods.FindResource(
            module,
            (nint)DefaultManifestResourceId,
            (nint)ResourceTypeManifest);
        if (resource == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            return error is ErrorResourceTypeNotFound or ErrorResourceNameNotFound or ErrorResourceDataNotFound
                ? Reliable(ExecutableElevationLevel.AsInvoker)
                : Unreliable();
        }

        try
        {
            var size = NativeMethods.SizeofResource(module, resource);
            var loadedResource = NativeMethods.LoadResource(module, resource);
            var data = loadedResource == 0 ? 0 : NativeMethods.LockResource(loadedResource);
            if (size == 0 || data == 0 || size > int.MaxValue)
            {
                return Unreliable();
            }

            var bytes = new byte[(int)size];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return Parse(bytes);
        }
        catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
        {
            // manifest 访问或解析不可靠时 fail-closed，绝不猜测成 asInvoker。
            return Unreliable();
        }
    }

    /// <summary>以禁用 DTD 和外部解析器的方式读取 requestedExecutionLevel。</summary>
    private static ExecutableElevationInspection Parse(byte[] manifest)
    {
        using var stream = new MemoryStream(manifest, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        var requestedLevel = document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "requestedExecutionLevel");
        var level = requestedLevel?.Attribute("level")?.Value;
        if (level is null || level.Equals("asInvoker", StringComparison.OrdinalIgnoreCase))
        {
            return Reliable(ExecutableElevationLevel.AsInvoker);
        }

        if (level.Equals("highestAvailable", StringComparison.OrdinalIgnoreCase))
        {
            return Reliable(ExecutableElevationLevel.HighestAvailable);
        }

        return level.Equals("requireAdministrator", StringComparison.OrdinalIgnoreCase)
            ? Reliable(ExecutableElevationLevel.RequireAdministrator)
            : Unreliable();
    }

    /// <summary>构造可靠的提升级别结果。</summary>
    private static ExecutableElevationInspection Reliable(ExecutableElevationLevel level) => new(true, level);

    /// <summary>构造 fail-closed 的不可靠结果，级别值不得被调用方采用。</summary>
    private static ExecutableElevationInspection Unreliable() => new(false, ExecutableElevationLevel.AsInvoker);
}
