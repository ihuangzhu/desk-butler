using DeskButler.Core.Diagnostics;
using DeskButler.Core.Restore;
using DeskButler.Persistence.Paths;
using Microsoft.Data.Sqlite;

namespace DeskButler.Persistence.Sqlite;

/// <summary>以既有恢复结果维护最多三次的连续失败计数。</summary>
public sealed class SqliteFailureHistoryStore(AppDataPaths paths) : IFailureHistoryStore
{
    private readonly AppDataPaths paths = paths ?? throw new ArgumentNullException(nameof(paths));

    /// <inheritdoc />
    public async Task<FailureHistory> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureTableAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT scene_item_id, consecutive_failures FROM failure_history;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts[reader.GetString(0)] = reader.GetInt32(1);
        }

        return new FailureHistory(counts);
    }

    /// <inheritdoc />
    public async Task RecordAsync(RestoreResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureTableAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in result.Items)
        {
            if (item.Status is RestoreItemStatus.Skipped or RestoreItemStatus.Cancelled)
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            if (item.Status == RestoreItemStatus.Succeeded)
            {
                command.CommandText = "DELETE FROM failure_history WHERE scene_item_id = $id;";
            }
            else
            {
                command.CommandText = """
                    INSERT INTO failure_history(scene_item_id, consecutive_failures)
                    VALUES($id, 1)
                    ON CONFLICT(scene_item_id) DO UPDATE SET
                      consecutive_failures = MIN(3, failure_history.consecutive_failures + 1);
                    """;
            }

            command.Parameters.AddWithValue("$id", item.SceneItemId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>打开真实 SQLite 连接，供单次原子历史操作使用。</summary>
    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        paths.EnsureRootDirectoryExists();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Pooling = false,
            DefaultTimeout = 2
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // 打开失败的原始异常决定诊断分类，清理异常不得遮蔽它。
            }

            throw;
        }
    }

    /// <summary>兼容既有版本二模式，按需创建不重复领域模型的派生计数表。</summary>
    private static async Task EnsureTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS failure_history(
              scene_item_id TEXT PRIMARY KEY,
              consecutive_failures INTEGER NOT NULL CHECK(consecutive_failures BETWEEN 1 AND 3)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
