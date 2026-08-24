using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;

namespace DeskButler.Persistence.Diagnostics;

/// <summary>表示用户可在写 ZIP 前检查的单个诊断文件。</summary>
public sealed class DiagnosticBundleFile
{
    /// <summary>仅由导出器创建不可伪造的脱敏文件快照。</summary>
    internal DiagnosticBundleFile(string archiveName, string preview, byte[] content)
    {
        ArchiveName = archiveName;
        Preview = preview;
        Content = content;
    }

    /// <summary>获取 ZIP 内安全相对名称。</summary>
    public string ArchiveName { get; }

    /// <summary>获取用户可见的脱敏文本。</summary>
    public string Preview { get; }

    /// <summary>获取脱敏快照字节数。</summary>
    public int ByteCount => Content.Length;

    internal byte[] Content { get; }
}

/// <summary>表示只包含明确白名单和脱敏结果的诊断包预览。</summary>
public sealed class DiagnosticBundleManifest
{
    /// <summary>仅由所属导出器创建预览清单，防止调用方注入任意文件内容。</summary>
    internal DiagnosticBundleManifest(DiagnosticBundleExporter owner, IReadOnlyList<DiagnosticBundleFile> files)
    {
        Owner = owner;
        Files = files;
    }

    /// <summary>获取按白名单顺序排列的预览文件。</summary>
    public IReadOnlyList<DiagnosticBundleFile> Files { get; }

    internal DiagnosticBundleExporter Owner { get; }
}

