using DeskButler.Persistence.Paths;
using Microsoft.Data.Sqlite;

namespace DeskButler.Persistence.Sqlite;

/// <summary>负责以显式版本号创建和升级 SQLite 数据库模式。</summary>
public sealed class DatabaseMigrator
{
    private const int CurrentSchemaVersion = 2;
    private const int VersionOne = 1;
    private const int VersionTwo = 2;
    private const string CapturedAtStorageFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private readonly AppDataPaths paths;

    /// <summary>使用应用数据路径初始化数据库迁移器。</summary>
    /// <param name="paths">包含数据库目标路径的应用数据路径。</param>
    public DatabaseMigrator(AppDataPaths paths)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <summary>创建数据库、配置连接并将模式迁移到当前显式版本。</summary>
    /// <param name="cancellationToken">用于取消初始化操作的令牌。</param>
    /// <returns>初始化完成的任务。</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        paths.EnsureRootDirectoryExists();

        await using var connection = new SqliteConnection($"Data Source={paths.DatabasePath}");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var transaction = connection.BeginTransaction();
        await ExecuteNonQueryAsync(connection, transaction, "CREATE TABLE IF NOT EXISTS schema_info(version INTEGER NOT NULL);", cancellationToken).ConfigureAwait(false);

        var version = await ReadVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (version is null)
        {
            await ApplyVersionOneAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO schema_info(version) VALUES ($version);", cancellationToken, ("$version", VersionOne)).ConfigureAwait(false);
            version = VersionOne;
        }

        if (version == VersionOne)
        {
            await ApplyVersionTwoAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, transaction, "UPDATE schema_info SET version = $version;", cancellationToken, ("$version", VersionTwo)).ConfigureAwait(false);
            version = VersionTwo;
        }

        if (version != CurrentSchemaVersion)
        {
            throw new NotSupportedException($"不支持的 DeskButler 数据库模式版本：{version}。");
        }

        transaction.Commit();
    }

    /// <summary>在新连接上设置数据库持久化和完整性相关的 PRAGMA。</summary>
    /// <param name="connection">已打开的 SQLite 连接。</param>
    /// <param name="cancellationToken">用于取消初始化操作的令牌。</param>
    /// <returns>设置完成的任务。</returns>
    private static async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, null, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, null, "PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取当前数据库模式版本；尚未写入版本时返回空值。</summary>
    /// <param name="connection">已打开的 SQLite 连接。</param>
    /// <param name="transaction">包裹模式读取的事务。</param>
    /// <param name="cancellationToken">用于取消读取操作的令牌。</param>
    /// <returns>现有模式版本或空值。</returns>
    private static async Task<int?> ReadVersionAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM schema_info LIMIT 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null || result is DBNull ? null : Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>应用版本一包含快照、恢复记录和最近快照索引的初始模式。</summary>
    /// <param name="connection">已打开的 SQLite 连接。</param>
    /// <param name="transaction">包裹迁移的事务。</param>
    /// <param name="cancellationToken">用于取消迁移操作的令牌。</param>
    /// <returns>迁移完成的任务。</returns>
    private static Task ApplyVersionOneAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        const string schema = """
            CREATE TABLE scene_snapshots(
              id TEXT PRIMARY KEY,
              captured_at TEXT NOT NULL,
              capture_reason TEXT NOT NULL,
              format_version INTEGER NOT NULL,
              payload_json TEXT NOT NULL,
              is_valid INTEGER NOT NULL DEFAULT 1,
              invalid_reason TEXT NULL
            );
            CREATE INDEX ix_scene_snapshots_recent
              ON scene_snapshots(is_valid, captured_at DESC);
            CREATE TABLE restore_runs(
              id TEXT PRIMARY KEY,
              scene_id TEXT NOT NULL,
              started_at TEXT NOT NULL,
              completed_at TEXT NULL,
              result_json TEXT NULL
            );
            """;
        return ExecuteNonQueryAsync(connection, transaction, schema, cancellationToken);
    }

    /// <summary>应用版本二，将版本一的带偏移时间文本统一为可按 UTC 绝对时间排序的固定宽度格式。</summary>
    /// <param name="connection">已打开的 SQLite 连接。</param>
    /// <param name="transaction">包裹迁移的事务。</param>
    /// <param name="cancellationToken">用于取消迁移操作的令牌。</param>
    /// <returns>迁移完成的任务。</returns>
    private static async Task ApplyVersionTwoAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var timestamps = new List<SnapshotTimestamp>();
        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.Transaction = transaction;
            selectCommand.CommandText = "SELECT id, captured_at FROM scene_snapshots;";
            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                timestamps.Add(new SnapshotTimestamp(reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var timestamp in timestamps)
        {
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                "UPDATE scene_snapshots SET captured_at = $capturedAt WHERE id = $id;",
                cancellationToken,
                ("$capturedAt", NormalizeCapturedAtForStorage(timestamp.Value)),
                ("$id", timestamp.Id)).ConfigureAwait(false);
        }
    }

    /// <summary>将版本一存储的带偏移时间文本转换为固定宽度 UTC 排序格式。</summary>
    /// <param name="capturedAt">版本一中的原始捕获时间文本。</param>
    /// <returns>可按词法顺序比较的 UTC 时间文本。</returns>
    private static string NormalizeCapturedAtForStorage(string capturedAt)
    {
        var value = DateTimeOffset.Parse(
            capturedAt,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
        return value.ToUniversalTime().ToString(CapturedAtStorageFormat, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>在指定连接和可选事务中执行非查询 SQL。</summary>
    /// <param name="connection">已打开的 SQLite 连接。</param>
    /// <param name="transaction">可选的 SQLite 事务。</param>
    /// <param name="sql">待执行的 SQL。</param>
    /// <param name="cancellationToken">用于取消执行操作的令牌。</param>
    /// <param name="parameters">SQL 参数名和值。</param>
    /// <returns>执行完成的任务。</returns>
    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>表示版本一快照时间的标识和原始文本。</summary>
    /// <param name="Id">快照唯一标识。</param>
    /// <param name="Value">版本一存储的捕获时间文本。</param>
    private sealed record SnapshotTimestamp(string Id, string Value);
}
