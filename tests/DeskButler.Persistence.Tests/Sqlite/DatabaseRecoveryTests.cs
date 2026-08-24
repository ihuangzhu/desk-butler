using DeskButler.Persistence.Paths;
using DeskButler.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace DeskButler.Persistence.Tests.Sqlite;

public sealed class DatabaseRecoveryTests
{
    /// <summary>真实未来版本数据库必须原样拒绝，不能被误判为迁移失败后销毁。</summary>
    [Fact]
    public async Task FutureSchemaVersionIsRejectedWithoutRecoverySideEffects()
    {
        using var fixture = new TempDirectory();
        var paths = new AppDataPaths(fixture.Path);
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE schema_info(version INTEGER NOT NULL); INSERT INTO schema_info VALUES(999);";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        SqliteConnection.ClearAllPools();
        var original = await File.ReadAllBytesAsync(paths.DatabasePath, TestContext.Current.CancellationToken);
        var lifecycle = new RecordingLifecycle();
        var recovery = new DatabaseRecovery(paths, lifecycle, new DatabaseMigrator(paths));

        await Assert.ThrowsAsync<FutureSchemaVersionException>(
            () => recovery.InitializeAsync(TestContext.Current.CancellationToken));
        SqliteConnection.ClearAllPools();

        Assert.Equal(0, lifecycle.CloseCalls);
        Assert.Equal(original, await File.ReadAllBytesAsync(paths.DatabasePath, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(paths.RootDirectory, "database-recovery.marker.json")));
        Assert.False(Directory.Exists(paths.DiagnosticsDirectory));
        SqliteConnection.ClearAllPools();
    }

