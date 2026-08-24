using System.Security.Cryptography;
using System.Security;
using System.Text;
using DeskButler.Core.Capture;
using DeskButler.Core.Persistence;
using DeskButler.Core.Scenes;
using DeskButler.Core.Settings;
using DeskButler.Core.Time;

namespace DeskButler.Modules.WorkspaceRecovery.Capture;

/// <summary>协调安全窗口清单捕获、场景正规化和串行持久化。</summary>
public sealed class CaptureCoordinator : IDisposable
{
    private const int CurrentFormatVersion = 1;

    private readonly ButlerSettings settings;
    private readonly IWindowInventory inventory;
    private readonly SceneFilter filter;
    private readonly ISceneRepository repository;
    private readonly IClock clock;
    private readonly SemaphoreSlim saveGate = new(1, 1);

    /// <summary>创建只经既有捕获、过滤与仓库边界保存现场的协调器。</summary>
    /// <param name="settings">包含捕获开关的当前设置。</param>
    /// <param name="inventory">提供平台窗口候选的安全清单。</param>
    /// <param name="filter">应用产品排除规则的场景过滤器。</param>
    /// <param name="repository">保存并裁剪自动快照的仓库。</param>
    /// <param name="clock">提供快照捕获时刻的时钟。</param>
    public CaptureCoordinator(
        ButlerSettings settings,
        IWindowInventory inventory,
        SceneFilter filter,
        ISceneRepository repository,
        IClock clock)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        this.filter = filter ?? throw new ArgumentNullException(nameof(filter));
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>立即捕获并保存一次非空且与最新快照不同的正规化场景。</summary>
    /// <param name="reason">由调用方提供并原样持久化的稳定捕获原因。</param>
    /// <param name="cancellationToken">取消整个捕获、比较和保存串行操作的令牌。</param>
    public async Task SaveNowAsync(string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!settings.CaptureEnabled)
        {
            return;
        }

        await saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidates = await inventory.CaptureAsync(cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                return;
            }

            var items = MapNormalizedItems(candidates);
            if (items.Count == 0)
            {
                return;
            }

            var recent = await repository.GetRecentAsync(1, cancellationToken).ConfigureAwait(false);
            if (recent.Count > 0 && ScenesEqual(recent[0].Items, items))
            {
                return;
            }

