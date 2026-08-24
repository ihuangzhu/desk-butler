using DeskButler.Core.Capture;

namespace DeskButler.Modules.WorkspaceRecovery.Restore;

/// <summary>为每次恢复执行创建独立 HWND 原子认领目录。</summary>
internal interface IHandleRegistryFactory
{
    /// <summary>创建不与其他 ExecuteAsync 调用共享状态的目录。</summary>
    IHandleRegistry Create();
}

/// <summary>定义 lease、claim 和未来 reservation 的单锁状态机。</summary>
internal interface IHandleRegistry
{
    /// <summary>激活指定计划项的新一代 lease。</summary>
    HandleLease Activate(int itemIndex);

    /// <summary>在同一状态锁内让 lease 失活，使迟到 worker 无法认领或预留。</summary>
    void Deactivate(HandleLease lease);

    /// <summary>仅在 item work 真正结束时释放该 lease 持有的 HWND in-flight gate。</summary>
    void CompleteWork(HandleLease lease);

    /// <summary>仅当 lease 仍 active 且 HWND 未被其他项占用时原子认领。</summary>
    bool TryClaim(HandleLease lease, RuntimeWindowFingerprint fingerprint);

    /// <summary>仅由 active lease 为后续 Launch 项原子预留窗口。</summary>
    bool TryReserve(HandleLease lease, int futureItemIndex, RuntimeWindowFingerprint fingerprint);

    /// <summary>取得当前项 reservation 快照；快照本身不授予定位权。</summary>
    IReadOnlyList<RuntimeWindowFingerprint> GetReservationSnapshots(HandleLease lease);

    /// <summary>仅当 reservation 仍精确匹配时原子删除。</summary>
    bool RemoveReservationIfMatches(HandleLease lease, RuntimeWindowFingerprint reservedFingerprint);

    /// <summary>重验证后，仅在旧 reservation 仍存在且实例身份相同时原子转为 claim。</summary>
    bool TryClaimReservation(
        HandleLease lease,
        RuntimeWindowFingerprint reservedFingerprint,
        RuntimeWindowFingerprint currentFingerprint);
}

/// <summary>默认 HWND registry 工厂。</summary>
internal sealed class HandleRegistryFactory : IHandleRegistryFactory
{
    /// <summary>创建新的内存 registry。</summary>
    public IHandleRegistry Create() => new HandleRegistry();
}

/// <summary>以单锁保证 lease 检查与 HWND claim/reserve 不可分割。</summary>
internal sealed class HandleRegistry : IHandleRegistry
{
    private readonly object syncRoot = new();
    private readonly HashSet<HandleLease> activeLeases = [];
    private readonly HashSet<WindowInstanceIdentity> consumedInstances = [];
    private readonly HashSet<nint> consumedUnknownHandles = [];
    private readonly Dictionary<nint, InFlightRegistration> inFlightByHandle = [];
    private readonly Dictionary<WindowInstanceIdentity, ReservationRegistration> reservations = [];
    private long nextGeneration;

    /// <summary>创建带单调 generation 的 active lease。</summary>
    public HandleLease Activate(int itemIndex)
    {
        lock (syncRoot)
        {
            var lease = new HandleLease(itemIndex, checked(++nextGeneration));
            activeLeases.Add(lease);
            return lease;
        }
    }

    /// <summary>原子移除 active lease；重复失活无副作用。</summary>
    public void Deactivate(HandleLease lease)
    {
        lock (syncRoot)
        {
            activeLeases.Remove(lease);
        }
    }

    /// <summary>work finally 调用；Deactivate 不得代替此操作，以免迟到 Position 与后项并发。</summary>
    public void CompleteWork(HandleLease lease)
    {
        lock (syncRoot)
        {
            var ownedHandles = inFlightByHandle
                .Where(entry => entry.Value.Lease == lease)
                .Select(entry => entry.Key)
                .ToArray();
            foreach (var handle in ownedHandles)
            {
                inFlightByHandle.Remove(handle);
            }
        }
    }