/// <summary>分两阶段预览并导出只读白名单诊断文件。</summary>
public sealed class DiagnosticBundleExporter
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private static readonly HashSet<string> RemovedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "commandLine", "token", "password", "clipboard"
    };
    private readonly string rootDirectory;
    private readonly string userProfile;
    private readonly string[] approvedNames;

    /// <summary>使用明确根目录、用户目录和相对文件白名单创建导出器。</summary>
    public DiagnosticBundleExporter(string rootDirectory, string userProfile, IEnumerable<string> approvedNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfile);
        ArgumentNullException.ThrowIfNull(approvedNames);
        this.rootDirectory = Path.GetFullPath(rootDirectory);
        this.userProfile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userProfile));
        this.approvedNames = approvedNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>稳定读取白名单文件快照并生成用户可预览的脱敏清单。</summary>
    public async Task<DiagnosticBundleManifest> CreateManifestAsync(CancellationToken cancellationToken)
    {
        var files = new List<DiagnosticBundleFile>();
        var archiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manifest.json" };
        using var rootHandle = OpenDirectoryHandle(rootDirectory);
        var finalRoot = GetFinalPath(rootHandle);
        foreach (var configuredName in approvedNames)
        {
            var approvedName = NormalizeArchiveName(configuredName);
            if (!archiveNames.Add(approvedName))
            {
                throw new InvalidOperationException($"诊断白名单包含冲突归档名：{configuredName}");
            }

            var path = ResolveApprovedPath(approvedName);
            if (!File.Exists(path))
            {
                continue;
            }

            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("诊断白名单不得包含重解析点。");
            }

            RejectReparseComponents(path);
            byte[] content;
            await using (var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var finalFile = GetFinalPath(stream.SafeFileHandle);
                var finalPrefix = Path.TrimEndingDirectorySeparator(finalRoot) + Path.DirectorySeparatorChar;
                if (!finalFile.StartsWith(finalPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("诊断白名单句柄最终路径位于根目录之外。");
                }

                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
                content = memory.ToArray();
            }

            var redacted = RedactJsonLines(content);
            files.Add(new DiagnosticBundleFile(
                approvedName.Replace('\\', '/'), Encoding.UTF8.GetString(redacted), redacted));
        }

        return new DiagnosticBundleManifest(this, files.AsReadOnly());
    }

    /// <summary>只接受本实例生成的预览快照，并以临时文件原子生成 ZIP。</summary>
    public async Task ExportAsync(
        DiagnosticBundleManifest manifest,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!ReferenceEquals(manifest.Owner, this))
        {
            throw new InvalidOperationException("诊断预览必须由当前导出器创建。");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var fullDestination = Path.GetFullPath(destinationPath);
        if (approvedNames.Any(name =>
                string.Equals(ResolveApprovedPath(name), fullDestination, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("诊断包不得覆盖其白名单源文件。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        var temporaryPath = fullDestination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                64 * 1024, FileOptions.Asynchronous))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var file in manifest.Files)
                {
                    ValidateArchiveName(file.ArchiveName);
                    var entry = archive.CreateEntry(file.ArchiveName, CompressionLevel.Optimal);
                    await using var target = entry.Open();
                    await target.WriteAsync(file.Content, cancellationToken).ConfigureAwait(false);
                }

                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using var manifestTarget = manifestEntry.Open();
                var publicManifest = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    files = manifest.Files.Select(file => new { file.ArchiveName, Bytes = file.ByteCount })
                });
                await manifestTarget.WriteAsync(publicManifest, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullDestination, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    /// <summary>正规化白名单路径并拒绝根目录之外、目录或导出包自身候选。</summary>
    private string ResolveApprovedPath(string approvedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedName);
        if (Path.IsPathRooted(approvedName))
        {
            throw new InvalidOperationException("诊断白名单必须使用相对路径。");
        }

        var fullPath = Path.GetFullPath(Path.Combine(rootDirectory, approvedName));
        var prefix = Path.TrimEndingDirectorySeparator(rootDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("诊断白名单路径越界或指向导出包。");
        }

        return fullPath;
    }

    /// <summary>按 ZIP 斜杠规则规范化归档名，并拒绝空段、点段、驱动器和保留名。</summary>
    private static string NormalizeArchiveName(string configuredName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredName);
        var normalized = configuredName.Replace('\\', '/');
        if (Path.IsPathRooted(configuredName) || normalized.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("诊断白名单必须是安全相对归档名。");
        }

        var segments = normalized.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidOperationException("诊断白名单不得包含空段或点路径段。");
        }

        return string.Join('/', segments);
    }

    /// <summary>保守拒绝可信根以下任一重解析组件，再由文件句柄最终路径抵御替换竞态。</summary>
    private void RejectReparseComponents(string candidatePath)
    {
        var relative = Path.GetRelativePath(rootDirectory, candidatePath);
        var current = rootDirectory;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("诊断白名单不得穿过重解析点。");
                }
            }
        }
    }

    /// <summary>以备份语义打开可信根目录句柄；根自身可为用户明确配置的重解析目录。</summary>
    private static SafeFileHandle OpenDirectoryHandle(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return File.OpenHandle(directory, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        var handle = CreateFile(
            directory, GenericRead, FileShareRead | FileShareWrite | FileShareDelete,
            0, OpenExisting, FileFlagBackupSemantics, 0);
        if (handle.IsInvalid)
        {
            throw new IOException("无法打开诊断根目录句柄。", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return handle;
    }

    /// <summary>从已经打开且后续继续读取的句柄取得最终规范路径。</summary>
    private static string GetFinalPath(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("诊断文件句柄最终路径验证仅支持 Windows。");
        }

        var buffer = new char[32768];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
        {
            throw new IOException("无法解析诊断文件句柄最终路径。", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        var path = new string(buffer, 0, (int)length);
        return path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)
            ? "\\\\" + path[8..]
            : path.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase) ? path[4..] : path;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file, [Out] char[] path, uint pathLength, uint flags);

    /// <summary>逐行解析 JSON，递归删除敏感字段并按字段策略脱敏标题和路径。</summary>
    private byte[] RedactJsonLines(byte[] content)
    {
        var output = new StringBuilder();
        var text = Encoding.UTF8.GetString(content);
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length == 0)
            {
                continue;
            }

            JsonNode node;
            try
            {
                node = JsonNode.Parse(line) ?? throw new InvalidDataException("诊断日志包含空 JSON。");
            }
            catch (JsonException) when (index == lines.Length - 1 && !text.EndsWith('\n'))
            {
                // 活动 writer 的单次系统写尚未完成时只忽略最后半条，完整历史仍可预览。
                break;
            }

            RedactNode(node, null);
            output.Append(node.ToJsonString()).Append('\n');
        }

        return Encoding.UTF8.GetBytes(output.ToString());
    }

    /// <summary>依据键名递归移除凭据类数据，并替换路径中的用户目录和完整标题。</summary>
    private void RedactNode(JsonNode node, string? propertyName)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var pair in jsonObject.ToArray())
            {
                if (RemovedFields.Contains(pair.Key))
                {
                    jsonObject.Remove(pair.Key);
                    continue;
                }

                if (pair.Value is not null)
                {
                    RedactNode(pair.Value, pair.Key);
                }
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var child in jsonArray)
            {
                if (child is not null)
                {
                    RedactNode(child, propertyName);
                }
            }
        }
        else if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            // 标题可能直接包含文档名，默认完全隐藏；路径仅替换用户根以保留诊断结构。
            var replacement = propertyName?.Contains("title", StringComparison.OrdinalIgnoreCase) == true
                ? "[已脱敏]"
                : text.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            value.ReplaceWith(JsonValue.Create(replacement));
        }
    }

    /// <summary>拒绝 ZIP 绝对路径、父级跳转和目录条目，阻止 zip slip。</summary>
    private static void ValidateArchiveName(string archiveName)
    {
        if (Path.IsPathRooted(archiveName) || archiveName.Contains("..", StringComparison.Ordinal) ||
            archiveName.EndsWith('/') || archiveName.Contains('\\'))
        {
            throw new InvalidOperationException("诊断包条目名称不安全。");
        }
    }
}
