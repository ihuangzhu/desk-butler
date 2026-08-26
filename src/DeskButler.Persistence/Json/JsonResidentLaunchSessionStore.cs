using System.Text.Json;
using DeskButler.Core.ResidentApps;
using DeskButler.Persistence.Paths;

namespace DeskButler.Persistence.Json;

/// <summary>以原子 JSON 文件持久化单个登录会话的固定常驻应用启动计划。</summary>
public sealed class JsonResidentLaunchSessionStore : IResidentLaunchSessionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly AppDataPaths paths;
    private readonly TimeProvider timeProvider;
    private readonly ICorruptResidentSessionPreserver corruptSessionPreserver;

    /// <summary>使用应用数据路径初始化登录批次会话存储。</summary>
    /// <param name="paths">包含会话文件路径的应用数据路径。</param>
    public JsonResidentLaunchSessionStore(AppDataPaths paths)
        : this(paths, TimeProvider.System, new NativeCorruptResidentSessionPreserver())
    {
    }

    /// <summary>使用可注入的损坏证据保留边界初始化会话存储。</summary>
    /// <param name="paths">包含会话文件路径的应用数据路径。</param>
    /// <param name="timeProvider">用于生成损坏证据文件名的时间来源。</param>
    /// <param name="corruptSessionPreserver">仅负责移动损坏会话证据的文件操作边界。</param>
    internal JsonResidentLaunchSessionStore(
        AppDataPaths paths,
        TimeProvider timeProvider,
        ICorruptResidentSessionPreserver corruptSessionPreserver)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.corruptSessionPreserver = corruptSessionPreserver ?? throw new ArgumentNullException(nameof(corruptSessionPreserver));
    }

    /// <inheritdoc />
    public async Task<ResidentLaunchSession?> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var bytes = await File.ReadAllBytesAsync(paths.ResidentLaunchSessionFilePath, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<ResidentLaunchSessionDocument>(bytes, SerializerOptions)
                ?? throw new JsonException();
            return document.ToSession();
        }
        catch (JsonException)
        {
            // 损坏内容不能伪装成空计划；协调器必须改走显式的证据保全恢复流程。
            throw new InvalidDataException("常驻应用启动会话格式无效。");
        }
        catch (NotSupportedException)
        {
            throw new InvalidDataException("常驻应用启动会话格式无效。");
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(ResidentLaunchSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateSession(session);
        paths.EnsureRootDirectoryExists();

        var temporaryPath = paths.ResidentLaunchSessionFilePath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    ResidentLaunchSessionDocument.FromSession(session),
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(paths.ResidentLaunchSessionFilePath))
            {
                File.Replace(temporaryPath, paths.ResidentLaunchSessionFilePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, paths.ResidentLaunchSessionFilePath);
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

    /// <inheritdoc />
    public async Task<ResidentLaunchRecoveryResult> RecoverCorruptAsync(
        string currentLogonSessionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentLogonSessionId);
        cancellationToken.ThrowIfCancellationRequested();
        paths.EnsureRootDirectoryExists();

        if (File.Exists(paths.ResidentLaunchSessionFilePath))
        {
            var evidencePath = CreateCorruptEvidencePath();
            try
            {
                corruptSessionPreserver.Move(paths.ResidentLaunchSessionFilePath, evidencePath);
            }
            catch
            {
                // 证据保全失败时不可删除、替换或写入会话文件，避免任何原始故障字节被掩盖。
                return ResidentLaunchRecoveryResult.PreservationFailedFailClosed;
            }
        }

        await SaveAsync(
            new ResidentLaunchSession(1, currentLogonSessionId, true, []),
            cancellationToken).ConfigureAwait(false);
        return ResidentLaunchRecoveryResult.RecoveredWithEmptyPlan;
    }

    /// <summary>创建带 UTC 时间戳和唯一后缀的损坏会话证据路径。</summary>
    /// <returns>不会覆盖既有证据的目标文件完整路径。</returns>
    private string CreateCorruptEvidencePath()
    {
        var timestamp = timeProvider.GetUtcNow().ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        return Path.Combine(
            paths.RootDirectory,
            $"resident-launch-session.corrupt-{timestamp}-{Guid.NewGuid():N}.json");
    }

    /// <summary>验证仅支持格式版本 1 的完整固定计划。</summary>
    /// <param name="session">待验证的登录批次会话。</param>
    private static void ValidateSession(ResidentLaunchSession session)
    {
        if (session.FormatVersion != 1 || string.IsNullOrWhiteSpace(session.LogonSessionId) || session.Plan is null ||
            session.Plan.Any(item => item is null || string.IsNullOrWhiteSpace(item.LaunchIdentity)))
        {
            throw new ArgumentException("常驻应用启动会话格式无效。", nameof(session));
        }
    }

    /// <summary>定义格式版本 1 的稳定登录会话 JSON 形状。</summary>
    private sealed class ResidentLaunchSessionDocument
    {
        /// <summary>获取或设置固定格式版本。</summary>
        public int? FormatVersion { get; set; }

        /// <summary>获取或设置登录会话稳定身份。</summary>
        public string? LogonSessionId { get; set; }

        /// <summary>获取或设置计划是否完成。</summary>
        public bool? Completed { get; set; }

        /// <summary>获取或设置固定启动计划。</summary>
        public List<ResidentLaunchPlanItemDocument>? Plan { get; set; }

        /// <summary>从领域会话创建可序列化文档。</summary>
        /// <param name="session">已验证的领域登录批次会话。</param>
        /// <returns>稳定的 JSON 会话文档。</returns>
        public static ResidentLaunchSessionDocument FromSession(ResidentLaunchSession session)
        {
            return new ResidentLaunchSessionDocument
            {
                FormatVersion = session.FormatVersion,
                LogonSessionId = session.LogonSessionId,
                Completed = session.Completed,
                Plan = session.Plan.Select(ResidentLaunchPlanItemDocument.FromPlanItem).ToList()
            };
        }

        /// <summary>验证并转换 JSON 文档为领域登录批次会话。</summary>
        /// <returns>已验证的领域会话。</returns>
        /// <exception cref="JsonException">JSON 文档未满足固定格式版本 1 的完整形状。</exception>
        public ResidentLaunchSession ToSession()
        {
            if (FormatVersion != 1 || string.IsNullOrWhiteSpace(LogonSessionId) || !Completed.HasValue || Plan is null)
            {
                throw new JsonException();
            }

            var plan = Plan.Select(item => item?.ToPlanItem() ?? throw new JsonException()).ToArray();
            return new ResidentLaunchSession(FormatVersion.Value, LogonSessionId, Completed.Value, plan);
        }
    }

    /// <summary>定义固定计划单项的稳定 JSON 形状。</summary>
    private sealed class ResidentLaunchPlanItemDocument
    {
        /// <summary>获取或设置不含原始路径负载的启动身份。</summary>
        public string? LaunchIdentity { get; set; }

        /// <summary>获取或设置该计划项是否已尝试。</summary>
        public bool? Attempted { get; set; }

        /// <summary>从领域计划项创建可序列化文档。</summary>
        /// <param name="planItem">已验证的固定计划项。</param>
        /// <returns>稳定的 JSON 计划项文档。</returns>
        public static ResidentLaunchPlanItemDocument FromPlanItem(ResidentLaunchPlanItem planItem)
        {
            return new ResidentLaunchPlanItemDocument
            {
                LaunchIdentity = planItem.LaunchIdentity,
                Attempted = planItem.Attempted
            };
        }

        /// <summary>验证并转换 JSON 单项为领域计划项。</summary>
        /// <returns>已验证的固定计划项。</returns>
        /// <exception cref="JsonException">JSON 单项缺失必填字段或字段无效。</exception>
        public ResidentLaunchPlanItem ToPlanItem()
        {
            if (string.IsNullOrWhiteSpace(LaunchIdentity) || !Attempted.HasValue)
            {
                throw new JsonException();
            }

            return new ResidentLaunchPlanItem(LaunchIdentity, Attempted.Value);
        }
    }
}

/// <summary>隔离损坏会话证据的唯一可替换文件操作，供 fail-closed 测试注入拒绝移动。</summary>
internal interface ICorruptResidentSessionPreserver
{
    /// <summary>将损坏会话文件移动至唯一证据路径。</summary>
    /// <param name="sourcePath">待保全的损坏会话路径。</param>
    /// <param name="destinationPath">不会覆盖既有文件的证据目标路径。</param>
    void Move(string sourcePath, string destinationPath);
}

/// <summary>使用真实文件系统保全损坏会话证据。</summary>
internal sealed class NativeCorruptResidentSessionPreserver : ICorruptResidentSessionPreserver
{
    /// <inheritdoc />
    public void Move(string sourcePath, string destinationPath)
    {
        File.Move(sourcePath, destinationPath);
    }
}
