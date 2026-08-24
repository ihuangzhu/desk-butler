using DeskButler.Core.Diagnostics;
using DeskButler.Persistence.Diagnostics;

namespace DeskButler.Persistence.Tests.Diagnostics;

public sealed class RollingJsonLogTests
{
    /// <summary>五条约一兆记录不得让日志总量超过三兆加当前活动记录余量。</summary>
    [Fact]
    public async Task FiveLargeWritesStayWithinTotalCapPlusOneRecordMargin()
    {
        using var fixture = new TempDirectory();
        await using var log = new RollingJsonLog(fixture.Path, 1024 * 1024, 3 * 1024 * 1024);
        var payload = new string('x', 1024 * 1024);

        for (var index = 0; index < 5; index++)
        {
            await log.WriteAsync(new DiagnosticEvent(
                DateTimeOffset.UnixEpoch.AddSeconds(index), DiagnosticLevel.Information,
                "test", "large", new Dictionary<string, object?> { ["payload"] = payload }),
                TestContext.Current.CancellationToken);
        }

        await log.FlushAsync(TestContext.Current.CancellationToken);
        var bytes = Directory.EnumerateFiles(fixture.Path, "deskbutler*.jsonl")
            .Sum(path => new FileInfo(path).Length);
        Assert.InRange(bytes, 1, (4L * 1024 * 1024) + 4096);
    }

    /// <summary>大小写不同且深层嵌套的敏感字段必须整条拒绝且不得落盘。</summary>
    [Theory]
    [InlineData("commandLine")]
    [InlineData("TOKEN")]
    [InlineData("Password")]
    [InlineData("clipboard")]
    public async Task SensitiveFieldIsRejectedWithoutWriting(string field)
    {
        using var fixture = new TempDirectory();
        await using var log = new RollingJsonLog(fixture.Path, 1024, 3072);
        var value = new Dictionary<string, object?>
        {
            ["safe"] = new Dictionary<string, object?> { [field] = "secret" }
        };

        await Assert.ThrowsAsync<ArgumentException>(() => log.WriteAsync(
            new DiagnosticEvent(DateTimeOffset.UnixEpoch, DiagnosticLevel.Error, "test", "bad", value),
            TestContext.Current.CancellationToken));

        Assert.Equal(0, File.Exists(Path.Combine(fixture.Path, "deskbutler.jsonl"))
            ? new FileInfo(Path.Combine(fixture.Path, "deskbutler.jsonl")).Length
            : 0);
    }

    /// <summary>同一目录只允许一个独占写者，释放后新写者才能接管并追加。</summary>
    [Fact]
    public async Task WriterLockIsExclusiveAndReleasedByDispose()
    {
        using var fixture = new TempDirectory();
        await using (var first = new RollingJsonLog(fixture.Path, 1024, 3072))
        {
            Assert.Throws<IOException>(() => new RollingJsonLog(fixture.Path, 1024, 3072));
        }

        await using var second = new RollingJsonLog(fixture.Path, 1024, 3072);
        await second.WriteAsync(new DiagnosticEvent(
            DateTimeOffset.UnixEpoch, DiagnosticLevel.Information, "test", "ok"),
            TestContext.Current.CancellationToken);
    }

    /// <summary>同一写者的并发调用必须形成完整 JSONL 记录，不能交错或丢失。</summary>
    [Fact]
    public async Task ConcurrentWritesProduceCompleteJsonLines()
    {
        using var fixture = new TempDirectory();
        await using (var log = new RollingJsonLog(fixture.Path, 64 * 1024, 192 * 1024))
        {
            await Task.WhenAll(Enumerable.Range(0, 20).Select(index => log.WriteAsync(
                new DiagnosticEvent(DateTimeOffset.UnixEpoch, DiagnosticLevel.Information, "test", $"item-{index}"),
                TestContext.Current.CancellationToken)));
        }

        var lines = await File.ReadAllLinesAsync(
            Path.Combine(fixture.Path, "deskbutler.jsonl"), TestContext.Current.CancellationToken);
        Assert.Equal(20, lines.Length);
        Assert.All(lines, line => Assert.Equal(System.Text.Json.JsonValueKind.Object,
            System.Text.Json.JsonDocument.Parse(line).RootElement.ValueKind));
    }

    /// <summary>轮换文件系统操作失败后写者仍应可恢复使用，不能留下已释放活动流。</summary>
    [Fact]
    public async Task RotationFailureLeavesWriterReusable()
    {
        using var fixture = new TempDirectory();
        await using var log = new RollingJsonLog(fixture.Path, 256, 768);
        await log.WriteAsync(new DiagnosticEvent(
            DateTimeOffset.UnixEpoch, DiagnosticLevel.Information, "test", "first",
            new Dictionary<string, object?> { ["payload"] = new string('x', 220) }),
            TestContext.Current.CancellationToken);
        var blockingDirectory = Path.Combine(fixture.Path, "deskbutler.2.jsonl");
        Directory.CreateDirectory(blockingDirectory);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => log.WriteAsync(new DiagnosticEvent(
            DateTimeOffset.UnixEpoch, DiagnosticLevel.Information, "test", "blocked"),
            TestContext.Current.CancellationToken));
        Directory.Delete(blockingDirectory);

        await log.WriteAsync(new DiagnosticEvent(
            DateTimeOffset.UnixEpoch, DiagnosticLevel.Information, "test", "recovered"),
            TestContext.Current.CancellationToken);
    }

    /// <summary>重启前遗留半条必须截断，新记录和完整旧行都能独立解析。</summary>
    [Fact]
    public async Task ReopenTruncatesIncompleteTrailingRecordBeforeAppend()
    {
        using var fixture = new TempDirectory();
        var path = Path.Combine(fixture.Path, "deskbutler.jsonl");
        await File.WriteAllTextAsync(path, "{\"message\":\"kept\"}\n{\"partial\":",
            TestContext.Current.CancellationToken);

        await using (var log = new RollingJsonLog(fixture.Path, 4096, 12288))
        {
            await log.WriteAsync(new DiagnosticEvent(
                DateTimeOffset.UnixEpoch, DiagnosticLevel.Information, "test", "new"),
                TestContext.Current.CancellationToken);
        }

        var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.Equal(System.Text.Json.JsonValueKind.Object,
            System.Text.Json.JsonDocument.Parse(line).RootElement.ValueKind));
        Assert.Contains(lines, line => line.Contains("kept", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("new", StringComparison.Ordinal));
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
        public void Dispose() => Directory.Delete(Path, true);
    }
}
