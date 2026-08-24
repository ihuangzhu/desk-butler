using DeskButler.Persistence.Paths;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskButler.Persistence.Sqlite;

/// <summary>抽象恢复前关闭全部 SQLite 连接和池的生命周期边界。</summary>
public interface IDatabaseConnectionLifecycle
{
    /// <summary>关闭当前应用持有的所有连接并清空底层连接池。</summary>
    ValueTask CloseAllAsync(CancellationToken cancellationToken);
}

/// <summary>抽象可重复执行的新库创建与模式迁移。</summary>
public interface IDatabaseInitializer
{
    /// <summary>创建或迁移目标数据库。</summary>
    Task InitializeAsync(CancellationToken cancellationToken);
}

/// <summary>描述数据库是否回退及诊断界面需要展示的健康警告。</summary>
public sealed record DatabaseRecoveryResult(bool WasRecovered, string? BackupDirectory, string? HealthWarning)
{
    /// <summary>数据库正常初始化且无需告警的结果。</summary>
    public static DatabaseRecoveryResult Healthy { get; } = new(false, null, null);
}

/// <summary>在数据库损坏或迁移失败时保全证据后重建数据库。</summary>
public sealed class DatabaseRecovery
{
    private readonly AppDataPaths paths;
    private readonly IDatabaseConnectionLifecycle lifecycle;
    private readonly IDatabaseInitializer initializer;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Action<string, string>? copyFile;
    private readonly Action<string> deleteFile;
    private readonly Action<string>? afterBackupDirectoryValidated;
    private readonly Action<string>? afterEvidenceHandleOpened;
    private readonly Func<SafeFileHandle, string> evidenceFinalPathResolver;

