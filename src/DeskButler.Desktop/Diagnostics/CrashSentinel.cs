using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeskButler.Desktop.Diagnostics;

/// <summary>
/// 管理用于识别上一次非正常退出的当前运行标记。宿主必须先取得
/// <see cref="Hosting.SingleInstanceGuard"/>，再构造本类型，避免把第二实例当作崩溃启动。
/// </summary>
public sealed class CrashSentinel : IDisposable
{
    private const int MaxAcquireAttempts = 8;

    private readonly object syncRoot = new();
    private readonly string runToken;
    private ICrashMarkerHandle? markerHandle;
    private bool cleaned;
    private bool disposed;

    /// <summary>为指定应用数据目录创建崩溃哨兵，并持有当前 marker 的文件身份。</summary>
    /// <param name="dataDirectory">DeskButler 专属应用数据目录。</param>
    public CrashSentinel(string dataDirectory)
        : this(dataDirectory, NativeCrashMarkerFileOperations.Instance)
    {
    }

    /// <summary>使用可控文件操作边界构造崩溃哨兵。</summary>
    internal CrashSentinel(string dataDirectory, ICrashMarkerFileOperations fileOperations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(fileOperations);
        var fullDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(fullDirectory);
        var markerPath = Path.Combine(fullDirectory, "run.lock");
        runToken = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);

        markerHandle = AcquireMarker(fileOperations, markerPath, out var observedPreviousRun);
        IsPreviousRunUnclean = observedPreviousRun;
    }

    /// <summary>获取启动过程中是否曾观察到上一次运行留下的 marker。</summary>
    public bool IsPreviousRunUnclean { get; }

    /// <summary>在最终状态刷新完成后按持有文件身份删除 marker；宿主随后才能释放 mutex。</summary>
    public void MarkCleanExit()
    {
        lock (syncRoot)
        {
            if (cleaned)
            {
                return;
            }

            ObjectDisposedException.ThrowIf(disposed, this);
            var handle = markerHandle
                ?? throw new InvalidOperationException("当前运行 marker 句柄不可用。");
            handle.MarkDeleteOnClose();

            try
            {
                // 删除绑定 held identity；关闭最后句柄后路径消失，不按路径盲删。
                handle.Dispose();
            }
            finally
            {
                markerHandle = null;
                cleaned = true;
                disposed = true;
            }
        }
    }

    /// <summary>释放持有句柄但保留 marker，等同于非 clean 生命周期结束的进程级结果。</summary>
    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            markerHandle?.Dispose();
            markerHandle = null;
            disposed = true;
        }
    }

    /// <summary>原子建立或持有既有 marker；exists/open 竞态只重试有限次数。</summary>
    private ICrashMarkerHandle AcquireMarker(
        ICrashMarkerFileOperations fileOperations,
        string markerPath,
        out bool observedPreviousRun)
    {
        observedPreviousRun = false;
        for (var attempt = 0; attempt < MaxAcquireAttempts; attempt++)
        {
            if (fileOperations.TryCreateNew(markerPath, out var createdHandle))
            {
                var ownedHandle = createdHandle
                    ?? throw new InvalidOperationException("CREATE_NEW 成功但未返回 marker 句柄。");
                try
                {
                    WriteCurrentMarker(ownedHandle);
                    return ownedHandle;
                }
                catch
                {
                    ownedHandle.Dispose();
                    throw;
                }
            }

            observedPreviousRun = true;
            if (fileOperations.TryOpenExisting(markerPath, out var existingHandle))
            {
                return existingHandle
                    ?? throw new InvalidOperationException("OPEN_EXISTING 成功但未返回 marker 句柄。");
            }
        }

        throw new IOException($"run.lock 在 {MaxAcquireAttempts} 次原子取得尝试中持续消失，无法安全持有文件身份。");

    }

    /// <summary>把最小非敏感 metadata 完整刷入已原子建立并持续持有的 marker。</summary>
    private void WriteCurrentMarker(ICrashMarkerHandle marker)
    {
        using var writer = new StreamWriter(
            marker.Content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true);
        writer.WriteLine($"token={runToken}");
        writer.WriteLine($"pid={Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteLine($"utc={DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)}");
        writer.Flush();
        marker.FlushToDisk();
    }
}

/// <summary>隔离 marker 的 Win32 原子 create/open 行为。</summary>
internal interface ICrashMarkerFileOperations
{
    /// <summary>以 CREATE_NEW 尝试建立 marker；已存在时返回 false。</summary>
    bool TryCreateNew(string path, out ICrashMarkerHandle? marker);

