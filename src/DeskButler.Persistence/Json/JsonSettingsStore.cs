using System.Text.Json;
using DeskButler.Core.Settings;
using DeskButler.Persistence.Paths;

namespace DeskButler.Persistence.Json;

/// <summary>使用原子替换 JSON 文件保存当前用户设置。</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly AppDataPaths paths;
    private readonly TimeProvider timeProvider;

    /// <summary>使用应用数据路径初始化 JSON 设置存储。</summary>
    /// <param name="paths">包含设置文件路径的应用数据路径。</param>
    public JsonSettingsStore(AppDataPaths paths)
        : this(paths, TimeProvider.System)
    {
    }

    /// <summary>使用应用数据路径和时间来源初始化 JSON 设置存储。</summary>
    /// <param name="paths">包含设置文件路径的应用数据路径。</param>
    /// <param name="timeProvider">用于命名损坏设置备份的时间来源。</param>
    public JsonSettingsStore(AppDataPaths paths, TimeProvider timeProvider)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async Task<ButlerSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.SettingsFilePath))
        {
            return ButlerSettings.Default;
        }

        try
        {
            await using var stream = new FileStream(paths.SettingsFilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var document = await JsonSerializer.DeserializeAsync<SettingsDocument>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException("设置 JSON 为空。");
            return document.ToSettings();
        }
        catch (JsonException)
        {
            PreserveCorruptSettingsFile();
            return ButlerSettings.Default;
        }
        catch (FileNotFoundException)
        {
            // 另一并发读取可能已移动损坏源文件；该调用同样安全降级到默认设置。
            return ButlerSettings.Default;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(ButlerSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        paths.EnsureRootDirectoryExists();

        var temporaryPath = $"{paths.SettingsFilePath}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, SettingsDocument.FromSettings(settings), SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(paths.SettingsFilePath))
            {
                File.Replace(temporaryPath, paths.SettingsFilePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, paths.SettingsFilePath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>将损坏设置文件移动为带时间戳和唯一后缀的副本，避免覆盖唯一故障现场。</summary>
    private void PreserveCorruptSettingsFile()
    {
        var timestamp = timeProvider.GetUtcNow().ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        var corruptPath = Path.Combine(paths.RootDirectory, $"settings.corrupt-{timestamp}-{Guid.NewGuid():N}.json");
        try
        {
            File.Move(paths.SettingsFilePath, corruptPath);
        }
        catch (FileNotFoundException)
        {
            // 并发调用已完成原始文件的保留；当前调用无需再移动或删除任何备份。
        }
    }

    /// <summary>定义 JSON 文件的稳定数据传输形状，并在读取时恢复集合比较器语义。</summary>
    private sealed class SettingsDocument
    {
        /// <summary>获取或设置是否启用场景捕获。</summary>
        public bool CaptureEnabled { get; set; } = ButlerSettings.Default.CaptureEnabled;

        /// <summary>获取或设置是否启用开机启动。</summary>
        public bool StartupEnabled { get; set; } = ButlerSettings.Default.StartupEnabled;

        /// <summary>获取或设置恢复卡片自动收起秒数。</summary>
        public int RecoveryCardDismissSeconds { get; set; } = ButlerSettings.Default.RecoveryCardDismissSeconds;

        /// <summary>获取或设置用户排除的可执行文件路径。</summary>
        public List<string> ExcludedExecutablePaths { get; set; } = [];

        /// <summary>从领域设置创建可序列化的 JSON 文档。</summary>
        /// <param name="settings">要序列化的领域设置。</param>
        /// <returns>可持久化的设置文档。</returns>
        public static SettingsDocument FromSettings(ButlerSettings settings)
        {
            return new SettingsDocument
            {
                CaptureEnabled = settings.CaptureEnabled,
                StartupEnabled = settings.StartupEnabled,
                RecoveryCardDismissSeconds = settings.RecoveryCardDismissSeconds,
                ExcludedExecutablePaths = settings.ExcludedExecutablePaths.ToList()
            };
        }

        /// <summary>从 JSON 文档恢复具有大小写不敏感排除集合的领域设置。</summary>
        /// <returns>恢复后的领域设置。</returns>
        public ButlerSettings ToSettings()
        {
            return new ButlerSettings(
                CaptureEnabled,
                StartupEnabled,
                RecoveryCardDismissSeconds,
                new HashSet<string>(ExcludedExecutablePaths ?? [], StringComparer.OrdinalIgnoreCase));
        }
    }
}
