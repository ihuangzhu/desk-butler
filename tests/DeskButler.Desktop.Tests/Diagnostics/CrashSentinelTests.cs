using System.Globalization;
using DeskButler.Desktop.Diagnostics;

namespace DeskButler.Desktop.Tests.Diagnostics;

public sealed class CrashSentinelTests
{
    /// <summary>残留标记必须进入安全模式，并原样持有而不得覆盖故障现场。</summary>
    [Fact]
    public void LeftoverMarkerReportsPreviousRunAsUncleanWithoutOverwritingIt()
    {
        using var directory = TemporaryDirectory.Create();
        var markerPath = Path.Combine(directory.Path, "run.lock");
        File.WriteAllText(markerPath, "previous-marker");

        var sentinel = new CrashSentinel(directory.Path);
        try
        {
            Assert.True(sentinel.IsPreviousRunUnclean);
            Assert.Equal("previous-marker", ReadHeldMarker(markerPath));
        }
        finally
        {
            sentinel.MarkCleanExit();
        }
    }

    /// <summary>首次干净启动必须以新文件方式创建当前运行标记。</summary>
    [Fact]
    public void FirstRunIsCleanAndCreatesMarker()
    {
        using var directory = TemporaryDirectory.Create();
        var markerPath = Path.Combine(directory.Path, "run.lock");

        var sentinel = new CrashSentinel(directory.Path);
        try
        {
            var marker = ReadHeldMarker(markerPath);
            Assert.False(sentinel.IsPreviousRunUnclean);
            Assert.True(File.Exists(markerPath));
            Assert.True(Guid.TryParse(ReadMetadata(marker, "token"), out _));
            Assert.DoesNotContain(Environment.CommandLine, marker, StringComparison.Ordinal);
        }
        finally
        {
            sentinel.MarkCleanExit();
        }
    }

    /// <summary>已观察到旧 marker 后即使它在 open 前消失，重试创建也必须保留 unclean 判定。</summary>
    [Fact]
    public void ExistingMarkerDisappearingBeforeOpenRetriesWithoutReportingClean()
    {
        using var directory = TemporaryDirectory.Create();
        var markerPath = Path.Combine(directory.Path, "run.lock");
        File.WriteAllText(markerPath, "previous-marker");
        var fileOperations = new DeleteBeforeFirstOpenOperations(
            NativeCrashMarkerFileOperations.Instance,
            markerPath);

        var sentinel = new CrashSentinel(directory.Path, fileOperations);
        try
        {
            var marker = ReadHeldMarker(markerPath);
            Assert.True(sentinel.IsPreviousRunUnclean);
            Assert.NotEqual("previous-marker", marker);
            Assert.True(Guid.TryParse(ReadMetadata(marker, "token"), out _));
        }
        finally
        {
            sentinel.MarkCleanExit();
        }
    }

    /// <summary>marker 持续在 exists/open 间消失时，构造必须有限结束并暴露 IOException。</summary>
    [Fact]
    public async Task RepeatedExistingMarkerDisappearanceFailsWithinFiniteAttempts()
    {
        using var directory = TemporaryDirectory.Create();
        var construction = Task.Run(
            () => Record.Exception(() => new CrashSentinel(directory.Path, new AlwaysDisappearingOperations())),
            TestContext.Current.CancellationToken);

        var failure = await construction.WaitAsync(TestContext.Current.CancellationToken);

        Assert.IsType<IOException>(failure);
    }

    /// <summary>正常退出只需精确删除运行标记，多次调用仍安全。</summary>
    [Fact]
    public void MarkCleanExitRemovesMarkerIdempotently()
    {
        using var directory = TemporaryDirectory.Create();
        var markerPath = Path.Combine(directory.Path, "run.lock");
        var sentinel = new CrashSentinel(directory.Path);

        sentinel.MarkCleanExit();
        sentinel.MarkCleanExit();

        Assert.False(File.Exists(markerPath));
    }

    /// <summary>无法创建数据目录时必须向宿主暴露异常。</summary>
    [Fact]
    public void ConstructorPropagatesDirectoryFailures()
    {
        using var directory = TemporaryDirectory.Create();
        var occupiedPath = Path.Combine(directory.Path, "occupied");
        File.WriteAllText(occupiedPath, "file");

        Assert.ThrowsAny<IOException>(() => new CrashSentinel(occupiedPath));
    }

