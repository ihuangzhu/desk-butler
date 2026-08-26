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

    /// <summary>以禁用 DTD 和外部解析器的方式读取唯一且位于 Windows 支持层级中的 requestedExecutionLevel。</summary>
    private static ExecutableElevationInspection Parse(byte[] manifest)
    {
        XNamespace assemblyV1 = "urn:schemas-microsoft-com:asm.v1";
        XNamespace assemblyV2 = "urn:schemas-microsoft-com:asm.v2";
        XNamespace assemblyV3 = "urn:schemas-microsoft-com:asm.v3";
        using var stream = new MemoryStream(manifest, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root;
        if (root?.Name != assemblyV1 + "assembly")
        {
            return Unreliable();
        }

        var allRequestedLevels = document.Descendants()
            .Where(element => element.Name.LocalName == "requestedExecutionLevel")
            .Take(2)
            .ToArray();
        var supportedRequestedLevels = FindSupportedRequestedLevels(root, assemblyV2, assemblyV3)
            .Take(2)
            .ToArray();
        if (allRequestedLevels.Length != 1 ||
            supportedRequestedLevels.Length != 1 ||
            !ReferenceEquals(allRequestedLevels[0], supportedRequestedLevels[0]))
        {
            // manifest 资源存在时，缺失、重复、错层级或错 namespace 都无法可靠确定 UAC 行为。
            return Unreliable();
        }

        var level = supportedRequestedLevels[0].Attribute("level")?.Value;
        if (level == "asInvoker")
        {
            return Reliable(ExecutableElevationLevel.AsInvoker);
        }

        if (level == "highestAvailable")
        {
            return Reliable(ExecutableElevationLevel.HighestAvailable);
        }

        return level == "requireAdministrator"
            ? Reliable(ExecutableElevationLevel.RequireAdministrator)
            : Unreliable();
    }

    /// <summary>枚举 Windows manifest 明确支持的 asm.v2、v2/v3 混合与 asm.v3 请求权限层级。</summary>
    private static IEnumerable<XElement> FindSupportedRequestedLevels(
        XElement root,
        XNamespace assemblyV2,
        XNamespace assemblyV3)
    {
        return FindRequestedLevels(root, assemblyV2, assemblyV2)
            .Concat(FindRequestedLevels(root, assemblyV2, assemblyV3))
            .Concat(FindRequestedLevels(root, assemblyV3, assemblyV3));
    }

    /// <summary>按指定 trustInfo/security 与 requestedPrivileges 命名空间枚举完整的直接子级链。</summary>
    private static IEnumerable<XElement> FindRequestedLevels(
        XElement root,
        XNamespace trustNamespace,
        XNamespace privilegesNamespace)
    {
        return root.Elements(trustNamespace + "trustInfo")
            .SelectMany(trustInfo => trustInfo.Elements(trustNamespace + "security"))
            .SelectMany(security => security.Elements(privilegesNamespace + "requestedPrivileges"))
            .SelectMany(privileges => privileges.Elements(privilegesNamespace + "requestedExecutionLevel"));
    }

    /// <summary>构造可靠的提升级别结果。</summary>
    private static ExecutableElevationInspection Reliable(ExecutableElevationLevel level) => new(true, level);

    /// <summary>构造 fail-closed 的不可靠结果，级别值不得被调用方采用。</summary>
    private static ExecutableElevationInspection Unreliable() => new(false, ExecutableElevationLevel.AsInvoker);
}
