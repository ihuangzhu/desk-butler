using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using DeskButler.Core.ResidentApps;
using DeskButler.Core.Settings;
using DeskButler.Persistence.Paths;

namespace DeskButler.Persistence.Json;

/// <summary>使用原子替换 JSON 文件保存当前用户设置。</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly ConcurrentDictionary<string, byte> ReportedResidentDiagnosticFingerprints = new();

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly AppDataPaths paths;
    private readonly TimeProvider timeProvider;
    private readonly Action<ResidentNormalizationDiagnostic>? diagnosticSink;

    /// <summary>使用应用数据路径初始化 JSON 设置存储。</summary>
    /// <param name="paths">包含设置文件路径的应用数据路径。</param>
    public JsonSettingsStore(AppDataPaths paths)
        : this(paths, TimeProvider.System)
    {
    }

    /// <summary>使用应用数据路径和常驻条目诊断接收器初始化 JSON 设置存储。</summary>
    /// <param name="paths">包含设置文件路径的应用数据路径。</param>
    /// <param name="diagnosticSink">接收不含敏感原文的单项隔离或冲突诊断。</param>
    public JsonSettingsStore(AppDataPaths paths, Action<ResidentNormalizationDiagnostic>? diagnosticSink)
        : this(paths, TimeProvider.System, diagnosticSink)
    {
    }

    /// <summary>使用应用数据路径和时间来源初始化 JSON 设置存储。</summary>
    /// <param name="paths">包含设置文件路径的应用数据路径。</param>
    /// <param name="timeProvider">用于命名损坏设置备份的时间来源。</param>
    public JsonSettingsStore(AppDataPaths paths, TimeProvider timeProvider)
        : this(paths, timeProvider, null)
    {
    }

    /// <summary>使用应用数据路径、时间来源和可选诊断接收器初始化 JSON 设置存储。</summary>
    /// <param name="paths">包含设置文件路径的应用数据路径。</param>
    /// <param name="timeProvider">用于命名损坏设置备份的时间来源。</param>
    /// <param name="diagnosticSink">接收不含敏感原文的常驻条目诊断。</param>
    public JsonSettingsStore(
        AppDataPaths paths,
        TimeProvider timeProvider,
        Action<ResidentNormalizationDiagnostic>? diagnosticSink)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.diagnosticSink = diagnosticSink;
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
            byte[] documentBytes;
            await using (var stream = new FileStream(
                             paths.SettingsFilePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read | FileShare.Delete))
            await using (var buffer = new MemoryStream())
            {
                await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                documentBytes = buffer.ToArray();
            }
            var document = JsonSerializer.Deserialize<SettingsDocument>(documentBytes, SerializerOptions)
                ?? throw new JsonException("设置 JSON 为空。");
            var restored = document.ToSettings();
            PublishResidentDiagnostics(documentBytes, restored.Diagnostics);
            return restored.Settings;
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

    /// <summary>按 JSON 原始字节和诊断种类去重后交付常驻条目诊断。</summary>
    /// <remarks>诊断接收器属于旁路观测；其故障绝不能影响设置加载或泄露原始异常文本。</remarks>
    private void PublishResidentDiagnostics(
        ReadOnlySpan<byte> documentBytes,
        IReadOnlyList<ResidentNormalizationDiagnostic> diagnostics)
    {
        if (diagnosticSink is null || diagnostics.Count == 0)
        {
            return;
        }

        var kinds = diagnostics
            .Select(item => item.Kind)
            .Distinct()
            .OrderBy(kind => kind)
            .ToArray();
        var fingerprint = $"{Convert.ToHexString(SHA256.HashData(documentBytes))}:{string.Join(',', kinds)}";
        if (!ReportedResidentDiagnosticFingerprints.TryAdd(fingerprint, 0))
        {
            return;
        }

        foreach (var diagnostic in diagnostics)
        {
            try
            {
                diagnosticSink(diagnostic);
            }
            catch
            {
                // 诊断是非关键旁路；接收器异常不改变已恢复的设置结果。
            }
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

        /// <summary>获取或设置是否允许登录后启动常驻应用。</summary>
        public bool ResidentApplicationsEnabled { get; set; } = true;

        /// <summary>获取或设置逐项隔离读取的常驻应用 JSON 片段。</summary>
        public List<JsonElement> ResidentApplications { get; set; } = [];

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
                ExcludedExecutablePaths = settings.ExcludedExecutablePaths.ToList(),
                ResidentApplicationsEnabled = settings.ResidentApplicationsEnabled,
                ResidentApplications = settings.ResidentApplications
                    .Select(ResidentApplicationDocument.FromApplication)
                    .Select(application => JsonSerializer.SerializeToElement(application, SerializerOptions))
                    .ToList()
            };
        }

        /// <summary>从 JSON 文档恢复具有大小写不敏感排除集合的领域设置。</summary>
        /// <returns>恢复后的领域设置。</returns>
        public SettingsLoadResult ToSettings()
        {
            var source = new List<ResidentApplication>();
            foreach (var element in ResidentApplications ?? [])
            {
                try
                {
                    var document = element.Deserialize<ResidentApplicationDocument>(SerializerOptions);
                    source.Add(document is not null && document.IsComplete
                        ? document.ToApplication()
                        : ResidentApplicationDocument.InvalidPlaceholder);
                }
                catch (JsonException)
                {
                    source.Add(ResidentApplicationDocument.InvalidPlaceholder);
                }
                catch (NotSupportedException)
                {
                    source.Add(ResidentApplicationDocument.InvalidPlaceholder);
                }
            }

            var normalized = ResidentApplicationNormalizer.Normalize(source);
            var settings = new ButlerSettings(
                CaptureEnabled,
                StartupEnabled,
                RecoveryCardDismissSeconds,
                new HashSet<string>(ExcludedExecutablePaths ?? [], StringComparer.OrdinalIgnoreCase),
                ResidentApplicationsEnabled,
                normalized.Applications);
            return new SettingsLoadResult(settings, normalized.Diagnostics);
        }
    }

    /// <summary>保存一次设置解析得到的领域设置和不含敏感原文的常驻条目诊断。</summary>
    private sealed record SettingsLoadResult(
        ButlerSettings Settings,
        IReadOnlyList<ResidentNormalizationDiagnostic> Diagnostics);

    /// <summary>定义常驻应用的稳定 JSON 传输形状，并显式识别缺失的必填字段。</summary>
    private sealed class ResidentApplicationDocument
    {
        /// <summary>获取或设置应用启动路径。</summary>
        public string? LaunchPath { get; set; }

        /// <summary>获取或设置产品已知进程路径。</summary>
        public List<string>? KnownProcessPaths { get; set; }

        /// <summary>获取或设置面向用户的显示名称。</summary>
        public string? DisplayName { get; set; }

        /// <summary>获取或设置是否启用登录启动。</summary>
        public bool? Enabled { get; set; }

        /// <summary>获取或设置稳定排序输入。</summary>
        public int? LaunchOrder { get; set; }

        /// <summary>获取条目是否包含全部 JSON 必填字段。</summary>
        public bool IsComplete =>
            LaunchPath is not null && KnownProcessPaths is not null && DisplayName is not null &&
            Enabled.HasValue && LaunchOrder.HasValue;

        /// <summary>获取供正规化器隔离无效 JSON 条目的最小占位条目。</summary>
        public static ResidentApplication InvalidPlaceholder { get; } =
            new(string.Empty, new HashSet<string>(StringComparer.OrdinalIgnoreCase), string.Empty, false, 0);

        /// <summary>从领域常驻应用创建稳定 JSON 数据传输对象。</summary>
        /// <param name="application">要持久化的常驻应用。</param>
        /// <returns>可序列化的稳定 JSON 对象。</returns>
        public static ResidentApplicationDocument FromApplication(ResidentApplication application)
        {
            return new ResidentApplicationDocument
            {
                LaunchPath = application.LaunchPath,
                KnownProcessPaths = application.KnownProcessPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList(),
                DisplayName = application.DisplayName,
                Enabled = application.Enabled,
                LaunchOrder = application.LaunchOrder
            };
        }

        /// <summary>从已验证完整的 DTO 创建领域常驻应用。</summary>
        /// <returns>等待正规化器统一处理的领域条目。</returns>
        public ResidentApplication ToApplication()
        {
            return new ResidentApplication(
                LaunchPath!,
                new HashSet<string>(KnownProcessPaths!, StringComparer.OrdinalIgnoreCase),
                DisplayName!,
                Enabled!.Value,
                LaunchOrder!.Value);
        }
    }
}