    /// <summary>生命周期内持有的 marker 必须拒绝普通外部删除、写入和替换。</summary>
    [Fact]
    public void MarkerCannotBeDeletedWrittenOrReplacedWhileSentinelOwnsIt()
    {
        using var directory = TemporaryDirectory.Create();
        var markerPath = Path.Combine(directory.Path, "run.lock");
        var replacementPath = Path.Combine(directory.Path, "replacement.lock");
        var sentinel = new CrashSentinel(directory.Path);
        File.WriteAllText(replacementPath, "foreign-marker");

        try
        {
            AssertFileMutationDenied(() => File.Delete(markerPath));
            AssertFileMutationDenied(() => File.WriteAllText(markerPath, "foreign-marker"));
            AssertFileMutationDenied(() => File.Move(replacementPath, markerPath, overwrite: true));
            Assert.True(File.Exists(markerPath));
        }
        finally
        {
            sentinel.MarkCleanExit();
        }
    }

    /// <summary>正常清理只删除持有身份的 run.lock，不影响同目录 foreign 文件。</summary>
    [Fact]
    public void MarkCleanExitDeletesOnlyOwnedMarker()
    {
        using var directory = TemporaryDirectory.Create();
        var markerPath = Path.Combine(directory.Path, "run.lock");
        var foreignPath = Path.Combine(directory.Path, "foreign.lock");
        File.WriteAllText(foreignPath, "preserve-me");
        var sentinel = new CrashSentinel(directory.Path);

        sentinel.MarkCleanExit();

        Assert.False(File.Exists(markerPath));
        Assert.Equal("preserve-me", File.ReadAllText(foreignPath));
    }

    /// <summary>句柄所有权已释放时，clean 必须拒绝按路径删除后来替换的 foreign marker。</summary>
    [Fact]
    public void LostOwnershipRefusesToDeleteReplacementMarker()
    {
        using var directory = TemporaryDirectory.Create();
        var markerPath = Path.Combine(directory.Path, "run.lock");
        var sentinel = new CrashSentinel(directory.Path);
        sentinel.Dispose();
        File.Delete(markerPath);
        File.WriteAllText(markerPath, "foreign-marker");

        Assert.Throws<ObjectDisposedException>(sentinel.MarkCleanExit);
        Assert.Equal("foreign-marker", File.ReadAllText(markerPath));
    }

    private static string ReadMetadata(string marker, string key)
    {
        var prefix = $"{key}=";
        return Assert.Single(
                marker.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
                line => line.StartsWith(prefix, StringComparison.Ordinal))
            [prefix.Length..];
    }

    private static void AssertFileMutationDenied(Action mutation)
    {
        var failure = Record.Exception(mutation);
        Assert.NotNull(failure);
        Assert.True(
            failure is IOException or UnauthorizedAccessException,
            $"Expected a Windows file-sharing failure, but got {failure.GetType().FullName}.");
    }

    private static string ReadHeldMarker(string markerPath)
    {
        using var stream = new FileStream(
            markerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class DeleteBeforeFirstOpenOperations : ICrashMarkerFileOperations
    {
        private readonly ICrashMarkerFileOperations inner;
        private readonly string markerPath;
        private int deletePending = 1;

        internal DeleteBeforeFirstOpenOperations(ICrashMarkerFileOperations inner, string markerPath)
        {
            this.inner = inner;
            this.markerPath = markerPath;
        }

        public bool TryCreateNew(string path, out ICrashMarkerHandle? marker) =>
            inner.TryCreateNew(path, out marker);

        public bool TryOpenExisting(string path, out ICrashMarkerHandle? marker)
        {
            if (Interlocked.Exchange(ref deletePending, 0) == 1)
            {
                File.Delete(markerPath);
            }

            return inner.TryOpenExisting(path, out marker);
        }
    }

    private sealed class AlwaysDisappearingOperations : ICrashMarkerFileOperations
    {
        private int createAttempts;

        public bool TryCreateNew(string path, out ICrashMarkerHandle? marker)
        {
            if (Interlocked.Increment(ref createAttempts) > 32)
            {
                throw new InvalidOperationException("测试 watchdog：取得 marker 的重试没有有界结束。");
            }

            marker = null;
            return false;
        }

        public bool TryOpenExisting(string path, out ICrashMarkerHandle? marker)
        {
            marker = null;
            return false;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DeskButler.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