    /// <summary>在 lease 活性检查与句柄写入之间不释放状态锁。</summary>
    public bool TryClaim(HandleLease lease, RuntimeWindowFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        lock (syncRoot)
        {
            if (fingerprint.Handle == 0 || !activeLeases.Contains(lease))
            {
                return false;
            }

            if (inFlightByHandle.TryGetValue(fingerprint.Handle, out var inFlight))
            {
                return inFlight.Lease == lease &&
                       inFlight.InstanceIdentity == fingerprint.InstanceIdentity;
            }

            if (IsConsumedConflict(fingerprint.InstanceIdentity))
            {
                return false;
            }

            if (reservations.TryGetValue(fingerprint.InstanceIdentity, out var reservation))
            {
                if (reservation.ItemIndex != lease.ItemIndex || reservation.Fingerprint != fingerprint)
                {
                    return false;
                }

                reservations.Remove(fingerprint.InstanceIdentity);
            }

            ConsumeAndEnterFlight(lease, fingerprint.InstanceIdentity);
            return true;
        }
    }

    /// <summary>由 active 当前 lease 为严格归属的未来项预留 HWND。</summary>
    public bool TryReserve(
        HandleLease lease,
        int futureItemIndex,
        RuntimeWindowFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        lock (syncRoot)
        {
            if (fingerprint.Handle == 0 ||
                futureItemIndex <= lease.ItemIndex ||
                !activeLeases.Contains(lease))
            {
                return false;
            }

            if (IsConsumedConflict(fingerprint.InstanceIdentity))
            {
                return false;
            }

            if (!reservations.TryGetValue(fingerprint.InstanceIdentity, out var reservation))
            {
                reservations.Add(
                    fingerprint.InstanceIdentity,
                    new ReservationRegistration(futureItemIndex, fingerprint));
                return true;
            }

            if (reservation.ItemIndex != futureItemIndex)
            {
                return false;
            }

            // reservation 不占 HWND gate；同实例易变字段改变时刷新快照。
            reservations[fingerprint.InstanceIdentity] =
                new ReservationRegistration(futureItemIndex, fingerprint);
            return true;
        }
    }

    /// <summary>返回当前 active lease 的 reservation 不可变快照，不进行 claim。</summary>
    public IReadOnlyList<RuntimeWindowFingerprint> GetReservationSnapshots(HandleLease lease)
    {
        lock (syncRoot)
        {
            if (!activeLeases.Contains(lease))
            {
                return [];
            }

            return reservations.Values
                .Where(registration => registration.ItemIndex == lease.ItemIndex)
                .Select(registration => registration.Fingerprint)
                .ToArray();
        }
    }

    /// <summary>active lease 仅能删除精确属于自身 item 的旧 reservation。</summary>
    public bool RemoveReservationIfMatches(
        HandleLease lease,
        RuntimeWindowFingerprint reservedFingerprint)
    {
        ArgumentNullException.ThrowIfNull(reservedFingerprint);
        lock (syncRoot)
        {
            if (!activeLeases.Contains(lease) ||
                !reservations.TryGetValue(reservedFingerprint.InstanceIdentity, out var registration) ||
                registration.ItemIndex != lease.ItemIndex ||
                registration.Fingerprint != reservedFingerprint)
            {
                return false;
            }

            return reservations.Remove(reservedFingerprint.InstanceIdentity);
        }
    }

    /// <summary>在同一锁中验证 lease、旧快照和实例身份，再把 reservation 转为 claim。</summary>
    public bool TryClaimReservation(
        HandleLease lease,
        RuntimeWindowFingerprint reservedFingerprint,
        RuntimeWindowFingerprint currentFingerprint)
    {
        ArgumentNullException.ThrowIfNull(reservedFingerprint);
        ArgumentNullException.ThrowIfNull(currentFingerprint);
        lock (syncRoot)
        {
            if (currentFingerprint.Handle == 0 ||
                !activeLeases.Contains(lease) ||
                reservedFingerprint.InstanceIdentity != currentFingerprint.InstanceIdentity ||
                !reservations.TryGetValue(reservedFingerprint.InstanceIdentity, out var registration) ||
                registration.ItemIndex != lease.ItemIndex ||
                registration.Fingerprint != reservedFingerprint)
            {
                return false;
            }

            if (inFlightByHandle.TryGetValue(currentFingerprint.Handle, out var inFlight))
            {
                return inFlight.Lease == lease &&
                       inFlight.InstanceIdentity == currentFingerprint.InstanceIdentity;
            }

            if (IsConsumedConflict(currentFingerprint.InstanceIdentity))
            {
                return false;
            }

            reservations.Remove(reservedFingerprint.InstanceIdentity);
            ConsumeAndEnterFlight(lease, currentFingerprint.InstanceIdentity);
            return true;
        }
    }