    /// <summary>以 OPEN_EXISTING 尝试持有 marker；已消失时返回 false。</summary>
    bool TryOpenExisting(string path, out ICrashMarkerHandle? marker);
}

/// <summary>表示贯穿 CrashSentinel 生命周期的稳定文件身份。</summary>
internal interface ICrashMarkerHandle : IDisposable
{
    /// <summary>获取 marker 内容流。</summary>
    Stream Content { get; }

    /// <summary>把已写 metadata 刷到稳定存储。</summary>
    void FlushToDisk();

    /// <summary>把当前 held identity 标记为在最后句柄关闭时删除。</summary>
    void MarkDeleteOnClose();
}

/// <summary>使用 CreateFileW 与 held SafeFileHandle 实现 marker 身份边界。</summary>
internal sealed class NativeCrashMarkerFileOperations : ICrashMarkerFileOperations
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;

    private NativeCrashMarkerFileOperations()
    {
    }

    internal static NativeCrashMarkerFileOperations Instance { get; } = new();

    /// <inheritdoc />
    public bool TryCreateNew(string path, out ICrashMarkerHandle? marker)
    {
        var handle = CreateFile(
            path,
            GenericRead | GenericWrite | DeleteAccess,
            (uint)FileShare.Read,
            0,
            CreateNew,
            FileAttributeNormal,
            0);
        if (!handle.IsInvalid)
        {
            marker = CreateMarkerHandle(handle, FileAccess.ReadWrite, "无法持有新建的当前运行 marker。");
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        if (error is ErrorFileExists or ErrorAlreadyExists)
        {
            marker = null;
            return false;
        }

        throw CreateIOException("无法原子建立当前运行 marker。", error);
    }

    /// <inheritdoc />
    public bool TryOpenExisting(string path, out ICrashMarkerHandle? marker)
    {
        var handle = CreateFile(
            path,
            GenericRead | DeleteAccess,
            (uint)FileShare.Read,
            0,
            OpenExisting,
            FileAttributeNormal,
            0);
        if (!handle.IsInvalid)
        {
            marker = CreateMarkerHandle(handle, FileAccess.Read, "无法持有既有运行 marker。");
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        if (error is ErrorFileNotFound or ErrorPathNotFound)
        {
            marker = null;
            return false;
        }

        throw CreateIOException("无法持有既有运行 marker。", error);
    }

    /// <summary>把有效 native handle 包装为受控生命周期；失败时仍释放 handle。</summary>
    private static NativeCrashMarkerHandle CreateMarkerHandle(
        SafeFileHandle handle,
        FileAccess access,
        string errorMessage)
    {
        try
        {
            return new NativeCrashMarkerHandle(new FileStream(handle, access));
        }
        catch (Exception exception)
        {
            handle.Dispose();
            throw new IOException(errorMessage, exception);
        }
    }

    /// <summary>保留 Win32 error code 作为可观察 inner exception。</summary>
    private static IOException CreateIOException(string message, int error) =>
        new(message, new Win32Exception(error));

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);
}

/// <summary>持有单个 marker native identity，并提供 flush/delete-on-close 操作。</summary>
internal sealed class NativeCrashMarkerHandle : ICrashMarkerHandle
{
    private FileStream? stream;

    internal NativeCrashMarkerHandle(FileStream stream)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <inheritdoc />
    public Stream Content => stream ?? throw new ObjectDisposedException(nameof(NativeCrashMarkerHandle));

    /// <inheritdoc />
    public void FlushToDisk() =>
        (stream ?? throw new ObjectDisposedException(nameof(NativeCrashMarkerHandle))).Flush(flushToDisk: true);

    /// <inheritdoc />
    public void MarkDeleteOnClose()
    {
        var currentStream = stream ?? throw new ObjectDisposedException(nameof(NativeCrashMarkerHandle));
        var disposition = new FileDispositionInfo { DeleteFile = true };
        if (!SetFileInformationByHandle(
                currentStream.SafeFileHandle,
                FileInfoByHandleClass.FileDispositionInfo,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInfo>()))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException(
                "无法按当前运行 marker 的文件身份完成删除。",
                new Win32Exception(error));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var streamToDispose = Interlocked.Exchange(ref stream, null);
        streamToDispose?.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        FileInfoByHandleClass fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);

    private enum FileInfoByHandleClass
    {
        FileDispositionInfo = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        internal bool DeleteFile;
    }
}