    /// <summary>未来版本处于 WAL 时预检必须在副本完成，原 DB/WAL/SHM 字节均不得变化。</summary>
    [Fact]
    public async Task FutureSchemaInWalLeavesDatabaseAndSidecarsByteIdentical()
    {
        using var fixture = new TempDirectory();
        var paths = new AppDataPaths(fixture.Path);
        await using var writer = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False");
        await writer.OpenAsync(TestContext.Current.CancellationToken);
        await using (var command = writer.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE schema_info(version INTEGER NOT NULL); INSERT INTO schema_info VALUES(999);";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var files = new[] { paths.DatabasePath, paths.DatabasePath + "-wal", paths.DatabasePath + "-shm" };
        var before = files.ToDictionary(path => path,
            ReadSharedBytes, StringComparer.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<FutureSchemaVersionException>(() => new DatabaseRecovery(
            paths, new RecordingLifecycle(), new DatabaseMigrator(paths))
            .InitializeAsync(TestContext.Current.CancellationToken));

        foreach (var file in files)
        {
            Assert.Equal(before[file], ReadSharedBytes(file));
        }
        Assert.False(File.Exists(Path.Combine(paths.RootDirectory, "database-recovery.marker.json")));
    }

    /// <summary>以 SQLite 兼容共享模式读取仍由真实连接持有的数据库现场。</summary>
    private static byte[] ReadSharedBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>迁移失败必须先关闭连接，再完整备份 DB/WAL/SHM，随后才创建并迁移新库。</summary>
    [Fact]
    public async Task MigrationFailureBacksUpAllEvidenceThenCreatesFreshDatabase()
    {
        using var fixture = new TempDirectory();
        var paths = new AppDataPaths(fixture.Path);
        await File.WriteAllTextAsync(paths.DatabasePath, "corrupt-db", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(paths.DatabasePath + "-wal", "corrupt-wal", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(paths.DatabasePath + "-shm", "corrupt-shm", TestContext.Current.CancellationToken);
        var lifecycle = new RecordingLifecycle();
        var initializer = new FailsOnceInitializer(paths.DatabasePath);
        var recovery = new DatabaseRecovery(paths, lifecycle, initializer, () => new DateTimeOffset(2026, 8, 25, 1, 2, 3, TimeSpan.Zero));

        var result = await recovery.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(result.WasRecovered);
        Assert.NotNull(result.HealthWarning);
        Assert.Equal(1, lifecycle.CloseCalls);
        Assert.Equal(2, initializer.Calls);
        Assert.Equal("fresh", await File.ReadAllTextAsync(paths.DatabasePath, TestContext.Current.CancellationToken));
        Assert.Equal("corrupt-db", await File.ReadAllTextAsync(Path.Combine(result.BackupDirectory!, "deskbutler.db"), TestContext.Current.CancellationToken));
        Assert.Equal("corrupt-wal", await File.ReadAllTextAsync(Path.Combine(result.BackupDirectory!, "deskbutler.db-wal"), TestContext.Current.CancellationToken));
        Assert.Equal("corrupt-shm", await File.ReadAllTextAsync(Path.Combine(result.BackupDirectory!, "deskbutler.db-shm"), TestContext.Current.CancellationToken));
    }

    /// <summary>备份失败不得删除或覆盖唯一损坏数据库，且不得尝试创建新库。</summary>
    [Fact]
    public async Task BackupFailurePreservesOriginalAndDoesNotReinitialize()
    {
        using var fixture = new TempDirectory();
        var paths = new AppDataPaths(fixture.Path);
        await File.WriteAllTextAsync(paths.DatabasePath, "corrupt-db", TestContext.Current.CancellationToken);
        var initializer = new AlwaysFailsInitializer();
        var recovery = new DatabaseRecovery(
            paths, new RecordingLifecycle(), initializer, () => DateTimeOffset.UnixEpoch,
            (_, _) => throw new IOException("backup failed"));

        await Assert.ThrowsAsync<IOException>(() => recovery.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Equal("corrupt-db", await File.ReadAllTextAsync(paths.DatabasePath, TestContext.Current.CancellationToken));
        Assert.Equal(1, initializer.Calls);
    }

    /// <summary>Windows 独占锁必须由注入生命周期先释放，否则真实复制无法成功。</summary>
    [Fact]
    public async Task ConnectionLifecycleReleasesWindowsLockBeforeEvidenceCopy()
    {
        using var fixture = new TempDirectory();
        var paths = new AppDataPaths(fixture.Path);
        await File.WriteAllTextAsync(paths.DatabasePath, "locked-corrupt", TestContext.Current.CancellationToken);
        using var lifecycle = new LockHoldingLifecycle(paths.DatabasePath);
        var recovery = new DatabaseRecovery(
            paths, lifecycle, new FailsOnceInitializer(paths.DatabasePath), () => DateTimeOffset.UnixEpoch);

        var result = await recovery.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(result.WasRecovered);
        Assert.True(lifecycle.WasClosed);
        Assert.Equal("locked-corrupt", await File.ReadAllTextAsync(
            Path.Combine(result.BackupDirectory!, "deskbutler.db"), TestContext.Current.CancellationToken));
    }

    /// <summary>新库迁移再次失败时必须公开备份目录，且诊断证据仍保持原始字节。</summary>
    [Fact]
    public async Task RebuildFailureExposesPreservedEvidenceDirectory()
    {
        using var fixture = new TempDirectory();
        var paths = new AppDataPaths(fixture.Path);
        await File.WriteAllTextAsync(paths.DatabasePath, "corrupt-db", TestContext.Current.CancellationToken);
        var recovery = new DatabaseRecovery(
            paths, new RecordingLifecycle(), new AlwaysFailsInitializer(), () => DateTimeOffset.UnixEpoch);

        var exception = await Assert.ThrowsAsync<DatabaseRecoveryException>(
            () => recovery.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Equal("corrupt-db", await File.ReadAllTextAsync(
            Path.Combine(exception.BackupDirectory, "deskbutler.db"), TestContext.Current.CancellationToken));
    }

    /// <summary>锁定、磁盘满、IO、只读和权限故障必须原样上抛且不触碰证据。</summary>
    [Fact]
    public async Task NonRecoverableStorageFailuresDoNotBackupOrRebuild()
    {
        foreach (var exception in new Exception[]
                 {
                     new Microsoft.Data.Sqlite.SqliteException("busy", 5),
                     new Microsoft.Data.Sqlite.SqliteException("locked", 6),
                     new Microsoft.Data.Sqlite.SqliteException("full", 13),
                     new Microsoft.Data.Sqlite.SqliteException("ioerr", 10),
                     new Microsoft.Data.Sqlite.SqliteException("cantopen", 14),
                     new Microsoft.Data.Sqlite.SqliteException("readonly", 8),
                     new UnauthorizedAccessException("denied"),
                     new IOException("disk"),
                     new NotSupportedException("unsupported")
                 })
        {
            using var fixture = new TempDirectory();
            var paths = new AppDataPaths(fixture.Path);
            await File.WriteAllTextAsync(paths.DatabasePath, "original", TestContext.Current.CancellationToken);
            var lifecycle = new RecordingLifecycle();
            var initializer = new SpecificFailureInitializer(exception);
            var recovery = new DatabaseRecovery(paths, lifecycle, initializer);

            var actual = await Assert.ThrowsAsync(exception.GetType(),
                () => recovery.InitializeAsync(TestContext.Current.CancellationToken));

            Assert.Same(exception, actual);
            Assert.Equal(0, lifecycle.CloseCalls);
            Assert.Equal(1, initializer.Calls);
            Assert.Equal("original", await File.ReadAllTextAsync(
                paths.DatabasePath, TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(paths.DiagnosticsDirectory));
        }
    }

    /// <summary>只有 SQLite CORRUPT 和 NOTADB 明确损坏码可直接触发证据备份与重建。</summary>
    [Theory]
    [InlineData(11)]
    [InlineData(26)]
    public async Task ExplicitSqliteCorruptionCodesTriggerRecovery(int errorCode)
    {
        using var fixture = new TempDirectory();
        var paths = new AppDataPaths(fixture.Path);
        await File.WriteAllTextAsync(paths.DatabasePath, "corrupt", TestContext.Current.CancellationToken);
        var initializer = new SqliteFailureOnceInitializer(paths.DatabasePath, errorCode);
        var recovery = new DatabaseRecovery(paths, new RecordingLifecycle(), initializer);

        var result = await recovery.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(result.WasRecovered);
        Assert.Equal("corrupt", await File.ReadAllTextAsync(
            Path.Combine(result.BackupDirectory!, "deskbutler.db"), TestContext.Current.CancellationToken));
    }

    private sealed class SqliteFailureOnceInitializer(string databasePath, int errorCode) : IDatabaseInitializer
    {
        private int calls;

        /// <inheritdoc />
        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            if (calls++ == 0)
            {
                throw new Microsoft.Data.Sqlite.SqliteException("corrupt", errorCode);
            }

            return File.WriteAllTextAsync(databasePath, "fresh", cancellationToken);
        }
    }

    /// <summary>主库已删但 WAL 删除失败时 marker 必须保留，第二次启动从原备份继续完成。</summary>
    [Fact]
    public async Task PartialWorkingFileCleanupResumesFromMarkerOnNextStartup()
    {
        using var fixture = new TempDirectory();
        var paths = new AppDataPaths(fixture.Path);
        await File.WriteAllTextAsync(paths.DatabasePath, "corrupt-db", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(paths.DatabasePath + "-wal", "corrupt-wal", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(paths.DatabasePath + "-shm", "corrupt-shm", TestContext.Current.CancellationToken);
        var failWalOnce = true;
        var initializer = new FailsOnceInitializer(paths.DatabasePath);
        var recovery = new DatabaseRecovery(
            paths, new RecordingLifecycle(), initializer, () => DateTimeOffset.UnixEpoch, null,
            path =>
            {
                if (path.EndsWith("-wal", StringComparison.Ordinal) && failWalOnce)
                {
                    failWalOnce = false;
                    throw new IOException("wal cleanup failed");
                }

                File.Delete(path);
            });

        var interrupted = await Assert.ThrowsAsync<DatabaseRecoveryException>(
            () => recovery.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.False(File.Exists(paths.DatabasePath));
        Assert.True(File.Exists(paths.DatabasePath + "-wal"));
        Assert.True(File.Exists(Path.Combine(paths.RootDirectory, "database-recovery.marker.json")));
        Assert.Equal("corrupt-db", await File.ReadAllTextAsync(
            Path.Combine(interrupted.BackupDirectory, "deskbutler.db"), TestContext.Current.CancellationToken));

        var resumed = await recovery.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(resumed.WasRecovered);
        Assert.Equal("fresh", await File.ReadAllTextAsync(paths.DatabasePath, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(paths.RootDirectory, "database-recovery.marker.json")));
        Assert.Equal(interrupted.BackupDirectory, resumed.BackupDirectory);
    }

    /// <summary>缺失显式阶段的伪造 marker 必须在关闭连接或改动健康工作库前被拒绝。</summary>
    [Fact]
    public async Task MarkerMissingPhaseIsRejectedWithoutChangingWorkingDatabase()
    {
        using var fixture = new TempDirectory();
        var paths = new AppDataPaths(fixture.Path);
        await File.WriteAllTextAsync(paths.DatabasePath, "healthy", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(paths.DiagnosticsDirectory);
        var backup = Path.Combine(paths.DiagnosticsDirectory, "fake");
        Directory.CreateDirectory(backup);
        var marker = new JsonObject
        {
            ["Version"] = 1,
            ["RecoveryId"] = "fake",
            ["BackupDirectory"] = backup,
            ["Evidence"] = new JsonArray()
        };
        await File.WriteAllTextAsync(Path.Combine(paths.RootDirectory, "database-recovery.marker.json"),
            marker.ToJsonString(), TestContext.Current.CancellationToken);
        var lifecycle = new RecordingLifecycle();

        await Assert.ThrowsAsync<InvalidDataException>(() => new DatabaseRecovery(
            paths, lifecycle, new SpecificFailureInitializer(new InvalidOperationException("must not run")))
            .InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Equal("healthy", await File.ReadAllTextAsync(paths.DatabasePath, TestContext.Current.CancellationToken));
        Assert.Equal(0, lifecycle.CloseCalls);
    }

    /// <summary>格式完整但备份目录为空的 marker 也不得授权删除健康工作库。</summary>
    [Fact]
    public async Task MarkerWithEmptyEvidenceDirectoryIsRejectedWithoutChangingWorkingDatabase()
    {
        using var fixture = new TempDirectory();
        var paths = new AppDataPaths(fixture.Path);
        await File.WriteAllTextAsync(paths.DatabasePath, "healthy", TestContext.Current.CancellationToken);
        var backup = Path.Combine(paths.DiagnosticsDirectory, "fake-empty");
        Directory.CreateDirectory(backup);
        var marker = new JsonObject
        {
            ["Version"] = 1,
            ["RecoveryId"] = "fake-empty",
            ["BackupDirectory"] = backup,
            ["Phase"] = "evidence-backed-up",
            ["Evidence"] = new JsonArray(new JsonObject
            {
                ["LogicalName"] = "deskbutler.db",
                ["Length"] = 7,
                ["Sha256"] = new string('0', 64)
            })
        };
        await File.WriteAllTextAsync(Path.Combine(paths.RootDirectory, "database-recovery.marker.json"),
            marker.ToJsonString(), TestContext.Current.CancellationToken);
        var lifecycle = new RecordingLifecycle();

        await Assert.ThrowsAsync<InvalidDataException>(() => new DatabaseRecovery(
            paths, lifecycle, new SpecificFailureInitializer(new InvalidOperationException("must not run")))
            .InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Equal("healthy", await File.ReadAllTextAsync(paths.DatabasePath, TestContext.Current.CancellationToken));
        Assert.Equal(0, lifecycle.CloseCalls);
    }

    /// <summary>diagnostics 下看似合法的 RecoveryId junction 指向外部时必须在触碰工作库前拒绝。</summary>
    [Fact]
    public async Task MarkerBackupJunctionToExternalDirectoryIsRejected()
    {
        using var fixture = new TempDirectory();
        using var external = new TempDirectory();
        var paths = new AppDataPaths(fixture.Path);
        await File.WriteAllTextAsync(paths.DatabasePath, "healthy", TestContext.Current.CancellationToken);
        var evidenceBytes = "external-evidence"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(external.Path, "deskbutler.db"), evidenceBytes,
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(paths.DiagnosticsDirectory);
        var recoveryId = "junction-evidence";
        var junction = Path.Combine(paths.DiagnosticsDirectory, recoveryId);
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                   "cmd.exe", $"/c mklink /J \"{junction}\" \"{external.Path}\"")
        { CreateNoWindow = true, UseShellExecute = false })!)
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
        }
        var marker = new JsonObject
        {
            ["Version"] = 1,
            ["RecoveryId"] = recoveryId,
            ["BackupDirectory"] = junction,
            ["Phase"] = "evidence-backed-up",
            ["Evidence"] = new JsonArray(new JsonObject
            {
                ["LogicalName"] = "deskbutler.db",
                ["Length"] = evidenceBytes.Length,
                ["Sha256"] = Convert.ToHexString(SHA256.HashData(evidenceBytes))
            })
        };
        await File.WriteAllTextAsync(Path.Combine(paths.RootDirectory, "database-recovery.marker.json"),
            marker.ToJsonString(), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => new DatabaseRecovery(
            paths, new RecordingLifecycle(), new SpecificFailureInitializer(new InvalidOperationException()))
            .InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Equal("healthy", await File.ReadAllTextAsync(paths.DatabasePath, TestContext.Current.CancellationToken));
        Directory.Delete(junction);
    }

    /// <summary>已认证备份被清空或篡改后，续作必须拒绝且不删除仍存在的工作文件。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TamperedRecoveryEvidenceIsRejectedBeforeWorkingFileCleanup(bool deleteEvidence)
    {
        using var fixture = new TempDirectory();
        var paths = new AppDataPaths(fixture.Path);
        await File.WriteAllTextAsync(paths.DatabasePath, "corrupt-original", TestContext.Current.CancellationToken);
        var recovery = new DatabaseRecovery(
            paths, new RecordingLifecycle(), new FailsOnceInitializer(paths.DatabasePath), null, null,
            _ => throw new IOException("stop before cleanup"));
        var interrupted = await Assert.ThrowsAsync<DatabaseRecoveryException>(
            () => recovery.InitializeAsync(TestContext.Current.CancellationToken));
        var evidencePath = Path.Combine(interrupted.BackupDirectory, "deskbutler.db");
        if (deleteEvidence)
        {
            File.Delete(evidencePath);
        }
        else
        {
            await File.WriteAllTextAsync(evidencePath, "tampered", TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            () => recovery.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Equal("corrupt-original", await File.ReadAllTextAsync(paths.DatabasePath, TestContext.Current.CancellationToken));
    }

    /// <summary>校验后的证据句柄必须阻止 lifecycle 在工作库清理前删除或替换备份。</summary>
    [Fact]
    public async Task ResumeHoldsVerifiedEvidenceLeaseAcrossLifecycleAndCleanup()
    {
        using var fixture = new TempDirectory();
        var paths = new AppDataPaths(fixture.Path);
        await File.WriteAllTextAsync(paths.DatabasePath, "corrupt", TestContext.Current.CancellationToken);
        var initializer = new FailsOnceInitializer(paths.DatabasePath);
        var first = new DatabaseRecovery(paths, new RecordingLifecycle(), initializer, null, null,
            _ => throw new IOException("interrupt"));
        var interrupted = await Assert.ThrowsAsync<DatabaseRecoveryException>(
            () => first.InitializeAsync(TestContext.Current.CancellationToken));
        var protectedLifecycle = new EvidenceDeletingLifecycle(
            Path.Combine(interrupted.BackupDirectory, "deskbutler.db"));
        var resumed = await new DatabaseRecovery(paths, protectedLifecycle, initializer)
            .InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(protectedLifecycle.DeleteWasBlocked);
        Assert.Equal("corrupt", await File.ReadAllTextAsync(
            Path.Combine(resumed.BackupDirectory!, "deskbutler.db"), TestContext.Current.CancellationToken));
        Assert.Equal("fresh", await File.ReadAllTextAsync(paths.DatabasePath, TestContext.Current.CancellationToken));
    }

    /// <summary>首次恢复同样必须在删除工作库时持有刚验证的备份证据句柄。</summary>
    [Fact]
    public async Task InitialRecoveryHoldsVerifiedEvidenceLeaseDuringWorkingFileDeletion()
    {
        using var fixture = new TempDirectory();
        var paths = new AppDataPaths(fixture.Path);
        await File.WriteAllTextAsync(paths.DatabasePath, "corrupt", TestContext.Current.CancellationToken);
        var deleteWasBlocked = false;
        var recovery = new DatabaseRecovery(
            paths, new RecordingLifecycle(), new FailsOnceInitializer(paths.DatabasePath), null, null,
            workingPath =>
            {
                var backup = Directory.GetDirectories(paths.DiagnosticsDirectory).Single();
                try
                {
                    File.Delete(Path.Combine(backup, "deskbutler.db"));
                }
                catch (IOException)
                {
                    deleteWasBlocked = true;
                }
                File.Delete(workingPath);
            });

        var result = await recovery.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(deleteWasBlocked);
        Assert.Equal("corrupt", await File.ReadAllTextAsync(
            Path.Combine(result.BackupDirectory!, "deskbutler.db"), TestContext.Current.CancellationToken));
    }

    /// <summary>确定性竞态钩子证明目录验证后和证据打开后都不能替换已锁定身份。</summary>
    [Fact]
    public async Task ValidatedDirectoryAndEvidenceCannotBeReplacedThroughRaceHooks()
    {
        using var fixture = new TempDirectory();
        var paths = new AppDataPaths(fixture.Path);
        await File.WriteAllTextAsync(paths.DatabasePath, "corrupt", TestContext.Current.CancellationToken);
        var directoryBlocked = false;
        var evidenceBlocked = false;
        var recovery = new DatabaseRecovery(
            paths, new RecordingLifecycle(), new FailsOnceInitializer(paths.DatabasePath), null, null, null,
            backup =>
            {
                try { Directory.Move(backup, backup + ".moved"); }
                catch (IOException) { directoryBlocked = true; }
            },
            evidence =>
            {
                try { File.Move(evidence, evidence + ".moved"); }
                catch (IOException) { evidenceBlocked = true; }
            });

        var result = await recovery.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(directoryBlocked);
        Assert.True(evidenceBlocked);
        Assert.True(File.Exists(Path.Combine(result.BackupDirectory!, "deskbutler.db")));
    }

    /// <summary>证据 final-path 解析失败时句柄必须立即释放，可重复改名且无需等待 GC。</summary>
    [Fact]
    public async Task EvidenceHandleIsReleasedImmediatelyWhenFinalPathResolutionFails()
    {
        using var fixture = new TempDirectory();
        var paths = new AppDataPaths(fixture.Path);
        await File.WriteAllTextAsync(paths.DatabasePath, "corrupt", TestContext.Current.CancellationToken);
        var initializer = new FailsOnceInitializer(paths.DatabasePath);
        var interrupted = await Assert.ThrowsAsync<DatabaseRecoveryException>(() => new DatabaseRecovery(
            paths, new RecordingLifecycle(), initializer, deleteFile: _ => throw new IOException("interrupt"))
            .InitializeAsync(TestContext.Current.CancellationToken));
        var evidence = Path.Combine(interrupted.BackupDirectory, "deskbutler.db");
        var moved = evidence + ".moved";
        var recovery = new DatabaseRecovery(
            paths, new RecordingLifecycle(), initializer,
            evidenceFinalPathResolver: _ => throw new InvalidOperationException("injected final path failure"));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => recovery.InitializeAsync(TestContext.Current.CancellationToken));
            File.Move(evidence, moved);
            File.Move(moved, evidence);
        }
    }

    /// <summary>证据处理取消必须在 marker 前停止，并保持工作数据库不变。</summary>
    [Fact]
    public async Task CancellationBeforeEvidenceHashDoesNotAdvanceRecoveryMarker()
    {
        using var fixture = new TempDirectory();
        var paths = new AppDataPaths(fixture.Path);
        await File.WriteAllBytesAsync(paths.DatabasePath, new byte[1024 * 1024], TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var recovery = new DatabaseRecovery(
            paths, new RecordingLifecycle(), new FailsOnceInitializer(paths.DatabasePath));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => recovery.InitializeAsync(cancellation.Token));

        Assert.Equal(1024 * 1024, new FileInfo(paths.DatabasePath).Length);
        Assert.False(File.Exists(Path.Combine(paths.RootDirectory, "database-recovery.marker.json")));
    }

    private sealed class SpecificFailureInitializer(Exception exception) : IDatabaseInitializer
    {
        internal int Calls { get; private set; }

        /// <inheritdoc />
        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromException(exception);
        }
    }

    private sealed class RecordingLifecycle : IDatabaseConnectionLifecycle
    {
        internal int CloseCalls { get; private set; }

        /// <inheritdoc />
        public ValueTask CloseAllAsync(CancellationToken cancellationToken)
        {
            CloseCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EvidenceDeletingLifecycle(string evidencePath) : IDatabaseConnectionLifecycle
    {
        internal bool DeleteWasBlocked { get; private set; }

        /// <inheritdoc />
        public ValueTask CloseAllAsync(CancellationToken cancellationToken)
        {
            try
            {
                File.Delete(evidencePath);
            }
            catch (IOException)
            {
                DeleteWasBlocked = true;
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class LockHoldingLifecycle : IDatabaseConnectionLifecycle, IDisposable
    {
        private FileStream? stream;

        internal LockHoldingLifecycle(string databasePath) =>
            stream = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.None);

        internal bool WasClosed { get; private set; }

        /// <inheritdoc />
        public async ValueTask CloseAllAsync(CancellationToken cancellationToken)
        {
            await stream!.DisposeAsync();
            stream = null;
            WasClosed = true;
        }

        /// <summary>测试失败时仍释放独占句柄。</summary>
        public void Dispose() => stream?.Dispose();
    }

    private sealed class FailsOnceInitializer(string databasePath) : IDatabaseInitializer
    {
        internal int Calls { get; private set; }

        /// <inheritdoc />
        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 1)
            {
                throw new MigrationFailureException("migration failed");
            }

            return File.WriteAllTextAsync(databasePath, "fresh", cancellationToken);
        }
    }

    private sealed class AlwaysFailsInitializer : IDatabaseInitializer
    {
        internal int Calls { get; private set; }

        /// <inheritdoc />
        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Calls++;
            throw new MigrationFailureException("migration failed");
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DeskButler.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        /// <summary>删除本测试创建的隔离目录。</summary>
        public void Dispose()
        {
            // Microsoft.Data.Sqlite 池可能跨测试保留目录句柄；断言结束后统一释放测试连接池。
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Directory.Delete(Path, true);
        }
    }
}
