using DeskButler.Core.Scenes;
using DeskButler.Persistence.Paths;
using DeskButler.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace DeskButler.Persistence.Tests.Sqlite;

public sealed class SqliteSceneRepositoryTests
{
    private static readonly JsonSerializerOptions TestSerializerOptions = new(JsonSerializerDefaults.Web);


    /// <summary>验证第四次保存后仅保留捕获时间最新的三份有效快照，并按时间倒序返回。</summary>
    [Fact]
    public async Task SaveAsyncKeepsOnlyThreeNewestValidSnapshots()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();

        foreach (var minute in Enumerable.Range(1, 4))
        {
            await fixture.Repository.SaveAsync(SceneFactory.AtMinute(minute), CancellationToken.None);
        }

        var snapshots = await fixture.Repository.GetRecentAsync(10, CancellationToken.None);

        Assert.Equal([4, 3, 2], snapshots.Select(snapshot => snapshot.CapturedAt.Minute));
    }

    /// <summary>验证保存及裁剪任一步骤失败时，事务会回滚而不留下部分写入的新快照。</summary>
    [Fact]
    public async Task SaveAsyncRollsBackInsertionWhenPruningFails()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();

        foreach (var minute in Enumerable.Range(1, 3))
        {
            await fixture.Repository.SaveAsync(SceneFactory.AtMinute(minute), CancellationToken.None);
        }

        await fixture.ExecuteSqlAsync(
            "CREATE TRIGGER reject_snapshot_pruning BEFORE DELETE ON scene_snapshots " +
            "WHEN OLD.is_valid = 1 BEGIN SELECT RAISE(ABORT, 'test pruning failure'); END;");

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => fixture.Repository.SaveAsync(SceneFactory.AtMinute(4), CancellationToken.None));
        Assert.Contains("test pruning failure", exception.Message, StringComparison.Ordinal);

        var snapshots = await fixture.Repository.GetRecentAsync(10, CancellationToken.None);

        Assert.Equal([3, 2, 1], snapshots.Select(snapshot => snapshot.CapturedAt.Minute));
    }

    /// <summary>验证读取到无效 JSON 时会保留原始记录并把它标记为不可用，而不是把它作为有效快照返回。</summary>
    [Fact]
    public async Task GetRecentAsyncMarksMalformedPayloadAsInvalidWithoutDeletingIt()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var snapshot = SceneFactory.AtMinute(1);
        await fixture.Repository.SaveAsync(snapshot, CancellationToken.None);
        await fixture.ExecuteSqlAsync($"UPDATE scene_snapshots SET payload_json = '{{not-json' WHERE id = '{snapshot.Id:D}';");

        var snapshots = await fixture.Repository.GetRecentAsync(10, CancellationToken.None);

        Assert.Empty(snapshots);
        Assert.Equal(0L, await fixture.GetInt64Async($"SELECT is_valid FROM scene_snapshots WHERE id = '{snapshot.Id:D}';"));
        Assert.Equal(1L, await fixture.GetInt64Async($"SELECT COUNT(*) FROM scene_snapshots WHERE id = '{snapshot.Id:D}';"));
    }

    /// <summary>验证最新候选快照损坏时，读取会继续查找并返回请求数量的下一份有效快照。</summary>
    [Fact]
    public async Task GetRecentAsyncSkipsMalformedNewestSnapshotWithoutConsumingValidResultLimit()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var older = SceneFactory.AtMinute(1);
        var newest = SceneFactory.AtMinute(2);
        await fixture.Repository.SaveAsync(older, CancellationToken.None);
        await fixture.Repository.SaveAsync(newest, CancellationToken.None);
        await fixture.ExecuteSqlAsync($"UPDATE scene_snapshots SET payload_json = '{{not-json' WHERE id = '{newest.Id:D}';");

        var snapshot = Assert.Single(await fixture.Repository.GetRecentAsync(1, CancellationToken.None));

        Assert.Equal(older.Id, snapshot.Id);
        Assert.Equal(0L, await fixture.GetInt64Async($"SELECT is_valid FROM scene_snapshots WHERE id = '{newest.Id:D}';"));
    }

    /// <summary>验证跨 UTC 偏移量保存时，保留和读取均按绝对 UTC 时间而非本地钟表时间排序。</summary>
    [Fact]
    public async Task SaveAsyncOrdersAndRetainsSnapshotsByAbsoluteUtcTimeAcrossOffsets()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var oldest = SceneFactory.At(new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.FromHours(8)));
        var third = SceneFactory.At(new DateTimeOffset(2026, 8, 24, 3, 0, 0, TimeSpan.Zero));
        var second = SceneFactory.At(new DateTimeOffset(2026, 8, 24, 2, 30, 0, TimeSpan.FromHours(-2)));
        var newest = SceneFactory.At(new DateTimeOffset(2026, 8, 24, 4, 45, 0, TimeSpan.Zero));

        await fixture.Repository.SaveAsync(oldest, CancellationToken.None);
        await fixture.Repository.SaveAsync(third, CancellationToken.None);
        await fixture.Repository.SaveAsync(second, CancellationToken.None);
        await fixture.Repository.SaveAsync(newest, CancellationToken.None);

        var snapshots = await fixture.Repository.GetRecentAsync(10, CancellationToken.None);

        Assert.Equal([newest.Id, second.Id, third.Id], snapshots.Select(snapshot => snapshot.Id));
    }

    /// <summary>验证打开版本一数据库时会将旧的带偏移时间文本迁移为 UTC 排序格式，且不混合两种排序语义。</summary>
    [Fact]
    public async Task GetRecentAsyncMigratesVersionOneCapturedAtValuesToUtcSortableFormat()
    {
        await using var fixture = await RepositoryFixture.CreateVersionOneAsync();
        var earlierUtc = SceneFactory.At(new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.FromHours(8)));
        var laterUtc = SceneFactory.At(new DateTimeOffset(2026, 8, 24, 3, 0, 0, TimeSpan.Zero));
        await fixture.InsertVersionOneSnapshotAsync(earlierUtc);
        await fixture.InsertVersionOneSnapshotAsync(laterUtc);

        var snapshots = await fixture.Repository.GetRecentAsync(10, CancellationToken.None);

        Assert.Equal([laterUtc.Id, earlierUtc.Id], snapshots.Select(snapshot => snapshot.Id));
        Assert.Equal(2L, await fixture.GetInt64Async("SELECT version FROM schema_info;"));
    }

    /// <summary>验证标记不可用后的快照不再作为有效最近结果返回，且有效快照仍保持可读。</summary>
    [Fact]
    public async Task MarkInvalidAsyncExcludesSnapshotFromRecentResults()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var older = SceneFactory.AtMinute(1);
        var newer = SceneFactory.AtMinute(2);
        await fixture.Repository.SaveAsync(older, CancellationToken.None);
        await fixture.Repository.SaveAsync(newer, CancellationToken.None);

        await fixture.Repository.MarkInvalidAsync(newer.Id, "无法恢复的测试快照", CancellationToken.None);
        var snapshots = await fixture.Repository.GetRecentAsync(10, CancellationToken.None);

        var restored = Assert.Single(snapshots);
        Assert.Equal(older.Id, restored.Id);
    }

    /// <summary>验证包含窗口、显示器、资源管理器目录等复杂字段的快照可经真实 SQLite 往返且不丢失信息。</summary>
    [Fact]
    public async Task SaveAsyncRoundTripsComplexSceneFields()
    {
        await using var fixture = await RepositoryFixture.CreateAsync();
        var snapshot = SceneFactory.Complex();

        await fixture.Repository.SaveAsync(snapshot, CancellationToken.None);
        var restored = Assert.Single(await fixture.Repository.GetRecentAsync(10, CancellationToken.None));

        Assert.Equal(snapshot.Id, restored.Id);
        Assert.Equal(snapshot.FormatVersion, restored.FormatVersion);
        Assert.Equal(snapshot.CapturedAt, restored.CapturedAt);
        Assert.Equal(snapshot.CaptureReason, restored.CaptureReason);
        Assert.Equal(snapshot.Items.Count, restored.Items.Count);
        for (var index = 0; index < snapshot.Items.Count; index++)
        {
            Assert.Equal(snapshot.Items[index], restored.Items[index]);
        }
    }

    /// <summary>保存真实临时 SQLite 数据库的测试夹具，并在释放时删除测试根目录。</summary>
    private sealed class RepositoryFixture : IAsyncDisposable
    {
        private readonly string rootDirectory;

        /// <summary>使用临时根目录和对应快照仓库初始化测试夹具。</summary>
        /// <param name="rootDirectory">测试专用应用数据根目录。</param>
        /// <param name="paths">测试专用的应用数据路径。</param>
        /// <param name="repository">待测真实 SQLite 快照仓库。</param>
        private RepositoryFixture(string rootDirectory, AppDataPaths paths, SqliteSceneRepository repository)
        {
            this.rootDirectory = rootDirectory;
            Paths = paths;
            Repository = repository;
        }

        /// <summary>获取测试专用的应用数据路径。</summary>
        public AppDataPaths Paths { get; }

        /// <summary>获取待测真实 SQLite 快照仓库。</summary>
        public SqliteSceneRepository Repository { get; }

        /// <summary>创建隔离的真实 SQLite 仓库测试夹具。</summary>
        /// <returns>可用于持久化行为验证的夹具。</returns>
        public static async Task<RepositoryFixture> CreateAsync()
        {
            var rootDirectory = Path.Combine(Path.GetTempPath(), $"DeskButler.Persistence.Tests.{Guid.NewGuid():N}");
            var paths = new AppDataPaths(rootDirectory);
            var fixture = new RepositoryFixture(rootDirectory, paths, new SqliteSceneRepository(paths));
            await fixture.Repository.GetRecentAsync(1, CancellationToken.None);
            return fixture;
        }

        /// <summary>创建仍使用版本一时间文本格式的真实 SQLite 数据库夹具。</summary>
        /// <returns>可用于验证数据库迁移的夹具。</returns>
        public static async Task<RepositoryFixture> CreateVersionOneAsync()
        {
            var rootDirectory = Path.Combine(Path.GetTempPath(), $"DeskButler.Persistence.Tests.{Guid.NewGuid():N}");
            var paths = new AppDataPaths(rootDirectory);
            var fixture = new RepositoryFixture(rootDirectory, paths, new SqliteSceneRepository(paths));
            paths.EnsureRootDirectoryExists();
            await fixture.ExecuteSqlAsync(
                """
                CREATE TABLE schema_info(version INTEGER NOT NULL);
                INSERT INTO schema_info(version) VALUES(1);
                CREATE TABLE scene_snapshots(
                  id TEXT PRIMARY KEY,
                  captured_at TEXT NOT NULL,
                  capture_reason TEXT NOT NULL,
                  format_version INTEGER NOT NULL,
                  payload_json TEXT NOT NULL,
                  is_valid INTEGER NOT NULL DEFAULT 1,
                  invalid_reason TEXT NULL
                );
                CREATE INDEX ix_scene_snapshots_recent ON scene_snapshots(is_valid, captured_at DESC);
                CREATE TABLE restore_runs(
                  id TEXT PRIMARY KEY,
                  scene_id TEXT NOT NULL,
                  started_at TEXT NOT NULL,
                  completed_at TEXT NULL,
                  result_json TEXT NULL
                );
                """);
            return fixture;
        }

        /// <summary>按版本一格式向真实测试数据库写入快照，以验证后续迁移读取。</summary>
        /// <param name="snapshot">要使用版本一时间文本格式写入的快照。</param>
        /// <returns>写入完成的任务。</returns>
        public async Task InsertVersionOneSnapshotAsync(SceneSnapshot snapshot)
        {
            await using var connection = new SqliteConnection($"Data Source={Paths.DatabasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO scene_snapshots(id, captured_at, capture_reason, format_version, payload_json, is_valid, invalid_reason)
                VALUES($id, $capturedAt, $captureReason, $formatVersion, $payload, 1, NULL);
                """;
            command.Parameters.AddWithValue("$id", snapshot.Id.ToString("D"));
            command.Parameters.AddWithValue("$capturedAt", snapshot.CapturedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$captureReason", snapshot.CaptureReason);
            command.Parameters.AddWithValue("$formatVersion", snapshot.FormatVersion);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(snapshot, TestSerializerOptions));
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>在测试数据库中执行受控 SQL，以构造真实存储层边界条件。</summary>
        /// <param name="sql">待执行的测试 SQL。</param>
        /// <returns>SQL 执行完成的任务。</returns>
        public async Task ExecuteSqlAsync(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={Paths.DatabasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>执行返回单个整数的测试 SQL 查询。</summary>
        /// <param name="sql">待执行的测试查询。</param>
        /// <returns>查询返回的整数结果。</returns>
        public async Task<long> GetInt64Async(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={Paths.DatabasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>删除测试夹具创建的临时应用数据目录。</summary>
        /// <returns>释放完成的任务。</returns>
        public ValueTask DisposeAsync()
        {
            Repository.Dispose();
            // SQLite 连接池会持有临时数据库句柄；清空池后才能在 Windows 上可靠删除测试目录。
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>创建由文字常量定义的场景快照，避免测试期望复用生产逻辑。</summary>
    private static class SceneFactory
    {
        /// <summary>创建捕获分钟固定且包含一个窗口的快照。</summary>
        /// <param name="minute">捕获时间的分钟值。</param>
        /// <returns>用于保留策略测试的场景快照。</returns>
        public static SceneSnapshot AtMinute(int minute)
        {
            return At(new DateTimeOffset(2026, 8, 24, 9, minute, 0, TimeSpan.Zero));
        }

        /// <summary>创建捕获时刻固定且包含一个窗口的快照。</summary>
        /// <param name="capturedAt">快照的捕获时刻。</param>
        /// <returns>用于时间排序和保留策略测试的场景快照。</returns>
        public static SceneSnapshot At(DateTimeOffset capturedAt)
        {
            return new SceneSnapshot(
                Guid.NewGuid(),
                1,
                capturedAt,
                "自动检查点",
                [CreateItem("notepad", @"C:\\Windows\\System32\\notepad.exe", null)]);
        }

        /// <summary>创建包含多个不同窗口状态和可选字段的复杂场景快照。</summary>
        /// <returns>用于序列化往返验证的场景快照。</returns>
        public static SceneSnapshot Complex()
        {
            return new SceneSnapshot(
                Guid.Parse("b1de59e3-c158-46c2-ae0d-469daa7974db"),
                7,
                new DateTimeOffset(2026, 8, 24, 10, 45, 30, TimeSpan.FromHours(8)),
                "用户手动保存",
                [
                    CreateItem("explorer", @"C:\\Windows\\explorer.exe", @"C:\\Users\\Alice\\Documents") with
                    {
                        TitleHint = "项目文档",
                        Bounds = new WindowBounds(-1200, 90, 1180, 800),
                        State = SceneWindowState.Maximized,
                        Monitor = new MonitorIdentity("\\\\.\\DISPLAY2", new WindowBounds(-1920, 0, 1920, 1080), 144, 144)
                    },
                    CreateItem("editor", @"D:\\Apps\\Editor.exe", null) with
                    {
                        WindowClass = "EditorMainWindow",
                        TitleHint = null,
                        State = SceneWindowState.Minimized,
                        WasElevated = true
                    }
                ]);
        }

        /// <summary>创建具有稳定默认字段的场景窗口条目。</summary>
        /// <param name="id">窗口稳定标识。</param>
        /// <param name="executablePath">窗口所属可执行文件路径。</param>
        /// <param name="explorerPath">资源管理器目录或空值。</param>
        /// <returns>场景窗口条目。</returns>
        private static SceneItem CreateItem(string id, string executablePath, string? explorerPath)
        {
            return new SceneItem(
                id,
                executablePath,
                "Notepad",
                "记事本",
                explorerPath,
                new WindowBounds(20, 30, 900, 650),
                SceneWindowState.Normal,
                new MonitorIdentity("\\\\.\\DISPLAY1", new WindowBounds(0, 0, 1920, 1080), 96, 96),
                false);
        }
    }
}
