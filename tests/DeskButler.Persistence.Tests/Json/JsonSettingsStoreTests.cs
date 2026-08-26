using DeskButler.Core.ResidentApps;
using DeskButler.Core.Settings;
using DeskButler.Persistence.Json;
using DeskButler.Persistence.Paths;

namespace DeskButler.Persistence.Tests.Json;

public sealed class JsonSettingsStoreTests
{
    /// <summary>验证不存在设置文件时会返回领域层定义的默认设置。</summary>
    [Fact]
    public async Task LoadAsyncReturnsDefaultSettingsWhenFileIsMissing()
    {
        await using var fixture = new SettingsFixture();

        var settings = await fixture.Store.LoadAsync(CancellationToken.None);

        Assert.Equal(ButlerSettings.Default, settings);
        Assert.Empty(settings.ExcludedExecutablePaths);
    }

    /// <summary>验证保存后从真实 JSON 文件加载能完整还原设置，并保留大小写不敏感的排除集合语义。</summary>
    [Fact]
    public async Task SaveAsyncRoundTripsSettingsWithCaseInsensitiveExcludedPaths()
    {
        await using var fixture = new SettingsFixture();
        var expected = ButlerSettings.CreateLegacy(
            false,
            false,
            45,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                @"C:\\Tools\\Ignored.exe",
                @"D:\\Apps\\Other.exe"
            });

        await fixture.Store.SaveAsync(expected, CancellationToken.None);
        var restored = await fixture.Store.LoadAsync(CancellationToken.None);

