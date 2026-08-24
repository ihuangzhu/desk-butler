using System.Text;
using System.Text.Json;
using DeskButler.Core.Diagnostics;

namespace DeskButler.Persistence.Diagnostics;

/// <summary>以单写者、UTF-8 JSONL 和总量上限保存本地诊断。</summary>
public sealed class RollingJsonLog : IDiagnosticLog, IAsyncDisposable
{
    private const int DefaultFileBytes = 1024 * 1024;
    private const int DefaultTotalBytes = 3 * 1024 * 1024;
    private static readonly HashSet<string> ForbiddenFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "commandLine", "token", "password", "clipboard"
    };
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string directory;
    private readonly string activePath;
    private readonly int maximumFileBytes;
    private readonly int maximumTotalBytes;
    private readonly FileStream writerLock;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private FileStream activeStream;
    private bool disposed;

    /// <summary>创建使用默认单文件一兆、总量三兆上限的日志。</summary>
    public RollingJsonLog(string directory)
        : this(directory, DefaultFileBytes, DefaultTotalBytes)
    {
    }

    /// <summary>创建具有显式容量限制的单写者日志。</summary>
    public RollingJsonLog(string directory, int maximumFileBytes, int maximumTotalBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFileBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumTotalBytes, maximumFileBytes);
        this.directory = Path.GetFullPath(directory);
        this.maximumFileBytes = maximumFileBytes;
        this.maximumTotalBytes = maximumTotalBytes;
        Directory.CreateDirectory(this.directory);
        activePath = Path.Combine(this.directory, "deskbutler.jsonl");
        // 锁文件与活动文件分离，轮换时仍持续持有目录写者身份。
        writerLock = new FileStream(
            Path.Combine(this.directory, "deskbutler.writer.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        try
        {
            activeStream = OpenActiveStream();
        }
        catch
        {
            writerLock.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticEvent.Category);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticEvent.Message);
        var json = JsonSerializer.SerializeToElement(diagnosticEvent, SerializerOptions);
        RejectSensitiveFields(json);
        var record = Encoding.UTF8.GetBytes(json.GetRawText() + "\n");

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (activeStream.Length > 0 && activeStream.Length + record.Length > maximumFileBytes)
            {
                await RotateAsync(cancellationToken).ConfigureAwait(false);
            }

            await activeStream.WriteAsync(record, cancellationToken).ConfigureAwait(false);
            await activeStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            TrimArchives(record.Length);
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <summary>显式把活动日志刷新到操作系统，供退出和测试观察。</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await activeStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <summary>刷新并释放活动流、写者锁与串行门。</summary>
    public async ValueTask DisposeAsync()
    {
        await writeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            await activeStream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await activeStream.DisposeAsync().ConfigureAwait(false);
            await writerLock.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // 不释放串行门，使已经排队的调用能取得门并收到确定的 ObjectDisposedException。
            writeGate.Release();
        }
    }

    /// <summary>轮换活动文件并保留最多两个编号归档。</summary>
    private async Task RotateAsync(CancellationToken cancellationToken)
    {
        await activeStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await activeStream.DisposeAsync().ConfigureAwait(false);
        try
        {
            var second = Path.Combine(directory, "deskbutler.2.jsonl");
            var first = Path.Combine(directory, "deskbutler.1.jsonl");
            File.Delete(second);
            if (File.Exists(first))
            {
                File.Move(first, second);
            }

            File.Move(activePath, first);
        }
        finally
        {
            // 任何部分轮换失败后都重新取得活动文件，调用方可处理异常并继续使用写者。
            activeStream = OpenActiveStream();
        }
    }

    /// <summary>从最旧归档开始删除，允许活动记录自身作为唯一超限余量。</summary>
    private void TrimArchives(int activeRecordMargin)
    {
        var archives = new[]
        {
            Path.Combine(directory, "deskbutler.2.jsonl"),
            Path.Combine(directory, "deskbutler.1.jsonl")
        };
        foreach (var archive in archives)
        {
            var total = Directory.EnumerateFiles(directory, "deskbutler*.jsonl")
                .Sum(path => new FileInfo(path).Length);
            if (total <= maximumTotalBytes + activeRecordMargin)
            {
                return;
            }

            File.Delete(archive);
        }
    }

    /// <summary>以追加、异步和共享读取方式打开活动 JSONL。</summary>
    private FileStream OpenActiveStream() => new(
        PrepareActiveFile(), FileMode.Append, FileAccess.Write, FileShare.Read,
        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>重启时截断未以换行结束的最后半条，避免与下一条 JSON 拼接。</summary>
    private string PrepareActiveFile()
    {
        if (!File.Exists(activePath))
        {
            return activePath;
        }

        using var stream = new FileStream(activePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        if (stream.Length == 0)
        {
            return activePath;
        }

        stream.Position = stream.Length - 1;
        if (stream.ReadByte() == (byte)'\n')
        {
            return activePath;
        }

        for (var position = stream.Length - 1; position >= 0; position--)
        {
            stream.Position = position;
            if (stream.ReadByte() == (byte)'\n')
            {
                stream.SetLength(position + 1);
                return activePath;
            }
        }

        stream.SetLength(0);
        return activePath;
    }

    /// <summary>递归拒绝任何层级和大小写形式的敏感字段，避免误写后再补救。</summary>
    private static void RejectSensitiveFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (ForbiddenFields.Contains(property.Name))
                {
                    throw new ArgumentException($"诊断事件包含禁止字段：{property.Name}");
                }

                RejectSensitiveFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectSensitiveFields(item);
            }
        }
    }
}