    /// <summary>使用明确连接生命周期、迁移器和时间源创建恢复服务。</summary>
    public DatabaseRecovery(
        AppDataPaths paths,
        IDatabaseConnectionLifecycle lifecycle,
        IDatabaseInitializer initializer,
        Func<DateTimeOffset>? utcNow = null,
        Action<string, string>? copyFile = null,
        Action<string>? deleteFile = null,
        Action<string>? afterBackupDirectoryValidated = null,
        Action<string>? afterEvidenceHandleOpened = null,
        Func<SafeFileHandle, string>? evidenceFinalPathResolver = null)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.copyFile = copyFile;
        this.deleteFile = deleteFile ?? File.Delete;
        this.afterBackupDirectoryValidated = afterBackupDirectoryValidated;
        this.afterEvidenceHandleOpened = afterEvidenceHandleOpened;
        this.evidenceFinalPathResolver = evidenceFinalPathResolver ?? GetFinalPath;
    }

    /// <summary>正常初始化；任意 SQLite 损坏或迁移故障均先保全 DB/WAL/SHM 再重建。</summary>
    public async Task<DatabaseRecoveryResult> InitializeAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(MarkerPath))
        {
            return await ResumeAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return DatabaseRecoveryResult.Healthy;
        }
        catch (Exception exception) when (IsRecoverableDatabaseFailure(exception))
        {
            await lifecycle.CloseAllAsync(cancellationToken).ConfigureAwait(false);
            var backupDirectory = CreateUniqueBackupDirectory();
            var evidence = await CopyEvidenceAsync(backupDirectory, cancellationToken).ConfigureAwait(false);
            var marker = new RecoveryMarker(
                1, Path.GetFileName(backupDirectory), backupDirectory, "evidence-backed-up", evidence);
            await using var leases = await OpenValidatedEvidenceLeasesAsync(marker, cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteMarker(marker);
                DeleteWorkingDatabaseFiles();
                marker = marker with { Phase = "working-files-removed" };
                cancellationToken.ThrowIfCancellationRequested();
                WriteMarker(marker);
                await initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception rebuildFailure)
            {
                throw new DatabaseRecoveryException(
                    "损坏数据库已保存在诊断目录，但新数据库创建或迁移失败。",
                    backupDirectory, rebuildFailure);
            }

            File.Delete(MarkerPath);
            return new DatabaseRecoveryResult(
                true, backupDirectory,
                "数据库故障现场已备份，DeskButler 已创建新的本地数据库。");
        }
    }

    /// <summary>识别 SQLite 损坏码以及初始化阶段的迁移故障。</summary>
    private static bool IsRecoverableDatabaseFailure(Exception exception) =>
        exception is MigrationFailureException ||
        exception is SqliteException { SqliteErrorCode: 11 or 26 };

    /// <summary>以 UTC 时间和碰撞序号创建唯一诊断目录。</summary>
    private string CreateUniqueBackupDirectory()
    {
        var diagnosticsRoot = paths.DiagnosticsDirectory;
        Directory.CreateDirectory(diagnosticsRoot);
        var stem = "database.corrupt-" + utcNow().ToUniversalTime().ToString(
            "yyyyMMdd-HHmmss-fffffff", System.Globalization.CultureInfo.InvariantCulture);
        // 时间戳便于用户识别，随机后缀在并发进程或时钟重复时仍避免证据目录碰撞。
        var candidate = Path.Combine(diagnosticsRoot, $"{stem}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(candidate);
        return candidate;
    }

    /// <summary>复制存在的主库及 WAL/SHM；任一步失败都保留原工作文件。</summary>
    private async Task<List<RecoveryEvidence>> CopyEvidenceAsync(
        string backupDirectory,
        CancellationToken cancellationToken)
    {
        var evidence = new List<RecoveryEvidence>();
        foreach (var source in DatabaseFiles())
        {
            if (File.Exists(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var logicalName = Path.GetFileName(source);
                var destination = Path.Combine(backupDirectory, logicalName);
                if (copyFile is not null)
                {
                    copyFile(source, destination);
                    await using var durable = new FileStream(
                        destination, FileMode.Open, FileAccess.Write, FileShare.Read, 81920, FileOptions.WriteThrough);
                    await durable.FlushAsync(cancellationToken).ConfigureAwait(false);
                    durable.Flush(flushToDisk: true);
                }
                else
                {
                    await using var input = new FileStream(
                        source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                    await using var output = new FileStream(
                        destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 81920,
                        FileOptions.Asynchronous | FileOptions.WriteThrough);
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }

                await using var stream = new FileStream(
                    destination, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                var (length, hash) = await ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
                evidence.Add(new RecoveryEvidence(
                    logicalName, length, hash));
            }
        }

        if (!evidence.Any(item => StringComparer.OrdinalIgnoreCase.Equals(
                item.LogicalName, Path.GetFileName(paths.DatabasePath))))
        {
            throw new InvalidDataException("数据库恢复缺少主数据库证据。");
        }

        return evidence;
    }

    /// <summary>备份已完整后删除旧工作副本，让迁移器创建独立新库。</summary>
    private void DeleteWorkingDatabaseFiles()
    {
        foreach (var path in DatabaseFiles())
        {
            deleteFile(path);
        }
    }

    /// <summary>继续上次已留 marker 的清理或重建阶段，绝不把半清理现场当成健康库。</summary>
    private async Task<DatabaseRecoveryResult> ResumeAsync(CancellationToken cancellationToken)
    {
        var marker = JsonSerializer.Deserialize<RecoveryMarker>(File.ReadAllText(MarkerPath))
            ?? throw new InvalidDataException("数据库恢复 marker 为空。");
        await using var leases = await OpenValidatedEvidenceLeasesAsync(marker, cancellationToken).ConfigureAwait(false);
        await lifecycle.CloseAllAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (StringComparer.Ordinal.Equals(marker.Phase, "evidence-backed-up"))
            {
                DeleteWorkingDatabaseFiles();
                marker = marker with { Phase = "working-files-removed" };
                cancellationToken.ThrowIfCancellationRequested();
                WriteMarker(marker);
            }

            await initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
            File.Delete(MarkerPath);
            return new DatabaseRecoveryResult(
                true, marker.BackupDirectory,
                "数据库恢复已从上次中断阶段继续完成，原始故障现场仍保留在诊断目录。");
        }
        catch (Exception exception)
        {
            throw new DatabaseRecoveryException(
                "数据库恢复仍未完成；marker 与原始备份均已保留。",
                marker.BackupDirectory, exception);
        }
    }

    /// <summary>原子写入恢复阶段 marker，崩溃后可从最后完成阶段继续。</summary>
    private void WriteMarker(RecoveryMarker marker)
    {
        var temporary = MarkerPath + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, marker);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, MarkerPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>拒绝被篡改到诊断根之外或不存在目录的 marker 备份路径。</summary>
    private async Task<EvidenceLease> OpenValidatedEvidenceLeasesAsync(
        RecoveryMarker marker,
        CancellationToken cancellationToken)
    {
        if (marker.Version != 1 || string.IsNullOrWhiteSpace(marker.RecoveryId) ||
            string.IsNullOrWhiteSpace(marker.BackupDirectory) ||
            marker.Phase is not ("evidence-backed-up" or "working-files-removed") ||
            marker.Evidence is null || marker.Evidence.Count == 0)
        {
            throw new InvalidDataException("数据库恢复 marker 格式或阶段无效。");
        }

        var backupDirectory = marker.BackupDirectory;
        var fullBackup = Path.GetFullPath(backupDirectory);
        SafeFileHandle? diagnosticsHandle = null;
        SafeFileHandle? backupHandle = null;

        var allowed = DatabaseFiles().Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var leases = new List<FileStream>();
        try
        {
            diagnosticsHandle = OpenPathHandle(paths.DiagnosticsDirectory, directory: true, openReparsePoint: false);
            var diagnosticsFinal = GetFinalPath(diagnosticsHandle);
            backupHandle = OpenPathHandle(fullBackup, directory: true, openReparsePoint: true);
            RejectReparseHandle(backupHandle);
            var backupFinal = GetFinalPath(backupHandle);
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(backupFinal), diagnosticsFinal) ||
                !StringComparer.Ordinal.Equals(Path.GetFileName(backupFinal), marker.RecoveryId))
            {
                throw new InvalidDataException("数据库恢复 marker 指向无效备份目录。");
            }
            afterBackupDirectoryValidated?.Invoke(fullBackup);

            foreach (var item in marker.Evidence)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(item.LogicalName) || !allowed.Contains(item.LogicalName) ||
                    !seen.Add(item.LogicalName) || Path.GetFileName(item.LogicalName) != item.LogicalName)
                {
                    throw new InvalidDataException("数据库恢复 marker 含非法证据名称。");
                }

                var evidencePath = Path.Combine(fullBackup, item.LogicalName);
                SafeFileHandle evidenceHandle;
                try
                {
                    evidenceHandle = OpenPathHandle(evidencePath, directory: false, openReparsePoint: true);
                }
                catch (IOException exception)
                {
                    throw new InvalidDataException("数据库恢复证据缺失或无法安全打开。", exception);
                }
                var ownershipTransferred = false;
                try
                {
                    RejectReparseHandle(evidenceHandle);
                    var evidenceFinal = evidenceFinalPathResolver(evidenceHandle);
                    if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(evidenceFinal), backupFinal) ||
                        !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(evidenceFinal), item.LogicalName))
                    {
                        throw new InvalidDataException("数据库恢复证据最终路径越界。");
                    }
                    var stream = new FileStream(evidenceHandle, FileAccess.Read, 81920, isAsync: true);
                    leases.Add(stream);
                    ownershipTransferred = true;
                    afterEvidenceHandleOpened?.Invoke(evidencePath);
                    var (length, hash) = await ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
                    if (length != item.Length || !StringComparer.OrdinalIgnoreCase.Equals(hash, item.Sha256))
                    {
                        throw new InvalidDataException("数据库恢复证据完整性校验失败。");
                    }
                }
                finally
                {
                    if (!ownershipTransferred)
                    {
                        evidenceHandle.Dispose();
                    }
                }
            }

            if (!seen.Contains(Path.GetFileName(paths.DatabasePath)))
            {
                throw new InvalidDataException("数据库恢复 marker 缺少主数据库证据。");
            }

            return new EvidenceLease(leases, diagnosticsHandle, backupHandle);
        }
        catch
        {
            foreach (var lease in leases)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }

            backupHandle?.Dispose();
            diagnosticsHandle?.Dispose();

            throw;
        }
    }

    /// <summary>从单一 Windows 句柄同时锁定路径身份，且拒绝删除共享。</summary>
    private static SafeFileHandle OpenPathHandle(string path, bool directory, bool openReparsePoint)
    {
        var flags = directory ? 0x02000000u : 0x40000000u;
        if (openReparsePoint)
        {
            flags |= 0x00200000u;
        }
        var handle = CreateFileW(path, 0x80000000u, (uint)FileShare.Read, IntPtr.Zero, 3, flags, IntPtr.Zero);
        return handle.IsInvalid
            ? throw new IOException("无法打开数据库恢复证据句柄。", new Win32Exception(Marshal.GetLastWin32Error()))
            : handle;
    }

    /// <summary>从同一句柄读取文件属性并保守拒绝所有重解析点。</summary>
    private static void RejectReparseHandle(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取数据库恢复证据属性。");
        }
        if ((information.FileAttributes & 0x400u) != 0)
        {
            throw new InvalidDataException("数据库恢复证据路径不能是重解析点。");
        }
    }

    /// <summary>从已锁定句柄取得规范最终路径。</summary>
    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[32768];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法解析数据库恢复证据路径。");
        }
        return new string(buffer, 0, checked((int)length))
            .Replace("\\\\?\\UNC\\", "\\\\", StringComparison.OrdinalIgnoreCase)
            .Replace("\\\\?\\", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>分块异步计算证据摘要，并在每次读取之间响应取消。</summary>
    private static async Task<(long Length, string Hash)> ComputeHashAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            hash.AppendData(buffer, 0, read);
            length += read;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (length, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private string MarkerPath => Path.Combine(paths.RootDirectory, "database-recovery.marker.json");

    /// <summary>枚举 SQLite 主库和与其同一事务现场的 WAL/SHM 文件。</summary>
    private string[] DatabaseFiles() => [paths.DatabasePath, paths.DatabasePath + "-wal", paths.DatabasePath + "-shm"];

    private sealed record RecoveryMarker(
        int Version,
        string RecoveryId,
        string BackupDirectory,
        string Phase,
        IReadOnlyList<RecoveryEvidence> Evidence);

    private sealed record RecoveryEvidence(string LogicalName, long Length, string Sha256);

    /// <summary>集中释放通过认证且在恢复关键区间持有的证据句柄。</summary>
    private sealed class EvidenceLease(
        List<FileStream> streams,
        SafeFileHandle diagnosticsHandle,
        SafeFileHandle backupHandle) : IAsyncDisposable
    {
        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            backupHandle.Dispose();
            diagnosticsHandle.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        private System.Runtime.InteropServices.ComTypes.FILETIME creationTime;
        private System.Runtime.InteropServices.ComTypes.FILETIME accessTime;
        private System.Runtime.InteropServices.ComTypes.FILETIME writeTime;
        private uint volumeSerialNumber;
        private uint fileSizeHigh;
        private uint fileSizeLow;
        private uint numberOfLinks;
        private uint fileIndexHigh;
        private uint fileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file, out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file, [Out] char[] filePath, uint filePathSize, uint flags);
}

/// <summary>表示证据已保全但新库无法完成初始化的可诊断故障。</summary>
public sealed class DatabaseRecoveryException(string message, string backupDirectory, Exception innerException)
    : Exception(message, innerException)
{
    /// <summary>获取仍保存原始故障证据的目录。</summary>
    public string BackupDirectory { get; } = backupDirectory;
}

/// <summary>表示数据库内容无法按当前显式模式完成迁移，而非磁盘或权限故障。</summary>
public sealed class MigrationFailureException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>表示数据库来自比当前应用更新的版本，绝不能按损坏库自动重建。</summary>
public sealed class FutureSchemaVersionException(int actualVersion, int supportedVersion)
    : NotSupportedException($"数据库模式版本 {actualVersion} 高于当前支持版本 {supportedVersion}。")
{
    /// <summary>数据库中检测到的版本。</summary>
    public int ActualVersion { get; } = actualVersion;

    /// <summary>当前应用支持的最高版本。</summary>
    public int SupportedVersion { get; } = supportedVersion;
}

/// <summary>生产环境关闭 Microsoft.Data.Sqlite 全部池化连接。</summary>
public sealed class SqliteConnectionLifecycle : IDatabaseConnectionLifecycle
{
    /// <inheritdoc />
    public ValueTask CloseAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }
}