            var snapshot = new SceneSnapshot(
                Guid.NewGuid(),
                CurrentFormatVersion,
                clock.UtcNow,
                reason,
                items);
            await repository.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            saveGate.Release();
        }
    }

    /// <summary>释放保存串行门；调用方须先停止所有调度和模块回调。</summary>
    public void Dispose()
    {
        saveGate.Dispose();
    }

    /// <summary>过滤候选并生成与原生枚举顺序无关的正规化场景条目。</summary>
    /// <param name="candidates">平台窗口候选集合。</param>
    /// <returns>按稳定条目标识排序的安全场景条目。</returns>
    private List<SceneItem> MapNormalizedItems(IReadOnlyList<WindowCandidate> candidates)
    {
        var safeIdentities = new List<CandidateIdentity>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (TryCreateCandidateIdentity(candidate, out var identity))
            {
                safeIdentities.Add(identity);
            }
        }

        var identities = safeIdentities
            .OrderBy(item => item.BaseIdentity, StringComparer.Ordinal)
            .ThenBy(item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ExecutablePath, StringComparer.Ordinal)
            .ThenBy(item => item.Candidate.WindowClass, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Candidate.WindowClass, StringComparer.Ordinal)
            .ThenBy(item => item.ExplorerPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ExplorerPath, StringComparer.Ordinal)
            .ThenBy(item => item.Candidate.Title, StringComparer.Ordinal)
            .ThenBy(item => item.Candidate.Monitor.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Candidate.Monitor.DeviceName, StringComparer.Ordinal)
            .ThenBy(item => item.Candidate.Monitor.WorkArea.Left)
            .ThenBy(item => item.Candidate.Monitor.WorkArea.Top)
            .ThenBy(item => item.Candidate.Monitor.WorkArea.Width)
            .ThenBy(item => item.Candidate.Monitor.WorkArea.Height)
            .ThenBy(item => item.Candidate.Monitor.DpiX)
            .ThenBy(item => item.Candidate.Monitor.DpiY)
            .ThenBy(item => item.Candidate.Bounds.Left)
            .ThenBy(item => item.Candidate.Bounds.Top)
            .ThenBy(item => item.Candidate.Bounds.Width)
            .ThenBy(item => item.Candidate.Bounds.Height)
            .ThenBy(item => item.Candidate.State)
            .ThenBy(item => item.Candidate.WasElevatedOrInaccessible)
            .ToArray();

        var items = new List<SceneItem>(identities.Length);
        foreach (var group in identities.GroupBy(item => item.BaseIdentity, StringComparer.Ordinal))
        {
            var duplicateCount = group.Count();
            var duplicateIndex = 0;
            foreach (var identity in group)
            {
                duplicateIndex++;
                var candidate = identity.Candidate;
                var itemId = duplicateCount == 1
                    ? identity.BaseIdentity
                    : $"{identity.BaseIdentity}:{duplicateIndex:D4}";
                items.Add(new SceneItem(
                    itemId,
                    identity.ExecutablePath,
                    candidate.WindowClass,
                    candidate.Title,
                    identity.ExplorerPath,
                    candidate.Bounds,
                    candidate.State,
                    candidate.Monitor,
                    candidate.WasElevatedOrInaccessible));
            }
        }

        return items;
    }

    /// <summary>隔离单个候选的路径格式错误，并生成后续不会再触发路径解析的安全身份。</summary>
    /// <param name="candidate">待过滤和正规化的窗口候选。</param>
    /// <param name="identity">成功时返回包含持久化路径的候选身份。</param>
    /// <returns>候选可安全捕获时为真；被过滤或路径畸形时为假。</returns>
    private bool TryCreateCandidateIdentity(WindowCandidate candidate, out CandidateIdentity identity)
    {
        try
        {
            if (!filter.ShouldCapture(candidate))
            {
                identity = null!;
                return false;
            }

            var executablePath = Path.GetFullPath(candidate.ExecutablePath!);
            var explorerPath = NormalizeOptionalPath(candidate.ExplorerPath);
            identity = new CandidateIdentity(
                candidate,
                CreateBaseIdentity(candidate, executablePath, explorerPath),
                executablePath,
                explorerPath);
            return true;
        }
        catch (Exception exception) when (IsRecoverablePathException(exception))
        {
            // 单窗口路径可能在枚举后失效或畸形；只跳过该项，绝不吞取消、OOM 等非路径异常。
            identity = null!;
            return false;
        }
    }

    /// <summary>从不含 PID、句柄、窗口位置与状态的最小恢复身份生成 SHA-256 标识。</summary>
    /// <param name="candidate">已经通过安全过滤的候选窗口。</param>
    /// <returns>使用无歧义长度前缀编码生成的小写十六进制标识。</returns>
    private static string CreateBaseIdentity(
        WindowCandidate candidate,
        string executablePath,
        string? explorerPath)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            WriteIdentityField(writer, CanonicalizeWindowsPath(executablePath));
            WriteIdentityField(writer, candidate.WindowClass.ToUpperInvariant());
            WriteIdentityField(writer, CanonicalizeOptionalWindowsPath(explorerPath));
            WriteIdentityField(writer, candidate.Title);
        }

        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))))
            .ToLowerInvariant();
    }

    /// <summary>写入带显式字节长度的身份字段，使空值、空串和字段拼接均无歧义。</summary>
    /// <param name="writer">写入哈希原文的二进制写入器。</param>
    /// <param name="value">待编码字段；空值使用负长度哨兵。</param>
    private static void WriteIdentityField(BinaryWriter writer, string? value)
    {
        if (value is null)
        {
            writer.Write(-1);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    /// <summary>在有值时正规化资源管理器路径，并保留缺失路径。</summary>
    /// <param name="path">可能为空的目录路径提示。</param>
    /// <returns>完整路径或空值。</returns>
    private static string? NormalizeOptionalPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
    }

    /// <summary>把 Windows 路径投影为大小写不敏感且尾分隔符一致的比较形式。</summary>
    /// <param name="path">要正规化的绝对或相对 Windows 路径。</param>
    /// <returns>去除非根尾分隔符并转为大写的完整路径。</returns>
    private static string CanonicalizeWindowsPath(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();
    }

    /// <summary>在保留空值语义的同时正规化可选 Windows 路径。</summary>
    /// <param name="path">可能为空的 Windows 路径。</param>
    /// <returns>正规化路径或空值。</returns>
    private static string? CanonicalizeOptionalWindowsPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : CanonicalizeWindowsPath(path);
    }

    /// <summary>判断异常是否仅表示当前候选路径无法正规化。</summary>
    /// <param name="exception">路径过滤或正规化期间捕获的异常。</param>
    /// <returns>可安全隔离到单个候选的路径异常时为真。</returns>
    private static bool IsRecoverablePathException(Exception exception)
    {
        return exception is ArgumentException or NotSupportedException or PathTooLongException or SecurityException;
    }

    /// <summary>忽略快照元数据和集合顺序，比较两个完整场景的持久字段。</summary>
    /// <param name="left">已有快照条目。</param>
    /// <param name="right">本次捕获条目。</param>
    /// <returns>正规化条目完全相同时为真。</returns>
    private static bool ScenesEqual(IReadOnlyList<SceneItem> left, List<SceneItem> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        if (!TryBuildSceneMultiset(left, out var leftItems) ||
            !TryBuildSceneMultiset(right, out var rightItems) ||
            leftItems.Count != rightItems.Count)
        {
            return false;
        }

        return leftItems.All(pair => rightItems.TryGetValue(pair.Key, out var count) && count == pair.Value);
    }

    /// <summary>把场景条目投影为与身份一致的 canonical 多重集，忽略集合顺序和路径显示形式。</summary>
    /// <param name="items">要正规化的场景条目。</param>
    /// <param name="multiset">成功时返回比较键及其重复次数。</param>
    /// <returns>所有条目路径均可正规化时为真；已有快照含畸形路径时为假。</returns>
    private static bool TryBuildSceneMultiset(
        IEnumerable<SceneItem> items,
        out Dictionary<SceneComparisonKey, int> multiset)
    {
        multiset = [];
        try
        {
            foreach (var item in items)
            {
                var key = new SceneComparisonKey(
                    CanonicalizeWindowsPath(item.ExecutablePath),
                    item.WindowClass.ToUpperInvariant(),
                    item.TitleHint,
                    CanonicalizeOptionalWindowsPath(item.ExplorerPath),
                    item.Bounds,
                    item.State,
                    new MonitorComparisonKey(
                        item.Monitor.DeviceName.ToUpperInvariant(),
                        item.Monitor.WorkArea,
                        item.Monitor.DpiX,
                        item.Monitor.DpiY),
                    item.WasElevated);
                multiset.TryGetValue(key, out var count);
                multiset[key] = count + 1;
            }

            return true;
        }
        catch (Exception exception) when (IsRecoverablePathException(exception))
        {
            multiset.Clear();
            return false;
        }
    }

    /// <summary>绑定候选窗口与不含瞬时平台身份的基础标识。</summary>
    /// <param name="Candidate">窗口候选数据。</param>
    /// <param name="BaseIdentity">候选的基础身份哈希。</param>
    private sealed record CandidateIdentity(
        WindowCandidate Candidate,
        string BaseIdentity,
        string ExecutablePath,
        string? ExplorerPath);

    /// <summary>表示忽略路径显示形式和条目标识后的完整场景比较键。</summary>
    private sealed record SceneComparisonKey(
        string ExecutablePath,
        string WindowClass,
        string? TitleHint,
        string? ExplorerPath,
        WindowBounds Bounds,
        SceneWindowState State,
        MonitorComparisonKey Monitor,
        bool WasElevated);

    /// <summary>表示显示器名称大小写不敏感的场景比较键。</summary>
    private sealed record MonitorComparisonKey(
        string DeviceName,
        WindowBounds WorkArea,
        uint DpiX,
        uint DpiY);
}