        Assert.False(restored.CaptureEnabled);
        Assert.False(restored.StartupEnabled);
        Assert.Equal(45, restored.RecoveryCardDismissSeconds);
        Assert.True(restored.ExcludedExecutablePaths.Contains(@"c:\\tools\\ignored.exe"));
        Assert.True(restored.ExcludedExecutablePaths.Contains(@"d:\\apps\\OTHER.exe"));
    }

    /// <summary>验证损坏设置文件会被保留为带时间戳的副本，并以默认设置继续运行。</summary>
    [Fact]
    public async Task LoadAsyncPreservesCorruptFileAndReturnsDefaults()
    {
        await using var fixture = new SettingsFixture();
        Directory.CreateDirectory(fixture.Paths.RootDirectory);
        await File.WriteAllTextAsync(fixture.Paths.SettingsFilePath, "{ invalid json", CancellationToken.None);

        var settings = await fixture.Store.LoadAsync(CancellationToken.None);

        Assert.Equal(ButlerSettings.Default, settings);
        Assert.False(File.Exists(fixture.Paths.SettingsFilePath));
        var backup = Assert.Single(Directory.EnumerateFiles(fixture.Paths.RootDirectory, "settings.corrupt-*.json"));
        Assert.Equal("{ invalid json", await File.ReadAllTextAsync(backup, CancellationToken.None));
    }

    /// <summary>验证并发读取同一损坏设置文件时，每个调用都返回默认设置且原始文件只会保留为备份。</summary>
    [Fact]
    public async Task LoadAsyncReturnsDefaultsForConcurrentReadersOfCorruptFile()
    {
        await using var fixture = new SettingsFixture();
        Directory.CreateDirectory(fixture.Paths.RootDirectory);
        await File.WriteAllTextAsync(fixture.Paths.SettingsFilePath, $"{new string(' ', 1_048_576)}{{", CancellationToken.None);

        var settings = await Task.WhenAll(Enumerable.Range(0, 4).Select(
            _ => Task.Run(() => fixture.Store.LoadAsync(CancellationToken.None))));

        Assert.All(settings, value => Assert.Equal(ButlerSettings.Default, value));
        Assert.False(File.Exists(fixture.Paths.SettingsFilePath));
        Assert.Single(Directory.EnumerateFiles(fixture.Paths.RootDirectory, "settings.corrupt-*.json"));
    }

    /// <summary>验证目标备份名已存在时，损坏设置仍会生成独立备份并返回默认设置而不覆盖旧备份。</summary>
    [Fact]
    public async Task LoadAsyncPreservesBothFilesWhenTimestampedCorruptBackupAlreadyExists()
    {
        await using var fixture = new SettingsFixture();
        var timestamp = new DateTimeOffset(2026, 8, 24, 12, 0, 0, 123, TimeSpan.Zero);
        var existingBackup = Path.Combine(fixture.Paths.RootDirectory, "settings.corrupt-20260824120000123.json");
        Directory.CreateDirectory(fixture.Paths.RootDirectory);
        await File.WriteAllTextAsync(existingBackup, "已存在的损坏备份", CancellationToken.None);
        await File.WriteAllTextAsync(fixture.Paths.SettingsFilePath, "{ invalid json", CancellationToken.None);
        var store = new JsonSettingsStore(fixture.Paths, new FixedTimeProvider(timestamp));

        var settings = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(ButlerSettings.Default, settings);
        var backups = Directory.EnumerateFiles(fixture.Paths.RootDirectory, "settings.corrupt-*.json").ToArray();
        Assert.Equal(2, backups.Length);
        Assert.Contains(existingBackup, backups, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(backups, path => File.ReadAllText(path) == "{ invalid json");
        Assert.Equal("已存在的损坏备份", await File.ReadAllTextAsync(existingBackup, CancellationToken.None));
    }

    /// <summary>验证有效 JSON 中的空排除列表会归一为可用的空大小写不敏感集合。</summary>
    [Fact]
    public async Task LoadAsyncNormalizesNullExcludedPathsToEmptyCaseInsensitiveSet()
    {
        await using var fixture = new SettingsFixture();
        Directory.CreateDirectory(fixture.Paths.RootDirectory);
        await File.WriteAllTextAsync(
            fixture.Paths.SettingsFilePath,
            """{"captureEnabled":false,"startupEnabled":true,"recoveryCardDismissSeconds":22,"excludedExecutablePaths":null}""",
            CancellationToken.None);

        var settings = await fixture.Store.LoadAsync(CancellationToken.None);

        Assert.False(settings.CaptureEnabled);
        Assert.True(settings.StartupEnabled);
        Assert.Equal(22, settings.RecoveryCardDismissSeconds);
        Assert.Empty(settings.ExcludedExecutablePaths);
        Assert.True(settings.ExcludedExecutablePaths.IsProperSubsetOf(["C:\\Apps\\Other.exe"]));
    }

    /// <summary>验证缺少常驻应用字段的旧版设置会启用兼容默认值而不回退其他设置。</summary>
    [Fact]
    public async Task LoadAsyncUsesResidentCompatibilityDefaultsForLegacyJson()
    {
        await using var fixture = new SettingsFixture();
        Directory.CreateDirectory(fixture.Paths.RootDirectory);
        await File.WriteAllTextAsync(
            fixture.Paths.SettingsFilePath,
            """{"captureEnabled":false,"startupEnabled":false,"recoveryCardDismissSeconds":22,"excludedExecutablePaths":["C:\\Apps\\Excluded.exe"]}""",
            CancellationToken.None);

        var loaded = await fixture.Store.LoadAsync(CancellationToken.None);

        Assert.True(loaded.ResidentApplicationsEnabled);
        Assert.Empty(loaded.ResidentApplications);
        Assert.False(loaded.CaptureEnabled);
        Assert.False(loaded.StartupEnabled);
        Assert.Equal(22, loaded.RecoveryCardDismissSeconds);
        Assert.True(loaded.ExcludedExecutablePaths.Contains(@"C:\Apps\Excluded.exe"));
    }

    /// <summary>验证单个畸形常驻条目被隔离，且同一设置文档中的有效条目和旧字段仍可恢复。</summary>
    [Fact]
    public async Task LoadAsyncIsolatesMalformedResidentApplicationWithoutDiscardingSettings()
    {
        await using var fixture = new SettingsFixture();
        Directory.CreateDirectory(fixture.Paths.RootDirectory);
        await File.WriteAllTextAsync(
            fixture.Paths.SettingsFilePath,
            """
            {"captureEnabled":false,"startupEnabled":false,"recoveryCardDismissSeconds":33,"excludedExecutablePaths":["C:\\Apps\\Excluded.exe"],"residentApplicationsEnabled":false,"residentApplications":[{"launchPath":"C:\\Apps\\One.exe"},{"launchPath":"C:\\Apps\\Two.exe","knownProcessPaths":["C:\\Apps\\Two.exe"],"displayName":"Two","enabled":true,"launchOrder":4},{"launchPath":"D:\\Apps\\Three.exe","knownProcessPaths":["D:\\Apps\\Three.exe"],"displayName":"Three","enabled":false,"launchOrder":8}]}
            """,
            CancellationToken.None);

        var loaded = await fixture.Store.LoadAsync(CancellationToken.None);

        Assert.False(loaded.CaptureEnabled);
        Assert.False(loaded.StartupEnabled);
        Assert.Equal(33, loaded.RecoveryCardDismissSeconds);
        Assert.True(loaded.ExcludedExecutablePaths.Contains(@"C:\Apps\Excluded.exe"));
        Assert.False(loaded.ResidentApplicationsEnabled);
        Assert.Collection(
            loaded.ResidentApplications,
            first => Assert.Equal(@"C:\Apps\Two.exe", first.LaunchPath),
            second => Assert.Equal(@"D:\Apps\Three.exe", second.LaunchPath));
    }

    /// <summary>验证常驻应用完整往返会保留启动、识别、显示、启用和排序字段。</summary>
    [Fact]
    public async Task SaveAsyncRoundTripsEveryResidentApplicationField()
    {
        await using var fixture = new SettingsFixture();
        var expected = ButlerSettings.Default with
        {
            ResidentApplicationsEnabled = false,
            ResidentApplications =
            [
                new ResidentApplication(
                    @"C:\Apps\DeskOne.exe",
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Apps\DeskOne.exe", @"C:\Apps\DeskOne.Helper.exe" },
                    "Desk One",
                    false,
                    7)
            ]
        };

        await fixture.Store.SaveAsync(expected, CancellationToken.None);
        var loaded = await fixture.Store.LoadAsync(CancellationToken.None);

        Assert.False(loaded.ResidentApplicationsEnabled);
        var application = Assert.Single(loaded.ResidentApplications);
        Assert.Equal(@"C:\Apps\DeskOne.exe", application.LaunchPath);
        Assert.True(application.KnownProcessPaths.SetEquals([@"C:\Apps\DeskOne.exe", @"C:\Apps\DeskOne.Helper.exe"]));
        Assert.Equal("Desk One", application.DisplayName);
        Assert.False(application.Enabled);
        Assert.Equal(0, application.LaunchOrder);
    }

    /// <summary>验证同一 JSON 内容中的常驻条目诊断只会交付一次，而无时间戳依赖。</summary>
    [Fact]
    public async Task LoadAsyncDeduplicatesResidentDiagnosticsForIdenticalJsonBytes()
    {
        await using var fixture = new SettingsFixture();
        var diagnostics = new List<ResidentNormalizationDiagnostic>();
        var store = new JsonSettingsStore(fixture.Paths, diagnosticSink: diagnostics.Add);
        Directory.CreateDirectory(fixture.Paths.RootDirectory);
        await File.WriteAllTextAsync(
            fixture.Paths.SettingsFilePath,
            """{"residentApplications":[{"launchPath":null}]}""",
            CancellationToken.None);

        await store.LoadAsync(CancellationToken.None);
        await store.LoadAsync(CancellationToken.None);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(ResidentNormalizationIssue.InvalidPath, diagnostic.Kind);
    }

    /// <summary>验证目标设置文件已存在时，原子替换分支会保存最新设置并保持文件可读取。</summary>
    [Fact]
    public async Task SaveAsyncReplacesExistingSettingsFileWithReadableLatestSettings()
    {
        await using var fixture = new SettingsFixture();
        var first = ButlerSettings.Default with { RecoveryCardDismissSeconds = 20 };
        var latest = ButlerSettings.Default with
        {
            CaptureEnabled = false,
            RecoveryCardDismissSeconds = 50,
            ExcludedExecutablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\\Tools\\Latest.exe" }
        };
        await fixture.Store.SaveAsync(first, CancellationToken.None);

        await fixture.Store.SaveAsync(latest, CancellationToken.None);
        var restored = await fixture.Store.LoadAsync(CancellationToken.None);

        Assert.False(restored.CaptureEnabled);
        Assert.Equal(50, restored.RecoveryCardDismissSeconds);
        Assert.True(restored.ExcludedExecutablePaths.Contains(@"c:\\tools\\latest.exe"));
    }

    /// <summary>保存真实 JSON 设置文件的临时夹具，并在释放时删除临时根目录。</summary>
    private sealed class SettingsFixture : IAsyncDisposable
    {
        private readonly string rootDirectory = Path.Combine(Path.GetTempPath(), $"DeskButler.Settings.Tests.{Guid.NewGuid():N}");

        /// <summary>使用隔离临时目录初始化真实 JSON 设置存储。</summary>
        public SettingsFixture()
        {
            Paths = new AppDataPaths(rootDirectory);
            Store = new JsonSettingsStore(Paths);
        }

        /// <summary>获取测试专用的应用数据路径。</summary>
        public AppDataPaths Paths { get; }

        /// <summary>获取待测真实 JSON 设置存储。</summary>
        public JsonSettingsStore Store { get; }

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

    /// <summary>提供固定 UTC 时刻，使冲突备份文件名测试可重复执行。</summary>
    /// <param name="utcNow">要返回的固定 UTC 时刻。</param>
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        /// <summary>返回构造时指定的固定 UTC 时刻。</summary>
        /// <returns>固定的 UTC 时刻。</returns>
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }
}