    /// <summary>同实例永久防重；unknown PID0 无法与以后 runtime 证明不同，故按 HWND 保守永久阻止。</summary>
    private bool IsConsumedConflict(WindowInstanceIdentity identity) =>
        consumedInstances.Contains(identity) ||
        consumedUnknownHandles.Contains(identity.Handle) ||
        IsUnknownIdentity(identity) && consumedInstances.Any(consumed => consumed.Handle == identity.Handle);

    /// <summary>原子记录 instance 已消费并为 work 建立 HWND in-flight gate。</summary>
    private void ConsumeAndEnterFlight(HandleLease lease, WindowInstanceIdentity identity)
    {
        consumedInstances.Add(identity);
        if (IsUnknownIdentity(identity))
        {
            consumedUnknownHandles.Add(identity.Handle);
        }

        inFlightByHandle.Add(identity.Handle, new InFlightRegistration(lease, identity));
    }

    /// <summary>仅 Reuse 的 PID0/空身份属于不可证明实例。</summary>
    private static bool IsUnknownIdentity(WindowInstanceIdentity identity) =>
        identity.ProcessId == 0 &&
        identity.ExecutablePath is null &&
        identity.WindowClass.Length == 0;

    /// <summary>保存一个未来 item 的 reservation 快照；不占用 HWND in-flight gate。</summary>
    private sealed record ReservationRegistration(
        int ItemIndex,
        RuntimeWindowFingerprint Fingerprint);

    /// <summary>保存数值 HWND 当前由哪个 work lease 和实例占用。</summary>
    private sealed record InFlightRegistration(
        HandleLease Lease,
        WindowInstanceIdentity InstanceIdentity);
}

/// <summary>标识一个计划项单次执行的 generation lease。</summary>
/// <param name="ItemIndex">计划项索引。</param>
/// <param name="Generation">本次执行内单调 generation。</param>
internal readonly record struct HandleLease(int ItemIndex, long Generation);

/// <summary>表示一个 OS 窗口实例；标题、Explorer 路径和几何变化不产生新实例。</summary>
internal sealed record WindowInstanceIdentity(
    nint Handle,
    int ProcessId,
    string? ExecutablePath,
    string WindowClass)
{
    /// <summary>从候选建立仅用于当次恢复的规范化实例身份；PID 不持久化。</summary>
    internal static WindowInstanceIdentity Create(WindowCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return new WindowInstanceIdentity(
            candidate.Handle,
            candidate.ProcessId,
            NormalizePath(candidate.ExecutablePath),
            NormalizeWindowClass(candidate.WindowClass));
    }

    /// <summary>为无法枚举身份的 Reuse 计划建立独立排他键。</summary>
    internal static WindowInstanceIdentity ForReuse(nint handle) =>
        new(handle, 0, null, string.Empty);

    /// <summary>规范化 Windows 绝对路径并统一大小写；畸形路径视为空。</summary>
    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>规范化窗口类；运行期 fingerprint 按现有匹配语义忽略大小写及边缘空白。</summary>
    private static string NormalizeWindowClass(string? windowClass) =>
        string.IsNullOrWhiteSpace(windowClass) ? string.Empty : windowClass.Trim().ToUpperInvariant();
}

/// <summary>保存实例身份和只参与当前 owner 匹配的易变字段。</summary>
internal sealed record RuntimeWindowFingerprint(
    WindowInstanceIdentity InstanceIdentity,
    string? ExplorerPath,
    string? Title)
{
    internal nint Handle => InstanceIdentity.Handle;

    /// <summary>从完整候选建立运行期快照。</summary>
    internal static RuntimeWindowFingerprint Create(WindowCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return new RuntimeWindowFingerprint(
            WindowInstanceIdentity.Create(candidate),
            NormalizePath(candidate.ExplorerPath),
            NormalizeTitle(candidate.Title));
    }

    /// <summary>为 Reuse 计划的瞬时 HWND 创建仅用于原子排他 claim 的身份。</summary>
    internal static RuntimeWindowFingerprint ForHandle(nint handle) =>
        new(WindowInstanceIdentity.ForReuse(handle), null, null);

    /// <summary>规范化 Explorer 路径；不可用时不参与快照相等。</summary>
    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>规范化可选标题并按既有匹配语义忽略大小写。</summary>
    private static string? NormalizeTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) ? null : title.Trim().ToUpperInvariant();
}
