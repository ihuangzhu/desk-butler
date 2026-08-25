using System.Text.Json;
using DeskButler.Core.Persistence;
using DeskButler.Core.Scenes;
using DeskButler.Persistence.Paths;
using Microsoft.Data.Sqlite;

namespace DeskButler.Persistence.Sqlite;

/// <summary>使用 SQLite 原子保存、读取和标记场景快照。</summary>
public sealed class SqliteSceneRepository : ISceneRepository, IDisposable
{
    private const int MaximumValidSnapshots = 3;
    private const int CandidatePageSize = 16;
    private const int CurrentFormatVersion = 1;
    private const string CapturedAtStorageFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly AppDataPaths paths;
    private readonly DatabaseMigrator migrator;
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private bool initialized;

    /// <summary>使用应用数据路径初始化 SQLite 场景快照仓库。</summary>
    /// <param name="paths">包含 SQLite 数据库路径的应用数据路径。</param>
    public SqliteSceneRepository(AppDataPaths paths)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        migrator = new DatabaseMigrator(paths);
    }

    /// <inheritdoc />
    public async Task SaveAsync(SceneSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var payload = JsonSerializer.Serialize(snapshot, SerializerOptions);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await InsertSnapshotAsync(connection, transaction, snapshot, payload, cancellationToken).ConfigureAwait(false);
            await RemoveExpiredValidSnapshotsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // SQLite 的 ABORT 仅中止当前语句；显式回滚才能保证插入和裁剪绝不留下半完成状态。
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SceneSnapshot>> GetRecentAsync(int maximumCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCount);
        if (maximumCount == 0)
        {
            return [];
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = new List<SceneSnapshot>(maximumCount);
        var invalidRows = new List<(SnapshotRow Row, string Reason)>();
        var offset = 0;

        while (snapshots.Count < maximumCount)
        {
            var rows = await ReadRecentRowsAsync(CandidatePageSize, offset, cancellationToken).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                break;
            }

            offset += rows.Count;
            foreach (var row in rows)
            {
                if (row.FormatVersion != CurrentFormatVersion)
                {
                    invalidRows.Add((row, $"unsupported-format-version:{row.FormatVersion}"));
                    continue;
                }

                try
                {
                    var snapshot = JsonSerializer.Deserialize<SceneSnapshot>(row.Payload, SerializerOptions)
                        ?? throw new JsonException("场景快照 JSON 为空。");
                    if (snapshot.Id != row.Id)
                    {
                        invalidRows.Add((row, "row-payload-id-mismatch"));
                        continue;
                    }

                    if (snapshot.FormatVersion != row.FormatVersion)
                    {
                        invalidRows.Add((row, "row-payload-format-version-mismatch"));
                        continue;
                    }

                    snapshots.Add(snapshot);
                }
                catch (JsonException)
                {
                    // 原始数据必须保留；收集后再标记，避免 OFFSET 在后续页查询时因集合变化而跳过候选行。
                    invalidRows.Add((row, "payload-json-invalid"));
                }

                if (snapshots.Count == maximumCount)
                {
                    break;
                }
            }
        }

        foreach (var invalidRow in invalidRows)
        {
            await MarkInvalidAsync(invalidRow.Row.Id, invalidRow.Reason, cancellationToken).ConfigureAwait(false);
        }

        return snapshots;
    }

    /// <inheritdoc />
    public async Task MarkInvalidAsync(Guid snapshotId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE scene_snapshots SET is_valid = 0, invalid_reason = $reason WHERE id = $id;";
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$id", snapshotId.ToString("D", System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>释放用于一次性数据库初始化的同步资源。</summary>
    public void Dispose()
    {
        initializationGate.Dispose();
    }

    /// <summary>确保数据库模式只在仓库实例的首次使用时迁移一次。</summary>
    /// <param name="cancellationToken">用于取消初始化操作的令牌。</param>
    /// <returns>初始化完成的任务。</returns>
    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (initialized)
        {
            return;
        }

        await initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!initialized)
            {
                await migrator.InitializeAsync(cancellationToken).ConfigureAwait(false);
                initialized = true;
            }
        }
        finally
        {
            initializationGate.Release();
        }
    }

    /// <summary>打开一个已启用外键约束的 SQLite 连接。</summary>
    /// <param name="cancellationToken">用于取消打开操作的令牌。</param>
    /// <returns>已打开并配置的 SQLite 连接。</returns>
    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={paths.DatabasePath}");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>把完整快照作为有效记录写入当前事务。</summary>
    /// <param name="connection">已打开的 SQLite 连接。</param>
    /// <param name="transaction">保存和裁剪共享的事务。</param>
    /// <param name="snapshot">要写入的场景快照。</param>
    /// <param name="payload">序列化后的快照内容。</param>
    /// <param name="cancellationToken">用于取消写入操作的令牌。</param>
    /// <returns>插入完成的任务。</returns>
    private static async Task InsertSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SceneSnapshot snapshot,
        string payload,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO scene_snapshots(id, captured_at, capture_reason, format_version, payload_json, is_valid, invalid_reason)
            VALUES($id, $capturedAt, $captureReason, $formatVersion, $payload, 1, NULL);
            """;
        command.Parameters.AddWithValue("$id", snapshot.Id.ToString("D", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$capturedAt", FormatCapturedAtForStorage(snapshot.CapturedAt));
        command.Parameters.AddWithValue("$captureReason", snapshot.CaptureReason);
        command.Parameters.AddWithValue("$formatVersion", snapshot.FormatVersion);
        command.Parameters.AddWithValue("$payload", payload);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>在当前保存事务内删除超出三份保留上限的最旧有效快照。</summary>
    /// <param name="connection">已打开的 SQLite 连接。</param>
    /// <param name="transaction">保存和裁剪共享的事务。</param>
    /// <param name="cancellationToken">用于取消裁剪操作的令牌。</param>
    /// <returns>裁剪完成的任务。</returns>
    private static async Task RemoveExpiredValidSnapshotsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM scene_snapshots
            WHERE is_valid = 1
              AND id IN (
                  SELECT id FROM scene_snapshots
                  WHERE is_valid = 1
                  ORDER BY captured_at DESC, id DESC
                  LIMIT -1 OFFSET $keepCount
              );
            """;
        command.Parameters.AddWithValue("$keepCount", MaximumValidSnapshots);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取尚被标记为有效的一页原始快照行。</summary>
    /// <param name="pageSize">本页最多读取的行数。</param>
    /// <param name="offset">从最新候选行起跳过的行数。</param>
    /// <param name="cancellationToken">用于取消读取操作的令牌。</param>
    /// <returns>按最新优先排序的原始快照行。</returns>
    private async Task<List<SnapshotRow>> ReadRecentRowsAsync(int pageSize, int offset, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, format_version, payload_json, is_valid, invalid_reason
            FROM scene_snapshots
            WHERE is_valid = 1
            ORDER BY captured_at DESC, id DESC
            LIMIT $pageSize OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$pageSize", pageSize);
        command.Parameters.AddWithValue("$offset", offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<SnapshotRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new SnapshotRow(
                Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetString(2),
                reader.GetInt64(3) != 0, reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return rows;
    }

    /// <summary>将捕获时刻标准化为固定宽度 UTC 文本，以支持 SQLite 的绝对时间词法排序。</summary>
    /// <param name="capturedAt">要保存的捕获时刻。</param>
    /// <returns>可按文本顺序正确比较的 UTC 时间。</returns>
    private static string FormatCapturedAtForStorage(DateTimeOffset capturedAt)
    {
        return capturedAt.ToUniversalTime().ToString(CapturedAtStorageFormat, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>表示从数据库读取但尚未反序列化的快照行。</summary>
    /// <param name="Id">快照唯一标识。</param>
    /// <param name="FormatVersion">数据库行声明的格式版本。</param>
    /// <param name="Payload">快照 JSON 内容。</param>
    /// <param name="IsValid">数据库行当前有效标志。</param>
    /// <param name="InvalidReason">数据库行当前无效原因。</param>
    private sealed record SnapshotRow(
        Guid Id, int FormatVersion, string Payload, bool IsValid, string? InvalidReason);
}
