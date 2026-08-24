using DeskButler.Core.Restore;
using DeskButler.Persistence.Paths;
using DeskButler.Persistence.Sqlite;

namespace DeskButler.Persistence.Tests.Sqlite;

public sealed class SqliteFailureHistoryStoreTests
{
    /// <summary>失败次数在三次饱和，成功会清零，跳过和取消不会伪造结果。</summary>
    [Fact]
    public async Task RecordsExistingRestoreResultsWithoutExceedingThreeFailures()
    {
        using var fixture = new TempDirectory();
        var store = new SqliteFailureHistoryStore(new AppDataPaths(fixture.Path));
        var failed = new RestoreResult([new RestoreItemResult("editor", RestoreItemStatus.Failed, "timeout")]);
        for (var index = 0; index < 5; index++)
        {
            await store.RecordAsync(failed, TestContext.Current.CancellationToken);
        }

        var saturated = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, saturated.CountFor("editor"));

        await store.RecordAsync(
            new RestoreResult([
                new RestoreItemResult("editor", RestoreItemStatus.Succeeded),
                new RestoreItemResult("skipped", RestoreItemStatus.Skipped),
                new RestoreItemResult("cancelled", RestoreItemStatus.Cancelled)]),
            TestContext.Current.CancellationToken);

        var reset = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, reset.CountFor("editor"));
        Assert.Equal(0, reset.CountFor("skipped"));
        Assert.Equal(0, reset.CountFor("cancelled"));
    }

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DeskButler.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        /// <summary>清空连接池后删除隔离数据库目录。</summary>
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Path, true);
        }
    }
}
