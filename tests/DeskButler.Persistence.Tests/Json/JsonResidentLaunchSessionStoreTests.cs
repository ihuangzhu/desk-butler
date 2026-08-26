using DeskButler.Core.ResidentApps;
using DeskButler.Persistence.Json;
using DeskButler.Persistence.Paths;

namespace DeskButler.Persistence.Tests.Json;

public sealed class JsonResidentLaunchSessionStoreTests
{
    /// <summary>验证会话文件不存在时不会伪造任何既有登录计划。</summary>
    [Fact]
    public async Task LoadAsyncReturnsNullWhenSessionFileIsMissing()
    {
        await using var fixture = new SessionFixture();

        var session = await fixture.Store.LoadAsync(CancellationToken.None);

        Assert.Null(session);
    }

    /// <summary>验证原子保存后的会话能完整往返固定版本、LUID、完成状态和计划项。</summary>
    [Fact]
    public async Task SaveAsyncRoundTripsResidentLaunchSession()
    {
        await using var fixture = new SessionFixture();
        var expected = new ResidentLaunchSession(
            1,
            "luid-first",
            false,
            [new ResidentLaunchPlanItem("app-one", true), new ResidentLaunchPlanItem("app-two", false)]);

        await fixture.Store.SaveAsync(expected, CancellationToken.None);
        var loaded = await fixture.Store.LoadAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(expected.FormatVersion, loaded.FormatVersion);
        Assert.Equal(expected.LogonSessionId, loaded.LogonSessionId);
        Assert.Equal(expected.Completed, loaded.Completed);
        Assert.Equal(expected.Plan, loaded.Plan);
        Assert.False(File.Exists(fixture.Paths.ResidentLaunchSessionFilePath + ".tmp"));
    }

    /// <summary>验证后续保存会原子替换旧 LUID，而不是保留上一登录批次。</summary>
    [Fact]
    public async Task SaveAsyncReplacesOldLogonSessionId()
    {
        await using var fixture = new SessionFixture();
        await fixture.Store.SaveAsync(new ResidentLaunchSession(1, "luid-old", false, []), CancellationToken.None);

        await fixture.Store.SaveAsync(
            new ResidentLaunchSession(1, "luid-current", true, [new ResidentLaunchPlanItem("app-one", true)]),
            CancellationToken.None);
        var loaded = await fixture.Store.LoadAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("luid-current", loaded.LogonSessionId);
        Assert.True(loaded.Completed);
        Assert.Single(loaded.Plan);
    }

    /// <summary>验证目标替换失败时 finally 会清理临时文件。</summary>
    [Fact]
    public async Task SaveAsyncCleansTemporaryFileWhenAtomicReplacementFails()
    {
        await using var fixture = new SessionFixture();
        await fixture.Store.SaveAsync(new ResidentLaunchSession(1, "luid-old", false, []), CancellationToken.None);
        using var lockStream = new FileStream(
            fixture.Paths.ResidentLaunchSessionFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Store.SaveAsync(
            new ResidentLaunchSession(1, "luid-current", true, []),
            CancellationToken.None));

        Assert.False(File.Exists(fixture.Paths.ResidentLaunchSessionFilePath + ".tmp"));
    }

    /// <summary>验证损坏会话会先改名保留，再写入当前 LUID 的已完成空计划。</summary>
    [Fact]
    public async Task RecoverCorruptAsyncPreservesFileAndWritesCompletedEmptyPlan()
    {
        await using var fixture = new SessionFixture();
        Directory.CreateDirectory(fixture.Paths.RootDirectory);
        await File.WriteAllTextAsync(fixture.Paths.ResidentLaunchSessionFilePath, "{ invalid", CancellationToken.None);

        var result = await fixture.Store.RecoverCorruptAsync("luid-current", CancellationToken.None);
        var recovered = await fixture.Store.LoadAsync(CancellationToken.None);

        Assert.Equal(ResidentLaunchRecoveryResult.RecoveredWithEmptyPlan, result);
        Assert.NotNull(recovered);
        Assert.Equal("luid-current", recovered.LogonSessionId);
        Assert.True(recovered.Completed);
        Assert.Empty(recovered.Plan);
        var evidence = Assert.Single(Directory.EnumerateFiles(fixture.Paths.RootDirectory, "resident-launch-session.corrupt-*.json"));
        Assert.Equal("{ invalid", await File.ReadAllTextAsync(evidence, CancellationToken.None));
    }

    /// <summary>验证故障证据无法改名时会 fail-closed，原始字节及会话路径均不可被覆盖。</summary>
    [Fact]
    public async Task RecoverCorruptAsyncFailsClosedWhenEvidencePreservationIsRejected()
    {
        await using var fixture = new SessionFixture(new RejectingCorruptSessionPreserver());
        const string corruptBytes = "{ invalid";
        Directory.CreateDirectory(fixture.Paths.RootDirectory);
        await File.WriteAllTextAsync(fixture.Paths.ResidentLaunchSessionFilePath, corruptBytes, CancellationToken.None);

        var result = await fixture.Store.RecoverCorruptAsync("luid-current", CancellationToken.None);

        Assert.Equal(ResidentLaunchRecoveryResult.PreservationFailedFailClosed, result);
        Assert.Equal(corruptBytes, await File.ReadAllTextAsync(fixture.Paths.ResidentLaunchSessionFilePath, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.RootDirectory, "resident-launch-session.corrupt-*.json"));
        Assert.False(File.Exists(fixture.Paths.ResidentLaunchSessionFilePath + ".tmp"));
    }

    /// <summary>提供使用真实临时目录的会话存储夹具。</summary>
    private sealed class SessionFixture : IAsyncDisposable
    {
        private readonly string rootDirectory = Path.Combine(Path.GetTempPath(), $"DeskButler.ResidentSession.Tests.{Guid.NewGuid():N}");

        /// <summary>使用可选的损坏证据保留器初始化测试夹具。</summary>
        /// <param name="preserver">测试专用的损坏证据保留器。</param>
        public SessionFixture(ICorruptResidentSessionPreserver? preserver = null)
        {
            Paths = new AppDataPaths(rootDirectory);
            Store = preserver is null
                ? new JsonResidentLaunchSessionStore(Paths)
                : new JsonResidentLaunchSessionStore(Paths, TimeProvider.System, preserver);
        }

        /// <summary>获取测试专用应用数据路径。</summary>
        public AppDataPaths Paths { get; }

        /// <summary>获取待测会话存储。</summary>
        public JsonResidentLaunchSessionStore Store { get; }

        /// <summary>删除测试夹具创建的临时根目录。</summary>
        /// <returns>释放完成的任务。</returns>
        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>模拟拒绝移动损坏证据的文件系统边界。</summary>
    private sealed class RejectingCorruptSessionPreserver : ICorruptResidentSessionPreserver
    {
        /// <summary>始终拒绝损坏会话证据的移动操作。</summary>
        /// <param name="sourcePath">待保留的故障会话路径。</param>
        /// <param name="destinationPath">目标证据路径。</param>
        public void Move(string sourcePath, string destinationPath)
        {
            throw new IOException("模拟证据移动失败。");
        }
    }
}
